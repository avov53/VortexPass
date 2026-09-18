using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public static class WinwsProfileComposer
{
    public const string FlowsealCommit = "61d7596c7ce82b6f320e5c79ff4598856f4ae7d3";
    private const string GameUdpPortsWithoutWarp = "1024-1700,1702-2407,2409-4499,4501-65535";
    // Phasmophobia stays stability-first: service domains are handled by
    // hostlist rules, while long-lived Photon/Vivox UDP sessions stay direct.

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
        bool warpCompatibilityMode = false)
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
        var includeDiscordVoice = profiles.Discord;
        var includePhasmophobia = profiles.GameFilter && gameFilter.SelectedGames.Contains("phasmophobia", StringComparer.OrdinalIgnoreCase);
        var includeRoblox = profiles.GameFilter && gameFilter.SelectedGames.Contains("roblox", StringComparer.OrdinalIgnoreCase);

        if (!includeYouTubeWeb && !includeDiscordWeb && !includeTelegramWeb && !includeTelegramDesktop && !includePhasmophobia && !includeRoblox && !includeWarpBootstrap && string.IsNullOrWhiteSpace(customBypassListPath))
            return string.Empty;

        var tcpPorts = new SortedSet<int> { 443 };
        if (includeYouTubeWeb || includeDiscordWeb || includeRoblox || includePhasmophobia || !string.IsNullOrWhiteSpace(customBypassListPath))
            tcpPorts.Add(80);
        var tcpPortRanges = new List<string>();
        var udpPorts = new List<string> { "443" };
        if (includeWarpBootstrap) udpPorts.Add("2408");
        if (includeDiscordVoice)
        {
            foreach (var port in new[] { 2053, 2083, 2087, 2096, 8443 }) tcpPorts.Add(port);
            udpPorts.Add("19294-19344");
            udpPorts.Add("50000-50100");
        }

        var gameProtocol = NormalizeGameProtocol(gameFilter.Protocol);
        var includeGameFilter = profiles.GameFilter && (gameFilter.HasSelectedGames || includePhasmophobia || includeRoblox);
        if (includeGameFilter)
        {
            if (gameProtocol is "all" or "tcp")
                tcpPortRanges.Add("1024-65535");
            if (gameProtocol is "all" or "udp")
                udpPorts.Add(GameUdpPortsWithoutWarp);
        }

        var capture = new List<string>
        {
            $"--wf-tcp={string.Join(',', tcpPorts.Select(x => x.ToString()).Concat(tcpPortRanges))}",
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
                  TcpStrategy(level, tuning, "681", "files\\tls_clienthello_www_google_com.bin") + ExclusionOption(exclusionListPath) + " --new");
        rules.Add($"--filter-tcp=443 --ipset=files\\list-doh-ipset.txt " +
                  TcpStrategy(level, tuning, "681", "files\\tls_clienthello_www_google_com.bin") + ExclusionOption(exclusionListPath) + " --new");

        if (includeYouTubeWeb)
        {
            // Media connections are short-lived and highly parallel. Let fixed,
            // bounded rules win before the user's stronger generic strategy so
            // every avatar or video range does not inherit 8-18 TCP repeats.
            const string youtubeImageDomains = "ggpht.com,ytimg.com,googleusercontent.com,gstatic.com,cloudflare-ech.com";
            const string youtubeVideoDomains = "googlevideo.com,video.google.com,redirector.googlevideo.com";
            rules.Add($"--filter-tcp=443 --hostlist-domains={youtubeImageDomains} " +
                      YoutubeFastTcpStrategy() + ExclusionOption(exclusionListPath) + " --new");
            rules.Add($"--filter-udp=443 --hostlist-domains={youtubeImageDomains} " +
                      YoutubeFastQuicStrategy() + ExclusionOption(exclusionListPath) + " --new");
            rules.Add($"--filter-tcp=443 --hostlist-domains={youtubeVideoDomains} " +
                      YoutubeFastTcpStrategy() + ExclusionOption(exclusionListPath) + " --new");
            rules.Add($"--filter-udp=443 --hostlist-domains={youtubeVideoDomains} " +
                      YoutubeFastQuicStrategy() + ExclusionOption(exclusionListPath) + " --new");
            // Firefox can hide the original SNI with ECH and video shards use
            // short-lived rr*.googlevideo.com names. The IP fallback ensures
            // those flows still receive the same bounded YouTube strategy.
            rules.Add("--filter-tcp=443 --ipset=files\\list-youtube-ipset.txt " +
                      YoutubeFastTcpStrategy() + ExclusionOption(exclusionListPath) + " --new");
            rules.Add("--filter-udp=443 --ipset=files\\list-youtube-ipset.txt " +
                      YoutubeFastQuicStrategy() + ExclusionOption(exclusionListPath) + " --new");
            AddDomainRules(rules, "files\\list-youtube.txt", level, tuning, includeHttp: true, includeQuic: true, FastQuicRepeats(tuning), exclusionListPath);
        }

        if (includeDiscordWeb)
        {
            // Attachments, avatars and embedded media are latency-sensitive.
            // Let a small bounded profile win before the broader Discord rule;
            // Discord voice remains isolated in its UDP port rule below.
            const string discordCdnDomains = "cdn.discordapp.com,media.discordapp.net,images-ext-1.discordapp.net,images-ext-2.discordapp.net";
            rules.Add($"--filter-tcp=443 --hostlist-domains={discordCdnDomains} " +
                      TcpStrategy("moderate", 0, "681", "files\\tls_clienthello_www_google_com.bin") + ExclusionOption(exclusionListPath) + " --new");
            rules.Add($"--filter-udp=443 --hostlist-domains={discordCdnDomains} " +
                      "--filter-l7=quic --dpi-desync=fake --dpi-desync-repeats=2 --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2" + ExclusionOption(exclusionListPath) + " --new");
            AddDomainRules(rules, "files\\list-discord.txt", level, tuning, includeHttp: true, includeQuic: true, StableQuicRepeats(level, tuning), exclusionListPath);
        }

        if (includeTelegramWeb || includeTelegramDesktop)
        {
            // Known Web/WebSocket/media names should win before the heavier raw
            // IP rule. With the IP rule first every hosts-pinned request inherited
            // SYN-data processing and all three Web probes timed out together,
            // while the base page needed minutes to retry. Keep TCP/443 in the
            // IP fallback for hidden/raw MTProto gateways, but use it only when
            // no Telegram hostname rule matched.
            AddTelegramDomainRules(rules, includeTelegramWeb, exclusionListPath);
            var telegramPorts = includeTelegramDesktop ? "443,5222" : "443";
            var ipsetOption = $"--ipset={QuotePath("files\\list-telegram-ipset.txt")}";
            rules.Add($"--filter-tcp={telegramPorts} {ipsetOption} " + TelegramIpStrategy() + ExclusionOption(exclusionListPath) + " --new");
        }

        if (includeRoblox)
            AddDomainRules(rules, "files\\list-roblox.txt", level, tuning, includeHttp: true, includeQuic: true, StableQuicRepeats(level, tuning), exclusionListPath);

        if (includePhasmophobia)
            AddDomainRules(rules, "files\\list-phasmophobia.txt", level, tuning, includeHttp: true, includeQuic: true, StableQuicRepeats(level, tuning), exclusionListPath);

        if (!string.IsNullOrWhiteSpace(customBypassListPath))
            AddDomainRules(rules, customBypassListPath, level, tuning, includeHttp: true, includeQuic: true, StableQuicRepeats(level, tuning), exclusionListPath);

        if (includeDiscordVoice)
        {
            rules.Add($"--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun " +
                      "--dpi-desync=fake --dpi-desync-fake-discord=files\\ACTIVE_DISCORD_UDP.bin " +
                      "--dpi-desync-fake-stun=files\\ACTIVE_DISCORD_UDP.bin " +
                      $"--dpi-desync-repeats={VoiceRepeats(tuning)} --dpi-desync-cutoff=d2 --new");
            rules.Add("--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media " +
                      TcpStrategy(level, tuning, "681", "files\\tls_clienthello_www_google_com.bin") + ExclusionOption(exclusionListPath) + " --new");
        }

        if (includeGameFilter)
        {
            if (gameProtocol is "all" or "udp")
            {
                var gameUdpRepeats = Math.Clamp(2 + NormalizeTuning(tuning), 2, 5);
                rules.Add($"--filter-udp={GameUdpPortsWithoutWarp} --dpi-desync=fake --dpi-desync-repeats={gameUdpRepeats} " +
                          "--dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp=files\\ACTIVE_GAME_UDP.bin " +
                          "--dpi-desync-cutoff=d2 --new");
            }

            if (gameProtocol is "all" or "tcp")
            {
                rules.Add($"--filter-tcp=1024-65535 --dpi-desync=multisplit --dpi-desync-any-protocol=1 " +
                          "--dpi-desync-cutoff=n3 --dpi-desync-split-seqovl=568 --dpi-desync-split-pos=1 " +
                          "--dpi-desync-split-seqovl-pattern=files\\tls_clienthello_4pda_to.bin --new");
            }
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

    private static void AddTelegramDomainRules(List<string> rules, bool includeQuic, string? exclusionListPath)
    {
        var exclusion = ExclusionOption(exclusionListPath);
        rules.Add("--filter-tcp=443 --hostlist=files\\list-telegram.txt " +
                  TelegramWebTcpStrategy() + exclusion + " --new");
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

    private static string YoutubeFastQuicStrategy() =>
        // Match the current Flowseal general preset. Eleven fakes made some
        // Firefox/ISP combinations retry QUIC before falling back, which was
        // visible as very slow Shorts and thumbnails.
        "--filter-l7=quic --dpi-desync=fake --dpi-desync-repeats=6 " +
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

    public static int NormalizeTuning(int tuning) => Math.Clamp(tuning, 0, 3);
}
