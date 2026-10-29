using System.Diagnostics;
using System.Net;

namespace VortexPass.Core.Services;

public sealed record WarpTransferSample(string Route,int Round,long Bytes,long DurationMs,bool Complete,string State)
{
    public double? Mbps=>Complete&&DurationMs>0?Bytes*8.0/DurationMs/1000:null;
}
public sealed record WarpMediaComparison(DateTimeOffset CheckedAt,IReadOnlyList<WarpTransferSample> Samples,string Summary);
public static class WarpMediaBenchmark
{
    public const int ByteLimit=8*1024*1024;
    // Official Cloudflare speed-test payload; never a user's media URL.
    public static readonly Uri Target=new($"https://speed.cloudflare.com/__down?bytes={ByteLimit}");
    public static async Task<WarpMediaComparison> RunAsync(int socksPort,Action<int>? progress,CancellationToken token)
    {
        await using var bridge=new WarpHttpProxy();var port=await bridge.StartAsync([Target.Host],socksPort,token);
        var samples=new List<WarpTransferSample>();
        for(var round=1;round<=3;round++)
            foreach(var route in new[]{"socks5","httpBridge"})
            {
                token.ThrowIfCancellationRequested();progress?.Invoke(samples.Count+1);
                using var handler=new HttpClientHandler{UseProxy=true,Proxy=new WebProxy(new Uri(route=="socks5"?$"socks5://127.0.0.1:{socksPort}":$"http://127.0.0.1:{port}")),AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.None,UseCookies=false};
                using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
                samples.Add(await MeasureAsync(client,route,round,token));
            }
        var complete=samples.Count(s=>s.Complete);
        return new(DateTimeOffset.UtcNow,samples,$"Завершено {complete}/6 загрузок. Это скорость тестового файла через WARP; время запуска видео Telegram не измеряется. Правила и подключение не менялись.");
    }
    public static async Task<WarpTransferSample> MeasureAsync(HttpClient client,string route,int round,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var watch=Stopwatch.StartNew();long bytes=0;
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,Target);request.Headers.CacheControl=new(){NoCache=true};
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if(!response.IsSuccessStatusCode)return Result(false,"HTTP "+(int)response.StatusCode);
            var kind=response.Content.Headers.ContentType?.MediaType??"";
            if(kind.Contains("html",StringComparison.OrdinalIgnoreCase)||kind.Contains("json",StringComparison.OrdinalIgnoreCase))return Result(false,"Вместо файла получена страница ошибки");
            await using var body=await response.Content.ReadAsStreamAsync(deadline.Token);var buffer=new byte[81920];
            while(bytes<ByteLimit)
            {
                var n=await body.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,ByteLimit-bytes)),deadline.Token);if(n==0)break;
                if(bytes==0&&System.Text.Encoding.ASCII.GetString(buffer,0,Math.Min(n,128)).TrimStart().StartsWith("<",StringComparison.Ordinal))return Result(false,"HTML вместо файла");
                bytes+=n;
            }
            return Result(bytes==ByteLimit,bytes==ByteLimit?"complete":"Неполный файл");
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){return Result(false,"Тайм-аут 15 секунд");}
        catch(Exception ex)when(ex is HttpRequestException or IOException){return Result(false,"Ошибка транспорта");}
        WarpTransferSample Result(bool complete,string state)=>new(route,round,bytes,watch.ElapsedMilliseconds,complete,state);
    }
}
