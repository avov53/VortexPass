using System.Text.RegularExpressions;

namespace VortexPass.Core.Services;

public static class FirefoxDnsPolicy
{
    public static bool UsesStrictDoH(string preferences) => Regex.IsMatch(preferences,
        "user_pref\\(\\s*\"network\\.trr\\.mode\"\\s*,\\s*3\\s*\\)");

    // Read only: an inactive profile may also have this setting, so never claim
    // this proves which DNS the current browser tab uses.
    public static string? ReadHint()
    {
        try
        {
            var profiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(profiles) && Directory.EnumerateDirectories(profiles).Any(dir =>
                new[] { "prefs.js", "user.js" }.Any(file => File.Exists(Path.Combine(dir, file)) && UsesStrictDoH(File.ReadAllText(Path.Combine(dir, file))))))
                return "В одном из профилей Firefox задан строгий DNS-over-HTTPS. Если это текущий профиль и его resolver недоступен, Windows DNS не заменит его. Проверьте настройки DNS Firefox; приложение их не изменяет.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
