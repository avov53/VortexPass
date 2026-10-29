using System.Text.Json;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;
public static class SavedConnectionProfilePolicy
{
    private static readonly string[] Services = ["youtube", "discord", "telegram", "telegramweb", "telegramdesktop", "web", "gamefilter"];
    private static readonly string[] Games = ["roblox", "phasmophobia", "deadbydaylight"];
    public static SavedConnectionProfile Validate(SavedConnectionProfile profile)
    {
        var copy = Clone(profile);
        copy.Name ??= string.Empty; copy.Services ??= []; copy.Games ??= []; copy.Domains ??= [];
        if(copy.Services.Any(s=>s is null)||copy.Games.Any(s=>s is null)||copy.Domains.Any(s=>s is null))throw new ArgumentException("Профиль содержит пустые элементы списков.");
        copy.SiteRules=SiteRulePolicy.Validate(copy.SiteRules);
        if(copy.RobloxAssetsMode is not ("auto" or "standard" or "enhanced"))throw new ArgumentException("Неизвестный режим контента Roblox.");
        if(copy.TelegramWebRoute is not ("direct" or "warpProxy"))throw new ArgumentException("Неизвестный маршрут Telegram Web.");
        if (!Guid.TryParse(copy.Id, out _)) throw new ArgumentException("Некорректный ID профиля.");
        copy.Name = copy.Name.Trim();
        if (copy.Name.Length is < 1 or > 80 || copy.Name.Any(char.IsControl)) throw new ArgumentException("Название должно содержать от 1 до 80 символов.");
        if (copy.Services.Any(s => !Services.Contains(s)) || copy.Games.Any(g => !Games.Contains(g)))
            throw new ArgumentException("Профиль содержит неподдерживаемый сервис или игру.");
        copy.Services = copy.Services.Distinct().ToList(); copy.Games = copy.Games.Distinct().ToList();
        if (copy.Domains.Count > 500) throw new ArgumentException("Допускается до 500 доменов.");
        copy.Domains = UserDomainListService.Normalize(copy.Domains.Select(s => s.StartsWith("*.") ? s[2..] : s)).ToList();
        if (copy.Services.Count == 0 && copy.Games.Count == 0 && copy.Domains.Count == 0 && copy.SiteRules.Count==0)
            throw new ArgumentException("Выберите сервис, игру или добавьте домен.");
        if (!AdvisorStrategyPolicy.Levels.Contains(copy.Level) || copy.Tuning is < 0 or > 3 || copy.Mode is not ("mobile" or "standard"))
            throw new ArgumentException("Некорректный уровень или режим профиля.");
        if (copy.Services.Contains("gamefilter") && copy.Games.Count == 0) throw new ArgumentException("Выберите игры для Game Filter.");
        if (copy.Strategy is not null) AdvisorStrategyPolicy.Validate(copy.Strategy, copy.Level);
        return copy;
    }
    public static SavedConnectionProfile Clone(SavedConnectionProfile p) => JsonSerializer.Deserialize<SavedConnectionProfile>(JsonSerializer.Serialize(p))!;
    public static BypassProfileSettings ServiceFlags(SavedConnectionProfile p) => new()
    {
        YouTube = p.Services.Contains("youtube"), Discord = p.Services.Contains("discord"),
        Telegram = p.Services.Contains("telegram") || p.Services.Contains("telegramdesktop"),
        TelegramWeb = p.Services.Contains("telegram") || p.Services.Contains("telegramweb"),
        Web = p.Services.Contains("web"), GameFilter = p.Games.Count > 0 || p.Services.Contains("gamefilter"),
        Custom = p.Domains.Count > 0 || p.SiteRules.Count>0
    };
    public static List<string> SelectedServices(BypassProfileSettings p) => new[]
    {
        p.YouTube ? "youtube" : null, p.Discord ? "discord" : null,
        p.Telegram && p.TelegramWeb ? "telegram" : p.Telegram ? "telegramdesktop" : p.TelegramWeb ? "telegramweb" : null,
        p.Web ? "web" : null, p.GameFilter ? "gamefilter" : null
    }.Where(s => s is not null).Cast<string>().ToList();
}
