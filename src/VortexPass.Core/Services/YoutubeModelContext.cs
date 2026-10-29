using VortexPass.Core.Models;
namespace VortexPass.Core.Services;
public static class YoutubeModelContext
{
    // Categories and timings only: never pass raw host/IP/URL strings from diagnostics to the model.
    public static object Summarize(AdvisorServiceEvidence? evidence)
    {
        string State(string id)=>evidence?.Groups?.FirstOrDefault(g=>g.Id==id)?.State??"unknown";
        string Transport(string prefix)=>evidence?.Groups?.Any(g=>g.Id.StartsWith(prefix,StringComparison.Ordinal)&&g.State=="confirmed")==true?"confirmed":"unconfirmed";
        var text=string.Join(' ',evidence?.Groups?.Where(g=>g.Id.StartsWith("tls-",StringComparison.Ordinal)).Select(g=>g.Detail)??[]);
        var failure=text.Contains("расшифр",StringComparison.OrdinalIgnoreCase)||text.Contains("BAD_MAC",StringComparison.OrdinalIgnoreCase)?"integrity":text.Contains("cancel",StringComparison.OrdinalIgnoreCase)||text.Contains("timeout",StringComparison.OrdinalIgnoreCase)?"timeout":"unknown";
        return new{dns=State("dns"),ipv4Tcp=Transport("tcp-IPv4"),ipv6Tcp=Transport("tcp-IPv6"),tls=Transport("tls-"),tlsFailure=failure,page=State("page"),images=State("images"),api=State("api"),video=State("video"),pageProbeMs=evidence?.Groups?.FirstOrDefault(g=>g.Id=="page")?.DurationMs,tlsMs=LocalStrategyAdvisor.Median(evidence?.Groups?.Where(g=>g.Id.StartsWith("tls-",StringComparison.Ordinal)&&g.DurationMs.HasValue).Select(g=>g.DurationMs!.Value)??[]),evidence?.LatencyMs,evidence?.JitterMs,evidence?.Samples};
    }
}
