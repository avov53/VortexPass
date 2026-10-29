using System.Security.Cryptography;
using System.Text;
using VortexPass.Core.Models;
namespace VortexPass.Core.Services;

public static class YoutubeOnlyPolicy
{
    public const int ProbeVersion=3;
    public static void Validate(AdvisorStrategySpec baseline,AdvisorStrategySpec candidate)
    {
        AdvisorStrategyPolicy.Validate(candidate,baseline.Level);
        if((candidate with{YouTube=baseline.YouTube,YouTubeTransport=baseline.YouTubeTransport,YouTubeParameters=baseline.YouTubeParameters})!=baseline)
            throw new ArgumentException("Подбор YouTube не может изменять другие сервисы или общие параметры");
    }
    // Start with segmentation that never injects fake TLS or overlapping data.
    // Keep forwarding as a measured alternative; do not reuse the BAD_MAC hostfake as a default finalist.
    public static IReadOnlyList<AdvisorStrategySpec> Candidates(AdvisorStrategySpec baseline)=>new[]{"youtube-segment","direct","youtube-tls","minimal","split","disorder"}
        .Where(b=>AdvisorStrategyPolicy.YoutubeBlocks(baseline.Level).Contains(b)).Select(b=>baseline with{YouTube=b,YouTubeParameters=null}).Where(s=>s!=baseline).Distinct().Take(3).ToArray();
    public static string Outside(string command)=>string.Join(" --new",command.Split(" --new",StringSplitOptions.None).Where(r=>!WinwsProfileComposer.IsYoutubeRule(r)));
    public static string Signature(string command)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(" --new",command.Split(" --new").Where(WinwsProfileComposer.IsYoutubeRule)))));
    public static void AssertIsolated(string original,string candidate)
    {if(Outside(original)!=Outside(candidate))throw new InvalidOperationException("Изменены правила вне YouTube; испытание отклонено");}
    public static bool Group(ServiceHealthResult result,string id)=>result.Groups?.Any(g=>g.Id==id&&g.State=="confirmed")==true;
}
public sealed record YoutubeCandidate(string Id,AdvisorStrategySpec Strategy,IReadOnlyList<ServiceHealthResult> Samples)
{
    public bool PageScreened=>Samples.Count>0&&Samples.All(r=>r.ContentConfirmed==true||YoutubeOnlyPolicy.Group(r,"page"));
    public bool PageConfirmed=>Samples.Count>=3&&PageScreened;
    public bool MediaConfirmed=>Samples.Count>=3&&Samples.All(r=>YoutubeOnlyPolicy.Group(r,"video"));
    public bool FullyConfirmed=>Samples.Count>=3&&Samples.All(r=>r.ContentConfirmed==true);
    public long? PageMedianMs=>LocalStrategyAdvisor.Median(Samples.SelectMany(s=>s.Groups??[]).Where(g=>g.Id=="page"&&g.State=="confirmed"&&g.DurationMs.HasValue).Select(g=>g.DurationMs!.Value));
    public long? MedianMs=>Samples.Where(s=>s.LatencyMs.HasValue).Select(s=>s.LatencyMs!.Value).Order().Cast<long?>().ElementAtOrDefault(Samples.Count(s=>s.LatencyMs.HasValue)/2);
}
public static class YoutubeOnlySearch
{
    public static async Task<IReadOnlyList<YoutubeCandidate>> RunAsync(AdvisorStrategySpec baseline,
        Func<AdvisorStrategySpec,CancellationToken,Task<ServiceHealthResult>> trial,Action<string,int> progress,CancellationToken token,TimeSpan? budget=null,Func<AdvisorStrategySpec,string>? configurationKey=null)
    {
        using var work=CancellationTokenSource.CreateLinkedTokenSource(token);work.CancelAfter(budget??TimeSpan.FromSeconds(165));
        var measured=new List<YoutubeCandidate>();
        try
        {
            foreach(var spec in new[]{baseline}.Concat(YoutubeOnlyPolicy.Candidates(baseline)).DistinctBy(configurationKey??(s=>s.Key)))
            {progress("Короткая проверка только YouTube",measured.Count);measured.Add(new(Guid.NewGuid().ToString("N"),spec,[await trial(spec,work.Token)]));}
            var finalists=Rank(measured).Take(2).ToArray();
            foreach(var item in finalists)
            {
                var samples=new List<ServiceHealthResult>();
                for(var i=0;i<3;i++){progress($"Подтверждение YouTube: замер {i+1}/3",measured.Count);samples.Add(await trial(item.Strategy,work.Token));}
                measured.Remove(item);measured.Add(item with{Samples=samples});
            }
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){}
        token.ThrowIfCancellationRequested();
        return Rank(measured).ToArray();
    }
    private static IOrderedEnumerable<YoutubeCandidate> Rank(IEnumerable<YoutubeCandidate> list)=>list.OrderByDescending(r=>r.PageScreened).ThenByDescending(r=>r.Samples.All(s=>YoutubeOnlyPolicy.Group(s,"video"))).ThenByDescending(r=>r.FullyConfirmed).ThenBy(r=>r.MedianMs??long.MaxValue);
}
