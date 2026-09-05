namespace VortexPass.Core.Models;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartProtectionOnStartup { get; set; }
    public string StartupStrategy { get; set; } = "auto";
    public string SelectedDnsProvider { get; set; } = "auto";
    public bool MinimizeToTray { get; set; } = true;
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public BypassProfileSettings Profiles { get; set; } = new();
    public string BypassLevel { get; set; } = "medium";
    public int BypassTuning { get; set; }
    public List<FavoriteProfileSettings> FavoriteProfiles { get; set; } = [];
    public List<AdvisorHistoryEntry> AdvisorHistory { get; set; } = [];
    public int ProfileSettingsVersion { get; set; }
    public GameFilterSettings GameFilter { get; set; } = new();
    public List<string> CustomBypassDomains { get; set; } = [];
    public List<string> ExcludedDomains { get; set; } = DefaultDomainLists.CreateExclusions();
}

public sealed class FavoriteProfileSettings
{
    public string Level { get; set; } = "medium";
    public int Tuning { get; set; }
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
    public bool YouTube { get; set; }
    public bool Discord { get; set; }
    public bool Telegram { get; set; }
    public bool Web { get; set; }
    public bool GameFilter { get; set; }

    public bool AnyEnabled => YouTube || Discord || Telegram || Web || GameFilter;
}

public sealed class GameFilterSettings
{
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
