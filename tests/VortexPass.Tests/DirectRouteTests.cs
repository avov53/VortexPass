using VortexPass.Core.Models;
using VortexPass.Core.Services;

internal static class DirectRouteTests
{
    public static Task ScopedBlocks()
    {
        static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        var flags = new BypassProfileSettings { YouTube=true,Discord=true,TelegramWeb=true,GameFilter=true };
        var games = new GameFilterSettings { SelectedGames=["roblox","deadbydaylight","phasmophobia"] };
        var spec = new AdvisorStrategySpec { Level="medium" };
        foreach (var narrow in new[] { false,true })
        {
            var baseline=WinwsProfileComposer.Compose(flags,"medium",games,null,null,mobileCompatibilityMode:narrow,advisorStrategy:spec);
            foreach(var block in new[]{"flowseal-alt9","flowseal-general","flowseal-alt2"})
            {
                var changed=WinwsProfileComposer.Compose(flags,"medium",games,null,null,mobileCompatibilityMode:narrow,advisorStrategy:spec with{Telegram=block});
                foreach(var rule in baseline.Split(" --new",StringSplitOptions.TrimEntries).Where(r=>
                    !r.Contains("list-telegram.txt")&&!r.Contains("--hostlist-domains=kws.web.telegram.org")))
                    Require(changed.Contains(rule),"Telegram alternative changed another service, DNS, raw fallback or game rule");
                Require(!changed.Contains("1024-65535"),"Broad GameFilter imported");
                Require(changed.Split(" --new").Any(r=>r.Contains("list-telegram.txt")&&r.Contains("--dpi-desync="+(block=="flowseal-alt9"?"hostfakesplit":"multisplit"))),"Direct Telegram block missing");
            }
        }
        var general=WinwsProfileComposer.FlowsealTcpBlock("flowseal-general","medium");
        Require(general.Contains("--dpi-desync-split-seqovl=568")&&general.Contains("tls_clienthello_4pda_to.bin"),"General TCP layout differs from pinned release");
        var google=WinwsProfileComposer.FlowsealTcpBlock("flowseal-general","medium",true);
        Require(google.Contains("--ip-id=zero")&&google.Contains("--dpi-desync-split-seqovl=681"),"Google-specific rule lost");
        var alt=WinwsProfileComposer.FlowsealTcpBlock("flowseal-alt2","medium");
        Require(alt.Contains("--dpi-desync-split-pos=2")&&alt.Contains("--dpi-desync-split-seqovl=652"),"ALT2 layout differs from pinned release");
        var alt9=WinwsProfileComposer.FlowsealTcpBlock("flowseal-alt9","medium");
        Require(alt9.Contains("--dpi-desync=hostfakesplit")&&alt9.Contains("--dpi-desync-fooling=ts,md5sig")&&alt9.Contains("host=ozon.ru"),"ALT9 ordinary TCP rule changed");
        var alt9Google=WinwsProfileComposer.FlowsealTcpBlock("flowseal-alt9","medium",true);
        Require(alt9Google.Contains("host=www.google.com")&&!alt9Google.Contains("md5sig"),"ALT9 Google rule changed");
        foreach(var level in AdvisorStrategyPolicy.Levels.Skip(1))
        foreach(var block in AdvisorStrategyPolicy.Blocks(level).Where(b=>b.StartsWith("flowseal-")))
        {
            var command=WinwsProfileComposer.Compose(flags,level,games,null,null,advisorStrategy:new(){Level=level,YouTube=block,Discord=block,Telegram=block});
            foreach(var rule in command.Split(" --new").Where(r=>r.Contains("list-telegram.txt")||r.Contains("list-discord.txt")||r.Contains("list-youtube.txt")))
            {
                foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(rule,@"--dpi-desync-split-seqovl=(\d+)"))
                    Require(int.Parse(m.Groups[1].Value)<=AdvisorStrategyPolicy.MaxOverlap(level),"Overlap exceeds selected level");
                if(rule.Contains("--filter-tcp="))
                    foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(rule,@"--dpi-desync-repeats=(\d+)"))
                        Require(int.Parse(m.Groups[1].Value)<=AdvisorStrategyPolicy.MaxTcpRepeats(level),"Repeats exceed selected level");
            }
        }
        Require(WinwsConflictPolicy.ForeignPids([10,20,30],[10,20]).SequenceEqual(new[]{30}),"Foreign winws not detected");
        Require(WinwsConflictPolicy.ForeignPids([10,20],[10,20]).Length==0,"Owned bootstrap falsely conflicts");
        Require(!AdvisorStrategyPolicy.CanReuseStored(spec,"medium","T2-network","T2-network",6),"Old measurements were reused without verification");
        Require(AdvisorStrategyPolicy.CanReuseStored(spec,"medium","T2-network","T2-network",RouteProbePolicy.Version),"Current verified route cannot be reused");
        Require(!AdvisorStrategyPolicy.CanReuseStored(spec,"medium","T2-network","home-network",6),"Cached route crossed networks");
        Require(!AdvisorStrategyPolicy.CanReuseStored(spec with{Overlap=9999},"medium","network","network",6),"Unsafe stored configuration reused");
        return Task.CompletedTask;
    }
}
