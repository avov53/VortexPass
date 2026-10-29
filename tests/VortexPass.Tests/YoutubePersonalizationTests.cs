using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;
internal static class YoutubePersonalizationTests
{
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    public static async Task Run()
    {
        var original=new AdvisorStrategySpec{Level="medium",YouTube="youtube-tls",Discord="flowseal-general",TcpRepeats=4,QuicRepeats=3,Overlap=256};
        var command=WinwsProfileComposer.Compose(new(){YouTube=true,Discord=true,TelegramWeb=true,GameFilter=true},"medium",new(){SelectedGames=["roblox","phasmophobia"]},null,"fixture-exclusions.txt",includeWarpBootstrap:true,advisorStrategy:original);
        foreach(var technique in new[]{"split","disorder","fakeSplit","fakeDisorder"})
        {
            var candidate=YoutubePersonalizedSearch.Project(original,original with{YouTube="personalized",YouTubeParameters=new(technique,"sni",technique.StartsWith("fake")?2:0,1)});
            var trial=WinwsProfileComposer.ComposeYoutubeOnly(command,original,candidate);YoutubeOnlyPolicy.AssertIsolated(command,trial);
            foreach(var rule in trial.Split(" --new").Where(WinwsProfileComposer.IsYoutubeRule))
            {
                Check(!rule.Contains("seqovl"),"Personalized YouTube inherited global overlap");
                Check(!rule.Contains("--dpi-desync-repeats=4")&&!rule.Contains("--dpi-desync-repeats=3"),"Personalized YouTube inherited global repeat counts");
            }
        }
        var legacy=original.Key;var restored=JsonSerializer.Deserialize<AdvisorStrategySpec>(JsonSerializer.Serialize(original))!;Check(restored.Key==legacy,"Absent YouTube parameters changed an old key");
        var parsed=AdvisorStrategyPolicy.ParseYoutubeOutput("[{\"t\":0,\"s\":2,\"r\":2,\"q\":1}]",new("light","local","network","standard",["youtube"]));
        Check(parsed.Single().YouTubeParameters==new YoutubeParameters("split","firstAndSni",0,1),"Compact model output did not normalize canonical parameters");
        foreach(var invalid in new[]{"[{\"t\":99,\"s\":1,\"r\":2,\"q\":1}]","[{\"t\":2,\"s\":1,\"r\":2,\"q\":1}]","[{\"t\":0,\"s\":1,\"r\":2,\"q\":8}]","[{\"t\":0,\"s\":1,\"q\":1}]","[{\"t\":0,\"s\":1,\"r\":2,\"q\":1,\"command\":\"unsafe\"}]"})
        {
            var rejected=false;try{AdvisorStrategyPolicy.ParseYoutubeOutput(invalid,new("light","local","network","standard",["youtube"]));}catch(Exception e)when(e is ArgumentException or JsonException){rejected=true;}Check(rejected,"Unsafe or incomplete model output accepted");
        }
        var personalized=original with{YouTube="personalized",YouTubeParameters=new("split","sni",0,2)};
        var saved=JsonSerializer.Deserialize<AdvisorStrategySpec>(JsonSerializer.Serialize(personalized))!;Check(saved==personalized&&saved.Key!=legacy,"Personalized key/persistence was lost");
        ServiceHealthResult Probe(bool page,int ms)=>new("youtube",ServiceHealthState.Partial,"fixture",20,ContentConfirmed:false,Groups:[new("page",page?"confirmed":"unconfirmed","html",ms),new("images",page?"confirmed":"unconfirmed","image"),new("video","controlResourceUnconfirmed","no fixture")]);
        var baseline=new YoutubeCandidate("base",original,[Probe(true,500),Probe(true,500),Probe(true,500)]);
        Check(!YoutubePersonalizedSearch.Improves(baseline,new("too-small",personalized,[Probe(true,450),Probe(true,450),Probe(true,450)])),"Tiny latency difference replaced a working route");
        Check(YoutubePersonalizedSearch.Improves(baseline,new("faster",personalized,[Probe(true,350),Probe(true,350),Probe(true,350)])),"Confirmed 15%/100ms improvement ignored");
        using var cancel=new CancellationTokenSource();cancel.Cancel();var canceled=false;
        try{await YoutubePersonalizedSearch.RunAsync(original,(_,ct)=>Task.FromCanceled<ServiceHealthResult>(ct),(_,_)=>Task.FromResult<IReadOnlyList<AdvisorStrategySpec>>([]),(_,_)=>{},cancel.Token);}catch(OperationCanceledException){canceled=true;}Check(canceled,"Explicit cancellation became a result");
        var timed=await YoutubePersonalizedSearch.RunAsync(original,async(_,ct)=>{await Task.Delay(1000,ct);return Probe(true,1);},(_,_)=>Task.FromResult<IReadOnlyList<AdvisorStrategySpec>>([]),(_,_)=>{},default,TimeSpan.FromMilliseconds(20));Check(timed.Count==0,"Budget fabricated a measured recommendation");
    }
}
