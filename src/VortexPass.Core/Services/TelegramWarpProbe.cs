using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.RegularExpressions;

namespace VortexPass.Core.Services;
public static class TelegramWarpProbe
{
    public static async Task<ServiceHealthResult> CheckAsync(Uri proxy,CancellationToken token)
    {
        using var handler=new HttpClientHandler {UseProxy=true,Proxy=new WebProxy(proxy),AllowAutoRedirect=false};
        using var client=new HttpClient(handler);
        async Task<(bool Ok,string? Asset)> Page(string path)
        {
            try
            {
                using var response=await client.GetAsync("https://web.telegram.org/"+path+"/",HttpCompletionOption.ResponseHeadersRead,token);
                await using var stream=await response.Content.ReadAsStreamAsync(token);var bytes=new byte[65536];var n=0;
                while(n<bytes.Length){var read=await stream.ReadAsync(bytes.AsMemory(n),token);if(read==0)break;n+=read;}
                var html=System.Text.Encoding.UTF8.GetString(bytes,0,n);
                if(!response.IsSuccessStatusCode||!html.Contains("<",StringComparison.Ordinal))return(false,null);
                var match=Regex.Match(html,"(?:src|href)=[\"']([^\"']+\\.(?:js|css)(?:\\?[^\"']*)?)[\"']",RegexOptions.IgnoreCase);
                return(true,match.Success&&Uri.TryCreate(new Uri("https://web.telegram.org/"+path+"/"),match.Groups[1].Value,out var asset)&&asset.Host=="web.telegram.org"?asset.AbsoluteUri:null);
            }
            catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException){token.ThrowIfCancellationRequested();return(false,null);}
        }
        async Task<(bool Ok,long? Latency)> Socket()
        {var watch=Stopwatch.StartNew();try{using var ws=new ClientWebSocket();ws.Options.Proxy=new WebProxy(proxy);ws.Options.AddSubProtocol("binary");await ws.ConnectAsync(new("wss://pluto.web.telegram.org/apiws"),token);return(ws.State==WebSocketState.Open,watch.ElapsedMilliseconds);}catch(Exception ex)when(ex is WebSocketException or HttpRequestException or OperationCanceledException){token.ThrowIfCancellationRequested();return(false,null);}}
        var k=Page("k");var a=Page("a");var ws=Socket();await Task.WhenAll(k,a);var pages=k.Result.Ok&&a.Result.Ok;
        var assetUri=k.Result.Asset??a.Result.Asset;var assetOk=false;
        if(assetUri is not null)
        {try{using var asset=await client.GetAsync(assetUri,HttpCompletionOption.ResponseHeadersRead,token);await using var body=await asset.Content.ReadAsStreamAsync(token);var b=new byte[4096];var n=await body.ReadAsync(b,token);var kind=asset.Content.Headers.ContentType?.MediaType??"";assetOk=asset.IsSuccessStatusCode&&n>16&&(kind.Contains("javascript")||kind.Contains("css"))&&!System.Text.Encoding.UTF8.GetString(b,0,n).TrimStart().StartsWith("<",StringComparison.Ordinal);}catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException){token.ThrowIfCancellationRequested();}}
        var transport=await ws;var socket=transport.Ok;var confirmed=pages&&assetOk&&socket;
        return new("telegram",confirmed?ServiceHealthState.Healthy:ServiceHealthState.Partial,
            $"WARP: /a/ {(a.Result.Ok?"OK":"не подтверждён")}, /k/ {(k.Result.Ok?"OK":"не подтверждён")}, ресурс {(assetOk?"OK":"не подтверждён")}, WebSocket {(socket?"OK":"не подтверждён")}. Переписка и личные вложения требуют проверки в браузере.",transport.Latency,
            ContentConfirmed:confirmed,SystemDnsReady:true,Groups:[new("web",pages?"confirmed":"unconfirmed","Telegram Web через WARP"),new("static",assetOk?"confirmed":"unconfirmed","Статический ресурс"),new("transport",socket?"confirmed":"unconfirmed","WebSocket; не подтверждает сообщения"),new("browserSession","sessionRequired","Проверяется пользователем в браузере")]);
    }
}
