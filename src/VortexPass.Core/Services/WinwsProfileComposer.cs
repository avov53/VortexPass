using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public static class WinwsProfileComposer
{
    public const string FlowsealCommit = "61d7596c7ce82b6f320e5c79ff4598856f4ae7d3";
    private const string PhasmophobiaVoiceUdpPorts = "5055-5058,27000-27002,12000-65000";

    public static string Compose(BypassProfileSettings profiles, string level) =>
        Compose(profiles, level, new GameFilterSettings(), null, null, 0);

    /// <summary>
    /// Creates a second, deliberately narrow winws instance used only while the
    /// official Cloudflare client establishes and maintains its tunnel. Keeping
    /// this command separate means WARP can be toggled without restarting the
    /// user's YouTube/Discord/Telegram protection process.
    /// </summary>
    public static string ComposeWarpBootstrap() =>
        "--wf-tcp=443 --wf-udp=443,2408 " +
        "--filter-tcp=443 --hostlist=files\\list-warp.txt " +
        TcpStrategy("moderate", 0, "681", "files\\tls_clienthello_www_google_com.bin") + " --new " +
        "--filter-udp=443 --hostlist=files\\list-warp.txt --filter-l7=quic --dpi-desync=fake " +
        "--dpi-desync-repeats=3 --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2 --new " +
        "--filter-udp=2408 --dpi-desync=fake --dpi-desync-repeats=3 --dpi-desync-any-protocol=1 " +
        "--dpi-desync-fake-unknown-udp=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2 --new";

    public static string Compose(
        BypassProfileSettings profiles,
        string level,
        GameFilterSettings gameFilter,
        string? customBypassListPath,
        string? exclusionListPath,
        int tuning = 0,
        bool includeWarpBootstrap = false,
        bool warpCompatibilityMode = false,
        bool mobileCompatibilityMode = false,
        string youtubeTransport = "auto",
        string discordVoiceMode = "auto",
        string telegramStrategy = "strong",
        string youtubeMobileStrategy = "primary",
        string discordMobileStrategy = "primary")
    {
        level = NormalizeLevel(level);
        tuning = NormalizeTuning(tuning);
        var includeYouTubeWeb = profiles.YouTube || profiles.Web;
        var includeDiscordWeb = profiles.Discord || profiles.Web;
        // Once WARP is connected Telegram already travels inside the encrypted
        // system tunnel. Direct Telegram desync/ipset rules can only interfere
        // with CDN selection there, so retain the user's selections but suppress
        // their wire rules until the ordinary route is restored.
        var includeTelegramWeb = profiles.TelegramWeb && !warpCompatibilityMode;
        var includeTelegramDesktop = profiles.Telegram && !warpCompatibilityMode;
        var normalizedVoiceMode = NormalizeDiscordVoiceMode(discordVoiceMode);
        var includeDiscordVoice = profiles.Discord &&
            (!mobileCompatibilityMode || normalizedVoiceMode == "minimal");
        var includePhasmophobia = profiles.GameFilter && gameFilter.SelectedGames.Contains("phasmophobia", StringComparer.OrdinalIgnoreCase);
        var includeRoblox = profiles.GameFilter && gameFilter.SelectedGames.Contains("roblox", StringComparer.OrdinalIgnoreCase);
        var includeDeadByDaylight = profiles.GameFilter && gameFilter.SelectedGames.Contains("deadbydaylight", StringComparer.OrdinalIgnoreCase);

        if (!includeYouTubeWeb && !includeDiscordWeb && !includeTelegramWeb && !includeTelegramDesktop && !includePhasmophobia && !includeRoblox && !includeDeadByDaylight && !includeWarpBootstrap && string.IsNullOrWhiteSpace(customBypassListPath))
            return string.Empty;

        var tcpPorts = new SortedSet<int> { 443 };
        if (includeYouTubeWeb || includeDiscordWeb || includeRoblox || includePhasmophobia || includeDeadByDaylight || !string.IsNullOrWhiteSpace(customBypassListPath))
            tcpPorts.Add(80);
        var udpPorts = new List<string> { "443" };
        if (includeWarpBootstrap) udpPorts.Add("2408");
        if (includeDiscordVoice)
        {
            foreach (var port in new[] { 2053, 2083, 2087, 2096, 8443 }) tcpPorts.Add(port);
            udpPorts.Add("19294-19344");
            udpPorts.Add("50000-50100");
        }
        if (includePhasmophobia && !mobileCompatibilityMode)
            udpPorts.Add(PhasmophobiaVoiceUdpPorts);

        // Known games use dedicated routes below. Capturing almost every
        // ephemeral TCP/UDP port corrupts unrelated sessions and, in Roblox,
        // can let the web shell pass while the actual game connection stalls.
        // Roblox gameplay therefore stays direct; only its HTTPS/QUIC domains
        // are handled. Phasmophobia keeps its separate bounded Vivox rule.

        var capture = new List<string>
        {
            $"--wf-tcp={string.Join(',', tcpPorts)}",
            $"--wf-udp={string.Join(',', udpPorts)}"
        };
        var rules = new List<string>();

        if (includeWarpBootstrap)
        {
            // This allowlist intentionally does not inherit user exclusions: the
            // ordinary profiles leave WARP alone, while bootstrap helps only the
            // official control and tunnel handshake hosts.
            AddDomainRules(rules, "files\\list-warp.txt", "moderate", 0, includeHttp: false, includeQuic: true, quicRepeats: 3, exclusionListPath: null);
            rules.Add("--filter-udp=2408 --dpi-desync=fake --dpi-desync-repeats=3 " +
                      "--dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp=files\\quic_initial_www_google_com.bin " +
                      "--dpi-desync-cutoff=d2 --new");
        }

        // DNS over HTTPS (DoH) protection against TSPU resets
        rules.Add($"--filter-tcp=443 --hostlist=files\\list-doh.txt " +
                  TcpStrategy(level, tuning, "681", "files\\tls_clienthello_www_google_com.bin") + " --new");
        rules.Add($"--filter-tcp=443 --ipset=files\\list-doh-ipset.txt " +
                  TcpStrategy(level, tuning, "681", "files\\tls_clienthello_www_google_com.bin") + " --new");

        if (includeYouTubeWeb)
        {
            var includeYouTubeQuic = !mobileCompatibilityMode || !NormalizeYoutubeTransport(youtubeTransport).Equals("http2", StringComparison.Ordinal);
            // Media connections are short-lived and highly parallel. Let fixed,
            // bounded rules win before the user's stronger generic strategy so
            // every avatar or video range does not inherit 8-18 TCP repeats.
            const string youtubeImageDomains = "ggpht.com,ytimg.com,googleusercontent.com,gstatic.com,cloudflare-ech.com";
            const string youtubeVideoDomains = "googlevideo.com,video.google.com,redirector.googlevideo.com";
            var youtubeTcpStrategy = mobileCompatibilityMode
                ? MobileYoutubeTcpStrategy(youtubeMobileStrategy)
                : YoutubeFastTcpStrategy();
            rules.Add($"--filter-tcp=443 --hostlist-domains={youtubeImageDomains} " +
                      youtubeTcpStrategy + ExclusionOption(exclusionListPath) + " --new");
            if (includeYouTubeQuic)
                rules.Add($"--filter-udp=443 --hostlist-domains={youtubeImageDomains} " +
                          YoutubeFastQuicStrategy(mobileCompatibilityMode) + ExclusionOption(exclusionListPath) + " --new");
            rules.Add($"--filter-tcp=443 --hostlist-domains={youtubeVideoDomains} " +
                      youtubeTcpStrategy + ExclusionOption(exclusionListPath) + " --new");
            if (includeYouTubeQuic)
                rules.Add($"--filter-udp=443 --hostlist-domains={youtubeVideoDomains} " +
                          YoutubeFastQuicStrategy(mobileCompatibilityMode) + ExclusionOption(exclusionListPath) + " --new");
            // Firefox can hide the original SNI with ECH and video shards use
            // short-lived rr*.googlevideo.com names. The IP fallback ensures
            // those flows still receive the same bounded YouTube strategy.
            rules.Add("--filter-tcp=443 --ipset=files\\list-youtube-ipset.txt " +
                      youtubeTcpStrategy + ExclusionOption(exclusionListPath) + " --new");
            if (includeYouTubeQuic)
                rules.Add("--filter-udp=443 --ipset=files\\list-youtube-ipset.txt " +
                          YoutubeFastQuicStrategy(mobileCompatibilityMode) + ExclusionOption(exclusionListPath) + " --new");
            AddDomainRules(rules, "files\\list-youtube.txt", mobileCompatibilityMode ? "moderate" : level,
                mobileCompatibilityMode ? 0 : tuning, includeHttp: true, includeQuic: includeYouTubeQuic,
                mobileCompatibilityMode ? 2 : FastQuicRepeats(tuning), exclusionListPath);
        }

        if (includeDiscordWeb)
        {
            // Attachments, avatars and embedded media are latency-sensitive.
            // Let a small bounded profile win before the broader Discord rule;
            // Discord voice remains isolated in its UDP port rule below.
            const string discordCdnDomains = "cdn.discordapp.com,media.discordapp.net,images-ext-1.discordapp.net,images-ext-2.discordapp.net";
            rules.Add($"--filter-tcp=443 --hostlist-domains={discordCdnDomains} " +
                      (mobileCompatibilityMode ? MobileDiscordTcpStrategy(discordMobileStrategy) : TcpStrategy("moderate", 0, "681", "files\\tls_clienthello_www_google_com.bin")) + ExclusionOption(exclusionListPath) + " --new");
            rules.Add($"--filter-udp=443 --hostlist-domains={discordCdnDomains} " +
                      "--filter-l7=quic --dpi-desync=fake --dpi-desync-repeats=2 --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2" + ExclusionOption(exclusionListPath) + " --new");
            if (mobileCompatibilityMode)
            {
                var exclusion = ExclusionOption(exclusionListPath);
                rules.Add("--filter-tcp=80 --hostlist=files\\list-discord.txt " +
                          TcpStrategy("light", 0, "568", "files\\tls_clienthello_4pda_to.bin") + exclusion + " --new");
                rules.Add("--filter-tcp=443 --hostlist=files\\list-discord.txt " +
                          MobileDiscordTcpStrategy(discordMobileStrategy) + exclusion + " --new");
                rules.Add("--filter-udp=443 --hostlist=files\\list-discord.txt --filter-l7=quic --dpi-desync=fake " +
                          "--dpi-desync-repeats=2 --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2" + exclusion + " --new");
            }
            else
            {
                AddDomainRules(rules, "files\\list-discord.txt", level, tuning, includeHttp: true, includeQuic: true,
                    StableQuicRepeats(level, tuning), exclusionListPath);
            }
        }

        if (includeTelegramWeb || includeTelegramDesktop)
        {
            // Known Web/WebSocket/media names should win before the heavier raw
            // IP rule. With the IP rule first every hosts-pinned request inherited
            // SYN-data processing and all three Web probes timed out together,
            // while the base page needed minutes to retry. Keep TCP/443 in the
            // IP fallback for hidden/raw MTProto gateways, but use it only when
            // no Telegram hostname rule matched.
            AddTelegramDomainRules(rules, includeTelegramWeb, exclusionListPath, mobileCompatibilityMode, telegramStrategy, level);
            // Raw Telegram DC ranges differ significantly between mobile
            // operators. Avoid pinning/capturing them in compatibility mode;
            // the Desktop proxy and hostname rules remain narrow and reversible.
            if (!mobileCompatibilityMode)
            {
                var telegramPorts = includeTelegramDesktop ? "443,5222" : "443";
                var ipsetOption = $"--ipset={QuotePath("files\\list-telegram-ipset.txt")}";
                rules.Add($"--filter-tcp={telegramPorts} {ipsetOption} " + TelegramIpStrategy() + ExclusionOption(exclusionListPath) + " --new");
            }
        }

        if (includeRoblox)
        {
            // Roblox pages use many parallel image/CDN requests. The generic
            // 8+ repeat profile can make some shards stall while others load.
            // Give rbxcdn a bounded fast rule first and retain the complete
            // hostlist afterwards for API/setup endpoints.
            rules.Add("--filter-tcp=443 --hostlist-domains=rbxcdn.com " +
                      (mobileCompatibilityMode ? MobileYoutubeTcpStrategy("primary") : YoutubeFastTcpStrategy()) +
                      ExclusionOption(exclusionListPath) + " --new");
            rules.Add("--filter-udp=443 --hostlist-domains=rbxcdn.com --filter-l7=quic --dpi-desync=fake " +
                      $"--dpi-desync-repeats={(mobileCompatibilityMode ? 2 : 3)} --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2" +
                      ExclusionOption(exclusionListPath) + " --new");
            AddDomainRules(rules, "files\\list-roblox.txt", mobileCompatibilityMode ? "moderate" : level,
                mobileCompatibilityMode ? 0 : tuning, includeHttp: true, includeQuic: true,
                mobileCompatibilityMode ? 2 : StableQuicRepeats(level, tuning), exclusionListPath);
        }

        if (includePhasmophobia)
            AddDomainRules(rules, "files\\list-phasmophobia.txt", level, tuning, includeHttp: true, includeQuic: true, StableQuicRepeats(level, tuning), exclusionListPath);

        if (includeDeadByDaylight)
        {
            // DBD matchmaking endpoints can share infrastructure with Steam,
            // Epic and other games. Keep the first version domain-scoped: a
            // global Steam UDP capture would increase latency for unrelated
            // sessions and can conflict with anti-cheat traffic.
            AddDomainRules(rules, "files\\list-deadbydaylight.txt", mobileCompatibilityMode ? "moderate" : level,
                mobileCompatibilityMode ? 0 : tuning, includeHttp: true, includeQuic: true,
                mobileCompatibilityMode ? 2 : StableQuicRepeats(level, tuning), exclusionListPath);
        }

        if (!string.IsNullOrWhiteSpace(customBypassListPath))
            AddDomainRules(rules, customBypassListPath, level, tuning, includeHttp: true, includeQuic: true, StableQuicRepeats(level, tuning), exclusionListPath);

        if (includeDiscordVoice)
        {
            rules.Add($"--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun " +
                      "--dpi-desync=fake --dpi-desync-fake-discord=files\\ACTIVE_DISCORD_UDP.bin " +
                      "--dpi-desync-fake-stun=files\\ACTIVE_DISCORD_UDP.bin " +
                      $"--dpi-desync-repeats={(mobileCompatibilityMode ? 2 : VoiceRepeats(tuning))} --dpi-desync-cutoff=d2 --new");
            rules.Add("--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media " +
                      TcpStrategy(mobileCompatibilityMode ? "moderate" : level, mobileCompatibilityMode ? 0 : tuning, "681", "files\\tls_clienthello_www_google_com.bin") + ExclusionOption(exclusionListPath) + " --new");
        }

        if (includePhasmophobia && !mobileCompatibilityMode)
        {
            // Vivox can choose UDP ports dynamically. Keep this rule bounded
            // to a selected Phasmophobia profile and deliberately gentle so a
            // long voice session does not inherit the broad game preset.
            rules.Add($"--filter-udp={PhasmophobiaVoiceUdpPorts} --dpi-desync=fake " +
                      "--dpi-desync-repeats=2 --dpi-desync-any-protocol=1 " +
                      "--dpi-desync-fake-unknown-udp=files\\ACTIVE_GAME_UDP.bin --dpi-desync-cutoff=d2 --new");
        }

        return $"{string.Join(' ', capture)} {string.Join(' ', rules)}";
    }

    private static void AddDomainRules(List<string> rules, string hostlist, string level, int tuning, bool includeHttp, bool includeQuic, int quicRepeats, string? exclusionListPath)
    {
        var hostlistOption = $"--hostlist={QuotePath(hostlist)}";
        var exclusionOption = ExclusionOption(exclusionListPath);
        if (includeHttp)
        {
            rules.Add($"--filter-tcp=80 {hostlistOption} " +
                      TcpStrategy(level, tuning, "568", "files\\tls_clienthello_4pda_to.bin") + exclusionOption + " --new");
        }

        rules.Add($"--filter-tcp=443 {hostlistOption} " +
                  TcpStrategy(level, tuning, "681", "files\\tls_clienthello_www_google_com.bin") + exclusionOption + " --new");
        if (includeQuic)
            rules.Add($"--filter-udp=443 {hostlistOption} --filter-l7=quic --dpi-desync=fake " +
                      $"--dpi-desync-repeats={quicRepeats} --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2{exclusionOption} --new");
    }

    private static void AddTelegramDomainRules(List<string> rules, bool includeQuic, string? exclusionListPath, bool mobileCompatibilityMode = false, string telegramStrategy = "strong", string level = "medium")
    {
        var exclusion = ExclusionOption(exclusionListPath);
        const string websocketDomains = "kws.web.telegram.org,zws.web.telegram.org,kws2.web.telegram.org,kws2-1.web.telegram.org,kws4.web.telegram.org,kws4-1.web.telegram.org,zws2.web.telegram.org,zws2-1.web.telegram.org,zws4.web.telegram.org,zws4-1.web.telegram.org";
        // Mobile networks need an independently tunable WebSocket route. The
        // proven home profile must let these names fall through to the complete
        // Telegram hostlist below. A duplicate first-match rule left the Web
        // client at "waiting for network" on otherwise working home routes.
        if (mobileCompatibilityMode)
            rules.Add($"--filter-tcp=443 --hostlist-domains={websocketDomains} " +
                      MobileTelegramWebSocketStrategy(level) + exclusion + " --new");

        rules.Add("--filter-tcp=443 --hostlist=files\\list-telegram.txt " +
                  (mobileCompatibilityMode
                      ? NormalizeTelegramStrategy(telegramStrategy) == "balanced"
                          ? TcpStrategy("moderate", 0, "681", "files\\tls_clienthello_max_ru.bin")
                          : TelegramWebTcpStrategy()
                      : TelegramWebTcpStrategy()) + exclusion + " --new");
        if (includeQuic)
            rules.Add("--filter-udp=443 --hostlist=files\\list-telegram.txt --filter-l7=quic --dpi-desync=fake " +
                      "--dpi-desync-repeats=3 --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2" + exclusion + " --new");
    }

    private static string TelegramWebTcpStrategy() =>
        // Telegram Web is a long-lived TLS/WebSocket flow. Limit desync to the
        // handshake and split relative to SNI so pictures/messages do not inherit
        // repeated processing. badseq plus a randomized fake ClientHello is more
        // reliable on current TSPU paths than the old timestamp-only fake.
        "--dpi-desync=fake,multidisorder --dpi-desync-repeats=6 --dpi-desync-fooling=badseq " +
        "--dpi-desync-fake-tls=files\\tls_clienthello_max_ru.bin " +
        "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=max.ru --dpi-desync-split-pos=1,midsld " +
        "--dpi-desync-split-seqovl=681 --dpi-desync-split-seqovl-pattern=files\\tls_clienthello_max_ru.bin " +
        "--dpi-desync-cutoff=n3";

    private static string TelegramIpStrategy() =>
        "--dpi-desync=syndata,fake,fakeddisorder --dpi-desync-repeats=6 --dpi-desync-fooling=ts " +
        "--dpi-desync-split-pos=1 --dpi-desync-fake-tls=files\\tls_clienthello_max_ru.bin";

    private static string MobileTelegramWebSocketStrategy(string level) => NormalizeLevel(level) switch
    {
        "light" => TcpStrategy("light", 0, "681", "files\\tls_clienthello_max_ru.bin") + " --dpi-desync-cutoff=n3",
        "moderate" => "--dpi-desync=multisplit --dpi-desync-split-pos=1,midsld --dpi-desync-split-seqovl=681 " +
                      "--dpi-desync-split-seqovl-pattern=files\\tls_clienthello_max_ru.bin --dpi-desync-cutoff=n3",
        _ => TelegramWebTcpStrategy()
    };

    private static string MobileDiscordTcpStrategy(string strategy) => NormalizeMobileServiceStrategy(strategy) switch
    {
        "fallback" => "--dpi-desync=fake,multidisorder --dpi-desync-repeats=6 --dpi-desync-fooling=badseq " +
                                     "--dpi-desync-fake-tls=files\\tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com " +
                                     "--dpi-desync-split-pos=1,midsld --dpi-desync-split-seqovl=681 " +
                                     "--dpi-desync-split-seqovl-pattern=files\\tls_clienthello_www_google_com.bin --dpi-desync-cutoff=n3",
        _ => YoutubeFastTcpStrategy()
    };

    private static string MobileYoutubeTcpStrategy(string strategy) => NormalizeMobileServiceStrategy(strategy) switch
    {
        "fallback" => TcpStrategy("moderate", 0, "681", "files\\tls_clienthello_www_google_com.bin"),
        _ => YoutubeFastTcpStrategy()
    };

    public static string NormalizeMobileServiceStrategy(string? value) =>
        value?.Equals("fallback", StringComparison.OrdinalIgnoreCase) == true ? "fallback" : "primary";

    private static string YoutubeFastQuicStrategy(bool mobileCompatibilityMode = false) =>
        // Match the current Flowseal general preset. Eleven fakes made some
        // Firefox/ISP combinations retry QUIC before falling back, which was
        // visible as very slow Shorts and thumbnails.
        $"--filter-l7=quic --dpi-desync=fake --dpi-desync-repeats={(mobileCompatibilityMode ? 2 : 6)} " +
        "--dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2";

    private static string YoutubeFastTcpStrategy() =>
        "--ip-id=zero --dpi-desync=fake,multidisorder --dpi-desync-fake-tls-mod=sni=www.google.com " +
        "--dpi-desync-split-pos=1,midsld,host --dpi-desync-split-seqovl=681 " +
        "--dpi-desync-split-seqovl-pattern=files\\tls_clienthello_www_google_com.bin " +
        "--dpi-desync-fooling=ts --dpi-desync-repeats=6";

    private static string QuotePath(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
    private static string ExclusionOption(string? path) => string.IsNullOrWhiteSpace(path) ? string.Empty : $" --hostlist-exclude={QuotePath(path)}";

    private static string TcpStrategy(string level, int tuning, string overlap, string pattern) => level switch
    {
        "light" => $"--dpi-desync=fake --dpi-desync-repeats={Repeats(level, tuning)} --dpi-desync-autottl=2",
        "moderate" => $"--dpi-desync=fake,fakedsplit --dpi-desync-repeats={Repeats(level, tuning)} --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls={pattern}",
        "medium" => $"--dpi-desync=multisplit --dpi-desync-split-seqovl={AdjustedOverlap(overlap, tuning)} --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern={pattern}",
        // fake+multisplit with a large TLS overlap is too destructive on a
        // number of providers: winws remains alive, but ordinary TLS sessions
        // time out together. Keep the complex level stronger than moderate by
        // its repeat count, while using the provider-proven fakedsplit layout.
        "complex" => $"--dpi-desync=fake,fakedsplit --dpi-desync-repeats={Repeats(level, tuning)} --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls={pattern}",
        "aggressive" => $"--dpi-desync=fake,fakedsplit --dpi-desync-repeats={Repeats(level, tuning)} --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls=files\\stun.bin --dpi-desync-fake-tls={pattern}",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    private static int Repeats(string level, int tuning) => (level switch
    {
        "light" => 2,
        "moderate" => 4,
        "medium" => 6,
        "complex" => 8,
        "aggressive" => 12,
        _ => 6
    }) + NormalizeTuning(tuning) * 2;

    private static int FastQuicRepeats(int tuning) => Math.Clamp(2 + NormalizeTuning(tuning), 2, 4);
    private static int StableQuicRepeats(string level, int tuning) => Math.Clamp(Repeats(level, tuning), 2, 8);
    private static int VoiceRepeats(int tuning) => Math.Clamp(2 + NormalizeTuning(tuning), 2, 4);

    private static string AdjustedOverlap(string overlap, int tuning) =>
        int.TryParse(overlap, out var value) ? (value + NormalizeTuning(tuning) * 64).ToString() : overlap;

    public static string NormalizeLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "light" or "moderate" or "medium" or "complex" or "aggressive" => level.ToLowerInvariant(),
        _ => "medium"
    };

    public static string NormalizeGameProtocol(string? protocol) => protocol?.ToLowerInvariant() switch
    {
        "udp" or "tcp" or "all" => protocol.ToLowerInvariant(),
        _ => "all"
    };

    public static string NormalizeCompatibilityMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "mobile" => "mobile",
        _ => "standard"
    };

    public static string NormalizeYoutubeTransport(string? transport) => transport?.ToLowerInvariant() switch
    {
        "quic" or "http2" => transport.ToLowerInvariant(),
        _ => "auto"
    };

    public static string NormalizeDiscordVoiceMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "direct" or "minimal" or "off" => mode.ToLowerInvariant(),
        _ => "auto"
    };

    public static string NormalizeTelegramStrategy(string? strategy) => strategy?.ToLowerInvariant() switch
    {
        "balanced" => "balanced",
        _ => "strong"
    };

    public static int NormalizeTuning(int tuning) => Math.Clamp(tuning, 0, 3);
}
