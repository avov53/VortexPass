using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VortexPass.Core.Services;

public sealed record RobloxResourceCheck(string Host,string Kind,string State,string Detail,long? LatencyMs=null);
public sealed record RobloxPlayerDiagnostic(string State,string Detail,string[] Hosts,IReadOnlyList<RobloxResourceCheck> Resources);

public static class RobloxContentProbeService
{
    private static readonly HttpClient Client=new(new SocketsHttpHandler { UseProxy=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(3) });
    private static readonly SemaphoreSlim Gate=new(3);
    private static Uri[] _resources=[];
    private static DateTimeOffset _resourcesCheckedAt;
    public static bool Allowed(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&
        (uri.Host.Equals("roblox.com",StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith(".roblox.com",StringComparison.OrdinalIgnoreCase)||uri.Host.Equals("rbxcdn.com",StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith(".rbxcdn.com",StringComparison.OrdinalIgnoreCase));
    public static string ClassifyBody(int status,string? mediaType,byte[] bytes)
    {
        if(status is 401 or 403)return "authorizationRequired";
        if(status is 404 or 410)return "resourceUnavailable";
        if(status is not (200 or 206))return "routeUnavailable";
        var start=Encoding.UTF8.GetString(bytes.Take(128).ToArray()).TrimStart();
        if(start.StartsWith("<!doctype html",StringComparison.OrdinalIgnoreCase)||start.StartsWith("<html",StringComparison.OrdinalIgnoreCase)||mediaType?.Contains("text/html")==true)return "invalidContent";
        if(RouteProbePolicy.IsImage(status,mediaType,bytes))return "image";
        if(bytes.Length>=16&&(start.StartsWith("version ",StringComparison.Ordinal)||start.StartsWith("<roblox",StringComparison.Ordinal)||start.StartsWith("<roblox!",StringComparison.Ordinal)))return "model";
        return "unconfirmed";
    }
    public static Uri[] ExtractResources(string text)=>Regex.Matches(text,@"https://[^\s""'<>]+",RegexOptions.None,TimeSpan.FromSeconds(1))
        .Select(m=>Uri.TryCreate(WebUtility.HtmlDecode(m.Value.TrimEnd(')',']',',',';')),UriKind.Absolute,out var u)?u:null)
        .Where(u=>u is not null&&Allowed(u)&&(u.Host.EndsWith(".rbxcdn.com",StringComparison.OrdinalIgnoreCase)||u.Host.Equals("assetdelivery.roblox.com",StringComparison.OrdinalIgnoreCase))).Cast<Uri>().DistinctBy(u=>u.AbsoluteUri).Take(12).ToArray();

    public static async Task<RobloxResourceCheck> ResourceAsync(Uri uri,CancellationToken token,HttpClient? client=null)
    {
        if(!Allowed(uri))return new("","resource","unconfirmed","Недопустимый домен ресурса");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var entered=false;var watch=Stopwatch.StartNew();
        try
        {
            await Gate.WaitAsync(timeout.Token);entered=true;
            var current=uri;
            for(var redirect=0;redirect<4;redirect++)
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,current);request.Headers.Range=new(0,256*1024-1);
                using var response=await (client??Client).SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                if((int)response.StatusCode is >=300 and <400 && response.Headers.Location is { } location)
                {var next=new Uri(current,location);if(!Allowed(next))return new(current.Host,"resource","unconfirmed","Переадресация за пределы Roblox/CDN");current=next;continue;}
                await using var body=await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer=new byte[256*1024];var count=0;
                while(count<buffer.Length){var n=await body.ReadAsync(buffer.AsMemory(count),timeout.Token);if(n==0)break;count+=n;}
                var kind=ClassifyBody((int)response.StatusCode,response.Content.Headers.ContentType?.MediaType,buffer[..count]);
                return new(current.Host,kind is "image" or "model"?kind:"resource",kind is "image" or "model"?"confirmed":kind,
                    $"{current.Host}: HTTP {(int)response.StatusCode}, {count} B; {kind}",watch.ElapsedMilliseconds);
            }
            return new(uri.Host,"resource","unconfirmed","Слишком много переадресаций");
        }
        catch(OperationCanceledException){token.ThrowIfCancellationRequested();return new(uri.Host,"resource","routeUnavailable","Тайм-аут ресурса");}
        catch(Exception ex)when(ex is HttpRequestException or IOException){return new(uri.Host,"resource","routeUnavailable",uri.Host+": "+(ex is HttpRequestException http?http.HttpRequestError+": ":"")+YoutubeRouteProbe.Error(ex));}
        finally{if(entered)Gate.Release();}
    }

    public static async Task<RobloxPlayerDiagnostic> DiagnosePlayerAsync(string logDirectory,CancellationToken token)
    {
        var file=Directory.Exists(logDirectory)?new DirectoryInfo(logDirectory).EnumerateFiles("*.log").Where(f=>f.Name.Contains("Player",StringComparison.OrdinalIgnoreCase)&&f.LastWriteTimeUtc>DateTime.UtcNow.AddDays(-2)).OrderByDescending(f=>f.LastWriteTimeUtc).FirstOrDefault():null;
        if(file is null){_resources=[];return new("sessionRequired","Свежий журнал Roblox Player не найден. Запустите проблемную игру и повторите диагностику.",[],[]);}
        await using var stream=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        stream.Seek(Math.Max(0,stream.Length-1024*1024),SeekOrigin.Begin);using var reader=new StreamReader(stream);
        var text=await reader.ReadToEndAsync(token);
        var lines=text.Split('\n').Where(l=>l.Contains("asset",StringComparison.OrdinalIgnoreCase)||l.Contains("texture",StringComparison.OrdinalIgnoreCase)||l.Contains("mesh",StringComparison.OrdinalIgnoreCase)||l.Contains("http",StringComparison.OrdinalIgnoreCase));
        _resourcesCheckedAt=DateTimeOffset.UtcNow;
        var discovered=ExtractResources(string.Join('\n',lines));
        var checks=await Task.WhenAll(discovered.Select(u=>ResourceAsync(u,token)));
        var pairs=discovered.Zip(checks,(uri,check)=>(uri,check)).ToArray();
        _resources=pairs.Where(p=>p.check.Kind=="model").Take(1).Concat(pairs.Where(p=>p.check.Kind=="image").Take(1)).Concat(pairs.Where(p=>p.check.State is "routeUnavailable" or "invalidContent")).Concat(pairs).DistinctBy(p=>p.uri).Take(3).Select(p=>p.uri).ToArray();
        return new("complete",checks.Length==0?"В журнале нет проверяемых публичных CDN-ссылок. Модели не подтверждены.":"Проверены ссылки из свежего журнала Player; аккаунт и cookies не читались.",_resources.Select(u=>u.Host).Distinct().ToArray(),checks);
    }

    public static async Task<(bool Confirmed,ServiceRouteGroup[] Groups,long? Latency)> CheckAsync(CancellationToken token)
    {
        // Each strategy trial needs new connections; pooled TLS sessions from
        // the preceding winws strategy cannot validate the current candidate.
        using var client=new HttpClient(new SocketsHttpHandler {UseProxy=false,UseCookies=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(3)});
        var dnsReady=true;
        foreach(var host in new[]{"thumbnails.roblox.com","tr.rbxcdn.com"})
        try { using var dns=CancellationTokenSource.CreateLinkedTokenSource(token);dns.CancelAfter(TimeSpan.FromSeconds(2));if((await Dns.GetHostAddressesAsync(host,dns.Token)).Length==0)dnsReady=false; }
        catch(Exception ex)when(ex is System.Net.Sockets.SocketException or OperationCanceledException){token.ThrowIfCancellationRequested();dnsReady=false;}
        async Task<(ServiceRouteGroup Group,long? Latency)> Images(string group="images",string? endpoint=null)
        {
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var reply=await client.GetAsync(endpoint??"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=1&size=48x48&format=Png&isCircular=false",HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                if(reply.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)return(new(group,"authorizationRequired","API требует доступ; это не доказательство блокировки"),null);
                reply.EnsureSuccessStatusCode();await using var stream=await reply.Content.ReadAsStreamAsync(timeout.Token);
                var bytes=new byte[64*1024];var count=0;while(count<bytes.Length){var n=await stream.ReadAsync(bytes.AsMemory(count),timeout.Token);if(n==0)break;count+=n;}
                using var json=JsonDocument.Parse(bytes.AsMemory(0,count));var data=json.RootElement.GetProperty("data")[0];
                if(data.TryGetProperty("state",out var state)&&state.GetString()!="Completed")return(new(group,"unconfirmed","Thumbnail ещё не готов; это не сетевой отказ"),null);
                if(data.TryGetProperty("imageUrl",out var url)&&Uri.TryCreate(url.GetString(),UriKind.Absolute,out var uri)&&Allowed(uri))
                {var image=await ResourceAsync(uri,timeout.Token,client);return(new(group,image.State,image.Detail),image.LatencyMs);}
                return(new(group,"unconfirmed","API не вернул публичную картинку"),null);
            }
            catch(OperationCanceledException){token.ThrowIfCancellationRequested();return(new(group,"routeUnavailable","Тайм-аут thumbnail API"),null);}
            catch(Exception ex)when(ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException){return(new(group,"routeUnavailable","thumbnails.roblox.com: "+YoutubeRouteProbe.Error(ex)),null);}
        }
        async Task<ServiceRouteGroup> Delivery()
        {
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response=await client.GetAsync("https://assetdelivery.roblox.com/v2/asset?id=1",HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                return new("assetDelivery",(int)response.StatusCode<500?"confirmed":"routeUnavailable",$"Asset Delivery: HTTP {(int)response.StatusCode}; ответ API подтверждает транспорт, не доступ к модели");
            }
            catch(OperationCanceledException){token.ThrowIfCancellationRequested();return new("assetDelivery","routeUnavailable","Тайм-аут Asset Delivery");}
            catch(HttpRequestException){return new("assetDelivery","routeUnavailable","DNS/TLS/HTTP Asset Delivery не подтверждён");}
        }
        var imageTask=Images();var gameImageTask=Images("gameImages","https://thumbnails.roblox.com/v1/games/icons?universeIds=1537690962&size=150x150&format=Png&isCircular=false");var deliveryTask=Delivery();
        var resourcesTask=Task.WhenAll((_resourcesCheckedAt>DateTimeOffset.UtcNow.AddMinutes(-15)?_resources:[]).Take(3).Select(u=>ResourceAsync(u,token,client)));
        await Task.WhenAll(imageTask,gameImageTask,deliveryTask,resourcesTask);
        var resources=resourcesTask.Result;
        ServiceRouteGroup[] groups=[new("dns",dnsReady?"confirmed":"routeUnavailable","Windows DNS: thumbnails.roblox.com и tr.rbxcdn.com"),imageTask.Result.Group,gameImageTask.Result.Group,deliveryTask.Result,
            new("models",resources.Any(r=>r.Kind=="model"&&r.State=="confirmed")?"confirmed":"sessionRequired","Модели "+(resources.Any(r=>r.Kind=="model"&&r.State=="confirmed")?"подтверждены: получены байты CDN":"не подтверждены; нужна диагностика свежего журнала Player")),
            new("playerResources",resources.Length==0?"sessionRequired":resources.Any(r=>r.State is "routeUnavailable" or "invalidContent")?"routeUnavailable":resources.All(r=>r.State=="confirmed")?"confirmed":"unconfirmed",resources.Length==0?"Нет проверенных ссылок из журнала Player; модели не подтверждены":"Ресурсы Player: "+string.Join("; ",resources.Select(r=>r.Detail))),
            new("textures",resources.Any(r=>r.Kind=="image"&&r.State=="confirmed")?"confirmed":"sessionRequired","Текстуры игры проверяются по ссылкам из журнала Player"),
            new("gameSession","sessionRequired","Игровая UDP-сессия проверяется клиентом; маршрут не изменяется")];
        return(groups.Where(g=>g.Id is "images" or "gameImages" or "assetDelivery" or "models" or "textures" or "playerResources").All(g=>g.State=="confirmed"),groups,imageTask.Result.Latency);
    }

}

public static class RobloxSelectionPolicy
{
    public static bool HasConfirmedContent(IEnumerable<ServiceRouteGroup>? groups)=>groups?.Any(g=>g.Id is "images" or "models" or "textures" or "playerResources"&&g.State=="confirmed")==true;
    public static bool Preserves(IEnumerable<ServiceRouteGroup>? baseline,IEnumerable<ServiceRouteGroup>? candidate)
    {
        var required=(baseline??[]).Where(g=>g.State=="confirmed").Select(g=>g.Id).ToArray();
        return required.All(id=>candidate?.Any(g=>g.Id==id&&g.State=="confirmed")==true);
    }
    public static bool NeedsEnhancement(IEnumerable<ServiceRouteGroup>? groups)=>groups?.Any(g=>g.Id is "images" or "gameImages" or "assetDelivery" or "models" or "textures" or "playerResources" && g.State is "routeUnavailable" or "invalidContent")==true;
    public static int Score(IEnumerable<ServiceRouteGroup>? groups)=>groups?.Count(g=>g.Id is "images" or "gameImages" or "assetDelivery" or "models" or "textures" or "playerResources" && g.State=="confirmed")??0;
}
