using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VortexPass.App;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

internal static class ConnectionHandlerTests
{
    public static async Task Run()
    {
        // Execute the real save handler with isolated settings. No window constructor,
        // WebView, component service, adapter, WARP or protection is initialized.
        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        var type = typeof(MainWindow);
        void Set(string field, object value) => type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        var folder = Path.GetFullPath(Path.Combine("artifacts", "connection-handler-tests", Guid.NewGuid().ToString("N")));
        // This fixture changes DoH -> UDP later to verify DNS-bound evidence.
        var store = new AppSettingsStore(folder); var settings = new AppSettings { DnsMode = "doh" };
        using var warpService=new WarpService(folder);
        Set("_warpService",warpService);
        Set("_settings", settings); Set("_settingsStore", store); Set("_lifetime", new CancellationTokenSource());
        using var bootstrap=new WarpBootstrapService();
        Set("_warpBootstrap",bootstrap);Set("_warpBootstrapMode","none");
        var reconcileBootstrap=type.GetMethod("SetWarpBootstrapAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)reconcileBootstrap.Invoke(window,[true,CancellationToken.None,false])!;
        if(bootstrap.IsActive)throw new Exception("Route reconciliation restarted DPI for a confirmed no-DPI WARP route");
        Console.WriteLine("PASS actual WARP route reconciliation keeps no-DPI transport without winws");
        Set("_applicationLogPath", Path.Combine(folder, "test.log"));
        Set("_draftRecommendations", new Dictionary<string, CheckedDraftRecommendation>());
        settings.TelegramWebRoute="warpProxy";settings.Profiles=new(){TelegramWeb=true};
        var proxyOnly=type.GetMethod("IsProxyOnlyProfile",BindingFlags.Instance|BindingFlags.NonPublic)!;
        if(!(bool)proxyOnly.Invoke(window,[])!)throw new Exception("Telegram Web-only profile still requires main DPI");
        settings.Profiles.Discord=true;
        if((bool)proxyOnly.Invoke(window,[])!)throw new Exception("Mixed profile skipped Discord DPI");
        settings.TelegramWebRoute="direct";settings.Profiles=new();
        var dnsRequest=new AdvisorRequest("medium","Test ISP","network","standard",["youtube","discord"],0);
        var confirm=type.GetMethod("ConfirmAdvisorDnsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Task<IReadOnlyList<string>> Confirm(bool requireReady)=>
            (Task<IReadOnlyList<string>>)confirm.Invoke(window,[dnsRequest,CancellationToken.None,requireReady])!;
        Set("_advisorDnsLookup",new Func<CancellationToken,IEnumerable<string>,Task<IReadOnlyList<string>>>((_,_)=>Task.FromResult<IReadOnlyList<string>>(["www.youtube.com"])));
        var during=await Confirm(false);
        if(during.Count!=1||(bool)type.GetField("_advisorDnsNeedsDiagnosis",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!)
            throw new Exception("Mid-trial DNS failure aborted the run or lost evidence");
        var initialRejected=false;
        try{await Confirm(true);}catch(IOException){initialRejected=true;}
        if(!initialRejected)throw new Exception("Initial DNS failure silently allowed partial run");
        Set("_advisorAllowPartialDns",true);
        if((await Confirm(true)).Count!=1)throw new Exception("Explicit partial continuation lost failed domain");
        Set("_advisorDnsLookup",new Func<CancellationToken,IEnumerable<string>,Task<IReadOnlyList<string>>>((_,hosts)=>Task.FromResult<IReadOnlyList<string>>(hosts.ToArray())));
        var allRejected=false;try{await Confirm(true);}catch(IOException){allRejected=true;}
        if(!allRejected)throw new Exception("All unavailable services started a meaningless partial run");
        Set("_advisorAllowPartialDns",false);Set("_advisorDnsNeedsDiagnosis",false);
        var lookups=0;
        Set("_advisorDnsLookup",new Func<CancellationToken,IEnumerable<string>,Task<IReadOnlyList<string>>>((_,_)=>
            Task.FromResult<IReadOnlyList<string>>(++lookups==1?["www.youtube.com"]:[])));
        if((await Confirm(true)).Count!=0||lookups!=2)throw new Exception("Transient Windows DNS failure was not retried");
        using(var canceledDns=new CancellationTokenSource())
        {
            canceledDns.Cancel();var canceled=false;
            Set("_advisorDnsLookup",new Func<CancellationToken,IEnumerable<string>,Task<IReadOnlyList<string>>>((_,_)=>Task.FromResult<IReadOnlyList<string>>(["www.youtube.com"])));
            try{await (Task<IReadOnlyList<string>>)confirm.Invoke(window,[dnsRequest,canceledDns.Token,false])!;}catch(OperationCanceledException){canceled=true;}
            if(!canceled)throw new Exception("DNS retry ignored cancellation");
        }
        Console.WriteLine("PASS actual advisor DNS gate: initial rejection, explicit partial run, transient retry, mid-trial evidence and cancellation; no adapter changes");
        // Reproduce the log's ordering: preparation passed, DNS fails during
        // baseline, then the real advisor must still invoke the model and finish.
        Set("_advisorDnsLookup",new Func<CancellationToken,IEnumerable<string>,Task<IReadOnlyList<string>>>((_,_)=>Task.FromResult<IReadOnlyList<string>>(["www.youtube.com"])));
        var dnsSession=new DnsFailureSession(()=>Confirm(false));var dnsModel=new DnsRecoveryModel();
        var recovered=await new LocalStrategyAdvisor(dnsModel).RunAsync(dnsRequest,_=>Task.FromResult<IAdvisorTrialSession>(dnsSession),_=>{},[],default);
        if(!dnsModel.Called||!dnsSession.Disposed||recovered.Count==0||recovered.Any(r=>r.FullyConfirmed||r.Services.Count!=2||r.Services.Single(s=>s.Service=="youtube").Confirmed))
            throw new Exception("DNS failure after preparation prevented generation or became full confirmation");
        Console.WriteLine("PASS advisor end-to-end: mid-baseline DNS failure reaches model, preserves failed service, returns partial recommendations and disposes trial");
        async Task Save(object profile, string? recommendationId = null)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(recommendationId is null
                ? (object)new { profile } : new { profile, recommendationId }));
            await (Task)type.GetMethod("SaveConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [document.RootElement])!;
        }
        var id = Guid.NewGuid().ToString();
        await Save(new { id, name = "New", services = new[] { "telegramweb" }, updatedAt = "", favorite = true });
        if (settings.SavedConnections.Count != 1 || settings.SavedConnections[0].Favorite) throw new Exception("Save metadata regression");
        await Save(new { id, name = "Edited", services = new[] { "telegramweb" }, updatedAt = "invalid" });
        if (settings.SavedConnections.Count != 1 || settings.SavedConnections[0].Name != "Edited") throw new Exception("Edit duplicated profile");
        var p = new SavedConnectionProfile { Id = Guid.NewGuid().ToString(), Name = "Checked", Services = ["discord"], Provider = "Test ISP" };
        var networkMethod = type.GetMethod("CurrentNetworkKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var network = (string)networkMethod.Invoke(window, [])!;
        // An expired or foreign diagnostic cannot change DNS settings.
        Set("_dnsChoices",new Dictionary<string,(DnsResolverChoice Choice,string Network,string Composition,DateTimeOffset Expires)>
        { ["expired"]=(new("expired","google-udp","8.8.8.8",true,[]),network,DraftRecommendationPolicy.Composition(p),DateTimeOffset.UtcNow.AddMinutes(-1)) });
        using(var invalidChoice=JsonDocument.Parse(JsonSerializer.Serialize(new {choiceId="expired",draft=new {id=p.Id,name=p.Name,services=p.Services,provider=p.Provider,telegramWebRoute=p.TelegramWebRoute,robloxAssetsMode=p.RobloxAssetsMode}})))
        {
            var rejected=false;
            try { await (Task)type.GetMethod("ApplyAdvisorDnsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[invalidChoice.RootElement])!; }
            catch(ArgumentException){rejected=true;}
            if(!rejected||settings.DnsMode!="doh")throw new Exception("Expired DNS proof changed settings");
        }
        var recommendation = new AdvisorRecommendation("proof", new() { Level = p.Level }, p.Provider, 1, 1, true, 30, 1, "", [], RouteProbePolicy.Version);
        Set("_draftRecommendations", new Dictionary<string, CheckedDraftRecommendation>
        { ["proof"] = new(DraftRecommendationPolicy.Composition(p), p.Provider, network, DateTimeOffset.UtcNow.AddMinutes(30), recommendation) });
        await Save(new { id = p.Id, name = p.Name, services = p.Services, provider = p.Provider, telegramWebRoute=p.TelegramWebRoute,robloxAssetsMode=p.RobloxAssetsMode }, "proof");
        if (settings.SavedConnections.Single(x=>x.Id==p.Id).Strategy is null) throw new Exception("Checked route not saved");
        settings.DnsMode = "google-udp";
        if ((string)networkMethod.Invoke(window, [])! == network) throw new Exception("DNS mode did not invalidate route evidence");
        var rejectedDnsChange = false;
        try { await Save(new { id = p.Id, name = p.Name, services = p.Services, provider = p.Provider, telegramWebRoute=p.TelegramWebRoute,robloxAssetsMode=p.RobloxAssetsMode }, "proof"); }
        catch (ArgumentException) { rejectedDnsChange = true; }
        if (!rejectedDnsChange) throw new Exception("Recommendation from another DNS mode was accepted");
        settings.DnsMode = "doh";
        await Save(new { id=p.Id, name=p.Name, services=p.Services, games=new[] { "roblox" } });
        if (settings.SavedConnections.Single(x=>x.Id==p.Id).Strategy is not null || !settings.SavedConnections.Single(x=>x.Id==p.Id).RouteNeedsCheck)
            throw new Exception("Changed game composition retained checked route");
        if ((bool)type.GetField("_protectionRequested", BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!) throw new Exception("Save enabled protection");
        var robloxId=Guid.NewGuid().ToString();
        await Save(new {id=robloxId,name="Roblox assets",games=new[]{"roblox"},services=Array.Empty<string>(),robloxAssetsMode="auto"});
        if(settings.SavedConnections.Single(x=>x.Id==robloxId).RobloxAssetsMode!="auto")throw new Exception("New Roblox mode lost");
        await Save(new {id=robloxId,name="Roblox assets",games=new[]{"roblox"},services=Array.Empty<string>(),robloxAssetsMode="enhanced"});
        if(settings.SavedConnections.Single(x=>x.Id==robloxId).RobloxAssetsMode!="enhanced")throw new Exception("Roblox mode edit lost");
        var exported = ProfileTransferService.Export(settings.SavedConnections.Single(x=>x.Id==p.Id));
        async Task Import(string command)
        {
            using var document=JsonDocument.Parse(JsonSerializer.Serialize(new { type=command,requestId=Guid.NewGuid().ToString(),text=exported }));
            await (Task)type.GetMethod("TransferProfileAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[document.RootElement])!;
        }
        var before = settings.SavedConnections.Count;
        await Import("connections.import.preview");
        if(settings.SavedConnections.Count!=before)throw new Exception("Preview saved imported profile");
        await Import("connections.import");
        if(settings.SavedConnections.Count!=before+1||!settings.SavedConnections.Last().RouteNeedsCheck||settings.SavedConnections.Last().Id==p.Id)
            throw new Exception("Real import handler reused foreign route or ID");
        var warpProfile=new SavedConnectionProfile{Id=Guid.NewGuid().ToString(),Name="Web via WARP",Services=["telegramweb"],Provider="Test ISP",TelegramWebRoute="warpProxy"};
        var warpNetwork=(string)networkMethod.Invoke(window,[])!;
        var warpProof=new AdvisorRecommendation("warp-proof",new(){Level=warpProfile.Level},warpProfile.Provider,1,1,true,30,1,"",[],RouteProbePolicy.Version);
        Set("_draftRecommendations",new Dictionary<string,CheckedDraftRecommendation>{["warp-proof"]=new(DraftRecommendationPolicy.Composition(warpProfile),warpProfile.Provider,warpNetwork,DateTimeOffset.UtcNow.AddMinutes(5),warpProof)});
        await Save(new{id=warpProfile.Id,name=warpProfile.Name,services=warpProfile.Services,provider=warpProfile.Provider,telegramWebRoute="warpProxy",robloxAssetsMode=warpProfile.RobloxAssetsMode},"warp-proof");
        if(settings.SavedConnections.Single(x=>x.Id==warpProfile.Id).TelegramWebRoute!="warpProxy")throw new Exception("Checked WARP draft did not survive restored direct settings");
        Set("_warpStatus",new WarpStatus(WarpConnectionState.Disconnected,"fixture"));
        var updateWarp=type.GetMethod("UpdateWarpSettingsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        using(var change=JsonDocument.Parse("{\"transportSelection\":\"masque-h2\"}"))
            if(!await (Task<bool>)updateWarp.Invoke(window,[change.RootElement])!)throw new Exception("Canonical transport save failed");
        if(settings.Warp.TransportSelection!="masque-h2"||(await store.LoadAsync()).Warp.TransportSelection!="masque-h2")throw new Exception("Transport status and persisted settings differ");
        using(var change=JsonDocument.Parse("{\"protocol\":\"auto\",\"masque\":\"h3-only\"}"))
            if(!await (Task<bool>)updateWarp.Invoke(window,[change.RootElement])!||settings.Warp.TransportSelection!="auto")throw new Exception("Legacy message reintroduced hidden manual transport");
        using(var change=JsonDocument.Parse("{\"transportSelection\":\"masque-h3\"}"))
            if(!await (Task<bool>)updateWarp.Invoke(window,[change.RootElement])!)throw new Exception("Manual transport save failed");
        Directory.CreateDirectory(Path.Combine(folder, "settings.json.tmp"));
        try { await Save(new { id, name = "Must roll back", services = new[] { "discord" } }); throw new Exception("Expected storage failure"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (settings.SavedConnections.Single(x=>x.Id==id).Name != "Edited") throw new Exception("Failed save overwrote profile");
        using(var change=JsonDocument.Parse("{\"transportSelection\":\"auto\"}"))
            if(await (Task<bool>)updateWarp.Invoke(window,[change.RootElement])!)throw new Exception("Failed transport save reported success");
        if(settings.Warp.TransportSelection!="masque-h3"||(await store.LoadAsync()).Warp.TransportSelection!="masque-h3")throw new Exception("Failed transport save leaked Auto to memory or disk");
        using(var retry=JsonDocument.Parse(JsonSerializer.Serialize(new{provider="Test ISP",draft=new{id=warpProfile.Id,name=warpProfile.Name,services=warpProfile.Services,telegramWebRoute="warpProxy"}})))
            await (Task)type.GetMethod("UseAutoWarpAndRetryAdvisorAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[retry.RootElement])!;
        if(settings.Warp.TransportSelection!="masque-h3"||(bool)type.GetField("_advisorRunning",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!)throw new Exception("Failed save started advisor or changed manual transport");
        Console.WriteLine("PASS actual WARP settings handler: canonical/legacy messages, persistence, rollback and no advisor retry after storage failure");
        Set("_protectionRequested",false);Set("_advisorRunning",false);
        var youtubeProfile=new SavedConnectionProfile{Id=Guid.NewGuid().ToString(),Name="YT isolation fixture",Services=["youtube","discord"],Strategy=new(){YouTube="flowseal-general",Discord="flowseal-general"},ProbeVersion=15};
        settings=(AppSettings)type.GetField("_settings",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;settings.SavedConnections.Add(youtubeProfile);
        var unchanged=JsonSerializer.Serialize(settings);var dispatch=type.GetMethod("RunConnectionAdvisorAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        using(var input=JsonDocument.Parse(JsonSerializer.Serialize(new{scope="youtube",profileId=youtubeProfile.Id})))
        {
            var denied=false;try{await (Task)dispatch.Invoke(window,[input.RootElement])!;}catch(InvalidOperationException){denied=true;}
            if(!denied||JsonSerializer.Serialize(settings)!=unchanged)throw new Exception("YouTube-only dispatched into global setup or changed an inactive profile");
        }
        using(var input=JsonDocument.Parse("{\"scope\":\"unknown\"}"))
        {var denied=false;try{await (Task)dispatch.Invoke(window,[input.RootElement])!;}catch(ArgumentException){denied=true;}if(!denied||JsonSerializer.Serialize(settings)!=unchanged)throw new Exception("Unknown advisor scope bypassed validation");}
        Console.WriteLine("PASS actual YouTube-only dispatch rejects inactive profiles and unknown scopes before global setup; WARP/settings untouched");
        var runId=Guid.NewGuid().ToString();Set("_youtubeRunId",runId);Set("_youtubeApplying",true);Set("_advisorRunning",true);
        using(var duplicate=JsonDocument.Parse(JsonSerializer.Serialize(new{runId,id="same-candidate",allowPartial=true})))
            await (Task)type.GetMethod("ApplyYoutubeOnlyAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[duplicate.RootElement])!;
        if(JsonSerializer.Serialize(settings)!=unchanged)throw new Exception("Duplicate YouTube apply mutated settings");
        Set("_youtubeApplying",false);Set("_advisorRunning",false);
        Console.WriteLine("PASS actual YouTube apply ignores duplicate in-flight run before CLI or storage");
        Console.WriteLine("PASS real C# save handler: new/edit/legacy timestamps, checked draft route, no auto-connect, transactional storage failure");
    }
    private sealed class DnsRecoveryModel:ILocalStrategyModel
    {
        public bool Called;
        public Task<IReadOnlyList<AdvisorStrategySpec>> ProposeAsync(AdvisorRequest request,bool experimental,IReadOnlyList<AdvisorRecommendation> evidence,CancellationToken token)
        {Called=true;return Task.FromResult<IReadOnlyList<AdvisorStrategySpec>>(experimental?[]:[new(){Level=request.Level,YouTube="split",Discord="minimal"}]);}
    }
    private sealed class DnsFailureSession(Func<Task<IReadOnlyList<string>>> check):IAdvisorTrialSession
    {
        public bool Disposed;
        public Task<IReadOnlyList<AdvisorServiceEvidence>> CheckCurrentAsync(CancellationToken token)=>TestAsync(new(),token);
        public async Task<IReadOnlyList<AdvisorServiceEvidence>> TestAsync(AdvisorStrategySpec spec,CancellationToken token)
        {var missing=await check();return [new("youtube","dnsUnavailable",false,string.Join(", ",missing),null,null,3),new("discord","healthy",true,"fixture HTTPS content",30,1,3)];}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
}
