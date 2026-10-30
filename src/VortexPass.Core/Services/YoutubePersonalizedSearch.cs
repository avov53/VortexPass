using System.Diagnostics;
using VortexPass.Core.Models;
namespace VortexPass.Core.Services;
public static class YoutubePersonalizedSearch
{
    public static AdvisorStrategySpec Project(AdvisorStrategySpec baseline,AdvisorStrategySpec proposed)
    {
        if(proposed.YouTube!="personalized"||proposed.YouTubeParameters is null)throw new ArgumentException("ИИ должен предложить индивидуальные параметры YouTube");
        var candidate=baseline with{YouTube="personalized",YouTubeParameters=YoutubeParametersPolicy.Normalize(proposed.YouTubeParameters)};
        YoutubeOnlyPolicy.Validate(baseline,candidate);return candidate;
    }
    public static bool Improves(YoutubeCandidate baseline,YoutubeCandidate candidate)
    {
        if(candidate.Strategy==baseline.Strategy||!candidate.PageConfirmed)return false;
        if(!PreservesImages(baseline,candidate))return false;
        if(!baseline.PageConfirmed)return true;
        if(baseline.MediaConfirmed&&!candidate.MediaConfirmed)return false;
        if(candidate.MediaConfirmed&&!baseline.MediaConfirmed||candidate.FullyConfirmed&&!baseline.FullyConfirmed)return true;
        if(ImageScore(candidate)>ImageScore(baseline))return true;
        if(baseline.Samples.Any(s=>s.Groups?.Any(g=>g.Id=="page"&&g.DurationMs.HasValue)!=true)||candidate.Samples.Any(s=>s.Groups?.Any(g=>g.Id=="page"&&g.DurationMs.HasValue)!=true))return false;
        return baseline.PageMedianMs is {} old&&candidate.PageMedianMs is {} current&&old-current>=100&&current<=old*.85;
    }
    public static int ImageScore(YoutubeCandidate candidate)=>new[]{"images","avatars"}.Count(id=>candidate.Samples.Count>0&&candidate.Samples.All(s=>YoutubeOnlyPolicy.Group(s,id)));
    public static bool ContentScreened(YoutubeCandidate candidate)=>candidate.PageScreened&&!candidate.Samples.Any(s=>s.Groups?.Any(g=>g.Id is "images" or "avatars" && g.State is "unconfirmed" or "routeUnavailable")==true);
    public static bool PreservesImages(YoutubeCandidate baseline,YoutubeCandidate candidate)=>new[]{"images","avatars"}.All(id=>
        !baseline.Samples.All(s=>YoutubeOnlyPolicy.Group(s,id))||candidate.Samples.Count>0&&candidate.Samples.All(s=>YoutubeOnlyPolicy.Group(s,id)));
    public static IOrderedEnumerable<YoutubeCandidate> Rank(IEnumerable<YoutubeCandidate> candidates)=>candidates.OrderByDescending(c=>c.FullyConfirmed)
        .ThenByDescending(c=>c.PageScreened).ThenByDescending(ImageScore).ThenByDescending(c=>c.MediaConfirmed).ThenBy(c=>c.PageMedianMs??long.MaxValue).ThenBy(c=>c.MedianMs??long.MaxValue);
    public static async Task<IReadOnlyList<YoutubeCandidate>> RunAsync(AdvisorStrategySpec baseline,
        Func<AdvisorStrategySpec,CancellationToken,Task<ServiceHealthResult>> trial,
        Func<AdvisorRecommendation,CancellationToken,Task<IReadOnlyList<AdvisorStrategySpec>>> propose,
        Action<string,int> progress,CancellationToken token,TimeSpan? budget=null,Func<AdvisorStrategySpec,string>? configurationKey=null,
        string searchMode="normal",TimeSpan? modelBudget=null,string provider="local",ServiceHealthResult? baselineMeasurement=null)
    {
        var watch=Stopwatch.StartNew();var limit=budget??TimeSpan.FromSeconds(searchMode=="extended"?345:165);
        using var work=CancellationTokenSource.CreateLinkedTokenSource(token);work.CancelAfter(limit);var ct=work.Token;
        var measured=new List<YoutubeCandidate>();var key=configurationKey??(s=>s.Key);var seen=new HashSet<string>();
        var fallback=baseline with{YouTube=baseline.Level=="light"?"youtube-segment":"youtube-tls",YouTubeParameters=null};
        async Task<YoutubeCandidate?> Measure(AdvisorStrategySpec spec)
        {
            ct.ThrowIfCancellationRequested();
            YoutubeOnlyPolicy.Validate(baseline,spec);if(!seen.Add(key(spec)))return null;
            progress(spec==fallback?"Проверяем резерв YouTube":"Измеряем маршрут YouTube",measured.Count);
            var result=spec==baseline&&baselineMeasurement is not null?baselineMeasurement:await trial(spec,ct);
            var item=new YoutubeCandidate(Guid.NewGuid().ToString("N"),spec,[result]);measured.Add(item);return item;
        }
        async Task Confirm(YoutubeCandidate item)
        {
            var samples=new List<ServiceHealthResult>();
            for(var i=0;i<3;i++){ct.ThrowIfCancellationRequested();progress($"Подтверждение YouTube: {i+1}/3",measured.Count);samples.Add(await trial(item.Strategy,ct));}
            measured.Remove(item);measured.Add(item with{Samples=samples});
        }
        try
        {
            await Measure(baseline);
            var initial=measured[0].Samples[0];
            var evidence=LocalStrategyAdvisor.Rate(baseline,provider,[new("youtube",initial.State.ToString(),initial.ContentConfirmed==true,initial.Detail,initial.LatencyMs,null,1,initial.Groups)]);
            IReadOnlyList<AdvisorStrategySpec> proposals=[];
            using(var model=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                model.CancelAfter(modelBudget??TimeSpan.FromSeconds(searchMode=="extended"?40:20));progress("ИИ анализирует свежие измерения этой сети",measured.Count);
                try{proposals=await propose(evidence,model.Token).WaitAsync(model.Token);}
                catch(Exception ex)when(!ct.IsCancellationRequested&&(ex is OperationCanceledException or TimeoutException or IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException))
                {progress("ИИ недоступен или не вернул допустимых параметров; используется измеренный резерв",measured.Count);}
            }
            var personalLimit=searchMode=="extended"?5:2;var personalCount=0;
            foreach(var proposal in proposals)
            {
                if(personalCount>=personalLimit)break;
                AdvisorStrategySpec candidate;try{candidate=Project(baseline,proposal);}catch(ArgumentException){continue;}
                // Reserve time for a fallback screen and three confirmation rounds.
                if(limit-watch.Elapsed<TimeSpan.FromSeconds(40))break;
                if(await Measure(candidate) is not null)personalCount++;
            }
            if(!measured.Any(ContentScreened))await Measure(fallback);
            var finalists=measured.Where(c=>c.Strategy==baseline&&c.PageScreened).Concat(Rank(measured.Where(c=>c.PageScreened))).DistinctBy(c=>key(c.Strategy)).Take(2).ToArray();
            foreach(var finalist in finalists)
            {
                if(limit-watch.Elapsed<TimeSpan.FromSeconds(24))break;
                await Confirm(finalist);
            }
            if(!measured.Any(c=>c.PageConfirmed)&&limit-watch.Elapsed>=TimeSpan.FromSeconds(32))
            {
                var reserve=await Measure(fallback);
                if(reserve?.PageScreened==true)await Confirm(reserve);
            }
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){}
        token.ThrowIfCancellationRequested();
        return Rank(measured).ToArray();
    }
}
