using System.Text.RegularExpressions;
namespace VortexPass.Core.Services;
public static class FirefoxNetworkDiagnostics
{
    public static string Read(string? profilesDirectory=null)
    {
        profilesDirectory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Mozilla","Firefox","Profiles");
        if(!Directory.Exists(profilesDirectory))return "Профиль Firefox не найден. Проверьте настройки сети в браузере.";
        var keys=new[]{"network.trr.mode","network.proxy.type","network.http.http3.enable","network.dns.echconfig.enabled","network.dns.http3_echconfig.enabled"};
        var output=new List<string>();
        foreach(var folder in Directory.EnumerateDirectories(profilesDirectory).Take(5))
        {
            try
            {
                var file=Path.Combine(folder,"prefs.js");if(!File.Exists(file))continue;
                var values=new Dictionary<string,string>();
                foreach(var line in File.ReadLines(file))
                {
                    var match=Regex.Match(line,"^user_pref\\(\"([^\"]+)\",\\s*(true|false|[0-9]+)\\);$");
                    if(match.Success&&keys.Contains(match.Groups[1].Value))values[match.Groups[1].Value]=match.Groups[2].Value;
                }
                output.Add("Профиль "+(output.Count+1)+": "+string.Join("; ",keys.Select(k=>k+"="+values.GetValueOrDefault(k,"не задано явно"))));
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){output.Add("Сохранённые сетевые настройки профиля недоступны");}
        }
        return string.Join(" | ",output)+". Читаются только сохранённые сетевые параметры prefs.js, не cookies и авторизация. Не заданное значение не доказывает фактический режим DNS/ECH/HTTP3; его нужно сверить в Firefox. Настройки не менялись.";
    }
}
