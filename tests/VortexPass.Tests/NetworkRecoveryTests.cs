using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

internal static class NetworkRecoveryTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run()
    {
        await NegativeUdpFallback();
        var profile=new SavedConnectionProfile {Id=Guid.NewGuid().ToString(),Name="Roblox",Games=["roblox"],RobloxAssetsMode="enhanced"};
        var portable=ProfileTransferService.Export(profile);
        Check(ProfileTransferService.Parse(portable).RobloxAssetsMode=="enhanced","Exchange lost enhanced mode");
        using(var document=JsonDocument.Parse(portable))Check(document.RootElement.GetProperty("version").GetInt32()==5,"Exchange version not upgraded");
        var legacy=portable.Replace("\"version\": 5","\"version\": 3");
        Check(ProfileTransferService.Parse(legacy).RobloxAssetsMode=="standard","Legacy import changed behavior");
        Check(JsonSerializer.Deserialize<SavedConnectionProfile>("{}")!.RobloxAssetsMode=="standard"&&new SaveConnectionRequest().RobloxAssetsMode=="auto","Old/new profile defaults wrong");
        var proof=DraftRecommendationPolicy.Composition(profile);profile.RobloxAssetsMode="auto";
        Check(proof!=DraftRecommendationPolicy.Composition(profile),"Mode did not invalidate evidence");

        var profiles=new BypassProfileSettings {YouTube=true,Discord=true,TelegramWeb=true,GameFilter=true};
        var original=WinwsProfileComposer.Compose(profiles,"medium",new(){SelectedGames=["roblox","phasmophobia"]},null,null);
        foreach(var block in new[]{"minimal","split","disorder"})
        {
            var changed=WinwsProfileComposer.ApplyRobloxTcpBlock(original,block,"medium");
            var oldRules=original.Split(" --new",StringSplitOptions.TrimEntries);var newRules=changed.Split(" --new",StringSplitOptions.TrimEntries);
            Check(oldRules.Length==newRules.Length&&!changed.Contains("--new--"),"Rule separator corrupt");
            for(var i=0;i<oldRules.Length;i++)
            {
                var robloxTcp=oldRules[i].Contains("--filter-tcp=")&&(oldRules[i].Contains("list-roblox")||oldRules[i].Contains("rbxcdn"));
                if(!robloxTcp)Check(oldRules[i]==newRules[i],"Roblox changed other services or gameplay UDP");
                else Check(newRules[i].Contains("--dpi-desync-cutoff=n3"),"Roblox did not bound handshake processing");
            }
        }
        Check(WinwsProfileComposer.ApplyRobloxTcpBlock(original,"baseline","medium")==original,"Standard rollback changed command");
        Check(!RobloxSelectionPolicy.NeedsEnhancement([new("models","sessionRequired","unknown")]),"Unknown models triggered enhancement");
        Check(RobloxSelectionPolicy.NeedsEnhancement([new("playerResources","routeUnavailable","timeout")]),"Real resource failure ignored");

        Check(!RobloxSelectionPolicy.Preserves([new("images","confirmed","")],[new("images","routeUnavailable",""),new("models","confirmed","")]),"Enhancement regressed a working asset group");
        Check(RobloxSelectionPolicy.Preserves([new("images","confirmed","")],[new("images","confirmed",""),new("models","confirmed","")]),"Safe improvement rejected");
        var insufficient=LocalStrategyAdvisor.Rate(new(),"ISP",[new("youtube","healthy",true,"two measurements",10,0,2)]);
        Check(!insufficient.FullyConfirmed,"Two measurements were enough for a v9 recommendation");
        var partial=LocalStrategyAdvisor.Rate(new(),"ISP",[new("youtube","dnsUnavailable",false,"DNS failed",null,null,0),new("discord","healthy",true,"Web",40,1,3)]);
        Check(!partial.FullyConfirmed&&partial.Total==2&&partial.Confirmed==1,"Partial continuation dropped DNS-failed service");
        var rows=new[]{new DnsProbeResult("doh","google","example.com","A","ok",10,["1.1.1.1"],""),new DnsProbeResult("doh","google","blocked.example","A","nxdomain",10,[],"")};
        Check(!DnsDiagnosticsService.Choices(rows,["blocked.example"]).Single().Working,"One good control hid blocked target");
        Check(DnsDiagnosticsService.Choices(rows,["example.com"]).Single().Working,"Unselected failing domain blocked DNS choice");
        Check(DnsDiagnosticsService.Classify(new("x",1,3,false,false,0))=="nxdomain"&&DnsDiagnosticsService.Classify(new("x",28,0,false,false,0))=="nodata","DNS reason lost");
        Check(DnsDiagnosticsService.Classify(new("x",1,2,false,false,0))=="servfail","SERVFAIL became success");

        var signed="https://t1.rbxcdn.com/asset?signature=private-fixture";
        Check(RobloxContentProbeService.ExtractResources("ignored cookie=.ROBLOSECURITY; "+signed+" https://evil.example/a").Length==1,"Resource allowlist invalid");
        Check(!RobloxContentProbeService.Allowed(new("https://user:secret@t1.rbxcdn.com/a"))&&!RobloxContentProbeService.Allowed(new("http://t1.rbxcdn.com/a")),"Credential URL allowed");
        foreach(var status in new[]{401,403})Check(RobloxContentProbeService.ClassifyBody(status,"text/html",Encoding.UTF8.GetBytes("<html>error</html>"))=="authorizationRequired","Auth denial became blocked network");
        Check(RobloxContentProbeService.ClassifyBody(200,"application/octet-stream",Encoding.UTF8.GetBytes("<html>error from CDN</html>"))=="invalidContent","HTML accepted as mesh");
        using var client=new HttpClient(new Fixture());
        var resource=await RobloxContentProbeService.ResourceAsync(new(signed),default,client);
        Check(resource.Kind=="model"&&resource.State=="confirmed"&&!resource.Detail.Contains("private-fixture")&&!resource.Detail.Contains("signature"),"Real model failed or signed URL leaked");
        using var huge=new HttpClient(new HugeFixture());
        var image=await RobloxContentProbeService.ResourceAsync(new("https://t1.rbxcdn.com/image"),default,huge);
        Check(image.State=="confirmed"&&image.Detail.Contains("262144 B"),"Resource exceeded download budget or image failed");
        var concurrent=new ConcurrentFixture();using(var limited=new HttpClient(concurrent))
        {
            await Task.WhenAll(Enumerable.Range(0,9).Select(i=>RobloxContentProbeService.ResourceAsync(new("https://t1.rbxcdn.com/resource"+i),default,limited)));
            Check(concurrent.Peak<=3,"Resource parallelism exceeded three");
        }
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        var canceled=false;try{await RobloxContentProbeService.ResourceAsync(new(signed),cancel.Token,client);}catch(OperationCanceledException){canceled=true;}
        Check(canceled,"Resource cancellation ignored");
    }
    private static async Task NegativeUdpFallback()
    {
        foreach(var state in new[]{"nxdomain","servfail","nodata","truncated","invalid","real-nxdomain","tcp-unavailable","aaaa-nodata","positive","cancel"})
        {
            using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var port=((IPEndPoint)listener.LocalEndpoint).Port;
            using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));
            var question=AdvancedRouteTests.Question("youtube.example",state=="aaaa-nodata"?(ushort)28:(ushort)1);
            var needsTcp=state is not ("aaaa-nodata" or "positive");
            if(state=="tcp-unavailable")listener.Stop();
            var fixture=Task.Run(async()=>
            {
                var packet=await udp.ReceiveAsync(lifetime.Token);
                var negative=packet.Buffer.ToArray();negative[2]=0x81;
                negative[3]=state is "nxdomain" or "real-nxdomain" or "tcp-unavailable" or "cancel"?(byte)0x83:state=="servfail"?(byte)0x82:(byte)0x80;
                if(state=="truncated")negative[2]=0x83;
                if(state=="invalid")negative[0]^=1;
                await udp.SendAsync(state=="positive"?AdvancedRouteTests.Answer(question):negative,packet.RemoteEndPoint,lifetime.Token);
                if(!needsTcp||state=="tcp-unavailable")return;
                using var accepted=await listener.AcceptTcpClientAsync(lifetime.Token);
                if(state=="cancel"){lifetime.Cancel();return;}
                var stream=accepted.GetStream();var size=new byte[2];await stream.ReadExactlyAsync(size,lifetime.Token);
                var query=new byte[BinaryPrimitives.ReadUInt16BigEndian(size)];await stream.ReadExactlyAsync(query,lifetime.Token);
                var answer=state=="real-nxdomain"?negative:AdvancedRouteTests.Answer(query);
                BinaryPrimitives.WriteUInt16BigEndian(size,(ushort)answer.Length);await stream.WriteAsync(size,lifetime.Token);await stream.WriteAsync(answer,lifetime.Token);
            });
            try
            {
                PlainDnsReply reply;
                try {reply=await DnsDiagnosticsService.QueryPlainAsync(IPAddress.Loopback,question,lifetime.Token,port);}
                catch(Exception ex)when(state=="cancel"&&(ex is OperationCanceledException or IOException)){await fixture;continue;}
                Check(state!="cancel","TCP retry ignored cancellation");
                var wire=DnsWirePolicy.Inspect(question,reply.Payload);
                Check(wire.HasAddress==!(state is "real-nxdomain" or "tcp-unavailable" or "aaaa-nodata"),"Negative UDP fallback misclassified "+state);
                Check(reply.Transport==(state=="tcp-unavailable"?"udp; TCP unavailable":needsTcp?"tcp":"udp"),"Wrong diagnostic transport "+state);
                if(state is "aaaa-nodata" or "positive")Check(!listener.Pending(),"Healthy IPv4/normal AAAA unnecessarily retried TCP");
                await fixture;
            }
            finally {listener.Stop();}
        }
    }
    private sealed class Fixture:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<roblox!binary model fixture payload>",Encoding.UTF8,"application/octet-stream")});
    }
    private sealed class ConcurrentFixture:HttpMessageHandler
    {
        private int _active;public int Peak;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var active=Interlocked.Increment(ref _active);int old;
            do {old=Peak;if(old>=active)break;}while(Interlocked.CompareExchange(ref Peak,active,old)!=old);
            try {await Task.Delay(20,token);return new(HttpStatusCode.OK){Content=new StringContent("<roblox!binary model fixture>")};}
            finally {Interlocked.Decrement(ref _active);}
        }
    }
    private sealed class HugeFixture:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {var bytes=new byte[512*1024];new byte[]{137,80,78,71,13,10,26,10}.CopyTo(bytes,0);var content=new ByteArrayContent(bytes);content.Headers.ContentType=new("image/png");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent){Content=content});}
    }
}
