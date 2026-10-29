using VortexPass.Core.Models;

namespace VortexPass.Core.Services;
public static class TelegramWebPolicy
{
    public static readonly string[] Domains=["web.telegram.org","webk.telegram.org","webz.telegram.org","telegram-cdn.org","telegramusercontent.com"];
    public static List<SiteRule> Rules(IEnumerable<SiteRule> rules,string route,bool selected)=>
        rules.Concat(route=="warpProxy"&&selected?[new SiteRule {Id="00000000-0000-0000-0000-000000000010",Name="Telegram Web",Domain=Domains[0],ResourceDomains=Domains.Skip(1).ToList(),Connection="warpProxy"}]:[]).ToList();
}
