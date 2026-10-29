namespace VortexPass.Core.Models;

public sealed class SavedConnectionProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RobloxAssetsMode { get; set; } = "standard";
    public string TelegramWebRoute { get; set; } = "direct";
    public string Provider { get; set; } = string.Empty;
    public List<string> Services { get; set; } = [];
    public List<string> Games { get; set; } = [];
    public List<string> Domains { get; set; } = [];
    public List<SiteRule> SiteRules {get;set;}=[];
    public string Level { get; set; } = "medium";
    public int Tuning { get; set; }
    public string Mode { get; set; } = "standard";
    public bool FollowNetworkMode { get; set; } = true;
    public bool Favorite { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public AdvisorStrategySpec? Strategy { get; set; }
    public int ProbeVersion { get; set; }
    public bool RouteNeedsCheck { get; set; }
    public string StrategyNetworkKey { get; set; } = string.Empty;
    public string? WarpProofKey { get; set; }
    public YoutubeRouteVerification? YoutubeVerification {get;set;}
    public Dictionary<string, SavedConnectionRoute> NetworkRoutes { get; set; } = [];
}

public sealed record YoutubeRouteVerification(int Version,string NetworkKey,string StrategyKey,string RuleSignature,DateTimeOffset CheckedAt,bool PageConfirmed,bool MediaConfirmed,bool BrowserConfirmed=false,int SearchVersion=0);
public sealed record SavedConnectionRoute(AdvisorStrategySpec Strategy, string Provider, int ProbeVersion, DateTimeOffset ConfirmedAt,string? WarpProofKey=null,YoutubeRouteVerification? YoutubeVerification=null);
