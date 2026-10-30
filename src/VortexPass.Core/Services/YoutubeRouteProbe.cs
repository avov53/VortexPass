using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace VortexPass.Core.Services;
public static class YoutubeRouteProbe
{
    public static string Error(Exception error)
    {
        var parts=new List<string>();
        for(Exception? current=error;current is not null&&parts.Count<4;current=current.InnerException)
        {
            var text=Regex.Replace(current.Message,@"https?://[^\s]+",m=>Uri.TryCreate(m.Value,UriKind.Absolute,out var uri)?uri.Host:"[URL]");
            if(!parts.Contains(text))parts.Add(text.Length>180?text[..180]:text);
        }
        return string.Join(" → ",parts);
    }
    public static async Task<ServiceHealthResult> CheckAsync(CancellationToken token,TimeSpan? roundBudget=null)
    {
        using var round=CancellationTokenSource.CreateLinkedTokenSource(token);round.CancelAfter(roundBudget??TimeSpan.FromSeconds(8));var deadline=round.Token;
        var watch=Stopwatch.StartNew();var groups=new List<ServiceRouteGroup>();
        IPAddress[] addresses=[];
        try{using var dns=CancellationTokenSource.CreateLinkedTokenSource(deadline);dns.CancelAfter(TimeSpan.FromSeconds(2));addresses=await Dns.GetHostAddressesAsync("www.youtube.com",dns.Token);groups.Add(new("dns",addresses.Length>0?"confirmed":"unconfirmed",string.Join(", ",addresses.Select(a=>a.ToString()))));}
        catch(Exception ex)when(ex is SocketException or OperationCanceledException){token.ThrowIfCancellationRequested();groups.Add(new("dns","unconfirmed",Error(ex)));}
        using var handler=new SocketsHttpHandler{UseProxy=false,UseCookies=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(3)};
        using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        var connections=addresses.Where(a=>a.AddressFamily==AddressFamily.InterNetwork).Take(2).Concat(addresses.Where(a=>a.AddressFamily==AddressFamily.InterNetworkV6).Take(2)).Select(a=>Transport(a,deadline)).ToArray();
        var page=Resource(client,"page",new("https://www.youtube.com/"),deadline);
        var api=Resource(client,"api",new("https://suggestqueries.google.com/complete/search?client=firefox&ds=yt&q=vortexpass"),deadline);
        var image=Resource(client,"images",new("https://i.ytimg.com/vi/jNQXAC9IVRw/default.jpg"),deadline);
        var video=PublicMediaProbeService.CheckAsync("video",deadline,directFresh:true);
        var avatar=PublicMediaProbeService.CheckAsync("avatar",deadline,directFresh:true);
        await Task.WhenAll(connections.Cast<Task>().Concat(new Task[]{page,api,image,ObserveVideo(),ObserveAvatar()}));
        async Task ObserveVideo(){try{await video;}catch(OperationCanceledException)when(!token.IsCancellationRequested){}}
        async Task ObserveAvatar(){try{await avatar;}catch(OperationCanceledException)when(!token.IsCancellationRequested){}}
        foreach(var connection in connections)groups.AddRange(connection.Result.Groups);
        groups.Add(await page);groups.Add(await api);groups.Add(await image);
        var channelImage=avatar.Status==TaskStatus.RanToCompletion?avatar.Result:new PublicMediaResult(false,"Аватарка канала не проверена за время раунда");
        groups.Add(new("avatars",channelImage.Confirmed?"confirmed":channelImage.ResourceLocated?"unconfirmed":"controlResourceUnconfirmed",channelImage.Detail));
        var media=video.Status==TaskStatus.RanToCompletion?video.Result:new PublicMediaResult(false,"Не удалось получить/проверить контрольный видеоресурс за время раунда");
        groups.Add(new("video",media.Confirmed?"confirmed":"controlResourceUnconfirmed",media.Detail));
        groups.Add(new("browserSession","sessionRequired","Firefox: открытие страницы, видео и перемотка проверяются пользователем"));
        groups.Add(new("probeDuration","informational",$"Полная проверка: {watch.ElapsedMilliseconds} мс; это не пинг. Задержка маршрута измеряется отдельно по TCP."));
        var tcpLatency=LocalStrategyAdvisor.Median(connections.Where(c=>c.Result.TcpMs.HasValue).Select(c=>c.Result.TcpMs!.Value));
        token.ThrowIfCancellationRequested();return Evaluate(groups,tcpLatency??0) with{LatencyMs=tcpLatency};
    }
    public static ServiceHealthResult Evaluate(IReadOnlyList<ServiceRouteGroup> groups,long latency)
    {
        bool Ok(string id)=>groups.Any(g=>g.Id==id&&g.State=="confirmed");
        var full=Ok("page")&&Ok("api")&&Ok("images")&&Ok("avatars")&&Ok("video");
        var state=full?ServiceHealthState.Healthy:Ok("page")?ServiceHealthState.Partial:!Ok("dns")?ServiceHealthState.DnsUnavailable:groups.Any(g=>g.Id.StartsWith("tls-")&&g.State=="confirmed")?ServiceHealthState.HttpUnavailable:groups.Any(g=>g.Id.StartsWith("tcp-")&&g.State=="confirmed")?ServiceHealthState.TlsFailed:ServiceHealthState.TcpTimeout;
        var detail=$"Страница: {(Ok("page")?"подтверждена":"не подтверждена")}; API: {(Ok("api")?"ответ подтверждён":"не подтверждено")}; миниатюры: {(Ok("images")?"подтверждены":"не подтверждены")}; аватарки каналов: {(Ok("avatars")?"подтверждены":"не подтверждены")}; видео: {(Ok("video")?"реальные байты подтверждены":"контрольный ресурс не подтверждён")}. Браузерная сессия проверяется отдельно.";
        var stages=new ServiceProbeStages(string.Join("; ",groups.Where(g=>g.Id=="dns").Select(g=>g.Detail)),string.Join("; ",groups.Where(g=>g.Id.StartsWith("tcp-")).Select(g=>g.Detail)),string.Join("; ",groups.Where(g=>g.Id.StartsWith("tls-")).Select(g=>g.Detail)),groups.FirstOrDefault(g=>g.Id=="page")?.Detail??"не проверено");
        return new("youtube",state,detail,latency,stages,ContentConfirmed:full,Groups:groups,SystemDnsReady:Ok("dns"));
    }
    private static async Task<(IReadOnlyList<ServiceRouteGroup> Groups,long? TcpMs)> Transport(IPAddress address,CancellationToken token)
    {
        var family=address.AddressFamily==AddressFamily.InterNetwork?"IPv4":"IPv6";var label=family+" "+address;var output=new List<ServiceRouteGroup>();
        var watch=Stopwatch.StartNew();long? tcpMs=null;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMilliseconds(2500));
        try
        {
            using var tcp=new TcpClient(address.AddressFamily);await tcp.ConnectAsync(address,443,deadline.Token);tcpMs=watch.ElapsedMilliseconds;output.Add(new("tcp-"+label,"confirmed",label+$": TCP 443 OK ({tcpMs} мс)"));
            await using var tls=new SslStream(tcp.GetStream());await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="www.youtube.com",EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13},deadline.Token);
            output.Add(new("tls-"+label,"confirmed",label+": "+tls.SslProtocol,watch.ElapsedMilliseconds-tcpMs.Value));
        }
        catch(Exception ex)when(ex is IOException or SocketException or AuthenticationException or OperationCanceledException)
        {output.Add(new((output.Count>0?"tls-":"tcp-")+label,"unconfirmed",label+": "+Error(ex)));}
        return(output,tcpMs);
    }
    public static async Task<ServiceRouteGroup> Resource(HttpClient client,string kind,Uri uri,CancellationToken token)
    {
        var timing=Stopwatch.StartNew();
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.UserAgent.ParseAdd("Mozilla/5.0 VortexPass/0.0.7");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
            if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)return new(kind,"accessRestricted",$"{uri.Host}: HTTP {(int)response.StatusCode}; доступ ограничен, это не доказательство сетевого отказа");
            await using var stream=await response.Content.ReadAsStreamAsync(token);using var buffer=new MemoryStream();var block=new byte[8192];var limit=kind=="page"?256*1024:32*1024;
            while(buffer.Length<limit){var n=await stream.ReadAsync(block.AsMemory(0,(int)Math.Min(block.Length,limit-buffer.Length)),token);if(n==0)break;await buffer.WriteAsync(block.AsMemory(0,n),token);}
            var bytes=buffer.ToArray();var text=Encoding.UTF8.GetString(bytes);
            var valid=response.IsSuccessStatusCode&&(kind=="images"?RouteProbePolicy.IsImage((int)response.StatusCode,response.Content.Headers.ContentType?.MediaType,bytes):kind=="page"?text.Contains("ytcfg.set",StringComparison.Ordinal)||text.Contains("ytInitialData",StringComparison.Ordinal)||text.Contains("<ytd-app",StringComparison.Ordinal):text.TrimStart().StartsWith("[",StringComparison.Ordinal));
            return new(kind,valid?"confirmed":"unconfirmed",$"{uri.Host}: HTTP {(int)response.StatusCode}, {bytes.Length} B; "+(valid?"содержимое подтверждено":"ожидаемое содержимое не подтверждено"),timing.ElapsedMilliseconds);
        }
        catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException){return new(kind,"unconfirmed",uri.Host+": "+Error(ex));}
    }
}
