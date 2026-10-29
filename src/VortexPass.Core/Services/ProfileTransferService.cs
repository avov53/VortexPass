using System.Text;
using System.Text.Json;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public static class ProfileTransferService
{
    public const int MaxBytes = 64 * 1024;
    public const string Prefix = "vortexpass-profile:";
    public static string Export(SavedConnectionProfile p) => JsonSerializer.Serialize(new
    {
        format = "vortexpass-profile", version = 5, id = p.Id, name = p.Name, services = p.Services.Select(s=>s=="gamefilter"?"games":s),
        games = p.Games.Select(g=>g switch { "roblox"=>"Roblox", "phasmophobia"=>"Phasmophobia", "deadbydaylight"=>"Dead by Daylight", _=>g }), domains = p.Domains, siteRules = p.SiteRules,
        level = Array.IndexOf(AdvisorStrategyPolicy.Levels, p.Level) + 1, tuning = p.Tuning,
        telegramWebRoute = p.TelegramWebRoute, robloxAssetsMode = p.RobloxAssetsMode, provider = p.Provider, updatedAt = p.UpdatedAt
    }, new JsonSerializerOptions { WriteIndented = true });
    public static string Link(string json) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static SavedConnectionProfile Parse(string text)
    {
        if (text.Length > MaxBytes * 2) throw new ArgumentException("Размер профиля превышает 64 КБ.");
        text = text.Trim();
        if (text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var encoded = text[Prefix.Length..];
            if (encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("Код-ссылка повреждена.");
            encoded = encoded.Replace('-','+').Replace('_','/');
            try { text = new UTF8Encoding(false,true).GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length+3)/4*4,'='))); }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { throw new ArgumentException("Код-ссылка повреждена."); }
        }
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new ArgumentException("Размер профиля превышает 64 КБ.");
        try
        {
            using var doc = JsonDocument.Parse(text); var root = doc.RootElement;
            List<string> Strings(string field)
            {
                var value = root.GetProperty(field);
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 500 || value.EnumerateArray().Any(v=>v.ValueKind != JsonValueKind.String))
                    throw new ArgumentException("Профиль содержит неверный список настроек.");
                return value.EnumerateArray().Select(v=>v.GetString()!).ToList();
            }
            string[] allowed = ["format","version","id","name","services","games","domains","siteRules","level","tuning","provider","updatedAt","robloxAssetsMode","telegramWebRoute"];
            if (root.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new ArgumentException("Профиль содержит неподдерживаемые поля. Команды и сетевой кэш не импортируются.");
            if (root.GetProperty("format").GetString() != "vortexpass-profile" || root.GetProperty("version").GetInt32() is not (1 or 2 or 3 or 4 or 5))
                throw new ArgumentException("Неизвестный формат профиля VortexPass.");
            var siteRules=root.TryGetProperty("siteRules",out var rules)
                ? rules.Deserialize<List<SiteRule>>(new JsonSerializerOptions {PropertyNameCaseInsensitive=true}) ?? [] : [];
            siteRules=SiteRulePolicy.Validate(siteRules).Select(rule=>rule with {Id=Guid.NewGuid().ToString()}).ToList();
            var level = root.GetProperty("level");
            var levelName = level.ValueKind == JsonValueKind.Number && level.GetInt32() is >=1 and <=5 ? AdvisorStrategyPolicy.Levels[level.GetInt32()-1] : level.GetString()!;
            var profile = new SavedConnectionProfile { Id=Guid.NewGuid().ToString(), Name=root.GetProperty("name").GetString()!, Level=levelName,
                Services=Strings("services").Select(s=>s == "games" ? "gamefilter" : s).ToList(),
                Games=Strings("games").Select(g=>g switch { "Roblox"=>"roblox", "Phasmophobia"=>"phasmophobia", "Dead by Daylight"=>"deadbydaylight", _=>g }).ToList(),
                Domains=Strings("domains"),
                RobloxAssetsMode=root.GetProperty("version").GetInt32()>=4&&root.TryGetProperty("robloxAssetsMode",out var assetsMode)?assetsMode.GetString()??"standard":"standard",
                TelegramWebRoute=root.GetProperty("version").GetInt32()>=5&&root.TryGetProperty("telegramWebRoute",out var webRoute)?webRoute.GetString()??"direct":"direct",
                Tuning=root.TryGetProperty("tuning",out var tuning) ? tuning.GetInt32() : 0,
                Provider=root.TryGetProperty("provider",out var provider) ? provider.GetString() ?? "" : "",
                FollowNetworkMode=false, RouteNeedsCheck=true,SiteRules=siteRules };
            if (!string.IsNullOrWhiteSpace(profile.Provider)) profile.Provider=AdvisorStrategyPolicy.NormalizeProvider(profile.Provider);
            return SavedConnectionProfilePolicy.Validate(profile);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new ArgumentException("Не удалось прочитать профиль. Проверьте формат и состав настроек."); }
    }
}
