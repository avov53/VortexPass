using System.Text.Json.Serialization;

namespace VortexPass.Core.Models;

public sealed class LocalAdvisorSettings
{
    public string Provider { get; set; } = string.Empty;
    public AdvisorStrategySpec? AppliedStrategy { get; set; }
    public string AppliedNetworkKey { get; set; } = string.Empty;
    public string AppliedMode { get; set; } = "standard";
    public int AppliedProbeVersion { get; set; }
    public List<AdvisorRunHistory> History { get; set; } = [];
    public List<AdvisorRunHistory> YoutubeHistory { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdvisorStrategySpec
{
    public string Level { get; init; } = "medium";
    public int Tuning { get; init; }
    public string YouTube { get; init; } = "baseline";
    public string Discord { get; init; } = "baseline";
    public string Roblox { get; init; } = "baseline";
    public string Telegram { get; init; } = "baseline";
    public string RouteTemplate { get; init; } = "inherit";
    public string YouTubeTransport { get; init; } = "inherit";
    public string DiscordVoice { get; init; } = "inherit";
    public int? TcpRepeats { get; init; }
    public int? QuicRepeats { get; init; }
    public int? Overlap { get; init; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public YoutubeParameters? YouTubeParameters { get; init; }

    [JsonIgnore] public bool Experimental => TcpRepeats.HasValue || QuicRepeats.HasValue || Overlap.HasValue;
    [JsonIgnore] public string Key => $"{Level}:{Tuning}:{YouTube}:{Discord}:{Telegram}:{Roblox}:{RouteTemplate}:{YouTubeTransport}:{DiscordVoice}:{TcpRepeats}:{QuicRepeats}:{Overlap}" + (YouTubeParameters is null?"":":ytp:"+YouTubeParameters.Key);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record YoutubeParameters(string TcpTechnique="split",string SplitPattern="firstAndSni",int FakeRepeats=0,int QuicRepeats=2)
{
    [JsonIgnore] public string Key=>$"{TcpTechnique}/{SplitPattern}/{FakeRepeats}/{QuicRepeats}";
}

public sealed record AdvisorRequest(string Level, string Provider, string NetworkKey, string Mode,
    IReadOnlyList<string> Services, int PreferredTuning = 0, string RobloxAssetsMode = "standard", string TelegramWebRoute="direct", string SearchMode="normal",string Scope="all",bool PreserveNonYoutube=false);
public sealed record AdvisorServiceEvidence(string Service, string State, bool Confirmed, string Detail,
    long? LatencyMs, long? JitterMs, int Samples, IReadOnlyList<VortexPass.Core.Services.ServiceRouteGroup>? Groups = null,
    bool? SystemDnsReady = null);
public sealed record AdvisorRecommendation(string Id, AdvisorStrategySpec Strategy, string Provider,
    int Confirmed, int Total, bool FullyConfirmed, long? MedianLatencyMs, long? JitterMs,
    string Reason, IReadOnlyList<AdvisorServiceEvidence> Services, int ProbeVersion = 0,string? WarpProofKey=null,int YoutubeProbeVersion=0,int YoutubeSearchVersion=0,string? YoutubeRuleSignature=null);
public sealed record AdvisorRunHistory(string NetworkKey, string Provider, string Mode, string Level,
    DateTimeOffset CheckedAt, IReadOnlyList<AdvisorRecommendation> Recommendations,string TelegramWebRoute="direct");
public sealed record AdvisorProgress(string State, string Message, int Completed = 0, int Total = 0, int Phase=3);
public sealed record LocalModelStatus(bool Ready, string Name, string Detail);
