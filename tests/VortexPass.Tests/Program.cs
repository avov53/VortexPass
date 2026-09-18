using System.Net;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("YouTube uses bounded fast QUIC", Sync(TestYoutubeQuic)),
    ("YouTube comment avatar DNS hosts are allowed", Sync(TestYoutubeAvatarHosts)),
    ("Telegram Desktop and Web are separate", Sync(TestTelegramSeparation)),
    ("Telegram ping ignores slow page loading", Sync(TestTelegramReportedLatency)),
    ("Discord voice is isolated and stable", Sync(TestDiscordVoice)),
    ("Discord CDN uses a bounded fast route", Sync(TestDiscordCdnRoute)),
    ("DNS chooses the lowest latency success", Sync(TestDnsSelection)),
    ("Quad9 DNS wire messages are valid", Sync(TestDnsWireFormat)),
    ("Startup scheduler arguments are safe", Sync(TestStartupArguments)),
    ("WARP status and installer source are safe", Sync(TestWarpIntegration)),
    ("WARP transport is excluded from DPI", Sync(TestWarpDpiExclusions)),
    ("WARP bootstrap is narrow and restorable", Sync(TestWarpBootstrap)),
    ("WARP diagnostics redact registration secrets", Sync(TestWarpDiagnosticRedaction)),
    ("WARP endpoint statuses are classified safely", Sync(TestWarpProbeClassification)),
    ("WARP Telegram groups fail independently", TestWarpScenarioIndependence),
    ("WARP Telegram compatibility is reversible", Sync(TestWarpTelegramCompatibility)),
    ("Telegram sticker CDN hosts are covered", Sync(TestTelegramStickerHosts)),
    ("Telegram hosts fallback excludes Discord media", Sync(TestTelegramHostsFallbackScope)),
    ("Unrelated DNS entries do not suppress Telegram fallback", Sync(TestTelegramFallbackWithOtherProfiles)),
    ("Cloudflare trace and PoP location are parsed", Sync(TestCloudflarePopParsing)),
    ("WARP performance verdict is conservative", Sync(TestWarpPerformanceVerdict)),
    ("WARP routing profiles resolve safely", Sync(TestWarpRoutingProfiles)),
    ("WARP route compatibility follows actual profile", Sync(TestWarpActualRouteCompatibility)),
    ("WARP Telegram fallback selects a reachable route", Sync(TestWarpMeasuredRouteSelection)),
    ("WARP PAC is selective and reversible", Sync(TestWarpPacGeneration)),
    ("WARP PAC URL refreshes for browsers", TestWarpPacRefresh),
    ("WARP auto protocol picks lowest median", Sync(TestWarpProtocolSelection))
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures.Add($"FAIL {test.Name}: {ex.Message}"); }
}
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

static void TestYoutubeQuic()
{
    var command = WinwsProfileComposer.Compose(new BypassProfileSettings { YouTube = true }, "aggressive", new(), null, null, 3);
    Require(command.Contains("list-youtube.txt", StringComparison.Ordinal), "YouTube hostlist missing");
    Require(command.Contains("--dpi-desync-repeats=4 --dpi-desync-fake-quic", StringComparison.Ordinal), "QUIC repeats are not bounded to 4");
    var imageDomains = "--hostlist-domains=ggpht.com,ytimg.com,googleusercontent.com,gstatic.com,cloudflare-ech.com";
    var videoDomains = "--hostlist-domains=googlevideo.com,video.google.com,redirector.googlevideo.com";
    Require(command.Contains(imageDomains, StringComparison.Ordinal), "Latency-sensitive YouTube images have no narrow rule");
    Require(command.Contains(videoDomains, StringComparison.Ordinal), "YouTube video CDN has no narrow rule");
    Require(command.IndexOf(imageDomains, StringComparison.Ordinal) < command.IndexOf("--hostlist=files\\list-youtube.txt", StringComparison.Ordinal) &&
            command.IndexOf(videoDomains, StringComparison.Ordinal) < command.IndexOf("--hostlist=files\\list-youtube.txt", StringComparison.Ordinal),
        "Broad YouTube strategy wins before media routes");
    Require(command.Split("--dpi-desync=fake,multidisorder --dpi-desync-fake-tls-mod=sni=www.google.com", StringSplitOptions.None).Length - 1 >= 3,
        "YouTube image/video/IP TCP routes do not use the current Google strategy");
    Require(command.Contains("--filter-tcp=443 --ipset=files\\list-youtube-ipset.txt", StringComparison.Ordinal) &&
            command.Contains("--filter-udp=443 --ipset=files\\list-youtube-ipset.txt", StringComparison.Ordinal),
        "YouTube has no IP fallback for ECH or dynamic video shards");
    Require(command.Split("--dpi-desync-repeats=6 --dpi-desync-fake-quic", StringSplitOptions.None).Length - 1 >= 2,
        "YouTube image/video HTTP/3 bypass does not match the bounded Flowseal route");
    Require(!command.Contains("--dpi-desync-repeats=11 --dpi-desync-fake-quic", StringComparison.Ordinal),
        "YouTube media still uses the retry-heavy QUIC route");
}

static void TestYoutubeAvatarHosts()
{
    foreach (var index in Enumerable.Range(3, 4))
    {
        Require(HostsFileManager.IsAllowedDynamicHostForTests($"yt{index}.ggpht.com"),
            $"YouTube avatar host yt{index}.ggpht.com is rejected from temporary DNS");
        Require(HostsFileManager.IsAllowedDynamicHostForTests($"yt{index}.googleusercontent.com"),
            $"YouTube avatar host yt{index}.googleusercontent.com is rejected from temporary DNS");
    }
    Require(!HostsFileManager.IsAllowedDynamicHostForTests("rr1---sn.example.googlevideo.com"),
        "YouTube video CDN was pinned instead of remaining dynamic");
}

static void TestTelegramSeparation()
{
    var desktop = WinwsProfileComposer.Compose(new BypassProfileSettings { Telegram = true }, "medium", new(), null, null, 0);
    var web = WinwsProfileComposer.Compose(new BypassProfileSettings { TelegramWeb = true }, "medium", new(), null, null, 0);
    Require(!desktop.Contains("--filter-l7=quic", StringComparison.Ordinal), "Desktop unexpectedly captures QUIC");
    Require(web.Contains("--filter-l7=quic", StringComparison.Ordinal), "Telegram Web QUIC rule missing");
    var aggressiveWeb = WinwsProfileComposer.Compose(new BypassProfileSettings { TelegramWeb = true }, "aggressive", new(), null, null, 3);
    Require(aggressiveWeb.Contains("--filter-tcp=443 --ipset=files\\list-telegram-ipset.txt", StringComparison.Ordinal),
        "Telegram Web update channel lost its raw-IP TCP/443 fallback");
    Require(aggressiveWeb.Contains("--hostlist=files\\list-telegram.txt --dpi-desync=fake,multidisorder --dpi-desync-repeats=6 --dpi-desync-fooling=badseq", StringComparison.Ordinal),
        "Telegram Web did not receive the bounded modern TLS strategy");
    Require(aggressiveWeb.Contains("--dpi-desync-fake-tls-mod=rnd,dupsid,sni=max.ru", StringComparison.Ordinal) &&
            aggressiveWeb.Contains("--dpi-desync-cutoff=n3", StringComparison.Ordinal),
        "Telegram Web TLS strategy is not limited to the handshake");
    var combined = WinwsProfileComposer.Compose(new BypassProfileSettings { Telegram = true, TelegramWeb = true }, "aggressive", new(), null, null, 3);
    Require(combined.Contains("--filter-tcp=443,5222 --ipset=files\\list-telegram-ipset.txt", StringComparison.Ordinal),
        "Combined Telegram profile lost the Web update/Desktop raw-IP fallback");
    Require(combined.IndexOf("--hostlist=files\\list-telegram.txt", StringComparison.Ordinal) <
            combined.IndexOf("--ipset=files\\list-telegram-ipset.txt", StringComparison.Ordinal),
        "Heavy Telegram raw-IP fallback wins before the known Web/WebSocket hostname rule");
}

static void TestTelegramReportedLatency()
{
    Require(ServiceHealthCheckService.SelectTelegramReportedLatency(48, 1751) == 48,
        "Telegram page-load time was reported as network ping");
    Require(ServiceHealthCheckService.SelectTelegramReportedLatency(null, 220) is null,
        "Telegram page-load time was reported as ping without a transport measurement");
    Require(ServiceHealthCheckService.ClassifyTelegramWebState(true, false) == ServiceHealthState.Healthy,
        "Confirmed Telegram Web HTTP was overridden by optional probe timeouts");
    Require(ServiceHealthCheckService.ClassifyTelegramWebState(false, true) == ServiceHealthState.Healthy,
        "Working Telegram auxiliary routes were ignored after a primary timeout");
    Require(ServiceHealthCheckService.ClassifyTelegramWebState(false, false) == ServiceHealthState.HttpUnavailable,
        "Telegram was reported healthy without any confirmed Web route");
}

static void TestDiscordVoice()
{
    var broadWeb = WinwsProfileComposer.Compose(new BypassProfileSettings { Web = true }, "aggressive", new(), null, null, 3);
    var discord = WinwsProfileComposer.Compose(new BypassProfileSettings { Discord = true }, "aggressive", new(), null, null, 3);
    Require(!broadWeb.Contains("ACTIVE_DISCORD_UDP.bin", StringComparison.Ordinal), "Broad Web profile captures Discord voice");
    Require(discord.Contains("ACTIVE_DISCORD_UDP.bin", StringComparison.Ordinal), "Discord voice rule missing");
    Require(discord.Contains("--dpi-desync-repeats=4 --dpi-desync-cutoff=d2", StringComparison.Ordinal), "Voice repeats are not bounded");
}

static void TestDiscordCdnRoute()
{
    var command = WinwsProfileComposer.Compose(new BypassProfileSettings { Discord = true }, "aggressive", new(), null, null, 3);
    var cdn = "--hostlist-domains=cdn.discordapp.com,media.discordapp.net,images-ext-1.discordapp.net,images-ext-2.discordapp.net";
    Require(command.Contains(cdn, StringComparison.Ordinal), "Discord CDN route is missing");
    Require(command.IndexOf(cdn, StringComparison.Ordinal) < command.IndexOf("--hostlist=files\\list-discord.txt", StringComparison.Ordinal),
        "Broad Discord strategy wins before the latency-sensitive CDN route");
    var cdnStart = command.IndexOf(cdn, StringComparison.Ordinal);
    var nextRule = command.IndexOf(" --new", cdnStart, StringComparison.Ordinal);
    Require(command[cdnStart..nextRule].Contains("--dpi-desync-repeats=4", StringComparison.Ordinal),
        "Discord CDN TCP route uses aggressive repeats");
    Require(command.Contains($"--filter-udp=443 {cdn} --filter-l7=quic --dpi-desync=fake --dpi-desync-repeats=2", StringComparison.Ordinal),
        "Discord CDN QUIC route is not bounded");
}

static void TestDnsSelection()
{
    var failed = new DnsOverrideResult("example.test", null, "failed", "failure");
    var slow = new DnsOverrideResult("example.test", IPAddress.Parse("1.1.1.1"), "slow", "ok", 80);
    var fast = new DnsOverrideResult("example.test", IPAddress.Parse("8.8.8.8"), "fast", "ok", 12);
    Require(DnsOverrideService.SelectFastest([failed, slow, fast]) == fast, "Fastest successful resolver was not selected");
    DnsOverrideService.ClearCacheForTests();
    Require(DnsOverrideService.CachedEntryCountForTests == 0, "DNS cache could not be invalidated");
}

static void TestDnsWireFormat()
{
    var query = DnsOverrideService.BuildDnsWireQuery("example.com");
    Require(query.Length > 20 && query[2] == 1 && query[5] == 1, "DoH wire query header is invalid");
    // Minimal response: one question and one compressed A answer (1.2.3.4).
    var response = query.ToList();
    response[2] = 0x81;
    response[3] = 0x80;
    response[7] = 1;
    response.AddRange(new byte[] { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 1, 2, 3, 4 });
    var addresses = DnsOverrideService.ParseDnsWireAddresses(response.ToArray());
    Require(addresses.Count == 1 && addresses[0].Equals(IPAddress.Parse("1.2.3.4")), "DoH A response was not parsed");
}

static void TestStartupArguments()
{
    IReadOnlyList<string>? captured = null;
    var service = new WindowsStartupService(
        args => { captured = args.ToArray(); return (0, string.Empty); },
        legacyRunValueExists: () => false,
        deleteLegacyRunValue: () => { });
    service.SetEnabled(true, @"C:\Program Files\VortexPass\VortexPass.exe");
    Require(captured is not null && captured.Contains("/Create"), "Create command missing");
    var taskRun = captured![Array.IndexOf(captured.ToArray(), "/TR") + 1];
    Require(taskRun == "\"C:\\Program Files\\VortexPass\\VortexPass.exe\" --startup", "Executable path or startup flag is not quoted safely");
}

static void TestWarpIntegration()
{
    Require(WarpService.ParseState("Status update: Connected") == WarpConnectionState.Connected, "Connected status was not parsed");
    Require(WarpService.ParseState("Status update: Disconnected") == WarpConnectionState.Disconnected, "Disconnected status was not parsed");
    Require(WarpService.IsOfficialInstallerUri(new Uri(WarpService.InstallerUrl)), "Official installer URL rejected");
    Require(!WarpService.IsOfficialInstallerUri(new Uri("https://example.test/Cloudflare_WARP.msi")), "Untrusted installer host accepted");
    var selected = WarpService.SelectInstalledPath([@"C:\missing\warp-cli.exe", @"C:\Cloudflare\warp-cli.exe"], path => path.Contains("Cloudflare", StringComparison.Ordinal));
    Require(selected == @"C:\Cloudflare\warp-cli.exe", "Existing WARP path was not selected");
}

static void TestWarpDpiExclusions()
{
    var exclusions = DefaultDomainLists.CreateExclusions();
    Require(exclusions.Contains("engage.cloudflareclient.com", StringComparer.OrdinalIgnoreCase), "WARP tunnel endpoint is not excluded");
    Require(exclusions.Contains("api.cloudflareclient.com", StringComparer.OrdinalIgnoreCase), "WARP control endpoint is not excluded");
    Require(exclusions.Contains("cloudflare-dns.com", StringComparer.OrdinalIgnoreCase), "WARP DNS endpoint is not excluded from ordinary DPI");
    var game = WinwsProfileComposer.Compose(
        new BypassProfileSettings { GameFilter = true },
        "medium",
        new GameFilterSettings { Protocol = "udp", SelectedGames = ["phasmophobia"] },
        null,
        null,
        0);
    Require(!game.Contains("--filter-udp=1024-65535", StringComparison.Ordinal), "Broad game rule still captures WARP transport");
    Require(game.Contains("1702-2407,2409-4499,4501-65535", StringComparison.Ordinal), "WARP transport ports are not left direct");
}

static void TestWarpBootstrap()
{
    var command = WinwsProfileComposer.ComposeWarpBootstrap();
    Require(command.Contains("--wf-tcp=443", StringComparison.Ordinal), "Bootstrap TCP capture is missing");
    Require(!command.Contains("--wf-tcp=80", StringComparison.Ordinal), "Bootstrap captures arbitrary HTTP");
    Require(command.Contains("--wf-udp=443,2408", StringComparison.Ordinal), "Bootstrap UDP capture is not narrow");
    Require(command.Contains("--hostlist=files\\list-warp.txt", StringComparison.Ordinal), "Bootstrap host allowlist is missing");
    Require(command.Contains("--filter-udp=2408", StringComparison.Ordinal), "WARP transport rule is missing");
    Require(!command.Contains("list-youtube.txt", StringComparison.Ordinal), "Bootstrap unexpectedly includes YouTube");
    Require(!command.Contains("--filter-udp=1024-65535", StringComparison.Ordinal), "Bootstrap captures arbitrary UDP");
    Require(!command.Contains("list-doh.txt", StringComparison.Ordinal), "Bootstrap unexpectedly duplicates ordinary DNS rules");

    var lifecycle = new WarpBootstrapService();
    lifecycle.Begin(protectionWasRunning: true);
    Require(lifecycle.IsActive && lifecycle.Snapshot?.ProtectionWasRunning == true, "Bootstrap snapshot was not saved");
    var restored = lifecycle.End();
    Require(!lifecycle.IsActive && restored?.ProtectionWasRunning == true, "Bootstrap snapshot was not restored");
}

static void TestWarpDiagnosticRedaction()
{
    const string deviceId = "2dc1cb4b-d02b-4aa3-a1bf-3524b794ae68";
    const string license = "secret-license-value";
    var sanitized = WarpService.Sanitize($"Device ID: {deviceId} License: {license} Public key: abc123");
    Require(!sanitized.Contains(deviceId, StringComparison.Ordinal), "Device ID was not redacted");
    Require(!sanitized.Contains(license, StringComparison.Ordinal), "License was not redacted");
    Require(!sanitized.Contains("abc123", StringComparison.Ordinal), "Public key was not redacted");
    Require(WarpService.ConnectionProgress("Verifying connection settings", WarpConnectionState.Connecting) == .26, "26% status stage is not mapped");
    Require(WarpService.IsTerminalError("Tunnel connection failed"), "Terminal WARP error was not detected");
    Require(WarpService.NeedsEmergencyRecovery(WarpConnectionState.Connecting, false, false), "Connecting WARP is not recovered");
    Require(WarpService.NeedsEmergencyRecovery(WarpConnectionState.Error, false, true), "Failed bootstrap is not recovered");
    Require(!WarpService.NeedsEmergencyRecovery(WarpConnectionState.Connected, true, true), "Healthy connected WARP is recovered unexpectedly");
    Require(!WarpService.NeedsEmergencyRecovery(WarpConnectionState.Error, false, false), "Unrelated read error triggers destructive recovery");
}

static void TestWarpProbeClassification()
{
    Require(WarpService.ClassifyHttpStatus(200) == WarpProbeState.Available, "HTTP 200 should be available");
    Require(WarpService.ClassifyHttpStatus(401) == WarpProbeState.Restricted, "HTTP 401 should prove a restricted route");
    Require(WarpService.ClassifyHttpStatus(403) == WarpProbeState.Restricted, "HTTP 403 should prove a restricted route");
    Require(WarpService.ClassifyHttpStatus(429) == WarpProbeState.Restricted, "HTTP 429 should prove a restricted route");
    Require(WarpService.ClassifyHttpStatus(451) == WarpProbeState.RegionRestricted, "HTTP 451 should be a regional restriction");
    Require(WarpService.ClassifyHttpStatus(503) == WarpProbeState.ServiceError, "HTTP 503 should be a service error");
    Require(WarpService.AggregateScenarioState([WarpProbeState.Available, WarpProbeState.TimedOut]) == WarpProbeState.Partial,
        "A reachable endpoint plus timeout should be partial");
    Require(WarpService.AggregateScenarioState([WarpProbeState.TimedOut, WarpProbeState.Unavailable]) == WarpProbeState.TimedOut,
        "An entirely failed scenario with a timeout should report timeout");
}

static async Task TestWarpScenarioIndependence()
{
    static Task<WarpEndpointDiagnostic> Probe(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = uri.Host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.Equals("cdn.telesco.pe", StringComparison.OrdinalIgnoreCase)
            ? WarpProbeState.TimedOut
            : uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
                ? WarpProbeState.Restricted
                : WarpProbeState.Available;
        return Task.FromResult(new WarpEndpointDiagnostic(uri.Host, state, state == WarpProbeState.TimedOut ? "тайм-аут" : "OK"));
    }

    using var service = new WarpService(Path.GetTempPath(), endpointProbe: Probe);
    var result = await service.DiagnoseScenariosAsync(
        telegram: true,
        games: false,
        warpRouteActive: true,
        telegramProxyRunning: true,
        telegramProxyHasClientActivity: true);

    Require(result["telegram"].State == WarpProbeState.Available, "One Telegram CDN mirror should not make Web unavailable");
    Require(result["telegram"].Endpoints["webInterface"].State == WarpProbeState.Available, "Telegram Web group should remain available");
    Require(result["telegram"].Endpoints["mediaCdn"].State == WarpProbeState.Available, "Telegram media fallback should remain available");
    Require(result["telegram"].Endpoints["desktopProxy"].State == WarpProbeState.Available, "Desktop proxy state is missing");
}

static void TestWarpTelegramCompatibility()
{
    var profiles = new BypassProfileSettings { Telegram = true, TelegramWeb = true, YouTube = true };
    var ordinary = WinwsProfileComposer.Compose(profiles, "aggressive", new(), null, null, 3);
    var warp = WinwsProfileComposer.Compose(profiles, "aggressive", new(), null, null, 3,
        includeWarpBootstrap: true, warpCompatibilityMode: true);
    var restored = WinwsProfileComposer.Compose(profiles, "aggressive", new(), null, null, 3);

    Require(ordinary.Contains("list-telegram.txt", StringComparison.Ordinal), "Ordinary Telegram hostlist is missing");
    Require(ordinary.Contains("list-telegram-ipset.txt", StringComparison.Ordinal), "Ordinary Telegram ipset is missing");
    Require(!warp.Contains("list-telegram.txt", StringComparison.Ordinal), "Telegram hostlist is active inside WARP");
    Require(!warp.Contains("list-telegram-ipset.txt", StringComparison.Ordinal), "Telegram ipset is active inside WARP");
    Require(warp.Contains("list-warp.txt", StringComparison.Ordinal), "WARP bootstrap was removed by compatibility mode");
    Require(restored == ordinary, "Ordinary Telegram profile was not restored exactly");
}

static void TestTelegramStickerHosts()
{
    var path = Path.Combine(Directory.GetCurrentDirectory(), "components", "zapret", "files", "list-telegram.txt");
    var hosts = File.ReadAllLines(path);
    Require(hosts.Contains("cdn.telesco.pe", StringComparer.OrdinalIgnoreCase), "Primary Telegram sticker CDN is missing");
    for (var index = 1; index <= 6; index++)
        Require(hosts.Contains($"cdn{index}.telesco.pe", StringComparer.OrdinalIgnoreCase), $"Telegram sticker CDN {index} is missing");
    var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    var manifest = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "assets", "external-components.json"));
    Require(manifest.Contains(expectedHash, StringComparison.OrdinalIgnoreCase), "Telegram hostlist hash is stale in the component manifest");
}

static void TestTelegramHostsFallbackScope()
{
    Require(HostsFileManager.IsTelegramWebFallbackHost("web.telegram.org"), "Telegram Web is missing from hosts fallback");
    Require(HostsFileManager.IsTelegramWebFallbackHost("kws2.web.telegram.org"), "Telegram WebSocket is missing from hosts fallback");
    Require(HostsFileManager.IsTelegramWebFallbackHost("cdn.telesco.pe"), "Telegram media CDN is missing from hosts fallback");
    Require(!HostsFileManager.IsTelegramWebFallbackHost("finland10000.discord.media"), "Discord media leaked into Telegram hosts fallback");
}

static void TestTelegramFallbackWithOtherProfiles()
{
    var robloxOverrides = new[]
    {
        new HostsOverride(IPAddress.Parse("23.214.96.143"), RobloxThumbnailResolver.ThumbnailHost)
    };
    Require(HostsFileManager.ShouldUseTelegramStaticFallback(true, false, false, robloxOverrides),
        "Roblox DNS entries suppressed the Telegram static fallback");

    var telegramOverrides = robloxOverrides.Append(
        new HostsOverride(IPAddress.Parse("149.154.167.220"), "web.telegram.org"));
    Require(HostsFileManager.ShouldUseTelegramStaticFallback(true, false, false, telegramOverrides),
        "A DNS-only Telegram route suppressed the signed browser fallback before reachability validation");
    Require(!HostsFileManager.ShouldUseTelegramStaticFallback(true, true, true, robloxOverrides),
        "Telegram hosts fallback remained active inside WARP compatibility mode");
}

static void TestCloudflarePopParsing()
{
    const string json = """
        {"components":[
          {"name":"Frankfurt, Germany - (FRA)","status":"operational"},
          {"name":"Cleveland, OH, United States - (CLE)","status":"partial_outage"},
          {"name":"Cloudflare API","status":"operational"}
        ]}
        """;
    var locations = CloudflarePopDirectoryService.ParseLocations(json);
    Require(locations["FRA"].City == "Frankfurt" && locations["FRA"].Country == "Germany", "Frankfurt PoP was parsed incorrectly");
    Require(locations["CLE"].City == "Cleveland, OH" && locations["CLE"].Country == "United States", "US PoP was parsed incorrectly");
    Require(CloudflarePopDirectoryService.ShouldRefresh(DateTimeOffset.UtcNow.AddDays(-31), DateTimeOffset.UtcNow), "Stale PoP cache was accepted");
    Require(!CloudflarePopDirectoryService.ShouldRefresh(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow), "Fresh PoP cache was rejected");

    var trace = WarpService.ParseTrace("warp=on\ncolo=fra\nloc=ru\nip=192.0.2.1\n");
    Require(trace["colo"] == "fra" && trace["loc"] == "ru", "Cloudflare trace fields were not parsed");
}

static void TestWarpPerformanceVerdict()
{
    var current = new Dictionary<string, WarpPerformanceEndpoint>
    {
        ["telegram"] = new("Telegram", true, 90, 6, "OK"),
        ["youtube"] = new("YouTube", true, 85, 5, "OK")
    };
    var worse = WarpService.ComparePerformance(current, new Dictionary<string, long> { ["telegram"] = 40, ["youtube"] = 45 });
    Require(worse.Verdict == WarpPerformanceVerdict.Worse, "Slow WARP route was not reported as worse");
    var similar = WarpService.ComparePerformance(current, new Dictionary<string, long> { ["telegram"] = 80, ["youtube"] = 80 });
    Require(similar.Verdict == WarpPerformanceVerdict.Similar, "Comparable WARP route was not reported as similar");
    var missing = WarpService.ComparePerformance(current, null);
    Require(missing.Verdict == WarpPerformanceVerdict.BaselineUnavailable, "Missing baseline was not reported safely");
}

static void TestWarpRoutingProfiles()
{
    var settings = new WarpSettings();
    WarpRoutingPlanner.EnsureDefaults(settings);
    var youtube = settings.RouteProfiles.Single(profile => profile.BuiltInId == "youtube");
    youtube.Enabled = true;
    youtube.Connection = "warpProxy";
    settings.RouteProfiles.Add(new WarpRouteProfile
    {
        Name = "Direct overlap", Kind = "site", Enabled = true, Connection = "direct", Domains = ["youtube.com"]
    });
    settings.RouteProfiles.Add(new WarpRouteProfile
    {
        Name = "UDP game", Kind = "game", Enabled = true, Connection = "warpProxy", Domains = ["game.example"]
    });
    var plan = WarpRoutingPlanner.BuildPlan(settings, "network");
    Require(plan.Conflicts.Any(value => value.StartsWith("youtube.com", StringComparison.OrdinalIgnoreCase)), "Conflicting domain routes were applied silently");
    Require(plan.Warnings.Any(value => value.Contains("UDP", StringComparison.OrdinalIgnoreCase)), "Selective game SOCKS5 was not rejected");
    Require(!plan.ProxyDomains.Contains("game.example"), "Game UDP was sent to the web proxy");
}

static void TestWarpPacGeneration()
{
    var pac = WarpPacService.GeneratePac(["youtube.com", "*.telegram.org", "https://discord.com/app"], 41870);
    Require(pac.Contains("SOCKS5 127.0.0.1:41870; DIRECT", StringComparison.Ordinal), "PAC has no safe SOCKS fallback");
    Require(pac.Contains("youtube.com", StringComparison.Ordinal) && pac.Contains("telegram.org", StringComparison.Ordinal), "PAC domains are missing");
    Require(pac.EndsWith("return 'DIRECT'; }", StringComparison.Ordinal), "Non-profile traffic is not direct");
}

static async Task TestWarpPacRefresh()
{
    using var pac = new WarpPacService();
    var first = await pac.StartAsync(["telegram.org"], 41870, 0);
    var second = await pac.StartAsync(["telegram.org"], 41870, 0);
    Require(first.Query.StartsWith("?v=", StringComparison.Ordinal) && second.Query.StartsWith("?v=", StringComparison.Ordinal),
        "PAC URL has no browser cache version");
    Require(first.AbsoluteUri != second.AbsoluteUri, "PAC URL was reused after a route refresh");
}

static void TestWarpActualRouteCompatibility()
{
    var settings = new WarpSettings { RoutingMode = "smart" };
    WarpRoutingPlanner.EnsureDefaults(settings);
    var telegram = settings.RouteProfiles.Single(profile => profile.BuiltInId == "telegram");
    telegram.Enabled = true;
    telegram.Connection = "auto";
    Require(!WarpRoutingPlanner.UsesWarpForBuiltIn(settings, "network", "telegram"),
        "Auto profile without measurements incorrectly disabled the direct Telegram route");
    telegram.Connection = "warpProxy";
    Require(WarpRoutingPlanner.UsesWarpForBuiltIn(settings, "network", "telegram"),
        "Telegram proxy route did not enable WARP compatibility");
    telegram.Connection = "direct";
    settings.RoutingMode = "full";
    Require(WarpRoutingPlanner.UsesWarpForBuiltIn(settings, "network", "telegram"),
        "Full tunnel did not enable WARP compatibility");
}

static void TestWarpMeasuredRouteSelection()
{
    Require(WarpRoutingPlanner.SelectMeasuredWebRoute(false, null, true, 140) == "warpProxy",
        "Reachable WARP proxy was not selected after direct Telegram failure");
    Require(WarpRoutingPlanner.SelectMeasuredWebRoute(true, 35, true, 180) == "direct",
        "Slower WARP proxy replaced a healthy direct route");
    Require(WarpRoutingPlanner.SelectMeasuredWebRoute(true, 160, true, 80) == "warpProxy",
        "Faster WARP proxy was not selected");
    Require(WarpRoutingPlanner.SelectMeasuredWebRoute(false, null, false, null) is null,
        "Unavailable routes were reported as working");
}

static void TestWarpProtocolSelection()
{
    var selected = WarpService.SelectFastestProtocol(new Dictionary<string, long> { ["MASQUE"] = 273, ["WireGuard"] = 88 });
    Require(selected == "WireGuard", "Auto mode did not choose the lowest measured protocol");
}

static Func<Task> Sync(Action action) => () =>
{
    action();
    return Task.CompletedTask;
};

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
