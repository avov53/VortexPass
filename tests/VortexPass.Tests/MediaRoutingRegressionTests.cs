using System.Net;
using System.Net.Sockets;
using System.Text;
using VortexPass.Core.Services;
using VortexPass.Core.Models;

internal static class MediaRoutingRegressionTests
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    public static async Task Run()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var pac=new WarpPacService();
        var url=await pac.StartAsync(TelegramWebPolicy.Domains,41870,0,deadline.Token);
        using(var browser=new TcpClient())
        {
            await browser.ConnectAsync(IPAddress.Loopback,url.Port,deadline.Token);
            // No unsolicited response; fragmented browser headers must be read.
            await Task.Delay(60,deadline.Token);
            Check(!browser.Client.Poll(0,SelectMode.SelectRead),"PAC was sent and socket closed before the browser request");
            var stream=browser.GetStream();
            await stream.WriteAsync("GET /vortexpass.pac HTTP/1.1\r\nHost: localhost\r\n"u8.ToArray(),deadline.Token);
            Check(!browser.Client.Poll(0,SelectMode.SelectRead),"PAC server ignored incomplete request headers");
            await stream.WriteAsync("\r\n"u8.ToArray(),deadline.Token);
            using var reader=new StreamReader(stream);
            var response=await reader.ReadToEndAsync(deadline.Token);
            Check(response.StartsWith("HTTP/1.1 200 OK")&&response.Contains("FindProxyForURL")&&response.Contains("Cache-Control: no-store"),"Browser did not receive a complete PAC");
        }
        await pac.VerifyPacAsync(deadline.Token);
        foreach(var host in new[]{"weba.telegram.org","cdn-telegram.org","kws5-1.web.telegram.org","flora-1.web.telegram.org"})
            Check(WarpHttpProxy.Allows(host,TelegramWebPolicy.Domains),"Telegram dependency bypasses WARP: "+host);
        Check(!WarpHttpProxy.Allows("telegram.org.evil.example",TelegramWebPolicy.Domains)&&!WarpHttpProxy.Allows("discord.com",TelegramWebPolicy.Domains),"Telegram routing includes unrelated hosts");

        var command=WinwsProfileComposer.Compose(new(){YouTube=true,Discord=true,GameFilter=true},"medium",new(){SelectedGames=["roblox"]},null,null);
        var rules=command.Split(" --new");
        foreach(var host in new[]{"ggpht.com","rbxcdn.com","thumbnails.roblox.com"})
        {
            var rule=rules.First(r=>r.Contains("--filter-tcp=443 --hostlist-domains="+host));
            Check(rule.Contains("--dpi-desync=multisplit")&&!rule.Contains("seqovl")&&!rule.Contains("--dpi-desync-fake-tls")&&rule.Contains("cutoff=n3"),"CDN default injects overlapping/fake TLS: "+host);
        }
        var discord=WinwsProfileComposer.Compose(new(){Discord=true},"medium",new(),null,null);
        Check(string.Join("|",rules.Where(r=>r.Contains("discord")))==string.Join("|",discord.Split(" --new").Where(r=>r.Contains("discord"))),"Discord rules changed with media services");
        var original=new AdvisorStrategySpec{YouTube="youtube-tls"};
        ServiceHealthResult Evidence(bool avatars)=>YoutubeRouteProbe.Evaluate([new("page","confirmed","",400),new("api","confirmed",""),new("images","confirmed",""),new("video","confirmed",""),new("avatars",avatars?"confirmed":"unconfirmed","")],20);
        Check(!Evidence(false).ContentConfirmed!.Value&&Evidence(true).ContentConfirmed==true,"Thumbnail/video success concealed missing channel avatars");
        var baseline=new YoutubeCandidate("base",original,Enumerable.Repeat(Evidence(true),3).ToArray());
        var failed=new YoutubeCandidate("bad",original with {YouTube="minimal"},Enumerable.Repeat(Evidence(false),3).ToArray());
        Check(!YoutubePersonalizedSearch.Improves(baseline,failed),"Faster page replaced working avatars");
        var attempts=new List<string>();
        var results=await YoutubePersonalizedSearch.RunAsync(original with{YouTube="baseline"},(spec,_)=>
        {
            attempts.Add(spec.YouTube);return Task.FromResult(Evidence(spec.YouTube=="youtube-tls"));
        },(_,_)=>Task.FromResult<IReadOnlyList<AdvisorStrategySpec>>([]),(_,_)=>{},deadline.Token);
        Check(attempts.Contains("youtube-tls")&&results[0].Strategy.YouTube=="youtube-tls"&&results[0].FullyConfirmed,
            "Working page hid failed channel avatars and suppressed reserve testing");
    }
}
