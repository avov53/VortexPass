using VortexPass.Core.Models;
using VortexPass.Core.Services;
internal static class YoutubeGlobalAdvisorTests
{
    private static readonly YoutubeParameters A=new("split","sni",0,2);
    private static readonly YoutubeParameters B=new("fakeDisorder","firstAndSni",4,3);
    public static async Task Run()
    {
        foreach(var network in new[]{"a","b"})
        {
            var session=new Session(network);var model=new Model();
            var runner=new LocalStrategyAdvisor(model,modelBudget:TimeSpan.FromMilliseconds(100));
            var results=await runner.RunAsync(Request(network),_=>Task.FromResult<IAdvisorTrialSession>(session),_=>{},[],default);
            var best=results[0];
            if(best.Strategy.YouTubeParameters!=(network=="a"?A:B)||best.Services.Single(s=>s.Service=="youtube").Samples!=3||best.FullyConfirmed)
                throw new Exception("Individual network measurements did not select the correct parameters or falsely confirmed media");
            if(session.Attempts.Any(s=>s.YouTube=="youtube-tls")||!session.Restored||!model.SawFreshEvidence||session.Attempts.Select(s=>s.Key).Distinct().Count()>3)
                throw new Exception("Fallback was forced before personalized trials, evidence missing, or budget/restoration failed");
            foreach(var attempt in session.Attempts)YoutubeOnlyPolicy.Validate(session.CurrentStrategy!,attempt);
        }
        var fallback=new Session("none");var reserve=await new LocalStrategyAdvisor(new Model()).RunAsync(Request("none"),_=>Task.FromResult<IAdvisorTrialSession>(fallback),_=>{},[],default);
        if(reserve[0].Strategy.YouTube!="youtube-tls"||fallback.Attempts.FindIndex(s=>s.YouTube=="youtube-tls")<2)throw new Exception("Fallback did not follow failed personalized trials");
        var slowSession=new Session("none");await new LocalStrategyAdvisor(new Model(true),modelBudget:TimeSpan.FromMilliseconds(20)).RunAsync(Request("none"),_=>Task.FromResult<IAdvisorTrialSession>(slowSession),_=>{},[],default);
        if(!slowSession.Attempts.Any(s=>s.YouTube=="youtube-tls")||!slowSession.Restored)throw new Exception("Model timeout lost measured reserve or restoration");
        var working=new Session("a",true);var unchanged=await new LocalStrategyAdvisor(new Model()).RunAsync(Request("a"),_=>Task.FromResult<IAdvisorTrialSession>(working),_=>{},[],default);
        if(unchanged.Any(r=>r.Strategy!=working.CurrentStrategy))throw new Exception("Working route replaced without sufficient measured improvement");
        var serialized=System.Text.Json.JsonSerializer.Serialize(YoutubeModelContext.Summarize(new("youtube","failed",false,"secret",1,0,1,[new("tls-IPv4 1.2.3.4","unconfirmed","BAD_MAC https://secret.test/?token=secret")])));
        if(serialized.Contains("secret")||serialized.Contains("1.2.3.4")||!serialized.Contains("integrity"))throw new Exception("Model context leaks raw diagnostic strings");
    }
    private static AdvisorRequest Request(string network)=>new("medium","fixture",network,"standard",["youtube","discord","telegram"],TelegramWebRoute:"warpProxy",PreserveNonYoutube:true);
    private sealed class Model(bool slow=false):ILocalStrategyModel
    {
        public bool SawFreshEvidence;
        public async Task<IReadOnlyList<AdvisorStrategySpec>> ProposeAsync(AdvisorRequest request,bool experimental,IReadOnlyList<AdvisorRecommendation> evidence,CancellationToken token)
        {
            SawFreshEvidence=evidence.FirstOrDefault()?.Services.Any(s=>s.Service=="youtube")==true;
            if(slow)await Task.Delay(Timeout.Infinite,token);
            return [new(){YouTube="personalized",YouTubeParameters=A,Discord="minimal",TcpRepeats=2},new(){YouTube="personalized",YouTubeParameters=B,Telegram="split"}];
        }
    }
    private sealed class Session(string network,bool existingWorks=false):IAdvisorTrialSession
    {
        public AdvisorStrategySpec? CurrentStrategy=>new(){YouTube="flowseal-general",Discord="flowseal-general",Telegram="baseline"};
        public List<AdvisorStrategySpec> Attempts=[];public bool Restored;
        public Task<IReadOnlyList<AdvisorServiceEvidence>> CheckCurrentSamplesAsync(int samples,CancellationToken token)=>Task.FromResult(Evidence(CurrentStrategy!,samples));
        public Task<IReadOnlyList<AdvisorServiceEvidence>> TestAsync(AdvisorStrategySpec spec,CancellationToken token)=>TestSamplesAsync(spec,1,token);
        public Task<IReadOnlyList<AdvisorServiceEvidence>> TestSamplesAsync(AdvisorStrategySpec spec,int samples,CancellationToken token){Attempts.Add(spec);return Task.FromResult(Evidence(spec,samples));}
        private IReadOnlyList<AdvisorServiceEvidence> Evidence(AdvisorStrategySpec spec,int samples)
        {
            var success=spec.YouTube=="youtube-tls"||existingWorks&&spec.YouTube==CurrentStrategy!.YouTube||spec.YouTubeParameters==(network=="a"?A:network=="b"?B:null)&&spec.YouTubeParameters is not null;
            return [new("youtube","partial",false,"page/media separate",30,0,samples,[new("page",success?"confirmed":"unconfirmed","html",existingWorks?480:120),new("images",success?"confirmed":"unconfirmed","jpeg"),new("video","controlResourceUnconfirmed","no control resource")]),
                new("discord","healthy",true,"cdn",40,0,samples),new("telegram","healthy",true,"warp",30,0,3)];
        }
        public ValueTask DisposeAsync(){Restored=true;return ValueTask.CompletedTask;}
    }
}
