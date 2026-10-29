using System.Net;
using VortexPass.Core.Models;
using VortexPass.Core.Services;
internal static class RouteRecoveryTests
{
    private static void Check(bool value, string detail) { if (!value) throw new Exception(detail); }
    public static Task TelegramCandidates()
    {
        var bad = new TelegramRouteCandidate("web.telegram.org", IPAddress.Parse("149.154.167.99"), false, "web", "TLS timeout", 1);
        var fast = bad with { Address = IPAddress.Parse("149.154.167.233"), Confirmed = true, LatencyMs = 40 };
        var slow = fast with { Address = IPAddress.Parse("149.154.167.220"), LatencyMs = 200 };
        var result = TelegramRouteProbeService.SelectConfirmed([bad, slow, fast]);
        Check(result.Count == 1 && result[0].Address.Equals(fast.Address), "Unconfirmed DNS-only or duplicate host pinned");
        Check(TelegramRouteProbeService.SelectConfirmed([bad]).Count == 0, "Unreachable IP survived");
        Check(TelegramRouteProbeService.IsWebSocketHost("kws2.web.telegram.org"), "WebSocket hostname not recognized");
        Check(TelegramRouteProbeService.IsWebSocketHost("flora-1.web.telegram.org"), "Named DC media treated as HTML");
        Check(!TelegramRouteProbeService.IsWebSocketHost("kws2.web.telegram.org.attacker.example"), "Untrusted suffix treated as Telegram");
        Check(TelegramEndpointRegistry.TransportHosts.Length == 30 && TelegramEndpointRegistry.MediaHosts.Contains("kws5-1.web.telegram.org"), "DC5 media omitted");
        Check(TelegramEndpointRegistry.AllDataCentersReachable(Enumerable.Range(0,15).Select(i=>i%3==0).ToArray()), "Alternative per DC rejected");
        Check(!TelegramEndpointRegistry.AllDataCentersReachable(Enumerable.Range(0,15).Select(i=>i<12).ToArray()), "One missing DC declared healthy");
        Check(FirefoxDnsPolicy.UsesStrictDoH("user_pref(\"network.trr.mode\", 3);"), "Strict Firefox DoH not recognized");
        Check(!FirefoxDnsPolicy.UsesStrictDoH("user_pref(\"network.trr.mode\", 2);"), "Automatic Firefox DoH flagged strict");
        Check(RouteProbePolicy.RequiredDnsHosts.Contains("api.devices.cloudflare.com") && RouteProbePolicy.RequiredDnsHosts.Contains("updates.discord.com"), "DNS controls incomplete");
        var flags = new BypassProfileSettings { YouTube = true, Discord = true, TelegramWeb = true, GameFilter = true };
        var games = new GameFilterSettings { SelectedGames = ["roblox", "deadbydaylight", "phasmophobia"] };
        string OtherRules(AdvisorStrategySpec spec) => string.Join(" --new", WinwsProfileComposer.Compose(flags, "medium", games, null, null,
            advisorStrategy: spec).Split(" --new").Where(rule=>!rule.Contains("telegram", StringComparison.OrdinalIgnoreCase) && !rule.Contains("kws", StringComparison.OrdinalIgnoreCase)));
        Check(OtherRules(new() { Level="medium" }) == OtherRules(new() { Level="medium", Telegram="split" }), "Telegram strategy changed other services or games");
        return Task.CompletedTask;
    }
    public static Task Media()
    {
        Check(!RouteProbePolicy.IsImage(400, "image/jpeg", [255,216,255,1]), "HTTP 400 accepted as avatar");
        Check(!RouteProbePolicy.IsImage(200, "text/html", "<html>error"u8), "HTML image accepted");
        Check(RouteProbePolicy.IsImage(206, "image/jpeg", [255,216,255,1]), "Real JPEG rejected");
        Check(!RouteProbePolicy.IsVideo(200, "text/plain", "mapping info"u8), "report_mapping accepted as video");
        Check(RouteProbePolicy.IsVideo(206, "video/mp4", [0,0,0,24,102,116,121,112,1]), "Real MP4 range rejected");
        Check(!PublicMediaProbeService.IsAllowedControlUri("video", new("https://evil.googlevideo.com.attacker.example/videoplayback")), "Untrusted discovery URL accepted");
        Check(!PublicMediaProbeService.IsAllowedControlUri("video", new("https://user:pass@x.googlevideo.com/videoplayback")), "Embedded credentials accepted");
        Check(!WarpService.RequiresNativeOnboarding("Failed to contact WARP API: timeout"), "Network error misreported as onboarding");
        Check(WarpService.RequiresNativeOnboarding("complete setup in the GUI"), "Native onboarding not recognized");
        return Task.CompletedTask;
    }
    public static async Task Registration()
    {
        var calls = new List<string>();
        await WarpRegistrationBootstrap.RunAsync(false, _ => { calls.Add("bootstrap"); return Task.CompletedTask; },
            _ => { calls.Add("registration"); return Task.FromResult(new WarpStatus(WarpConnectionState.Disconnected, "Ready", Registered: true)); },
            () => { calls.Add("release"); return Task.CompletedTask; }, default);
        Check(calls.SequenceEqual(new[] { "bootstrap", "registration", "release" }), "Registration started before bootstrap");
        calls.Clear();
        try
        {
            await WarpRegistrationBootstrap.RunAsync(false, _ => Task.CompletedTask,
                _ => throw new IOException("network error"), () => { calls.Add("release"); return Task.CompletedTask; }, default);
        } catch (IOException) { }
        Check(calls.SequenceEqual(new[] { "release" }), "Failure leaked bootstrap");
        calls.Clear();
        await WarpRegistrationBootstrap.RunAsync(true, _ => Task.CompletedTask,
            _ => Task.FromResult(new WarpStatus(WarpConnectionState.Connected, "existing")), () => { calls.Add("release"); return Task.CompletedTask; }, default);
        Check(calls.Count == 0, "Existing WARP bootstrap stopped");
    }
    public static Task Ranking()
    {
        var r = LocalStrategyAdvisor.Rate(new(), "ISP", [new("discord", "healthy", true, "Web OK", 50, 5, 3),
            new("discord-voice", "sessionRequired", false, "waiting for call", null, null, 0)]);
        Check(r.FullyConfirmed && r.Total == 1 && r.Confirmed == 1, "Inactive voice downgraded Web ranking");
        Check(r.ProbeVersion == RouteProbePolicy.Version && new AdvisorRecommendation("old",new(),"ISP",0,0,false,null,null,"",[]).ProbeVersion == 0, "Old probes not distinguishable");
        return Task.CompletedTask;
    }
}
