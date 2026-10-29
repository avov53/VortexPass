using VortexPass.Core.Models;
using VortexPass.Core.Services;

internal static class YoutubeLifecycleTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run()
    {
        var states=new ComponentLifecycle();var first=states.Begin("dpi","fixture");
        var running=states.Set("dpi","fixture",first.Generation,ServiceState.Running)!;
        var planned=states.Set("dpi","fixture",first.Generation,ServiceState.Stopping,planned:true)!;
        var second=states.Begin("dpi","fixture");var newRunning=states.Set("dpi","fixture",second.Generation,ServiceState.Running)!;
        Check(!states.IsCurrent(running)&&!states.IsCurrent(planned)&&planned.Planned,"Delayed planned state remains current");
        Check(states.Set("dpi","fixture",first.Generation,ServiceState.Stopped) is null&&states.IsCurrent(newRunning),"Old exit replaced the new process");
        Parallel.For(0,1000,_=>states.Set("dpi","fixture",first.Generation,ServiceState.Error,"stale"));
        Check(states.Get("dpi")==newRunning,"Concurrent stale callbacks changed the live state");
        var exited=states.Set("dpi","fixture",second.Generation,ServiceState.Stopped)!;
        Check(states.IsCurrent(exited)&&!exited.Planned&&!states.IsCurrent(newRunning),"Real current process exit was suppressed");
        var ok=YoutubeRouteProbe.Evaluate([new("dns","confirmed","ok"),new("tls-IPv4","confirmed","TLS13"),new("page","confirmed","html"),new("images","confirmed","jpeg"),new("video","controlResourceUnconfirmed","no resource")],20);
        Check(YoutubeApplyTransaction.RouteConfirmed(ok)&&ok.ContentConfirmed==false,"Page-only fresh proof falsely confirms video");
        foreach(var failure in new[]{"none","cli","probe","storage","cancel"})
        {
            var calls=new List<string>();using var token=new CancellationTokenSource();var failed=false;
            try
            {
                await YoutubeApplyTransaction.RunAsync(_=>{calls.Add("wire");if(failure=="cli")throw new IOException("CLI");return Task.CompletedTask;},
                    _=>{calls.Add("probe");if(failure=="cancel")token.Cancel();return Task.FromResult(failure=="probe"?YoutubeRouteProbe.Evaluate([],0):ok);},
                    (proof,_)=>{calls.Add("save");if(failure=="storage")throw new IOException("storage");return Task.CompletedTask;},token.Token);
            }
            catch(Exception ex)when(ex is IOException or OperationCanceledException){failed=true;}
            Check(failed==(failure!="none"),"Apply failure became a successful transaction");
            Check(!calls.Contains("save")||failure is "none" or "storage","Saved without a valid fresh route");
            if(failure=="none")Check(string.Join(',',calls)=="wire,probe,save","Saved before fresh verification");
        }
        Check(!YoutubeApplyTransaction.RouteConfirmed(ok with{Groups=ok.Groups!.Where(g=>!g.Id.StartsWith("tls-")).ToArray()}),"Missing TLS passes apply gate");
        var directory=Path.GetFullPath(Path.Combine("artifacts","youtube-persistence-tests",Guid.NewGuid().ToString("N")));
        var store=new AppSettingsStore(directory);var settings=new AppSettings{DnsMode="google-udp"};
        var baseline=new AdvisorStrategySpec{YouTube="flowseal-general",Discord="flowseal-general"};var candidate=baseline with{YouTube="personalized",YouTubeParameters=new(){TcpTechnique="split",SplitPattern="firstByte",FakeRepeats=0,QuicRepeats=1}};
        var at=DateTimeOffset.UtcNow.AddHours(-1);var profile=new SavedConnectionProfile{Id=Guid.NewGuid().ToString(),Name="fixture",Strategy=baseline,ProbeVersion=RouteProbePolicy.Version,Provider="fixture",WarpProofKey="unchanged-warp-proof",RouteNeedsCheck=true};
        profile.NetworkRoutes["network"]=new(baseline,profile.Provider,RouteProbePolicy.Version,at,profile.WarpProofKey);settings.SavedConnections.Add(profile);WarpTransportPolicy.Normalize(settings);var warp=System.Text.Json.JsonSerializer.Serialize(settings.Warp);
        await YoutubeApplyTransaction.RunAsync(_=>Task.CompletedTask,_=>Task.FromResult(ok),async(_,token)=>{YoutubeRoutePersistence.Update(settings,profile,baseline,candidate,"network","rule-signature",false);await store.SaveAsync(settings,token);},default);
        var restarted=await new AppSettingsStore(directory).LoadAsync();var saved=restarted.SavedConnections.Single();var route=saved.NetworkRoutes["network"];
        Check(saved.Strategy==candidate&&route.Strategy==candidate&&AdvisorStrategyPolicy.CanReuseStored(route.Strategy,saved.Level,"network","network",route.ProbeVersion),"Restart lost the applied YouTube strategy or route cache");
        Check(route.ConfirmedAt==at&&route.WarpProofKey=="unchanged-warp-proof"&&saved.RouteNeedsCheck&&restarted.DnsMode=="google-udp"&&System.Text.Json.JsonSerializer.Serialize(restarted.Warp)==warp,"YouTube persistence changed other service confirmation, WARP or DNS");
        Check(saved.YoutubeVerification is {MediaConfirmed:false,BrowserConfirmed:false}&&route.YoutubeVerification?.RuleSignature=="rule-signature","Persistence falsely confirmed media/browser or lost YouTube signature");
        Check(saved.YoutubeVerification?.SearchVersion==YoutubeParametersPolicy.SearchVersion&&saved.Strategy.YouTubeParameters==candidate.YouTubeParameters,"Restart lost personalized parameters or their search version");
        restarted.StartWithWindows=true;restarted.StartProtectionOnStartup=true;restarted.StartupConnectionId=saved.Id;
        await store.SaveAsync(restarted);var startup=await store.LoadAsync();
        Check(startup.StartProtectionOnStartup&&startup.StartupConnectionId==saved.Id,"Startup lost explicit profile identity");
        startup.SavedConnections.Single().Name="renamed";await store.SaveAsync(startup);
        Check((await store.LoadAsync()).StartupConnectionId==saved.Id,"Rename changed startup profile");
        startup.StartupConnectionId="";startup.StartProtectionOnStartup=true;await store.SaveAsync(startup);
        var migrated=await store.LoadAsync();Check(migrated.StartWithWindows&&!migrated.StartProtectionOnStartup&&migrated.StartupProfileChoiceRequired,"Legacy startup chose a random profile or disabled app startup");
        StartupConnectionPolicy.Select(migrated,saved.Id);await store.SaveAsync(migrated);
        var boot=await store.LoadAsync();Check(boot.StartProtectionOnStartup&&boot.StartupConnectionId==saved.Id&&!boot.StartupProfileChoiceRequired,"Selecting startup profile did not enable and persist auto-connect");
        Check(boot.SavedConnections.Single().Strategy==candidate&&boot.DnsMode=="google-udp","Startup selection changed the saved route or DNS");
        Check(StartupConnectionPolicy.Reason(boot,true)=="подключаем выбранный профиль"&&StartupConnectionPolicy.Reason(boot,false).Contains("--startup"),"Boot decision is ambiguous");
        var polls=0;await StartupConnectionPolicy.WaitReadyAsync(()=>++polls>=2,TimeSpan.FromSeconds(2),default);Check(polls>=2,"Busy startup was skipped silently");
        using var stop=new CancellationTokenSource();stop.Cancel();var aborted=false;try{await StartupConnectionPolicy.WaitReadyAsync(()=>false,TimeSpan.FromSeconds(2),stop.Token);}catch(OperationCanceledException){aborted=true;}Check(aborted,"Explicit startup cancellation ignored");
    }
}
