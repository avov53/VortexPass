using System.Net;
using System.Text;
using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

internal static class YoutubeOnlyTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run()
    {
        await YoutubeLifecycleTests.Run();
        await YoutubePersonalizationTests.Run();
        var baseline=new AdvisorStrategySpec{Level="medium",YouTube="flowseal-general",Discord="flowseal-general",Telegram="baseline"};
        var command=WinwsProfileComposer.Compose(new(){YouTube=true,Discord=true,Telegram=true,TelegramWeb=true,GameFilter=true},"medium",new(){SelectedGames=["roblox","phasmophobia"]},null,@"C:\Users\Роман\list-exclude.txt",includeWarpBootstrap:true,advisorStrategy:baseline);
        Check(WinwsProfileComposer.ComposeYoutubeOnly(command,baseline,baseline)==command,"Control command was rewritten");
        foreach(var spec in YoutubeOnlyPolicy.Candidates(baseline))
        {
            var changed=WinwsProfileComposer.ComposeYoutubeOnly(command,baseline,spec);YoutubeOnlyPolicy.AssertIsolated(command,changed);
            Check(YoutubeOnlyPolicy.Outside(command)==YoutubeOnlyPolicy.Outside(changed)&&YoutubeOnlyPolicy.Signature(command)!=YoutubeOnlyPolicy.Signature(changed),"Other service rules changed or YouTube remained unchanged");
            var youtube=changed.Split(" --new").Where(WinwsProfileComposer.IsYoutubeRule).ToArray();
            var fallback=Array.FindIndex(youtube,r=>r.Contains("--ipset="));
            Check(fallback>0&&youtube.Skip(fallback).All(r=>r.Contains("--ipset=")),"IP fallback shadows hostname rules");
        }
        foreach(var unsafeSpec in new[]{baseline with{Discord="minimal"},baseline with{Telegram="split"},baseline with{TcpRepeats=2},baseline with{Overlap=256},baseline with{RouteTemplate="narrow"},baseline with{Tuning=1}})
        {var rejected=false;try{WinwsProfileComposer.ComposeYoutubeOnly(command,baseline,unsafeSpec);}catch(ArgumentException){rejected=true;}Check(rejected,"Global/service change accepted by YouTube scope");}
        var outsideRejected=false;try{YoutubeOnlyPolicy.AssertIsolated(command,command+" --filter-tcp=9999");}catch(InvalidOperationException){outsideRejected=true;}Check(outsideRejected,"Isolation assertion failed open");
        var tlsCommand=WinwsProfileComposer.ComposeYoutubeOnly(command,baseline,baseline with{YouTube="youtube-tls"});
        var tlsRules=tlsCommand.Split(" --new").Where(WinwsProfileComposer.IsYoutubeRule).Where(r=>r.Contains("--filter-tcp="));
        Check(tlsRules.All(r=>r.Contains("--dpi-desync=fake,multidisorder")&&!r.Contains("--dpi-desync-split-seqovl=")&&r.Contains("--dpi-desync-cutoff=n3")),"YouTube TLS candidate inherited failing overlap or unbounded processing");
        var hostfake=baseline with{YouTube="flowseal-alt9"};
        var hostfakeCommand=WinwsProfileComposer.ComposeYoutubeOnly(command,baseline,hostfake);
        var segment=WinwsProfileComposer.ComposeYoutubeOnly(hostfakeCommand,hostfake,hostfake with{YouTube="youtube-segment"});
        Check(YoutubeOnlyPolicy.Outside(hostfakeCommand)==YoutubeOnlyPolicy.Outside(segment),"Segment-only repair changed Discord, WARP or other services");
        Check(segment.Split(" --new").Where(WinwsProfileComposer.IsYoutubeRule).Where(r=>r.Contains("--filter-tcp=")).All(r=>r.Contains("--dpi-desync=multisplit")&&r.Contains("--dpi-desync-split-pos=1,midsld")&&!r.Contains("fake")&&!r.Contains("seqovl")&&!r.Contains("--ip-id=")&&r.Contains("--dpi-desync-cutoff=n3")),"Segment candidate retained fake TLS, host replacement or overlap from BAD_MAC profile");
        var wrongService=false;try{AdvisorStrategyPolicy.Validate(baseline with{Discord="youtube-tls"},"medium");}catch(ArgumentException){wrongService=true;}Check(wrongService,"YouTube-specific TLS strategy accepted for Discord");
        Check(new AppSettings().DnsMode=="google-udp"&&JsonSerializer.Deserialize<AppSettings>("{}")!.DnsMode=="google-udp"&&JsonSerializer.Deserialize<AppSettings>("{\"DnsMode\":\"doh\"}")!.DnsMode=="doh","DNS default overwrites an explicit saved choice");
        var attempts=new List<string>();
        var results=await YoutubeOnlySearch.RunAsync(baseline,(s,t)=>{attempts.Add(s.YouTube);return Task.FromResult(Result(s.YouTube=="youtube-tls",s.YouTube=="youtube-tls"));},(_,_)=>{},default,configurationKey:s=>WinwsProfileComposer.ComposeYoutubeOnly(command,baseline,s));
        Check(attempts.Count==10&&attempts[1]=="youtube-segment"&&results[0].Strategy.YouTube=="youtube-tls"&&results[0].PageConfirmed&&results[0].MediaConfirmed,"Search did not confirm the isolated working candidate three times");
        Check(results.Where(c=>c.Samples.Count==1).All(c=>!c.PageConfirmed&&!c.FullyConfirmed),"One screen became a confirmed recommendation");
        var empty=await YoutubeOnlySearch.RunAsync(baseline,async(s,t)=>{await Task.Delay(1000,t);return Result(true,true);},(_,_)=>{},default,TimeSpan.FromMilliseconds(20));Check(empty.Count==0,"Unmeasured timeout fabricated recommendations");
        using(var cancel=new CancellationTokenSource())
        {cancel.Cancel();var cancelled=false;try{await YoutubeOnlySearch.RunAsync(baseline,(s,t)=>Task.FromCanceled<ServiceHealthResult>(cancel.Token),(_,_)=>{},cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"Cancellation ignored");}
        var duplicateCalls=0;await YoutubeOnlySearch.RunAsync(baseline,(s,t)=>{duplicateCalls++;return Task.FromResult(Result(false,false));},(_,_)=>{},default,configurationKey:_=>"same");Check(duplicateCalls==4,"Identical configurations screened repeatedly");
        var noMedia=Result(true,false);Check(noMedia.ContentConfirmed==false&&noMedia.State==ServiceHealthState.Partial,"Page-only check confirmed video");
        var tls=YoutubeRouteProbe.Evaluate([new("dns","confirmed","ok"),new("tcp-IPv4","confirmed","ok"),new("tls-IPv4","unconfirmed","reset")],12);Check(tls.State==ServiceHealthState.TlsFailed,"TLS failure misclassified");
        var detail=YoutubeRouteProbe.Error(new HttpRequestException("TLS failed",new IOException("Peer closed https://r1.googlevideo.com/videoplayback?sig=secret")));
        Check(detail.Contains("Peer closed")&&!detail.Contains("secret"),"TLS inner cause missing or URL parameters leaked");
        foreach(var fixture in new[]{("page",204,"",false),("page",200,"<html>Error</html>",false),("page",200,"<html>ytcfg.set({})</html>",true),("images",200,"<html>not an image</html>",false)})
        {
            using var client=new HttpClient(new Fixture(fixture.Item2,fixture.Item3));var group=await YoutubeRouteProbe.Resource(client,fixture.Item1,new("https://www.youtube.com/"),default);Check((group.State=="confirmed")==fixture.Item4,"Unexpected content confirms a resource");
        }
        using(var client=new HttpClient(new Fixture(403,"")))
        {var restricted=await YoutubeRouteProbe.Resource(client,"page",new("https://www.youtube.com/"),default);Check(restricted.State=="accessRestricted","403 treated as network failure");}
        var folder=Path.Combine(Path.GetTempPath(),"vortex-firefox-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(folder,"profile"));
        try{await File.WriteAllTextAsync(Path.Combine(folder,"profile","prefs.js"),"user_pref(\"network.trr.mode\", 3);\nuser_pref(\"private.auth\", \"secret\");");var text=FirefoxNetworkDiagnostics.Read(folder);Check(text.Contains("network.trr.mode=3")&&!text.Contains("secret")&&!text.Contains("private.auth"),"Firefox diagnostic read/exported unrelated values");}
        finally{if(!Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected test cleanup target");Directory.Delete(folder,true);}
        var settings=new AppSettings();settings.SavedConnections.Add(new(){Id=Guid.NewGuid().ToString(),Name="fixture",Services=["youtube","discord"],Strategy=baseline,ProbeVersion=15,YoutubeVerification=new(1,"network",baseline.Key,"signature",DateTimeOffset.UtcNow,true,false)});
        var reloaded=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;Check(reloaded.SavedConnections[0].ProbeVersion==15&&reloaded.SavedConnections[0].YoutubeVerification?.MediaConfirmed==false&&new SavedConnectionProfile().YoutubeVerification is null,"YouTube proof migration resets other verification or confirms media");
        foreach(var failSettings in new[]{false,true})
        {
            var attemptedSettings=false;var failed=false;
            try{await YoutubeOnlyRollback.RestoreAsync(_=>throw new IOException("CLI restore failed"),_=>{attemptedSettings=true;if(failSettings)throw new IOException("settings restore failed");return Task.CompletedTask;},new IOException("original failure"),default);}
            catch(AggregateException ex){failed=true;Check(ex.InnerExceptions.Count==(failSettings?3:2),"Restore failure concealed original or sibling error");}
            Check(attemptedSettings&&failed,"CLI failure skipped settings restoration or reported success");
        }
    }
    private static ServiceHealthResult Result(bool page,bool video)=>YoutubeRouteProbe.Evaluate([new("dns","confirmed","ok"),new("page",page?"confirmed":"unconfirmed","page"),new("api","confirmed","api"),new("images","confirmed","image"),new("video",video?"confirmed":"controlResourceUnconfirmed","video")],page?10:100);
    private sealed class Fixture(int status,string body):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new ByteArrayContent(Encoding.UTF8.GetBytes(body))});}
}
