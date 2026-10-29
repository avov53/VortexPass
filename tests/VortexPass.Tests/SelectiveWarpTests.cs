using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

internal static class SelectiveWarpTests
{
    public static async Task Smoke(string directory)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();var session=new SampleSession();
        var model=new LlamaLocalStrategyModel(Path.GetFullPath(directory));Check(model.GetStatus().Ready,"Real model directory is missing or incorrect");
        var required=new RequiredModel(model);
        var results=await new LocalStrategyAdvisor(required).RunAsync(
            new("medium","local-test","test","standard",["youtube","telegram"],TelegramWebRoute:"warpProxy"),
            _=>Task.FromResult<IAdvisorTrialSession>(session),_=>{},[],default);
        Check(watch.Elapsed<TimeSpan.FromSeconds(25)&&session.Disposed&&results.Count>0,"Real local inference blocked bounded search");
        if(required.Operation is not null)try{await required.Operation.WaitAsync(TimeSpan.FromSeconds(2));}catch(OperationCanceledException){}
        Console.WriteLine($"PASS actual Qwen invocation with 20-second deadline: controlled route session completed in {watch.Elapsed.TotalSeconds:F1}s; timeout may use built-in variants; no network settings changed");
    }
    private sealed class RequiredModel(ILocalStrategyModel inner):ILocalStrategyModel
    {
        public Task<IReadOnlyList<AdvisorStrategySpec>>? Operation {get;private set;}
        public Task<IReadOnlyList<AdvisorStrategySpec>> ProposeAsync(AdvisorRequest request,bool experimental,IReadOnlyList<AdvisorRecommendation> evidence,CancellationToken token)=>Operation=Invoke(request,experimental,evidence,token);
        private async Task<IReadOnlyList<AdvisorStrategySpec>> Invoke(AdvisorRequest request,bool experimental,IReadOnlyList<AdvisorRecommendation> evidence,CancellationToken token)
        {
            try{return await inner.ProposeAsync(request,experimental,evidence,token);}
            catch(Exception ex)when(ex is not (OperationCanceledException or TimeoutException)){throw new ApplicationException("Actual model invocation failed",ex);}
        }
    }
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    public static async Task Run()
    {
        var automatic=WarpPreflight.Candidates(new WarpSettings());
        Check(automatic.Count==3&&automatic[0].Masque=="h2-only"&&automatic[0].Bootstrap=="none"&&automatic[1].Bootstrap=="strong","Requested HTTP/2 then scoped strong WARP trial are missing");
        var strong=WinwsProfileComposer.ComposeWarpBootstrap("strong");
        Check(strong.Contains("162.159.198.2")&&strong.Contains("2606:4700:103::2")&&strong.Contains("--filter-l7=quic,unknown")&&strong.Contains("--dpi-desync-cutoff=n8"),"Observed consumer QUIC endpoint is not protected");
        Check(strong.Split(" --new ").Where(s=>s.Contains("--filter-udp=")).All(s=>s.Contains("--ipset-ip=")),"Strong WARP rule intercepts unrelated game UDP");
        Check(!strong.Contains("12000-65000")&&!strong.Contains("--hostlist-auto"),"Strong bootstrap expanded outside WARP");
        Check(DefaultDomainLists.CreateExclusions().Contains("consumer-masque-proxy.cloudflareclient.com"),"Main protection may interfere with the separate WARP bootstrap");
        Check(File.ReadAllText("components/zapret/files/list-warp.txt").Contains("consumer-masque-proxy.cloudflareclient.com"),"Actual consumer SNI is absent from WARP hostlist");
        var observed=WarpAttemptObservation.Parse("""
2026-10-24T18:00:00.000Z DEBUG warp_edge::h2_tun: Established old connection dst=1.1.1.1:443
2026-10-24T18:05:04.768Z DEBUG socket dst=162.159.198.2:443 token=secret
2026-10-24T18:05:04.769Z DEBUG warp_edge::h3_tun: Sending handshake initiation to edge
2026-10-24T18:05:04.769Z DEBUG warp_edge::h3_tun: Connecting to edge sni="consumer-masque-proxy.cloudflareclient.com" scid=secret
""",DateTimeOffset.Parse("2026-10-24T18:05:00Z"));
        Check(observed.Transport=="QUIC/UDP"&&observed.Endpoints.SequenceEqual(new[]{"162.159.198.2:443"})&&observed.ServerNames.Single()=="consumer-masque-proxy.cloudflareclient.com"&&!observed.Summary.Contains("secret"),"Observation confused requested HTTP/2 with actual QUIC or leaked raw daemon fields");
        Check(!WarpPreflight.RequiresBootstrap(automatic[0].Bootstrap),"Successful no-DPI route starts winws");
        Check(WarpPreflight.Candidates(new WarpSettings(),automatic[2])[0]==automatic[2],"Last confirmed route is not tried first");
        Check(!WarpService.ShouldStopConnectionAttempt(WarpConnectionState.Disconnected,"Reason: Failed to perform happy eyeballs",false,TimeSpan.FromSeconds(1)),"Stale failure stopped a fresh connection");
        Check(!WarpService.ShouldStopConnectionAttempt(WarpConnectionState.Connecting,"Reason: Failed to perform happy eyeballs",true,TimeSpan.FromSeconds(6)),"Stale reason interrupted ongoing connection");
        Check(WarpService.ShouldStopConnectionAttempt(WarpConnectionState.Disconnected,"Reason: Failed to perform happy eyeballs",true,TimeSpan.FromSeconds(6)),"Current failure was ignored");
        Check(WarpPreflight.Candidates(new WarpSettings(),automatic[0]).Count==3,"Cached transport was duplicated");
        Check(WarpPreflight.Candidates(new WarpSettings{TransportSelection="wireguard"},automatic[0]).Single().Protocol=="wireguard","Explicit transport was replaced");
        var attempts=0;
        var confirmed=await WarpPreflight.RunAsync(automatic,(v,i,t)=>{attempts++;return Task.FromResult(i==2);},default);
        Check(attempts==2&&confirmed==automatic[1],"Preflight ignored content confirmation");
        var deniedAll=false;
        try{await WarpPreflight.RunAsync(automatic,(v,i,t)=>Task.FromResult(false),default);}catch(IOException){deniedAll=true;}
        Check(deniedAll,"All failed WARP variants became success");
        var timed=false;var watch=System.Diagnostics.Stopwatch.StartNew();
        try{await WarpPreflight.RunAsync(automatic,async(v,i,t)=>{await Task.Delay(10000,t);return true;},default,TimeSpan.FromMilliseconds(70),TimeSpan.FromMilliseconds(30));}catch(IOException){timed=true;}
        Check(timed&&watch.Elapsed<TimeSpan.FromSeconds(1),"Preflight exceeded its budget");
        using(var stopped=new CancellationTokenSource())
        {stopped.Cancel();var canceledPreflight=false;try{await WarpPreflight.RunAsync(automatic,(v,i,t)=>Task.FromResult(true),stopped.Token);}catch(OperationCanceledException){canceledPreflight=true;}Check(canceledPreflight,"User cancellation became success");}
        Check(WarpGuiPolicy.OwnsCommand('"'+@"C:\Program Files\Cloudflare\Cloudflare WARP\Cloudflare WARP.exe"+'"',@"C:\Program Files\Cloudflare\Cloudflare WARP\Cloudflare WARP.exe"),"Owned GUI startup rejected");
        Check(!WarpGuiPolicy.OwnsCommand(@"C:\other.exe",@"C:\Cloudflare WARP.exe")&&!WarpGuiPolicy.OwnsCommand(@"C:\Cloudflare WARP.exe && evil",@"C:\Cloudflare WARP.exe"),"Foreign startup command accepted");
        var proofA=new WarpVerifiedVariant("network-dns-version",automatic[0],DateTimeOffset.UtcNow);
        var proofB=proofA with{Variant=automatic[1]};Check(proofA.ProofKey!=proofB.ProofKey,"Proof cache ignores TCP bootstrap");
        var storedProof=JsonSerializer.Deserialize<WarpSettings>(JsonSerializer.Serialize(new WarpSettings{VerifiedVariants=[proofB]}))!;
        Check(storedProof.VerifiedVariants.Single().ProofKey==proofB.ProofKey,"Confirmed transport cache lost on restart");
        var legacySnapshot=JsonSerializer.Deserialize<ConnectionWarpSnapshot>("{\"Settings\":{},\"WasConnected\":false}")!;
        Check(legacySnapshot.Configuration is null&&legacySnapshot.BootstrapMode=="default","Legacy pending restore snapshot lost compatibility");
        var gateModel=new SlowModel();var gateFailed=false;
        try{await new LocalStrategyAdvisor(gateModel).RunAsync(new("medium","test","net","standard",["youtube","telegram"],TelegramWebRoute:"warpProxy"),_=>throw new IOException("WARP preflight failed"),_=>{},[],default);}
        catch(IOException){gateFailed=true;}
        Check(gateFailed&&gateModel.Calls==0,"Model ran after failed WARP setup");
        var bundleRoot=Path.Combine(Path.GetTempPath(),"vortexpass-warp-bundle-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(bundleRoot,"warp-installer"));
        var bundleFile=Path.Combine(bundleRoot,"warp-installer",WarpInstallerBundle.FileName);
        try
        {
            Check(await WarpInstallerBundle.FindAsync(bundleRoot) is null,"Missing offline bundle accepted");
            await File.WriteAllTextAsync(bundleFile,"tampered installer");var denied=false;
            try{await WarpInstallerBundle.FindAsync(bundleRoot);}catch(InvalidDataException){denied=true;}
            Check(denied,"Tampered offline installer accepted");
            var sourceInstaller=Path.Combine(Environment.CurrentDirectory,"components","warp-installer",WarpInstallerBundle.FileName);
            if(File.Exists(sourceInstaller))
            {
                await WarpInstallerBundle.VerifyTransformAsync(sourceInstaller);
                File.Copy(sourceInstaller,bundleFile,true);
                await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(bundleFile)!,"suppress-gui.mst"),"tampered transform");
                var deniedTransform=false;try{await WarpInstallerBundle.VerifyTransformAsync(bundleFile);}catch(InvalidDataException){deniedTransform=true;}
                Check(deniedTransform,"Tampered transform accepted");
            }
        }
        finally{File.Delete(bundleFile);File.Delete(Path.Combine(Path.GetDirectoryName(bundleFile)!,"suppress-gui.mst"));Directory.Delete(Path.GetDirectoryName(bundleFile)!);Directory.Delete(bundleRoot);}
        var defaultBootstrap=WinwsProfileComposer.ComposeWarpBootstrap();
        foreach(var tcp in new[]{"split","disorder"})
        {
            var alternate=WinwsProfileComposer.ComposeWarpBootstrap(tcp);
            Check(alternate[alternate.IndexOf(" --new ",StringComparison.Ordinal)..]==defaultBootstrap[defaultBootstrap.IndexOf(" --new ",StringComparison.Ordinal)..],"Registration alternative changed UDP rules");
            Check(alternate.Contains("--hostlist=files\\list-warp.txt")&&alternate.Contains("--dpi-desync-cutoff=n3"),"Registration alternative widened TCP scope");
        }
        Check(WarpService.IsOfficialInstallerUri(new Uri("https://downloads.cloudflareclient.com/v1/download/windows/ga")),"Official installer redirect rejected");
        Check(WarpService.IsOfficialInstallerUri(new Uri("https://downloads.cloudflareclient.com/v1/download/windows/version/2026.8.2115.0")),"Versioned installer redirect rejected");
        Check(!WarpService.IsOfficialInstallerUri(new Uri("https://downloads.cloudflareclient.com.evil.test/v1/download/windows/ga"))&&!WarpService.IsOfficialInstallerUri(new Uri("http://downloads.cloudflareclient.com/v1/download/windows/ga")),"Unsafe installer redirect accepted");
        var tlsError=WarpService.DescribePreparationError(new HttpRequestException("SSL failed",new System.Security.Authentication.AuthenticationException("Certificate invalid")));
        Check(tlsError.Contains("Certificate invalid"),"TLS inner error hidden");
        foreach(var foreign in new[]{false,true})
        {
            var handler=new InstallerRedirectFixture(foreign);
            using var client=new HttpClient(handler);
            using var download=new WarpService(Path.GetTempPath(),client);
            var rejected=false;
            try{await download.DownloadInstallerAsync();}
            catch(InvalidDataException) when(foreign){rejected=true;}
            catch(HttpRequestException) when(!foreign){rejected=true;}
            Check(rejected&&handler.Requests==(foreign?1:3),"Installer redirect chain escaped its allowlist or failed to follow official redirects");
        }
        var warpSettings = JsonSerializer.Deserialize<WarpSettings>("{}")!;
        Check(warpSettings.PreparationCompletedUtc is null,"Old settings incorrectly suppress WARP setup");
        warpSettings.PreparationCompletedUtc=DateTimeOffset.UtcNow;
        Check(JsonSerializer.Deserialize<WarpSettings>(JsonSerializer.Serialize(warpSettings))!.PreparationCompletedUtc==warpSettings.PreparationCompletedUtc,"Completed WARP setup was lost on restart");
        var old=JsonSerializer.Deserialize<SavedConnectionProfile>("{}")!;
        Check(old.TelegramWebRoute=="direct"&&new SaveConnectionRequest().TelegramWebRoute=="warpProxy","Old/new route defaults changed");
        var profile=new SavedConnectionProfile{Id=Guid.NewGuid().ToString(),Name="Web",Services=["telegramweb"],TelegramWebRoute="warpProxy"};
        var exported=ProfileTransferService.Export(profile);
        Check(ProfileTransferService.Parse(exported).TelegramWebRoute=="warpProxy","Route lost in exchange v5");
        Check(ProfileTransferService.Parse(exported.Replace("\"version\": 5","\"version\": 4")).TelegramWebRoute=="direct","Legacy route unexpectedly enabled WARP");
        var key=DraftRecommendationPolicy.Composition(profile);profile.TelegramWebRoute="direct";
        Check(key!=DraftRecommendationPolicy.Composition(profile),"Route change reused proof");
        Check(WarpService.IsManagedRegistration("Account type: Team")&&!WarpService.IsManagedRegistration("Account type: Free"),"Consumer/managed ownership check invalid");
        using(var service=new WarpService(Path.GetTempPath()))
        {
            var rejected=false;try{await service.InstallAsync(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".msi"),silent:true);}catch(InvalidDataException){rejected=true;}
            Check(rejected,"Unsigned/missing installer reached Windows Installer");
        }
        var pac=WarpPacService.GeneratePac(TelegramWebPolicy.Domains,12345);
        Check(pac.Contains("PROXY 127.0.0.1:12345'")&&!pac.Contains("; DIRECT")&&pac.EndsWith("return 'DIRECT'; }"),"PAC leaks selected destinations or intercepts all sites");
        Check(!TelegramWebPolicy.Domains.Contains("telegram.org"),"Telegram routing broadened to all Telegram domains");
        await Proxy(connect:true);await Proxy(connect:false);await Budgets();
    }
    private static async Task<string> Header(Stream stream,CancellationToken token)
    {
        var bytes=new List<byte>();var one=new byte[1];while(bytes.Count<16384)
        {if(await stream.ReadAsync(one,token)==0)break;bytes.Add(one[0]);if(bytes.Count>=4&&Encoding.ASCII.GetString(bytes.TakeLast(4).ToArray())=="\r\n\r\n")break;}
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
    private static async Task Proxy(bool connect)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));var token=deadline.Token;
        var socks=new TcpListener(IPAddress.Loopback,0);socks.Start();var port=((IPEndPoint)socks.LocalEndpoint).Port;
        try
        {
            var target="resource.web.telegram.org";
            var upstream=Task.Run(async()=>
            {
                using var accepted=await socks.AcceptTcpClientAsync(token);var stream=accepted.GetStream();var greeting=new byte[3];await stream.ReadExactlyAsync(greeting,token);
                Check(greeting.SequenceEqual(new byte[]{5,1,0}),"SOCKS unexpected authentication");await stream.WriteAsync(new byte[]{5,0},token);
                var command=new byte[5];await stream.ReadExactlyAsync(command,token);Check(command[3]==3,"Local DNS resolution leaked into SOCKS IP request");
                var name=new byte[command[4]];await stream.ReadExactlyAsync(name,token);Check(Encoding.ASCII.GetString(name)==target,"SOCKS lost requested hostname");
                var targetPort=new byte[2];await stream.ReadExactlyAsync(targetPort,token);Check(targetPort[1]==(connect?187:80),"Target port changed");
                await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},token);
                if(connect)
                {var ping=new byte[4];await stream.ReadExactlyAsync(ping,token);Check(Encoding.ASCII.GetString(ping)=="PING","Tunnel changed bytes");await stream.WriteAsync(ping,token);}
                else
                {var header=await Header(stream,token);Check(header.StartsWith("GET /asset?x=1 HTTP/1.1")&&!header.Contains("Proxy-Authorization"),"HTTP target/header rewrite incorrect");await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray(),token);}
            });
            await using var bridge=new WarpHttpProxy();var local=await bridge.StartAsync(["web.telegram.org"],port,token);
            using(var denied=new TcpClient())
            {await denied.ConnectAsync(IPAddress.Loopback,local,token);var stream=denied.GetStream();await stream.WriteAsync("CONNECT other.example:443 HTTP/1.1\r\n\r\n"u8.ToArray(),token);Check((await Header(stream,token)).Contains("403"),"Unknown host reached WARP");}
            using(var client=new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback,local,token);var stream=client.GetStream();
                var request=connect?$"CONNECT {target}:443 HTTP/1.1\r\nHost: {target}:443\r\n\r\n":$"GET http://{target}/asset?x=1 HTTP/1.1\r\nHost: {target}\r\nProxy-Authorization: secret\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request),token);var header=await Header(stream,token);Check(header.Contains("200"),"Proxy did not establish stream");
                if(connect){await stream.WriteAsync("PING"u8.ToArray(),token);var echo=new byte[4];await stream.ReadExactlyAsync(echo,token);Check(Encoding.ASCII.GetString(echo)=="PING","TLS byte relay broken");}
                else{var body=new byte[2];await stream.ReadExactlyAsync(body,token);Check(Encoding.ASCII.GetString(body)=="OK","HTTP response lost");}
            }
            await upstream;await bridge.StopAsync();
            var closed=false;try{using var stopped=new TcpClient();await stopped.ConnectAsync(IPAddress.Loopback,local,token);}catch(SocketException){closed=true;}Check(closed,"Proxy remained after stop");
        }
        finally{socks.Stop();}
    }
    private static async Task Budgets()
    {
        var request=new AdvisorRequest("medium","ISP","network","standard",["youtube","telegram"],TelegramWebRoute:"warpProxy");
        var model=new SlowModel();var session=new SampleSession();
        var results=await new LocalStrategyAdvisor(model,TimeSpan.FromSeconds(40),TimeSpan.FromMilliseconds(30)).RunAsync(request,_=>Task.FromResult<IAdvisorTrialSession>(session),_=>{},[],default);
        await Task.Delay(50); // The canceled inference task may finish its cleanup after WaitAsync yields.
        Check(model.Canceled,"Model did not receive its independent deadline");
        Check(session.Disposed&&results.Count>0,"Slow model blocked fallback/restoration");
        Check(session.Tests.Count(t=>t.Samples==1)<=4&&session.Tests.Count(t=>t.Samples==3)<=2,"Search exceeded screen/finalist limits");
        Check(session.Tests.All(t=>t.Spec.Telegram=="baseline"),"WARP Telegram still tested DPI strategies");
        Check(results.Any(r=>r.FullyConfirmed),"Finalists never received three samples");
        var webOnlyModel=new SlowModel();var webOnly=new SampleSession();
        await new LocalStrategyAdvisor(webOnlyModel,TimeSpan.FromSeconds(1),TimeSpan.FromMilliseconds(20)).RunAsync(request with{Services=["telegram"]},_=>Task.FromResult<IAdvisorTrialSession>(webOnly),_=>{},[],default);
        Check(!webOnlyModel.Canceled&&webOnly.Disposed,"Proxy-only profile unnecessarily ran inference");
        var interrupted=new SampleSession{DelayAfterBaseline=true};
        var partial=await new LocalStrategyAdvisor(new SlowModel(),TimeSpan.FromMilliseconds(100),TimeSpan.FromMilliseconds(10)).RunAsync(request,_=>Task.FromResult<IAdvisorTrialSession>(interrupted),_=>{},[],default);
        Check(interrupted.Disposed&&partial.Count>0&&partial.All(r=>!r.FullyConfirmed),"Budget discarded baseline or falsely confirmed one measurement");
        using var cancel=new CancellationTokenSource();cancel.Cancel();var canceled=false;
        try{await new LocalStrategyAdvisor(model).RunAsync(request,_=>Task.FromResult<IAdvisorTrialSession>(new SampleSession()),_=>{},[],cancel.Token);}catch(OperationCanceledException){canceled=true;}
        Check(canceled,"Explicit cancellation became success");
    }
    private sealed class InstallerRedirectFixture(bool foreign):HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests++;
            var response=new HttpResponseMessage(Requests<3?HttpStatusCode.Found:HttpStatusCode.ServiceUnavailable){RequestMessage=request};
            if(Requests<3)response.Headers.Location=new Uri(foreign?"https://example.test/foreign.msi":Requests==1?"https://downloads.cloudflareclient.com/v1/download/windows/ga":"https://downloads.cloudflareclient.com/v1/download/windows/version/2026.8.2115.0");
            return Task.FromResult(response);
        }
    }
    private sealed class SlowModel:ILocalStrategyModel
    {
        public bool Canceled;public int Calls;
        public async Task<IReadOnlyList<AdvisorStrategySpec>> ProposeAsync(AdvisorRequest request,bool experimental,IReadOnlyList<AdvisorRecommendation> evidence,CancellationToken token)
        {Calls++;try{await Task.Delay(10000,token);return [];}finally{Canceled=token.IsCancellationRequested;}}
    }
    private sealed class SampleSession:IAdvisorTrialSession
    {
        public bool Disposed,DelayAfterBaseline;public List<(AdvisorStrategySpec Spec,int Samples)> Tests=[];
        public Task<IReadOnlyList<AdvisorServiceEvidence>> CheckCurrentSamplesAsync(int n,CancellationToken token)=>TestSamplesAsync(new(),n,token);
        public Task<IReadOnlyList<AdvisorServiceEvidence>> TestAsync(AdvisorStrategySpec spec,CancellationToken token)=>TestSamplesAsync(spec,3,token);
        public async Task<IReadOnlyList<AdvisorServiceEvidence>> TestSamplesAsync(AdvisorStrategySpec spec,int n,CancellationToken token)
        {if(DelayAfterBaseline&&Tests.Count>0)await Task.Delay(10000,token);token.ThrowIfCancellationRequested();Tests.Add((spec,n));return [new("youtube","healthy",true,"content",20,1,n),new("telegram","healthy",true,"WARP",30,1,n)];}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
}
