using System.Net;
using System.Net.Sockets;
using System.Text;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

internal static class WarpStreamingTests
{
    private static void Check(bool value,string detail){if(!value)throw new Exception(detail);}
    public static async Task Run()
    {
        await HalfClose();await StopAndLimit();await StableDns();await DownloadLimits();await RestrictedCdn();
        var settings=new AppSettings();var variant=new WarpTransportVariant("masque","h2-only","split");
        var key=WarpPreflight.CacheKey(settings,"fixture");settings.Warp.VerifiedVariants=[new(key.Replace("|checks:15","|checks:14"),variant,DateTimeOffset.UtcNow,"QUIC/UDP")];
        Check(WarpPreflight.Candidates(settings,"fixture")[0]==variant,"v14 working transport hint was discarded");
        var storeDirectory=Path.Combine(Path.GetTempPath(),"vortex-v15-"+Guid.NewGuid().ToString("N"));
        try{var store=new AppSettingsStore(storeDirectory);await store.SaveAsync(settings);var restored=await store.LoadAsync();Check(WarpPreflight.Candidates(restored,"fixture")[0]==variant,"Confirmed route lost on restart");}
        finally{if(Directory.Exists(storeDirectory))Directory.Delete(storeDirectory,true);}
    }
    private static async Task<string> Header(Stream stream,CancellationToken token)
    {var result=new List<byte>();var b=new byte[1];while(result.Count<16384){var n=await stream.ReadAsync(b,token);if(n==0)break;result.Add(b[0]);if(result.TakeLast(4).SequenceEqual(new byte[]{13,10,13,10}))break;}return Encoding.ASCII.GetString(result.ToArray());}
    private static async Task Socks(Stream stream,CancellationToken token)
    {
        var greeting=new byte[3];await stream.ReadExactlyAsync(greeting,token);await stream.WriteAsync(new byte[]{5,0},token);
        var prefix=new byte[5];await stream.ReadExactlyAsync(prefix,token);Check(prefix[3]==3,"SOCKS leaked local DNS");
        var rest=new byte[prefix[4]+2];await stream.ReadExactlyAsync(rest,token);await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},token);
    }
    private static async Task HalfClose()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));var token=deadline.Token;
        var socks=new TcpListener(IPAddress.Loopback,0);socks.Start();
        await using var proxy=new WarpHttpProxy{DiagnosticsEnabled=true};
        try
        {
            var payload=Enumerable.Range(0,1024*1024).Select(i=>(byte)(i%251)).ToArray();
            var server=Task.Run(async()=>
            {
                using var peer=await socks.AcceptTcpClientAsync(token);var stream=peer.GetStream();await Socks(stream,token);
                using var request=new MemoryStream();await stream.CopyToAsync(request,token);Check(Encoding.ASCII.GetString(request.ToArray())=="PING","Upload truncated");
                await Task.Delay(100,token);await stream.WriteAsync(payload,token);peer.Client.Shutdown(SocketShutdown.Send);
            });
            var port=await proxy.StartAsync(["web.telegram.org"],((IPEndPoint)socks.LocalEndpoint).Port,token);
            using var browser=new TcpClient();await browser.ConnectAsync(IPAddress.Loopback,port,token);var input=browser.GetStream();
            await input.WriteAsync("CONNECT web.telegram.org:443 HTTP/1.1\r\nHost: web.telegram.org\r\n\r\n"u8.ToArray(),token);
            Check((await Header(input,token)).StartsWith("HTTP/1.1 200"),"CONNECT failed");
            await input.WriteAsync("PING"u8.ToArray(),token);browser.Client.Shutdown(SocketShutdown.Send);
            using var received=new MemoryStream();await input.CopyToAsync(received,token);await server;
            Check(received.ToArray().SequenceEqual(payload),"Half-close cancelled the delayed large response");
            await proxy.StopAsync();var metrics=proxy.Metrics;
            Check(metrics.ActiveTunnels==0&&metrics.SentBytes==4&&metrics.ReceivedBytes==payload.Length&&metrics.Handshakes==1,"Relay counters or cleanup incorrect");
            Check(!System.Text.Json.JsonSerializer.Serialize(metrics).Contains("telegram"),"Metrics contain destinations");
        }
        finally{socks.Stop();}
    }
    private static async Task StopAndLimit()
    {
        await using var proxy=new WarpHttpProxy();var port=await proxy.StartAsync(["web.telegram.org"],1,default);
        var clients=new List<TcpClient>();
        try
        {
            for(var i=0;i<65;i++){var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,port);clients.Add(client);}
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while(proxy.Metrics.Rejected==0){await Task.Delay(10,deadline.Token);}
            Check(proxy.Metrics.ActiveTunnels==64&&!proxy.Metrics.Enabled,"Connection limit/default diagnostics changed");
            await proxy.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));Check(proxy.Metrics.ActiveTunnels==0,"Stop left orphaned proxy connections");
        }
        finally{foreach(var client in clients)client.Dispose();}
    }
    private static async Task StableDns()
    {
        var reserves=0;
        await Task.WhenAll(Enumerable.Range(0,32).Select(async i=>
        {
            var value=await StableDnsFallback.QueryAsync(new[]{"primary","reserve"},(p,t)=>{if(p=="reserve")Interlocked.Increment(ref reserves);return Task.FromResult(new byte[]{(byte)(i%2==0?3:0)});},default);
            Check(value.Provider=="primary","Negative answer promoted reserve");
        }));Check(reserves==0,"NXDOMAIN/NODATA triggered reserve queries");
        var calls=new List<string>();
        var fallback=await StableDnsFallback.QueryAsync(new[]{"primary","reserve"},(p,t)=>{calls.Add(p);if(p=="primary")throw new TimeoutException();return Task.FromResult(new byte[]{1});},default);
        Check(fallback.Provider=="reserve"&&calls.SequenceEqual(new[]{"primary","reserve"}),"Transport failure did not use ordered reserve");
        var failed=false;try{await StableDnsFallback.QueryAsync(new[]{1,2},(p,t)=>throw new IOException(),default);}catch(IOException){failed=true;}Check(failed,"All DNS failures became success");
        using var cancel=new CancellationTokenSource();cancel.Cancel();var stopped=false;try{await StableDnsFallback.QueryAsync(new[]{1},(p,t)=>Task.FromResult(new byte[]{1}),cancel.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"DNS cancellation ignored");
    }
    private static async Task DownloadLimits()
    {
        using var handler=new PayloadHandler();using var client=new HttpClient(handler);
        var sample=await WarpMediaBenchmark.MeasureAsync(client,"fixture",1,default);
        Check(sample.Complete&&sample.Bytes==WarpMediaBenchmark.ByteLimit&&handler.Body.ReadBytes==WarpMediaBenchmark.ByteLimit,"Download exceeded byte limit");
        using var html=new HttpClient(new PayloadHandler(true));var rejected=await WarpMediaBenchmark.MeasureAsync(html,"fixture",1,default);
        Check(!rejected.Complete&&rejected.Bytes==0&&rejected.Mbps is null,"HTML produced a throughput success");
    }
    private sealed class PayloadHandler(bool html=false):HttpMessageHandler
    {
        public CountingBody Body {get;}=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(Body)};response.Content.Headers.ContentType=new(html?"text/html":"application/octet-stream");return Task.FromResult(response);}
    }
    private static async Task RestrictedCdn()
    {
        foreach(var status in new[]{401,403})
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            try
            {
                var serve=Task.Run(async()=>{using var peer=await listener.AcceptTcpClientAsync(timeout.Token);var stream=peer.GetStream();await Header(stream,timeout.Token);await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Restricted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),timeout.Token);});
                var method=typeof(ServiceHealthCheckService).GetMethod("CheckDiscordAssetEndpointAsync",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
                var task=(Task)method.Invoke(null,new object[]{"fixture",$"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/",timeout.Token})!;
                await task;var result=task.GetType().GetProperty("Result")!.GetValue(task)!;
                Check((bool)result.GetType().GetProperty("Success")!.GetValue(result)!&&(bool)result.GetType().GetProperty("Partial")!.GetValue(result)!,"401/403 became a network failure or confirmed content");await serve;
            }
            finally{listener.Stop();}
        }
    }
    private sealed class CountingBody():MemoryStream(new byte[WarpMediaBenchmark.ByteLimit+100])
    {public long ReadBytes {get;private set;} protected override void Dispose(bool disposing){if(CanRead)ReadBytes=Position;base.Dispose(disposing);}}
}
