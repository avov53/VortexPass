using VortexPass.Core.Models;
using VortexPass.Core.Services;

internal static class LocalAdvisorTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static Task Policy()
    {
        var spec = new AdvisorStrategySpec { Level = "light" };
        void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } catch (System.Text.Json.JsonException) { return; } throw new Exception("Unsafe candidate accepted"); }
        Reject(() => AdvisorStrategyPolicy.Validate(spec with { Level = "aggressive" }, "light"));
        Reject(() => AdvisorStrategyPolicy.Validate(spec with { Telegram = "disorder" }, "light"));
        Reject(() => AdvisorStrategyPolicy.Validate(spec with { TcpRepeats = 99 }, "light"));
        Reject(() => AdvisorStrategyPolicy.Validate(spec with { Overlap = 257 }, "light"));
        Reject(() => AdvisorStrategyPolicy.Validate(spec with { RouteTemplate = "shell" }, "light"));
        Reject(() => AdvisorStrategyPolicy.Validate(spec with { DiscordVoice = "socks5" }, "light"));
        Reject(() => AdvisorStrategyPolicy.Validate(spec with { YouTubeTransport = "udp-all" }, "light"));
        Reject(() => AdvisorStrategyPolicy.Validate(spec with { QuicRepeats = 2 }, "light", false));
        Reject(() => AdvisorStrategyPolicy.ParseModelOutput("[{\"level\":\"light\",\"command\":\"--wf-udp=1-65535\"}]", "light", false));
        Reject(() => AdvisorStrategyPolicy.NormalizeProvider("\n"));
        Check(AdvisorStrategyPolicy.NormalizeProvider(" Т2 ") == "Т2", "Manual provider not retained");
        Check(LlamaLocalStrategyModel.ExtractJson("[]\n[end of text]") == "[]", "Pinned CLI marker not handled");
        Reject(() => LlamaLocalStrategyModel.ExtractJson("[]\n[1]"));
        AdvisorStrategyPolicy.Validate(spec with { TcpRepeats = 2, QuicRepeats = 4, Overlap = 256 }, "light");
        return Task.CompletedTask;
    }
    public static Task Commands()
    {
        var adaptive = new AdvisorStrategySpec { Level = "medium", RouteTemplate = "narrow", YouTubeTransport = "http2", DiscordVoice = "direct" };
        var narrow = WinwsProfileComposer.Compose(new() { YouTube = true, Discord = true, TelegramWeb = true }, "medium", new(), null, null, advisorStrategy: adaptive);
        Check(!narrow.Contains("ACTIVE_DISCORD_UDP") && !narrow.Contains("list-telegram-ipset"), "Unified direct template captured voice/raw IP");
        Check(!narrow.Split(" --new").Any(r => r.Contains("quic") && (r.Contains("youtube") || r.Contains("ggpht") || r.Contains("googlevideo"))), "HTTP2 kept YouTube QUIC rules");
        var voice = WinwsProfileComposer.Compose(new() { Discord = true }, "medium", new(), null, null, advisorStrategy: adaptive with { DiscordVoice = "minimal" });
        Check(voice.Contains("--filter-udp=19294-19344,50000-50100") && !voice.Contains("1024-65535"), "Unified minimal voice not narrow");
        var gentle = WinwsProfileComposer.Compose(new() { YouTube = true, TelegramWeb = true }, "light", new(), null, null,
            advisorStrategy: adaptive with { Level = "light" });
        foreach (var rule in gentle.Split(" --new").Where(r => r.Contains("--filter-tcp=") && !r.Contains("list-doh")))
            Check(!System.Text.RegularExpressions.Regex.Matches(rule, @"--dpi-desync-repeats=(\d+)")
                .Any(m => int.Parse(m.Groups[1].Value) > 2), "Narrow template exceeds selected level");
        var profiles = new BypassProfileSettings { YouTube = true, Discord = true, TelegramWeb = true, GameFilter = true };
        var games = new GameFilterSettings { SelectedGames = ["roblox", "phasmophobia", "deadbydaylight"] };
        foreach (var mode in new[] { false, true })
        {
            var original = WinwsProfileComposer.Compose(profiles, "medium", games, null, null, mobileCompatibilityMode: mode);
            var baseline = WinwsProfileComposer.Compose(profiles, "medium", games, null, null, mobileCompatibilityMode: mode,
                advisorStrategy: new() { Level = "medium" });
            Check(original == baseline, "Baseline changed home/mobile commands");
            var altered = WinwsProfileComposer.Compose(profiles, "medium", games, null, null, mobileCompatibilityMode: mode,
                advisorStrategy: new() { Level = "medium", Tuning = 3, Telegram = "split", Discord = "minimal", TcpRepeats = 2, QuicRepeats = 2, Overlap = 256 });
            Check(original.Split(" --filter-")[0] == altered.Split(" --filter-")[0], "AI changed capture ports");
            var originalRules = original.Split(" --new", StringSplitOptions.TrimEntries);
            var protectedRules = originalRules.Where(r => r.Contains("list-doh") || r.Contains("ACTIVE_") || r.Contains("list-roblox") || r.Contains("rbxcdn") || r.Contains("deadbydaylight") || r.Contains("list-phasmophobia") || r.Contains("list-telegram-ipset") || r.Contains("--hostlist-domains=discord.media"));
            foreach (var rule in protectedRules) Check(altered.Contains(rule, StringComparison.Ordinal), "AI changed DNS, gameplay or voice rules");
            Check(altered.Contains("--dpi-desync-repeats=2"), "Bounded parameter not composed");
            Check(!altered.Contains("--wf-udp=1-65535"), "AI broadened capture");
        }
        return Task.CompletedTask;
    }
    public static Task Ranking()
    {
        var spec = new AdvisorStrategySpec();
        var fastPartial = LocalStrategyAdvisor.Rate(spec, "Т2", [new("youtube", "partial", false, "No video", 1, 0, 3)]);
        var slowFull = LocalStrategyAdvisor.Rate(spec, "Т2", [new("youtube", "healthy", true, "Video OK", 500, 20, 3)]);
        var oneSample = LocalStrategyAdvisor.Rate(spec, "Т2", [new("youtube", "healthy", true, "One probe", 1, 0, 1)]);
        Check(LocalStrategyAdvisor.Rank([fastPartial, slowFull])[0] == slowFull, "Latency beat working content");
        Check(!oneSample.FullyConfirmed, "One sample claimed stable success");
        Check(LocalStrategyAdvisor.Median([1, 3, 100]) == 3, "Median incorrect");
        return Task.CompletedTask;
    }
    public static async Task Lifecycle()
    {
        await YoutubeGlobalAdvisorTests.Run();
        var model = new FakeModel();
        var runner = new LocalStrategyAdvisor(model);
        var session = new FakeSession();
        var request = new AdvisorRequest("medium", "Т2", "network-a", "mobile", ["youtube"]);
        var result = await runner.RunAsync(request, _ => Task.FromResult<IAdvisorTrialSession>(session), _ => { }, [], default);
        Check(session.Restored, "Success did not restore original protection");
        Check(result.Count is >=1 and <=3 && result.Take(2).All(r => r.FullyConfirmed), "Three verified candidates not returned");
        Check(!model.ExperimentalCalled, "Expanded before verified blocks failed");

        session = new FakeSession { Fail = true };
        try { await runner.RunAsync(request, _ => Task.FromResult<IAdvisorTrialSession>(session), _ => { }, [], default); }
        catch (InvalidOperationException) { }
        Check(session.Restored, "Trial exception did not restore protection");

        using var cancel = new CancellationTokenSource();
        session = new FakeSession { Cancel = cancel };
        try { await runner.RunAsync(request, _ => Task.FromResult<IAdvisorTrialSession>(session), _ => { }, [], cancel.Token); }
        catch (OperationCanceledException) { }
        Check(session.Restored, "Cancellation did not restore protection");

        session = new FakeSession { Partial = true };
        result = await runner.RunAsync(request, _ => Task.FromResult<IAdvisorTrialSession>(session), _ => { }, [], default);
        Check(!model.ExperimentalCalled && result.All(r => !r.FullyConfirmed), "Partial results were hidden or experimental stage skipped");
        Check(result.All(r => r.Strategy.Level == "medium"), "Selected level changed");
        session = new FakeSession();
        result = await runner.RunAsync(request with { PreferredTuning = 2 }, _ => Task.FromResult<IAdvisorTrialSession>(session), _ => { }, [], default);
        Check(result.All(r => r.Strategy.Tuning == 2), "Selected tuning changed during generation");
        var telegramSession = new TelegramSession();
        await runner.RunAsync(request with { Services=["youtube","discord","telegram"] },
            _=>Task.FromResult<IAdvisorTrialSession>(telegramSession),_=>{},[],default);
        Check(telegramSession.Tested.Select(s=>s.Key).Distinct().Count()<=4,"Normal search exceeded screening budget");
        Check(telegramSession.Restored,"Focused session did not restore protection");
    }
    public static async Task Smoke(string directory)
    {
        var model = new LlamaLocalStrategyModel(Path.GetFullPath(directory));
        var request = new AdvisorRequest("medium", "Т2", "local-test", "mobile", ["youtube", "discord", "telegram"]);
        foreach (var experimental in new[] { false, true })
        {
            var proposals = await model.ProposeAsync(request, experimental, [], default);
            Check(proposals.Count > 0, "No model proposals");
            Console.WriteLine($"MODEL {(experimental ? "bounded" : "blocks")}: {proposals.Count} validated candidates");
            foreach (var p in proposals) Console.WriteLine(p.Key);
        }
    }
    private sealed class FakeModel : ILocalStrategyModel
    {
        public bool ExperimentalCalled;
        public Task<IReadOnlyList<AdvisorStrategySpec>> ProposeAsync(AdvisorRequest request, bool experimental, IReadOnlyList<AdvisorRecommendation> evidence, CancellationToken cancellationToken)
        {
            ExperimentalCalled |= experimental;
            return Task.FromResult<IReadOnlyList<AdvisorStrategySpec>>([new() { Level = request.Level, Tuning = 0,
                Telegram = experimental ? "minimal" : "baseline", TcpRepeats = experimental ? 2 : null }]);
        }
    }
    private sealed class FakeSession : IAdvisorTrialSession
    {
        public bool Restored, Fail, Partial;
        public CancellationTokenSource? Cancel;
        public Task<IReadOnlyList<AdvisorServiceEvidence>> TestAsync(AdvisorStrategySpec strategy, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("Trial failed");
            Cancel?.Cancel(); cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AdvisorServiceEvidence>>([new("youtube", Partial ? "partial" : "healthy", !Partial, "Measured", 42, 2, 3)]);
        }
        public ValueTask DisposeAsync() { Restored = true; return ValueTask.CompletedTask; }
    }
    private sealed class TelegramSession : IAdvisorTrialSession
    {
        public AdvisorStrategySpec? CurrentStrategy => new() { Level="medium",YouTube="split",Discord="minimal",RouteTemplate="narrow",TcpRepeats=2 };
        public List<AdvisorStrategySpec> Tested=[];
        public bool Restored;
        public Task<IReadOnlyList<AdvisorServiceEvidence>> TestAsync(AdvisorStrategySpec spec,CancellationToken token)
        {
            Tested.Add(spec);
            return Task.FromResult<IReadOnlyList<AdvisorServiceEvidence>>([new("youtube","healthy",true,"content",40,0,3),
                new("discord","healthy",true,"content",50,0,3),new("telegram",spec.Telegram=="split"?"healthy":"partial",
                    spec.Telegram=="split","transport",60,0,3)]);
        }
        public ValueTask DisposeAsync(){Restored=true;return ValueTask.CompletedTask;}
    }
}
