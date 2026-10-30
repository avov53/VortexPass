using VortexPass.Core.Models;
using System.Text.RegularExpressions;

namespace VortexPass.Core.Services;

public static class WinwsProfileComposer
{
    // Documented WARP ingress/API ranges plus the exact consumer proxy edges observed in warp-svc.
    public const string WarpIngressIps="162.159.192.0/24,162.159.193.0/24,162.159.197.0/24,162.159.198.2,162.159.137.105,162.159.138.105,2606:4700:100::/48,2606:4700:102::/48,2606:4700:103::2";
    private static string StrongWarpBootstrap()=>
        "--wf-tcp=443 --wf-udp=443,2408 "+
        "--filter-tcp=443 --hostlist=files\\list-warp.txt --dpi-desync=fake,multidisorder --dpi-desync-repeats=6 --dpi-desync-split-pos=1,midsld --dpi-desync-fooling=badseq --dpi-desync-fake-tls=files\\tls_clienthello_www_google_com.bin --dpi-desync-cutoff=n3 --new "+
        $"--filter-tcp=443 --ipset-ip={WarpIngressIps} --dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-cutoff=n3 --new "+
        $"--filter-udp=443 --ipset-ip={WarpIngressIps} --filter-l7=quic,unknown --dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-repeats=6 --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-fake-unknown-udp=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=n8 --new "+
        $"--filter-udp=2408 --ipset-ip={WarpIngressIps} --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=n8 --new";
    public const string FlowsealVersion = "1.10.3";
    public const string FlowsealCommit = "865da4f4c3659523bf79bc6edf0446e7d7969614";
    private const string PhasmophobiaVoiceUdpPorts = "5055-5058,27000-27002,12000-65000";

    public static string Compose(BypassProfileSettings profiles, string level) =>
        Compose(profiles, level, new GameFilterSettings(), null, null, 0);

    /// <summary>
    /// Creates a second, deliberately narrow winws instance used only while the
    /// official Cloudflare client establishes and maintains its tunnel. Keeping
    /// this command separate means WARP can be toggled without restarting the
    /// user's YouTube/Discord/Telegram protection process.
    /// </summary>
    public static string ComposeWarpBootstrap(string tcpMode = "default") =>
        tcpMode=="strong"?StrongWarpBootstrap():
        "--wf-tcp=443 --wf-udp=443,2408 " +
        "--filter-tcp=443 --hostlist=files\\list-warp.txt " +
        (tcpMode switch
        {
            "default" => TcpStrategy("moderate", 0, "681", "files\\tls_clienthello_www_google_com.bin"),
            "split" => "--dpi-desync=multisplit --dpi-desync-split-pos=1,midsld --dpi-desync-cutoff=n3",
            "disorder" => "--dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-cutoff=n3",
            _ => throw new ArgumentOutOfRangeException(nameof(tcpMode))
        }) + " --new " +
        "--filter-udp=443 --hostlist=files\\list-warp.txt --filter-l7=quic --dpi-desync=fake " +
        "--dpi-desync-repeats=3 --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2 --new " +
        $"--filter-udp=2408 --ipset-ip={WarpIngressIps} --dpi-desync=fake --dpi-desync-repeats=3 --dpi-desync-any-protocol=1 " +
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
        string discordMobileStrategy = "primary",
        AdvisorStrategySpec? advisorStrategy = null, IReadOnlyList<SiteRule>? siteRules = null)
    {
        level = NormalizeLevel(level);
        tuning = NormalizeTuning(tuning);
        var baseTuning = tuning;
        if (advisorStrategy is not null)
        {
            AdvisorStrategyPolicy.Validate(advisorStrategy, level);
            tuning = advisorStrategy.Tuning;
            // Network-independent, validated templates; no ISP lookup or WARP side effect.
            if (advisorStrategy.RouteTemplate != "inherit") mobileCompatibilityMode = advisorStrategy.RouteTemplate == "narrow";
            if (advisorStrategy.YouTubeTransport != "inherit") youtubeTransport = advisorStrategy.YouTubeTransport;
            if (advisorStrategy.DiscordVoice != "inherit") discordVoiceMode = advisorStrategy.DiscordVoice;
        }
        var includeYouTubeWeb = profiles.YouTube || profiles.Web;
        var includeDiscordWeb = profiles.Discord || profiles.Web;
        // Once WARP is connected Telegram already travels inside the encrypted
        // system tunnel. Direct Telegram desync/ipset rules can only interfere
        // with CDN selection there, so retain the user's selections but suppress
        // their wire rules until the ordinary route is restored.
        var includeTelegramWeb = profiles.TelegramWeb && !warpCompatibilityMode;
        var includeTelegramDesktop = profiles.Telegram && !warpCompatibilityMode;
        var normalizedVoiceMode = NormalizeDiscordVoiceMode(discordVoiceMode);
        var includeDiscordVoice = profiles.Discord && (normalizedVoiceMode == "minimal" || normalizedVoiceMode == "auto" && !mobileCompatibilityMode);
        var includePhasmophobia = profiles.GameFilter && gameFilter.SelectedGames.Contains("phasmophobia", StringComparer.OrdinalIgnoreCase);
        var includeRoblox = profiles.GameFilter && gameFilter.SelectedGames.Contains("roblox", StringComparer.OrdinalIgnoreCase);
        var includeDeadByDaylight = profiles.GameFilter && gameFilter.SelectedGames.Contains("deadbydaylight", StringComparer.OrdinalIgnoreCase);
        var customRules=SiteRulePolicy.Validate(siteRules);

        if (!includeYouTubeWeb && !includeDiscordWeb && !includeTelegramWeb && !includeTelegramDesktop && !includePhasmophobia && !includeRoblox && !includeDeadByDaylight && !includeWarpBootstrap && string.IsNullOrWhiteSpace(customBypassListPath) && customRules.Count==0)
            return string.Empty;

        var tcpPorts = new SortedSet<int> { 443 };
        if (includeYouTubeWeb || includeDiscordWeb || includeRoblox || includePhasmophobia || includeDeadByDaylight || !string.IsNullOrWhiteSpace(customBypassListPath))
            tcpPorts.Add(80);
        var udpPorts = new List<string> { "443" };
        foreach(var r in customRules.Where(r=>r.Connection is "direct" or "auto" && r.Protocol!="quic"))
            foreach(var port in SiteRulePolicy.Ports(r.Ports).DefaultIfEmpty(443))tcpPorts.Add(port);
        if (includeWarpBootstrap) udpPorts.Add("2408");
        if (includeDiscordVoice)
        {
            foreach (var port in new[] { 2053, 2083, 2087, 2096, 8443 }) tcpPorts.Add(port);
            udpPorts.Add("19294-19344");
            udpPorts.Add("50000-50100");
        }
        if (includePhasmophobia && (!mobileCompatibilityMode || advisorStrategy?.RouteTemplate == "narrow"))
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
        var dnsStrategy = TcpStrategy(level == "light" ? "light" : "complex", 0, "681", "files\\tls_clienthello_www_google_com.bin") + " --dpi-desync-cutoff=n3";
        rules.Add($"--filter-tcp=443 --hostlist=files\\list-doh.txt " +
                  dnsStrategy + " --new");
        rules.Add($"--filter-tcp=443 --ipset=files\\list-doh-ipset.txt " +
                  dnsStrategy + " --new");
        foreach(var item in customRules.Where(r=>r.Connection is "auto" or "direct").SelectMany(r=>SiteRulePolicy.Domains(r).Select(domain=>(Rule:r,Domain:domain))).OrderByDescending(item=>item.Domain.Length))
        {
            var r=item.Rule;var domains=item.Domain;
            if(r.Protocol!="quic")
            {
                var ports=SiteRulePolicy.Ports(r.Ports);if(ports.Length==0)ports=[443];
                var ruleLevel=level!="light"&&r.Connection=="auto"&&advisorStrategy?.RouteTemplate=="narrow"?"moderate":level;
                var strategy=TcpStrategy(ruleLevel,r.Connection=="auto"?0:tuning,"681","files\\tls_clienthello_www_google_com.bin");
                strategy=Regex.Replace(strategy,@"--dpi-desync-repeats=(\d+)",m=>$"--dpi-desync-repeats={Math.Min(int.Parse(m.Groups[1].Value),AdvisorStrategyPolicy.MaxTcpRepeats(level))}");
                rules.Add($"--filter-tcp={string.Join(',',ports)} --hostlist-domains={domains} {strategy} --dpi-desync-cutoff=n3"+ExclusionOption(exclusionListPath)+" --new");
            }
            if(r.Protocol=="quic" || r.Protocol=="auto" && (string.IsNullOrEmpty(r.Ports)||SiteRulePolicy.Ports(r.Ports).Contains(443)) && !r.Components.Any(c=>c is "voice" or "game"))
                rules.Add($"--filter-udp=443 --hostlist-domains={domains} --filter-l7=quic --dpi-desync=fake --dpi-desync-repeats=2 --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2"+ExclusionOption(exclusionListPath)+" --new");
        }

        if (includeYouTubeWeb)
        {
            var includeYouTubeQuic = !NormalizeYoutubeTransport(youtubeTransport).Equals("http2", StringComparison.Ordinal);
            // Media connections are short-lived and highly parallel. Let fixed,
            // bounded rules win before the user's stronger generic strategy so
            // every avatar or video range does not inherit 8-18 TCP repeats.
            const string youtubeImageDomains = "ggpht.com,ytimg.com,googleusercontent.com,gstatic.com,cloudflare-ech.com";
            const string youtubeVideoDomains = "googlevideo.com,video.google.com,redirector.googlevideo.com";
            var youtubeTcpStrategy = mobileCompatibilityMode
                ? MobileYoutubeTcpStrategy(youtubeMobileStrategy)
                : YoutubeFastTcpStrategy();
            rules.Add($"--filter-tcp=443 --hostlist-domains={youtubeImageDomains} " +
                      ContentTcpStrategy() + ExclusionOption(exclusionListPath) + " --new");
            if (includeYouTubeQuic)
                rules.Add($"--filter-udp=443 --hostlist-domains={youtubeImageDomains} " +
                          YoutubeFastQuicStrategy(mobileCompatibilityMode) + ExclusionOption(exclusionListPath) + " --new");
            rules.Add($"--filter-tcp=443 --hostlist-domains={youtubeVideoDomains} " +
                      youtubeTcpStrategy + ExclusionOption(exclusionListPath) + " --new");
            if (includeYouTubeQuic)
                rules.Add($"--filter-udp=443 --hostlist-domains={youtubeVideoDomains} " +
                          YoutubeFastQuicStrategy(mobileCompatibilityMode) + ExclusionOption(exclusionListPath) + " --new");
            // Specific hostname rules precede the broad Google IP fallback.
            AddDomainRules(rules, "files\\list-youtube.txt", mobileCompatibilityMode ? "moderate" : level,
                mobileCompatibilityMode ? 0 : tuning, includeHttp: true, includeQuic: includeYouTubeQuic,
                mobileCompatibilityMode ? 2 : FastQuicRepeats(tuning), exclusionListPath);
            // Firefox can hide the original SNI with ECH and video shards use
            // short-lived rr*.googlevideo.com names. The IP fallback ensures
            // those flows still receive the same bounded YouTube strategy.
            rules.Add("--filter-tcp=443 --ipset=files\\list-youtube-ipset.txt " +
                      youtubeTcpStrategy + ExclusionOption(exclusionListPath) + " --new");
            if (includeYouTubeQuic)
                rules.Add("--filter-udp=443 --ipset=files\\list-youtube-ipset.txt " +
                          YoutubeFastQuicStrategy(mobileCompatibilityMode) + ExclusionOption(exclusionListPath) + " --new");
        }

        if (includeDiscordWeb)
        {
            // Upstream ALT9 places Discord's Google-hosted updater in list-google,
            // before the ordinary Discord block. Preserve that first-match distinction.
            if (advisorStrategy?.Discord == "flowseal-alt9")
                rules.Add("--filter-tcp=443 --hostlist-domains=stable.dl2.discordapp.net " +
                    FlowsealTcpBlock("flowseal-alt9", level, google: true) + " --dpi-desync-cutoff=n3" + ExclusionOption(exclusionListPath) + " --new");
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
                      ContentTcpStrategy() +
                      ExclusionOption(exclusionListPath) + " --new");
            // Thumbnail API is a separate hop from the returned rbxcdn image.
            rules.Add("--filter-tcp=443 --hostlist-domains=thumbnails.roblox.com " +
                      ContentTcpStrategy() + ExclusionOption(exclusionListPath) + " --new");
            rules.Add("--filter-udp=443 --hostlist-domains=rbxcdn.com --filter-l7=quic --dpi-desync=fake " +
                      $"--dpi-desync-repeats={(mobileCompatibilityMode ? 2 : 3)} --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2" +
                      ExclusionOption(exclusionListPath) + " --new");
            AddDomainRules(rules, "files\\list-roblox.txt", mobileCompatibilityMode ? "moderate" : level,
                mobileCompatibilityMode ? 0 : baseTuning, includeHttp: true, includeQuic: true,
                mobileCompatibilityMode ? 2 : StableQuicRepeats(level, baseTuning), exclusionListPath);
        }

        if (includePhasmophobia)
            AddDomainRules(rules, "files\\list-phasmophobia.txt", level, baseTuning, includeHttp: true, includeQuic: true, StableQuicRepeats(level, baseTuning), exclusionListPath);

        if (includeDeadByDaylight)
        {
            // DBD matchmaking endpoints can share infrastructure with Steam,
            // Epic and other games. Keep the first version domain-scoped: a
            // global Steam UDP capture would increase latency for unrelated
            // sessions and can conflict with anti-cheat traffic.
            AddDomainRules(rules, "files\\list-deadbydaylight.txt", mobileCompatibilityMode ? "moderate" : level,
                mobileCompatibilityMode ? 0 : baseTuning, includeHttp: true, includeQuic: true,
                mobileCompatibilityMode ? 2 : StableQuicRepeats(level, baseTuning), exclusionListPath);
        }

        if (!string.IsNullOrWhiteSpace(customBypassListPath))
            AddDomainRules(rules, customBypassListPath, level, baseTuning, includeHttp: true, includeQuic: true, StableQuicRepeats(level, baseTuning), exclusionListPath);

        if (includeDiscordVoice)
        {
            rules.Add($"--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun " +
                      "--dpi-desync=fake --dpi-desync-fake-discord=files\\ACTIVE_DISCORD_UDP.bin " +
                      "--dpi-desync-fake-stun=files\\ACTIVE_DISCORD_UDP.bin " +
                      $"--dpi-desync-repeats={(mobileCompatibilityMode ? 2 : VoiceRepeats(baseTuning))} --dpi-desync-cutoff=d2 --new");
            rules.Add("--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media " +
                      TcpStrategy(mobileCompatibilityMode ? "moderate" : level, mobileCompatibilityMode ? 0 : baseTuning, "681", "files\\tls_clienthello_www_google_com.bin") + ExclusionOption(exclusionListPath) + " --new");
        }

        if (includePhasmophobia && (!mobileCompatibilityMode || advisorStrategy?.RouteTemplate == "narrow"))
        {
            // Vivox can choose UDP ports dynamically. Keep this rule bounded
            // to a selected Phasmophobia profile and deliberately gentle so a
            // long voice session does not inherit the broad game preset.
            rules.Add($"--filter-udp={PhasmophobiaVoiceUdpPorts} --dpi-desync=fake " +
                      "--dpi-desync-repeats=2 --dpi-desync-any-protocol=1 " +
                      "--dpi-desync-fake-unknown-udp=files\\ACTIVE_GAME_UDP.bin --dpi-desync-cutoff=d2 --new");
        }

        if (advisorStrategy is not null)
            rules = rules.Select(rule => ComposeAdvisorRule(rule, advisorStrategy)).ToList();
        var command=$"{string.Join(' ', capture)} {string.Join(' ', rules)}";
        if(command.Length>28000)throw new ArgumentException("Слишком много правил для одной команды winws. Сократите число доменов и портов.");
        return command;
    }

    // Model values are declarative. Only these existing service rules can be
    // changed; capture ports, DNS, WARP, raw-IP Telegram, games and voice stay intact.
    private static string ComposeAdvisorRule(string rule, AdvisorStrategySpec spec)
    {
        if(IsYoutubeRule(rule)&&spec.YouTubeParameters is {} yp)
        {
            YoutubeParametersPolicy.Validate(yp,spec.Level);
            var filter=Regex.Replace(rule,@"(?:--dpi-desync[^\s]*|--ip-id=[^\s]*)\s*","").Replace(" --new","",StringComparison.Ordinal).Trim();
            var parameters=rule.Contains("--filter-tcp=",StringComparison.Ordinal)?YoutubeParametersPolicy.Tcp(yp,rule.Contains("--filter-tcp=443",StringComparison.Ordinal)):
                $"--dpi-desync=fake --dpi-desync-repeats={yp.QuicRepeats} --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin --dpi-desync-cutoff=d2";
            return filter+" "+parameters+" --new";
        }
        string? block = null;
        if (rule.Contains("list-youtube", StringComparison.Ordinal) || rule.Contains("--hostlist-domains=ggpht.com", StringComparison.Ordinal) ||
            rule.Contains("--hostlist-domains=googlevideo.com", StringComparison.Ordinal)) block = spec.YouTube;
        else if (rule.Contains("list-discord.txt", StringComparison.Ordinal) || rule.Contains("--hostlist-domains=cdn.discordapp.com", StringComparison.Ordinal)) block = spec.Discord;
        else if (rule.Contains("list-telegram.txt", StringComparison.Ordinal) || rule.Contains("--hostlist-domains=kws.web.telegram.org", StringComparison.Ordinal)) block = spec.Telegram;
        else if ((rule.Contains("list-roblox.txt",StringComparison.Ordinal)||rule.Contains("--hostlist-domains=rbxcdn.com",StringComparison.Ordinal)||rule.Contains("--hostlist-domains=thumbnails.roblox.com",StringComparison.Ordinal)) && rule.Contains("--filter-tcp=",StringComparison.Ordinal)) block=spec.Roblox;
        if (block is null) return rule;
        if (block == "baseline" && (rule.Contains("list-roblox.txt",StringComparison.Ordinal)||rule.Contains("rbxcdn",StringComparison.Ordinal)||rule.Contains("thumbnails.roblox.com",StringComparison.Ordinal))) return rule;
        if (block == "direct")
            return Regex.Replace(rule, @"(?:--dpi-desync[^\s]*|--ip-id=[^\s]*)\s*", "").Trim();
        var tcp = rule.Contains("--filter-tcp=", StringComparison.Ordinal);
        if (tcp && block != "baseline")
        {
            var pattern = rule.Contains("telegram", StringComparison.Ordinal)
                ? "files\\tls_clienthello_max_ru.bin" : "files\\tls_clienthello_www_google_com.bin";
            var strategy = block switch
            {
                "minimal" when rule.Contains("roblox",StringComparison.Ordinal)||rule.Contains("rbxcdn",StringComparison.Ordinal) => ContentTcpStrategy(),
                "split" when rule.Contains("roblox",StringComparison.Ordinal)||rule.Contains("rbxcdn",StringComparison.Ordinal) => "--dpi-desync=multisplit --dpi-desync-split-pos=midsld --dpi-desync-cutoff=n3",
                "disorder" when rule.Contains("roblox",StringComparison.Ordinal)||rule.Contains("rbxcdn",StringComparison.Ordinal) => "--dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-fooling=badseq --dpi-desync-fake-tls=files\\tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --dpi-desync-repeats=2 --dpi-desync-cutoff=n3",
                "youtube-segment" => "--dpi-desync=multisplit --dpi-desync-split-pos=1,midsld",
                "youtube-tls" => "--dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld " +
                    "--dpi-desync-fooling=badseq --dpi-desync-fake-tls=files\\tls_clienthello_www_google_com.bin " +
                    "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com " +
                    $"--dpi-desync-repeats={Math.Min(6,AdvisorStrategyPolicy.MaxTcpRepeats(spec.Level))}",
                "minimal" => TcpStrategy("light", 0, "681", pattern),
                "split" => TcpStrategy("moderate", 0, "681", pattern),
                "disorder" => pattern.Contains("max_ru", StringComparison.Ordinal) ? TelegramWebTcpStrategy() : YoutubeFastTcpStrategy(),
                "flowseal-alt9" or "flowseal-general" or "flowseal-alt2" or "flowseal-fake-tls" => FlowsealTcpBlock(block, spec.Level,
                    rule.Contains("--filter-tcp=443", StringComparison.Ordinal) &&
                    (rule.Contains("youtube", StringComparison.Ordinal) || rule.Contains("ggpht.com", StringComparison.Ordinal) || rule.Contains("googlevideo.com", StringComparison.Ordinal))),
                _ => throw new ArgumentException("Неизвестный блок ИИ")
            };
            rule = Regex.Replace(rule, @"(?:--dpi-desync[^\s]*|--ip-id=[^\s]*)\s*", "").Trim();
            rule = rule.Replace(" --new", "", StringComparison.Ordinal) + " " + strategy + " --dpi-desync-cutoff=n3 --new";
        }
        if (spec.RouteTemplate != "inherit" || block != "baseline" && (rule.Contains("roblox",StringComparison.Ordinal) || rule.Contains("rbxcdn",StringComparison.Ordinal)))
        {
            // Verified templates still obey the selected level's resource limits.
            rule = Regex.Replace(rule, @"--dpi-desync-repeats=(\d+)", match =>
                $"--dpi-desync-repeats={Math.Min(int.Parse(match.Groups[1].Value), tcp ? AdvisorStrategyPolicy.MaxTcpRepeats(spec.Level) : 4)}");
            rule = Regex.Replace(rule, @"--dpi-desync-split-seqovl=(\d+)", match =>
                $"--dpi-desync-split-seqovl={Math.Min(int.Parse(match.Groups[1].Value), AdvisorStrategyPolicy.MaxOverlap(spec.Level))}");
        }
        if (tcp && spec.TcpRepeats is { } repeat)
            rule = SetAdvisorOption(rule, "dpi-desync-repeats", repeat);
        if (!tcp && spec.QuicRepeats is { } quic)
            rule = SetAdvisorOption(rule, "dpi-desync-repeats", quic);
        if (tcp && spec.Overlap is { } overlap && rule.Contains("--dpi-desync-split-seqovl=", StringComparison.Ordinal))
            rule = SetAdvisorOption(rule, "dpi-desync-split-seqovl", overlap);
        return rule;
    }

    public static bool IsYoutubeRule(string rule)=>rule.Contains("list-youtube",StringComparison.Ordinal)||
        rule.Contains("--hostlist-domains=ggpht.com",StringComparison.Ordinal)||rule.Contains("--hostlist-domains=googlevideo.com",StringComparison.Ordinal);
    public static string ComposeYoutubeOnly(string original,AdvisorStrategySpec baseline,AdvisorStrategySpec candidate)
    {
        YoutubeOnlyPolicy.Validate(baseline,candidate);
        if(candidate==baseline)return original;
        var rules=original.Split(" --new",StringSplitOptions.None);
        var indexes=Enumerable.Range(0,rules.Length).Where(i=>IsYoutubeRule(rules[i])).ToArray();
        if(indexes.Length==0)throw new ArgumentException("В текущей команде нет блока YouTube");
        // Reorder only YouTube slots: hostname filters must precede the Google IP fallback.
        var selected=indexes.Select(i=>rules[i]).Where(r=>candidate.YouTubeTransport!="http2"||!r.Contains("--filter-udp=",StringComparison.Ordinal))
            .OrderBy(r=>r.Contains("--ipset=",StringComparison.Ordinal)?1:0)
            .Select(r=>" "+ComposeAdvisorRule(r.Trim()+" --new",candidate).Replace(" --new","",StringComparison.Ordinal)).ToArray();
        if(selected.Length!=indexes.Length)throw new ArgumentException("Изменение транспортного охвата требует отдельного набора правил; этот подбор сохраняет QUIC-охват");
        for(var i=0;i<indexes.Length;i++)rules[indexes[i]]=selected[i];
        var result=string.Join(" --new",rules);
        if(result.Length>28000)throw new ArgumentException("Команда YouTube превышает безопасный предел длины winws");
        YoutubeOnlyPolicy.AssertIsolated(original,result);return result;
    }

    public static string ApplyRobloxTcpBlock(string command,string block,string level)
    {
        if(block is not ("baseline" or "minimal" or "split" or "disorder")||!AdvisorStrategyPolicy.Blocks(level).Contains(block))throw new ArgumentException("Недопустимый блок Roblox");
        if(block=="baseline")return command;
        var spec=new AdvisorStrategySpec {Level=level,Roblox=block};
        return string.Join(" --new",command.Split(" --new").Select(rule=>
            rule.Contains("--filter-tcp=",StringComparison.Ordinal)&&(rule.Contains("list-roblox.txt",StringComparison.Ordinal)||rule.Contains("--hostlist-domains=rbxcdn.com",StringComparison.Ordinal)||rule.Contains("--hostlist-domains=thumbnails.roblox.com",StringComparison.Ordinal))
            ? (rule.StartsWith(" ",StringComparison.Ordinal)?" ":"")+ComposeAdvisorRule(rule.Trim()+" --new",spec).Replace(" --new","",StringComparison.Ordinal):rule));
    }

    private static string SetAdvisorOption(string rule, string option, int value) =>
        Regex.IsMatch(rule, $@"--{option}=\S+")
            ? Regex.Replace(rule, $@"--{option}=\S+", $"--{option}={value}")
            : rule.Replace(" --new", $" --{option}={value} --new", StringComparison.Ordinal);

    // Pinned Flowseal 1.10.3 TCP blocks, applied only to the selected service.
    // Counts and overlap obey the requested level; no global IPSet/GameFilter is imported.
    public static string FlowsealTcpBlock(string block, string level, bool google = false)
    {
        if (!AdvisorStrategyPolicy.Blocks(level).Contains(block)) throw new ArgumentException("Блок недоступен для выбранного уровня.");
        var overlap = Math.Min(block == "flowseal-alt2" ? 652 : block == "flowseal-general" && !google ? 568 : 681, AdvisorStrategyPolicy.MaxOverlap(level));
        var pattern = block == "flowseal-general" && !google ? "files\\tls_clienthello_4pda_to.bin" : "files\\tls_clienthello_www_google_com.bin";
        var split = $"--dpi-desync-split-pos={(block == "flowseal-alt2" ? 2 : 1)} --dpi-desync-split-seqovl={overlap} --dpi-desync-split-seqovl-pattern={pattern}";
        return block switch
        {
            "flowseal-alt9" => (google ? "--ip-id=zero " : "") +
                $"--dpi-desync=hostfakesplit --dpi-desync-repeats={Math.Min(4,AdvisorStrategyPolicy.MaxTcpRepeats(level))} " +
                $"--dpi-desync-fooling={(google ? "ts" : "ts,md5sig")} --dpi-desync-hostfakesplit-mod=host={(google ? "www.google.com" : "ozon.ru")}",
            "flowseal-general" or "flowseal-alt2" => (google ? "--ip-id=zero " : "") + "--dpi-desync=multisplit " + split,
            "flowseal-fake-tls" => (google ? "--ip-id=zero " : "") + "--dpi-desync=fake,multisplit " + split +
                $" --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=10000000 --dpi-desync-repeats={Math.Min(8, AdvisorStrategyPolicy.MaxTcpRepeats(level))} --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com",
            _ => throw new ArgumentException("Неизвестный блок Flowseal.")
        };
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
        var websocketDomains = "kws.web.telegram.org,zws.web.telegram.org," + string.Join(",", TelegramEndpointRegistry.TransportHosts);
        // Mobile networks need an independently tunable WebSocket route. The
        // proven home profile must let these names fall through to the complete
        // Telegram hostlist below. A duplicate first-match rule left the Web
        // client at "waiting for network" on otherwise working home routes.
        if (mobileCompatibilityMode)
            rules.Add($"--filter-tcp=443 --hostlist-domains={websocketDomains} " +
                      MobileTelegramWebSocketStrategy(level) + exclusion + " --new");

        rules.Add("--filter-tcp=443 --hostlist=files\\list-telegram.txt --hostlist-domains=" + websocketDomains + " " +
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

    // CDN defaults must not inject overlapping fake bytes into real TLS records.
    // Explicit measured per-service candidates can still replace this block.
    private static string ContentTcpStrategy() => "--dpi-desync=multisplit --dpi-desync-split-pos=1,midsld --dpi-desync-cutoff=n3";

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
