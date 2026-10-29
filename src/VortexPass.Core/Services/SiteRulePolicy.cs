using VortexPass.Core.Models;
namespace VortexPass.Core.Services;
public static class SiteRulePolicy
{
    public static List<SiteRule> Validate(IEnumerable<SiteRule>? input)
    {
        var rules=(input??[]).ToArray();if(rules.Length>50)throw new ArgumentException("Допускается до 50 правил сайтов.");
        var result=new List<SiteRule>();
        foreach(var r in rules)
        {
            if(r is null||!Guid.TryParse(r.Id,out _)||result.Any(v=>v.Id==r.Id)||string.IsNullOrWhiteSpace(r.Name)||r.Name.Trim().Length>80||r.Name.Any(char.IsControl))throw new ArgumentException("Проверьте название и ID правила.");
            if(r.Components is null||r.Components.Count==0||r.Components.Any(c=>c is not ("page" or "images" or "video" or "voice" or "game")))throw new ArgumentException("Выберите поддерживаемые компоненты сайта.");
            if(r.Protocol is not ("auto" or "tcp" or "quic" or "udp")||r.Connection is not ("auto" or "direct" or "warpProxy" or "warpSystem"))throw new ArgumentException("Неверный протокол или способ подключения.");
            if(r.ResourceDomains?.Any(s=>s is null)==true)throw new ArgumentException("Список ресурсов содержит пустой элемент.");
            if(r.Protocol=="udp")throw new ArgumentException("Произвольный UDP по домену не поддерживается. Используйте системный туннель или поддерживаемый игровой профиль.");
            if(r.Connection=="warpProxy"&&(r.Protocol=="quic"||r.Components.Any(c=>c is "voice" or "game")))throw new ArgumentException("WARP-прокси обслуживает только Web/TCP; UDP, QUIC, голос и игры не поддерживаются.");
            if(r.Connection=="warpProxy"&&!string.IsNullOrWhiteSpace(r.Ports))throw new ArgumentException("WARP PAC выбирает домен Web-приложения; отдельные порты для прокси задавать нельзя.");
            var domain=Domain(r.Domain);var resources=UserDomainListService.Normalize((r.ResourceDomains??[]).Where(s=>!string.IsNullOrWhiteSpace(s)).Select(s=>s.StartsWith("*.")?s[2..]:s)).ToList();
            if(resources.Count>100)throw new ArgumentException("В одном правиле допускается до 100 доменов ресурсов.");
            var ports=Ports(r.Ports);if(r.Protocol=="quic"&&ports.Any(p=>p!=443))throw new ArgumentException("Проверяемый QUIC поддерживается только на порту 443.");
            result.Add(r with {Name=r.Name.Trim(),Domain=domain,ResourceDomains=resources,Components=r.Components.Distinct().ToList(),Ports=string.Join(',',ports)});
        }
        var owners=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var r in result)foreach(var domain in Domains(r))
        {
            if(owners.TryGetValue(domain,out var connection)&&connection!=r.Connection)throw new ArgumentException("Один домен указан с разными способами подключения.");
            foreach(var (existing,route) in owners)
                if(route!=r.Connection&&(domain.EndsWith('.'+existing,StringComparison.OrdinalIgnoreCase)||existing.EndsWith('.'+domain,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Пересекающиеся домены имеют конфликтующие способы подключения.");
            owners[domain]=r.Connection;
        }
        if(owners.Count>500)throw new ArgumentException("Профиль содержит больше 500 доменов правил.");
        return result;
    }
    public static string Domain(string value)=>!string.IsNullOrWhiteSpace(value)?UserDomainListService.Normalize([value.Trim().StartsWith("*.")?value.Trim()[2..]:value.Trim()]).Single():throw new ArgumentException("Укажите основной домен правила.");
    public static IEnumerable<string> Domains(SiteRule rule)=>new[]{rule.Domain}.Concat(rule.ResourceDomains).Distinct();
    public static int[] Ports(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return [];
        var ports=new SortedSet<int>();
        foreach(var entry in value.Split(','))
        {
            var range=entry.Trim().Split('-');
            if(range.Length>2||!int.TryParse(range[0],out var first)||first<1||first>65535)throw new ArgumentException("Порты должны быть от 1 до 65535.");
            var last=first;if(range.Length==2&&(!int.TryParse(range[1],out last)||last<first||last>65535))throw new ArgumentException("Неверный диапазон портов.");
            if(last-first>128)throw new ArgumentException("Диапазон слишком широк. Укажите узкие порты сервиса.");
            for(var port=first;port<=last;port++)ports.Add(port);
            if(ports.Count>128)throw new ArgumentException("В правиле допускается до 128 портов.");
        }
        return ports.ToArray();
    }
}
