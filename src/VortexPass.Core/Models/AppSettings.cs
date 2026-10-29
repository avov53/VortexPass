using VortexPass.Core.Services;
namespace VortexPass.Core.Models;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartProtectionOnStartup { get; set; }
    public string StartupConnectionId { get; set; } = string.Empty;
    public bool StartupProfileChoiceRequired { get; set; }
    public string StartupStrategy { get; set; } = "auto";
    public string LastSuccessfulStrategy { get; set; } = "medium";
    public int LastSuccessfulTuning { get; set; }
    public DateTimeOffset? LastSuccessfulStrategyUtc { get; set; }
    public string LastSuccessfulNetworkKey { get; set; } = string.Empty;
    public string SelectedDnsProvider { get; set; } = "auto";
    public string DnsMode { get; set; } = "google-udp";
    public bool NetworkDiagnosticDetails {get;set;}
    public string GoogleDnsAddress { get; set; } = "auto";
    public bool MinimizeToTray { get; set; } = true;
    public string Theme { get; set; } = "dark";
    public bool ReduceMotion { get; set; }
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public BypassProfileSettings Profiles { get; set; } = new();
    public string BypassLevel { get; set; } = "medium";
    public int BypassTuning { get; set; }
    public List<FavoriteProfileSettings> FavoriteProfiles { get; set; } = [];
    public List<SavedConnectionProfile> SavedConnections { get; set; } = [];
    public string SelectedConnectionId { get; set; } = string.Empty;
    public int ConnectionsMigrationVersion { get; set; }
    public List<AdvisorHistoryEntry> AdvisorHistory { get; set; } = [];
    public LocalAdvisorSettings LocalAdvisor { get; set; } = new();
    public int ProfileSettingsVersion { get; set; }
    public string CompatibilityMode { get; set; } = "standard";
    public List<NetworkCompatibilitySettings> NetworkCompatibilityProfiles { get; set; } = [];
    public GameFilterSettings GameFilter { get; set; } = new();
    public WarpSettings Warp { get; set; } = new();
    public List<string> CustomBypassDomains { get; set; } = [];
    public List<SiteRule> CurrentSiteRules {get;set;}=[];
    public string TelegramWebRoute {get;set;}="direct";
    public ConnectionWarpSnapshot? PendingConnectionWarpRestore {get;set;}
    public List<string> ExcludedDomains { get; set; } = DefaultDomainLists.CreateExclusions();
}

public sealed class FavoriteProfileSettings
{
    public string Level { get; set; } = "medium";
    public int Tuning { get; set; }
    public string Mode { get; set; } = "standard";
}

public sealed class AdvisorHistoryEntry
{
    public string ProviderKey { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string Level { get; set; } = "medium";
    public int Tuning { get; set; }
    public long Score { get; set; }
    public int Healthy { get; set; }
    public int Total { get; set; }
    public long? LatencyMs { get; set; }
    public DateTimeOffset LastTestUtc { get; set; }
}

public sealed class BypassProfileSettings
{
    public bool Custom { get; set; }
    public bool YouTube { get; set; }
    public bool Discord { get; set; }
    public bool Telegram { get; set; }
    public bool TelegramWeb { get; set; }
    public bool Web { get; set; }
    public bool GameFilter { get; set; }

    public bool AnyEnabled => YouTube || Discord || Telegram || TelegramWeb || Web || GameFilter || Custom;
}

public sealed class NetworkCompatibilitySettings
{
    public string NetworkKey { get; set; } = string.Empty;
    public string Mode { get; set; } = "standard";
    public string ProviderKey { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string YouTubeTransport { get; set; } = "auto";
    public int YouTubeHttp3Successes { get; set; }
    public int YouTubeHttp3Failures { get; set; }
    public string DiscordVoiceMode { get; set; } = "auto";
    public string DiscordVoiceResolvedMode { get; set; } = "direct";
    public string TelegramStrategy { get; set; } = "strong";
    public Dictionary<string, MobileServiceRouteState> ServiceRoutes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool MobileWarpFallbackOwned { get; set; }
    public bool MobileWarpWasConnected { get; set; }
    public Dictionary<string, string> TelegramRoutes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public MobileDnsState MobileDns { get; set; } = new();
    public string OptimizationState { get; set; } = "idle";
    public string OptimizationDetail { get; set; } = string.Empty;
    public DateTimeOffset? LastSuccessfulOptimizationUtc { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MobileServiceRouteState
{
    public string ServiceId { get; set; } = string.Empty;
    public string Strategy { get; set; } = "primary";
    public string LastSuccessfulStrategy { get; set; } = string.Empty;
    public string AppliedRoute { get; set; } = "winws";
    public string State { get; set; } = "idle";
    public string Detail { get; set; } = string.Empty;
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? LastConfirmedUtc { get; set; }
    public DateTimeOffset? LastCheckedUtc { get; set; }
}

public sealed class MobileDnsState
{
    public string NetworkKey { get; set; } = string.Empty;
    public string Resolver { get; set; } = string.Empty;
    public string State { get; set; } = "idle";
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset? LastCheckedUtc { get; set; }
}

public sealed class WarpSettings
{
    public string? TransportSelection { get; set; }
    public string TransportSelectionSource { get; set; } = "default";
    public WarpLegacyTransportBackup? LegacyTransportBackup { get; set; }
    public DateTimeOffset? PreparationCompletedUtc { get; set; }
    public bool SuppressGui { get; set; } = true;
    public bool AutomaticTransport { get; set; } = true;
    public List<WarpGuiStartupEntry> GuiStartupBackup { get; set; } = [];
    public List<WarpVerifiedVariant> VerifiedVariants { get; set; } = [];
    public bool Telegram { get; set; } = true;
    public bool Games { get; set; }
    public string RoutingMode { get; set; } = "smart";
    public string ProtocolPreference { get; set; } = "auto";
    public string MasquePreference { get; set; } = "h3-with-h2-fallback";
    public string DnsMode { get; set; } = "auto";
    public int ProxyPort { get; set; } = 41870;
    public List<WarpRouteProfile> RouteProfiles { get; set; } = [];
    public List<WarpNetworkOptimization> NetworkOptimizations { get; set; } = [];
    public List<string> OwnedSplitTunnelHosts { get; set; } = [];
    public List<string> OwnedSplitTunnelIps { get; set; } = [];
    public WarpPerformanceBaseline? PerformanceBaseline { get; set; }
    public bool RequestedConnected { get; set; }
    public string DetectedCliPath { get; set; } = string.Empty;
    public DateTimeOffset? LastCheckedUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
}

public sealed class WarpRouteProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новый профиль";
    public string Kind { get; set; } = "site";
    public string BuiltInId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Connection { get; set; } = "auto";
    public List<string> Domains { get; set; } = [];
    public List<string> IpRanges { get; set; } = [];
    public string ExecutablePath { get; set; } = string.Empty;
}

public sealed class WarpNetworkOptimization
{
    public string NetworkKey { get; set; } = string.Empty;
    public string SelectedProtocol { get; set; } = "MASQUE";
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CheckedAt { get; set; }
    public Dictionary<string, string> RouteDecisions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> ProtocolMedianMs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WarpPerformanceBaseline
{
    public string NetworkKey { get; set; } = string.Empty;
    public DateTimeOffset CheckedAt { get; set; }
    public Dictionary<string, long> MedianLatencyMs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> JitterMs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GameFilterSettings
{
    public string RobloxAssetsMode { get; set; } = "standard";
    public List<string> SelectedGames { get; set; } = [];
    public string Protocol { get; set; } = "all";
    public string IpsetMode { get; set; } = "any";

    public bool HasSelectedGames => SelectedGames.Count > 0;
}

public static class DefaultDomainLists
{
    private const string Exclusions = """
pusher.com
live-video.net
ttvnw.net
twitch.tv
mail.ru
citilink.ru
yandex.com
nvidia.com
donationalerts.com
vk.com
yandex.kz
mts.ru
multimc.org
ya.ru
dns-shop.ru
habr.com
3dnews.ru
microsoft.com
microsoftonline.com
live.com
minecraft.net
xboxlive.com
akamaitechnologies.com
msi.com
2ip.ru
yandex.ru
boosty.to
tanki.su
lesta.ru
korabli.su
tanksblitz.ru
reg.ru
epicgames.dev
epicgames.com
unrealengine.com
riotgames.com
riotcdn.net
leagueoflegends.com
playvalorant.com
marketplace.visualstudio.com
gallery.vsassets.io
gallerycdn.vsassets.io
gosuslugi.ru
gov.ru
nalog.ru
spb.ru
mos.ru
yandex.net
vk.ru
vk.me
vkvideo.ru
ok.ru
mycdn.me
okcdn.ru
odkl.ru
wb.ru
geobasket.ru
paywb.com
rwb.ru
wb-basket.ru
wbbasket.ru
wbpay.ru
wibes.ru
wildberries.ru
ozon.by
ozon.com
ozon.com.by
ozon.com.kz
ozon.kz
ozon.ru
ozon.tm
ozone.ru
ozonru.me
ozonusercontent.com
alfabank.ru
gazprombank.ru
gpb.ru
dbo-dengi.online
mtsdengi.ru
psbank.ru
bankline.ru
rosbank.ru
abr.ru
rshb.ru
sber.ru
sberbank.com
sberbank.ru
cdn-tinkoff.ru
tbank-online.com
tbank.ru
tochka-tech.com
tochka.com
vtb.ru
steamcommunity.com
curseforge.com
www.curseforge.com
api.curseforge.com
minecraft.curseforge.com
forgecdn.net
media.forgecdn.net
edge.forgecdn.net
mediafilez.forgecdn.net
overwolf.com
api.overwolf.com
content.overwolf.com
overwolfcdn.com
curseforge-overwolf-client-prod.s3.amazonaws.com
battle.net
blizzard.com
blizzard.net
cdn.blizzard.com
dist.blizzard.com
level3.blizzard.com
patch.battle.net
blz-contentstack.com
blzddist1-a.akamaihd.net
blznav.akamaized.net
akamaized.net
akamaihd.net
api.cloudflareclient.com
engage.cloudflareclient.com
consumer-masque-proxy.cloudflareclient.com
updates.cloudflareclient.com
warp.cloudflare.com
cloudflare-dns.com
one.one.one.one
""";

    public static List<string> CreateExclusions() => Exclusions
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static int MergeDefaultExclusions(List<string>? target)
    {
        if (target is null) return 0;
        var existing = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var domain in CreateExclusions())
        {
            if (!existing.Add(domain)) continue;
            target.Add(domain);
            added++;
        }

        target.Sort(StringComparer.OrdinalIgnoreCase);
        return added;
    }
}

