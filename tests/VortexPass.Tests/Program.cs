using System.Net;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

if(args is ["--component-lifecycle-fixture",var fixtureMode])
{Console.WriteLine("isolated lifecycle fixture");await Task.Delay(fixtureMode=="exit"?900:Timeout.Infinite);return 0;}
if(args is ["--test-component-lifecycle"])
{await ExternalLifecycleTests.Run();Console.WriteLine("PASS owned process restarts, current exits and generation filtering without WinDivert");return 0;}

if(args is ["--test-youtube-only"])
{await YoutubeOnlyTests.Run();Console.WriteLine("PASS isolated YouTube policy, bounded search, content and privacy regression checks");return 0;}
if(args is ["--test-youtube-advisor"])
{await YoutubeGlobalAdvisorTests.Run();Console.WriteLine("PASS general advisor YouTube fallback, timeout, page/media distinction, budgets and working-block preservation");return 0;}
if(args is ["--probe-youtube-personal-model",var youtubeModelDirectory])
{
    var watch=System.Diagnostics.Stopwatch.StartNew();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var request=new AdvisorRequest("medium","local","fixture","standard",["youtube"],Scope:"youtube");
    var current=LocalStrategyAdvisor.Rate(new(),"local",[new("youtube","partial",false,"synthetic probe",30,0,1,[new("dns","confirmed","fixture"),new("tls-IPv4","unconfirmed","timeout"),new("page","unconfirmed","fixture"),new("video","controlResourceUnconfirmed","fixture")])]);
    try{var proposals=await new LlamaLocalStrategyModel(Path.GetFullPath(youtubeModelDirectory)).ProposeAsync(request,false,[current],deadline.Token);foreach(var p in proposals){AdvisorStrategyPolicy.Validate(p,"medium");Console.WriteLine(p.YouTubeParameters?.Key);}Console.WriteLine($"PASS actual local personalized model: {proposals.Count} bounded proposals in {watch.Elapsed.TotalSeconds:F1}s; synthetic inputs, no network changes");return 0;}
    catch(OperationCanceledException){Console.WriteLine("Model exceeded ordinary 20s budget; real application must use measured fallback");return 3;}
}

if(args is ["--check-youtube-isolation",var youtubeBackup])
{
    var profile=System.Text.Json.JsonSerializer.Deserialize<SavedConnectionProfile>(await File.ReadAllTextAsync(Path.Combine(youtubeBackup,"selected-profile.json")))!;
    var line=await File.ReadAllTextAsync(Path.Combine(youtubeBackup,"winws-from-user-log.txt"));var marker="Запуск: winws.exe ";var offset=line.IndexOf(marker,StringComparison.Ordinal);
    if(offset<0||profile.Strategy is null)throw new InvalidDataException("Saved working command or strategy is missing");
    var command=line[(offset+marker.Length)..].TrimEnd('\r','\n');
    foreach(var spec in YoutubeOnlyPolicy.Candidates(profile.Strategy))
    {var result=WinwsProfileComposer.ComposeYoutubeOnly(command,profile.Strategy,spec);YoutubeOnlyPolicy.AssertIsolated(command,result);Console.WriteLine($"PASS real saved command: {spec.YouTube}; non-YouTube rules identical; {result.Length} chars; not executed");}
    return 0;
}
if(args is ["--probe-youtube-readonly"])
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(9));var result=await YoutubeRouteProbe.CheckAsync(timeout.Token);
    Console.WriteLine(result.Detail);foreach(var g in result.Groups??[])Console.WriteLine(g.Id+": "+g.State+"; "+g.Detail);
    Console.WriteLine("Read-only probe: no DNS, WARP, PAC, profile or browser settings changed.");return 0;
}

if(args is [var warpProbeMode,var warpComponentsDirectory] && warpProbeMode is "--probe-selective-warp" or "--probe-warp-h2-direct" or "--probe-telegram-warp")
{
    var directTransportOnly=warpProbeMode=="--probe-warp-h2-direct";
    using var service=new WarpService(Path.Combine(Path.GetTempPath(),"vortexpass-warp-probe"));
    using var bootstrap=new WarpBootstrapService(Path.GetFullPath(warpComponentsDirectory));
    var before=await service.GetStatusAsync();
    if(before.State!=WarpConnectionState.Disconnected||!before.Registered)
    {Console.WriteLine("BLOCKED: test requires an already registered, disconnected WARP client; registration and terms are not modified");return 2;}
    await service.ValidateConsumerAsync(default);
    var snapshot=await service.CaptureNativeConfigurationAsync(default);
    try
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var sourceSettings=await new AppSettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VortexPass")).LoadAsync(deadline.Token);
        var candidates=WarpPreflight.Candidates(sourceSettings,before.Version);
        Console.WriteLine($"WARP settings: {WarpTransportPolicy.Selection(sourceSettings.Warp)}; source={sourceSettings.Warp.TransportSelectionSource}; candidates={candidates.Count}; first={candidates[0].Masque}/{candidates[0].Bootstrap}");
        if(directTransportOnly)candidates=candidates.Take(1).ToArray();
        var result=await WarpPreflight.RunAsync(candidates,async(variant,attempt,token)=>
        {
            Console.WriteLine($"WARP attempt {attempt}: requested {variant.Protocol}/{variant.Masque}/{variant.Bootstrap}");
            var started=DateTimeOffset.UtcNow;
            try
            {
            await bootstrap.StopAsync();
            if(!directTransportOnly&&WarpPreflight.RequiresBootstrap(variant.Bootstrap))await bootstrap.StartAsync(WinwsProfileComposer.ComposeWarpBootstrap(variant.Bootstrap),false,token);
            await service.SetConnectedAsync(false,token);
            await service.ApplyConfigurationAsync(new("proxy",variant.Protocol,variant.Masque,"system",41870),false,token,rollbackOnError:false);
            var status=await service.SetConnectedAsync(true,token);
            if(status.State!=WarpConnectionState.Connected){Console.WriteLine("Tunnel: "+status.Detail);return false;}
            using var proof=CancellationTokenSource.CreateLinkedTokenSource(token);proof.CancelAfter(TimeSpan.FromSeconds(4));
            return await service.ConfirmProxyRouteAsync(41870,proof.Token);
            }
            finally{Console.WriteLine((await WarpAttemptObservation.ReadAsync(started)).Summary);}
        },deadline.Token);
        Console.WriteLine($"PASS real proxy trace warp=on: {result.Masque}/{(directTransportOnly?"without bootstrap":result.Bootstrap)}; no PAC or adapter DNS changes");
        if(warpProbeMode=="--probe-telegram-warp")
        {
            using var pac=new WarpPacService();
            using var telegramDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await pac.StartAsync(TelegramWebPolicy.Domains,41870,0,telegramDeadline.Token);
                var telegram=await TelegramWarpProbe.CheckAsync(pac.HttpProxyUri!,telegramDeadline.Token);
                Console.WriteLine((telegram.ContentConfirmed==true?"PASS ":"PARTIAL ")+telegram.Detail);
                if(telegram.ContentConfirmed!=true)return 3;
            }
            finally{await pac.StopAsync();}
        }
        return 0;
    }
    finally
    {
        using var restore=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await service.RestoreNativeConfigurationAsync(snapshot,false,restore.Token);
            if(await service.CaptureNativeConfigurationAsync(restore.Token)!=snapshot)throw new Exception("Original WARP settings were not restored");
            Console.WriteLine("PASS original WARP configuration restored; disconnected");
        }
        finally{await bootstrap.StopAsync();}
    }
}

if(args is ["--verify-warp-bundle",var warpBundleDirectory])
{
    var installer=await WarpInstallerBundle.FindAsync(Path.GetFullPath(warpBundleDirectory));
    if(installer is null)throw new Exception("Offline WARP installer missing");
    await WarpInstallerBundle.VerifyTransformAsync(installer);
    Console.WriteLine($"PASS offline WARP SHA-256 and Cloudflare signature: {new FileInfo(installer).Length} bytes; no installer execution or device registration");
    return 0;
}

if(args is ["--download-warp",var warpDownloadDirectory])
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
    using var service=new WarpService(Path.GetFullPath(warpDownloadDirectory));
    var installer=await service.DownloadInstallerAsync(null,timeout.Token);
    Console.WriteLine($"PASS official WARP download and Cloudflare signature: {new FileInfo(installer).Length} bytes; installer was not executed");
    return 0;
}

if(args is ["--smoke-fast-advisor",var fastModelDirectory])
{await SelectiveWarpTests.Smoke(fastModelDirectory);return 0;}

if (args is ["--probe-dns"])
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(50));
    var results=await DnsDiagnosticsService.CheckAsync(["www.youtube.com","thumbnails.roblox.com","assetdelivery.roblox.com"],timeout.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {networkSettingsChanged=false,results,choices=DnsDiagnosticsService.Choices(results,["www.youtube.com","thumbnails.roblox.com","assetdelivery.roblox.com"])}));
    return 0;
}

if (args is ["--probe-telegram-transports"])
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    using var gate = new SemaphoreSlim(2);
    var results = await Task.WhenAll(TelegramEndpointRegistry.NamedDataCenters.Select(async name =>
    {
        await gate.WaitAsync(timeout.Token);
        try { return await TelegramWebSocketProbe.CheckAsync(name + ".web.telegram.org", timeout.Token, sustained: true); }
        finally { gate.Release(); }
    }));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { networkSettingsChanged = false, results }));
    return 0;
}

if (args is ["--probe-current-route"])
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var checker = new ServiceHealthCheckService();
    var missing = await RouteProbePolicy.MissingWindowsDnsAsync(timeout.Token);
    var results = await Task.WhenAll(new[] { "telegram", "discord", "youtube" }.Select(id => checker.CheckAsync(
        id, true, true, timeout.Token, webSelected: id == "telegram", discordVoiceMode: "off", probeOptionalTransport: false)));
    var warp = await new WarpService(Path.Combine(Path.GetTempPath(), "vortexpass-readonly-route-probe")).GetStatusAsync(timeout.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { probeOnly = true, networkSettingsChanged = false,
        windowsDnsMissing = missing, warp = new { state = warp.State.ToString(), installed = warp.CliPath is not null },
        services = results.Select(r => new { service = r.Id, state = r.State.ToString(), r.Detail, groups = r.Groups }) }));
    return 0;
}

if (args is ["--smoke-ai", var modelDirectory])
{
    await LocalAdvisorTests.Smoke(modelDirectory);
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("DNS recovery, Roblox scope, bounded resources and portable modes",NetworkRecoveryTests.Run),
    ("WARP transport migration, canonical selection and transactional retry",WarpTransportTests.Run),
    ("WARP half-close, large response, owned shutdown, stream limits, stable DNS and persisted route",WarpStreamingTests.Run),
    ("YouTube-only byte isolation, bounded finalists, real-content gates, Firefox privacy and separate proof",YoutubeOnlyTests.Run),
    ("Selective WARP HTTP/CONNECT, remote DNS, profile v5 and bounded search",SelectiveWarpTests.Run),
    ("Telegram proxy bundle matches every pinned runtime file", () => TelegramDesktopProxyService.VerifyBundleAsync(Path.Combine(Directory.GetCurrentDirectory(), "components", "zapret", "TgWsProxyHeadless"))),
    ("Telegram obfuscated MTProto validates CTR, fragments, persistence and nonce", TelegramTransportTests.Run),
    ("Pinned direct blocks preserve other services and distinguish owned winws", DirectRouteTests.ScopedBlocks),
    ("DNS wire validates questions, records, TTL and TCP fallback", AdvancedRouteTests.Dns),
    ("Site rules are scoped, validated and bound to recommendations", AdvancedRouteTests.Rules),
    ("Telegram HTTPS validates real MTProto response", AdvancedRouteTests.Mtproto),
    ("Profile exchange validates settings and excludes foreign routes", ProfileTransferTests.Run),
    ("Saved connections validate supported settings and domains", SavedConnectionTests.Validation),
    ("Saved connection snapshots do not mutate active services", SavedConnectionTests.Snapshot),
    ("Telegram pinning requires confirmed hostname routes", RouteRecoveryTests.TelegramCandidates),
    ("Media probes reject HTML errors and mapping text", RouteRecoveryTests.Media),
    ("WARP registration bootstrap has scoped ownership", RouteRecoveryTests.Registration),
    ("AI session-only checks do not reduce Web ranking", RouteRecoveryTests.Ranking),
    ("Local AI rejects unsafe parameters and foreign levels", LocalAdvisorTests.Policy),
    ("Local AI keeps capture, games, DNS and mobile baseline unchanged", LocalAdvisorTests.Commands),
    ("Local AI ranks working content ahead of latency", LocalAdvisorTests.Ranking),
    ("Local AI restores trials on success, error and cancellation", LocalAdvisorTests.Lifecycle),
    ("YouTube uses bounded fast QUIC", Sync(TestYoutubeQuic)),
    ("YouTube comment avatar DNS hosts are allowed", Sync(TestYoutubeAvatarHosts)),
    ("Telegram Desktop and Web are separate", Sync(TestTelegramSeparation)),
    ("Telegram ping ignores slow page loading", Sync(TestTelegramReportedLatency)),
    ("Discord voice is isolated and stable", Sync(TestDiscordVoice)),
    ("Discord CDN uses a bounded fast route", Sync(TestDiscordCdnRoute)),
    ("Mobile DNS bootstrap hosts stay narrow", Sync(TestMobileDnsBootstrapHosts)),
    ("Mobile DNS lifecycle policy is fail-safe", Sync(TestMobileDnsLifecyclePolicy)),
    ("Mobile compatibility stays narrow and reversible", Sync(TestMobileCompatibilityProfile)),
    ("Phasmophobia voice has a home-only stable route", Sync(TestPhasmophobiaVoiceRoute)),
    ("Dead by Daylight uses a narrow game route", Sync(TestDeadByDaylightRoute)),
    ("Mobile Telegram route starts strong and falls back narrowly", Sync(TestMobileTelegramStrategies)),
    ("Mobile partial health states are honest", Sync(TestMobilePartialHealthStates)),
    ("DNS chooses the lowest latency success", Sync(TestDnsSelection)),
    ("Quad9 DNS wire messages are valid", Sync(TestDnsWireFormat)),
    ("DNS resolver ignores transient empty winners", Sync(TestDnsResponsePolicy)),
    ("Startup scheduler arguments are safe", Sync(TestStartupArguments)),
    ("Update transport failures stay non-blocking", Sync(TestUpdateTransportFailure)),
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
    ("WARP auto protocol picks lowest median", Sync(TestWarpProtocolSelection)),
    ("Mobile service strategies are independent", Sync(TestMobileServiceStrategies)),
    ("Mobile WARP fallback preserves direct services", Sync(TestMobileWarpFallbackScope)),
    ("Mobile route health requires real content", Sync(TestMobileRouteHealthPolicy)),
    ("Mobile route cache avoids transient restarts", Sync(TestMobileRouteCachePolicy))
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
    Require(aggressiveWeb.Split(" --new").Any(rule => rule.Contains("--hostlist=files\\list-telegram.txt", StringComparison.Ordinal) &&
        rule.Contains("--dpi-desync=fake,multidisorder --dpi-desync-repeats=6 --dpi-desync-fooling=badseq", StringComparison.Ordinal)),
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
    Require(ServiceHealthCheckService.ClassifyTelegramWebState(true, false) == ServiceHealthState.Partial,
        "Confirmed Telegram Web HTTP was overridden by optional probe timeouts");
    Require(ServiceHealthCheckService.ClassifyTelegramWebState(false, true) == ServiceHealthState.Partial,
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

static void TestMobileDnsBootstrapHosts()
{
    Require(HostsFileManager.IsAllowedDynamicHostForTests("www.youtube.com"),
        "Existing non-mobile YouTube DNS support unexpectedly changed");
    foreach (var host in new[] { "discord.com", "discord.gg", "gateway.discord.gg" })
        Require(!HostsFileManager.IsAllowedDynamicHostForTests(host), $"Mobile hosts still permit dangerous pinning for {host}");
    Require(!HostsFileManager.IsAllowedDynamicHostForTests("rr1---sn.example.googlevideo.com"),
        "Mobile DNS bootstrap pinned a YouTube video CDN");
    Require(!HostsFileManager.IsAllowedDynamicHostForTests("cdn.discordapp.com"),
        "Mobile DNS bootstrap pinned Discord CDN media");
    Require(HostsFileManager.IsAllowedDynamicHostForTests("tr.rbxcdn.com") &&
            HostsFileManager.IsAllowedDynamicHostForTests("thumbnails.roblox.com"),
        "Independently validated Roblox CDN routes are rejected");

    var command = WinwsProfileComposer.Compose(new BypassProfileSettings { YouTube = true }, "medium", new(), null, "user-exclusions.txt", 0,
        mobileCompatibilityMode: true);
    var firstRule = command[..command.IndexOf(" --new", StringComparison.Ordinal)];
    Require(firstRule.Contains("list-doh.txt", StringComparison.Ordinal) && !firstRule.Contains("hostlist-exclude", StringComparison.Ordinal),
        "Dedicated DoH rule is still disabled by the general exclusion list");
}

static void TestMobileDnsLifecyclePolicy()
{
    Require(MobileDnsPolicy.ShouldRun(true, "mobile", false), "Mobile protection did not start its DNS guard");
    Require(!MobileDnsPolicy.ShouldRun(false, "mobile", false), "DNS guard runs with protection disabled");
    Require(MobileDnsPolicy.ShouldRun(true, "standard", false), "Home protection did not start its DNS guard");
    Require(!MobileDnsPolicy.ShouldRun(true, "mobile", true), "DNS guard conflicts with WARP-owned DNS");
    Require(!MobileDnsPolicy.ShouldRun(true, "standard", true), "Home DNS guard conflicts with WARP-owned DNS");
    Require(!MobileDnsPolicy.WarpOwnsSystemDns(true, "proxy") &&
            !MobileDnsPolicy.WarpOwnsSystemDns(true, "smart") &&
            MobileDnsPolicy.WarpOwnsSystemDns(true, "full") &&
            MobileDnsPolicy.WarpOwnsSystemDns(true, "dnsOnly") &&
            !MobileDnsPolicy.WarpOwnsSystemDns(false, "full"),
        "Connected WARP mode did not suppress the competing local DNS guard");
    Require(MobileDnsPolicy.CanPinEmergencyHost("web.telegram.org"), "Telegram emergency host was rejected");
    Require(!MobileDnsPolicy.CanPinEmergencyHost("www.youtube.com") && !MobileDnsPolicy.CanPinEmergencyHost("discord.com"),
        "YouTube or Discord can still be pinned by the mobile emergency route");
    Require(MobileDnsPolicy.MustDiscardLegacyRoutes(6) && !MobileDnsPolicy.MustDiscardLegacyRoutes(7),
        "Legacy seven-host cache migration boundary is incorrect");
}

static void TestMobileCompatibilityProfile()
{
    var profiles = new BypassProfileSettings
    {
        YouTube = true,
        Discord = true,
        Telegram = true,
        TelegramWeb = true,
        GameFilter = true
    };
    var games = new GameFilterSettings { SelectedGames = ["roblox"], Protocol = "all" };
    var mobile = WinwsProfileComposer.Compose(profiles, "aggressive", games, null, null, 3,
        mobileCompatibilityMode: true, youtubeTransport: "auto", discordMobileStrategy: "fallback");
    Require(!mobile.Contains("--filter-tcp=1024-65535", StringComparison.Ordinal),
        "Mobile profile captured every high TCP port");
    Require(!mobile.Contains("1024-1700,1702-2407,2409-4499,4501-65535", StringComparison.Ordinal),
        "Mobile profile captured almost every UDP port");
    Require(!mobile.Contains("list-telegram-ipset.txt", StringComparison.Ordinal),
        "Mobile profile pinned Telegram to raw DC ranges");
    Require(mobile.Contains("--hostlist=files\\list-telegram.txt", StringComparison.Ordinal),
        "Mobile profile lost the narrow Telegram hostname route");
    var websocketRule = "--hostlist-domains=kws.web.telegram.org,zws.web.telegram.org";
    Require(mobile.Contains(websocketRule, StringComparison.Ordinal) &&
            mobile.IndexOf(websocketRule, StringComparison.Ordinal) < mobile.IndexOf("--hostlist=files\\list-telegram.txt", StringComparison.Ordinal),
        "Mobile Telegram WebSocket rule does not precede the general Telegram rule");
    Require(mobile.Contains("--hostlist=files\\list-discord.txt --dpi-desync=fake,multidisorder", StringComparison.Ordinal) &&
            mobile.Contains("--dpi-desync-fooling=badseq", StringComparison.Ordinal),
        "Mobile Discord does not use its dedicated TCP strategy");
    Require(!mobile.Contains("ACTIVE_DISCORD_UDP.bin", StringComparison.Ordinal),
        "Composer auto mode unexpectedly guessed a voice route");
    Require(mobile.Contains("--filter-tcp=443 --hostlist=files\\list-roblox.txt --dpi-desync=fake,fakedsplit --dpi-desync-repeats=4", StringComparison.Ordinal) &&
            mobile.Contains("--filter-udp=443 --hostlist=files\\list-roblox.txt --filter-l7=quic --dpi-desync=fake --dpi-desync-repeats=2", StringComparison.Ordinal),
        "Mobile Roblox retained the slow high-repeat image/CDN route");

    var minimalVoice = WinwsProfileComposer.Compose(profiles, "aggressive", games, null, null, 3,
        mobileCompatibilityMode: true, youtubeTransport: "auto", discordVoiceMode: "minimal");
    Require(minimalVoice.Contains("--filter-udp=19294-19344,50000-50100", StringComparison.Ordinal) &&
            minimalVoice.Contains("--dpi-desync-repeats=2", StringComparison.Ordinal),
        "Mobile minimal voice lost its narrow UDP rule");
    var directVoice = WinwsProfileComposer.Compose(profiles, "aggressive", games, null, null, 3,
        mobileCompatibilityMode: true, discordVoiceMode: "direct");
    Require(!directVoice.Contains("ACTIVE_DISCORD_UDP.bin", StringComparison.Ordinal),
        "Direct Discord voice unexpectedly applies desync");

    var http2 = WinwsProfileComposer.Compose(new BypassProfileSettings { YouTube = true }, "aggressive", new(), null, null, 3,
        mobileCompatibilityMode: true, youtubeTransport: "http2");
    Require(!http2.Contains("--filter-udp=443 --hostlist=files\\list-youtube.txt", StringComparison.Ordinal) &&
            !http2.Contains("--filter-udp=443 --ipset=files\\list-youtube-ipset.txt", StringComparison.Ordinal),
        "HTTP/2 fallback still applies YouTube QUIC rules");

    var ordinary = WinwsProfileComposer.Compose(profiles, "aggressive", games, null, null, 3);
    Require(!ordinary.Contains("--filter-tcp=1024-65535", StringComparison.Ordinal) &&
            !ordinary.Contains("1024-1700,1702-2407,2409-4499,4501-65535", StringComparison.Ordinal) &&
            ordinary.Contains("list-roblox.txt", StringComparison.Ordinal) &&
            ordinary.Contains("list-telegram-ipset.txt", StringComparison.Ordinal),
        "Ordinary Roblox profile is not narrow or lost its service routes");
    Require(ordinary.Contains("--filter-tcp=443 --hostlist-domains=rbxcdn.com", StringComparison.Ordinal),
        "Roblox CDN did not receive its bounded fast route");
    var homeWebSocketStart = ordinary.IndexOf(websocketRule, StringComparison.Ordinal);
    var homeTelegramStart = ordinary.IndexOf("--hostlist=files\\list-telegram.txt", StringComparison.Ordinal);
    Require(homeWebSocketStart < 0 || homeWebSocketStart > homeTelegramStart &&
        ordinary.Split(" --new").Count(rule => rule.Contains(websocketRule, StringComparison.Ordinal)) == 1,
        "Home Telegram profile shadows its proven general hostlist with a duplicate WebSocket rule");
    Require(homeTelegramStart >= 0,
        "Home Telegram general hostlist rule is missing");
    Require(WinwsProfileComposer.NormalizeCompatibilityMode("MOBILE") == "mobile" &&
            WinwsProfileComposer.NormalizeCompatibilityMode("unknown") == "standard",
        "Compatibility mode normalization is unsafe");
}

static void TestPhasmophobiaVoiceRoute()
{
    var profiles = new BypassProfileSettings { GameFilter = true };
    var games = new GameFilterSettings { SelectedGames = ["phasmophobia"], Protocol = "tcp" };
    var home = WinwsProfileComposer.Compose(profiles, "medium", games, null, null, 0);
    const string voicePorts = "5055-5058,27000-27002,12000-65000";
    Require(home.Contains($"--filter-udp={voicePorts} --dpi-desync=fake --dpi-desync-repeats=2", StringComparison.Ordinal),
        "Phasmophobia voice did not receive its stable UDP route");
    Require(home.Contains($"--wf-udp=443,{voicePorts}", StringComparison.Ordinal),
        "Phasmophobia voice UDP is not captured when the game UI is set to TCP");

    var mobile = WinwsProfileComposer.Compose(profiles, "medium", games, null, null, 0, mobileCompatibilityMode: true);
    Require(!mobile.Contains(voicePorts, StringComparison.Ordinal),
        "Mobile compatibility unexpectedly enabled the wide Vivox voice range");
}

static void TestDeadByDaylightRoute()
{
    var profiles = new BypassProfileSettings { GameFilter = true };
    var games = new GameFilterSettings { SelectedGames = ["deadbydaylight"], Protocol = "all" };
    var home = WinwsProfileComposer.Compose(profiles, "medium", games, null, null, 0);
    Require(home.Contains("--hostlist=files\\list-deadbydaylight.txt", StringComparison.Ordinal),
        "Dead by Daylight hostlist is missing");
    Require(home.Contains("--filter-tcp=80", StringComparison.Ordinal) &&
            home.Contains("--filter-tcp=443", StringComparison.Ordinal) &&
            home.Contains("--filter-udp=443", StringComparison.Ordinal),
        "Dead by Daylight web/matchmaking route is incomplete");
    Require(!home.Contains("--filter-udp=27000-27031", StringComparison.Ordinal) &&
            !home.Contains("--filter-udp=1024-65535", StringComparison.Ordinal),
        "Dead by Daylight captured broad Steam or game UDP traffic");

    var mobile = WinwsProfileComposer.Compose(profiles, "aggressive", games, null, null, 3,
        mobileCompatibilityMode: true);
    Require(mobile.Contains("--hostlist=files\\list-deadbydaylight.txt --dpi-desync=fake,fakedsplit --dpi-desync-repeats=4", StringComparison.Ordinal) &&
            mobile.Contains("--filter-udp=443 --hostlist=files\\list-deadbydaylight.txt --filter-l7=quic --dpi-desync=fake --dpi-desync-repeats=2", StringComparison.Ordinal),
        "Mobile Dead by Daylight route is not bounded");
}

static void TestMobileTelegramStrategies()
{
    var profiles = new BypassProfileSettings { TelegramWeb = true };
    var strong = WinwsProfileComposer.Compose(profiles, "moderate", new(), null, null, 0,
        mobileCompatibilityMode: true, telegramStrategy: "strong");
    Require(strong.Contains("--dpi-desync=fake,multidisorder", StringComparison.Ordinal) &&
            strong.Contains("--dpi-desync-fooling=badseq", StringComparison.Ordinal) &&
            strong.Contains("--dpi-desync-repeats=6", StringComparison.Ordinal) &&
            strong.Contains("--dpi-desync-cutoff=n3", StringComparison.Ordinal),
        "Strong mobile Telegram strategy is incomplete");
    Require(!strong.Contains("list-telegram-ipset.txt", StringComparison.Ordinal),
        "Strong mobile Telegram strategy captured raw Telegram IP ranges");

    var balanced = WinwsProfileComposer.Compose(profiles, "moderate", new(), null, null, 0,
        mobileCompatibilityMode: true, telegramStrategy: "balanced");
    Require(balanced.Contains("--dpi-desync=fake,fakedsplit", StringComparison.Ordinal),
        "Fallback Telegram strategy is not the narrow balanced variant");
    Require(WinwsProfileComposer.NormalizeTelegramStrategy("unknown") == "strong" &&
            WinwsProfileComposer.NormalizeTelegramStrategy("BALANCED") == "balanced",
        "Telegram strategy normalization is unsafe");
}

static void TestMobilePartialHealthStates()
{
    Require(ServiceHealthCheckService.ClassifyYouTubeState(true, true, true, false, true) == ServiceHealthState.Partial,
        "Missing YouTube previews were reported as a full outage");
    Require(ServiceHealthCheckService.ClassifyYouTubeState(true, true, true, true, false) == ServiceHealthState.Healthy,
        "Working HTTP/2 fallback was not accepted as healthy YouTube");
    Require(ServiceHealthCheckService.ClassifyYouTubeState(true, true, true, true, true) == ServiceHealthState.Healthy,
        "Fully confirmed YouTube was not healthy");
    Require(ServiceHealthCheckService.ClassifyYouTubeState(false, false, false, false, false) == ServiceHealthState.HttpUnavailable,
        "Unavailable YouTube shell was not reported as unavailable");

    var flowseal = File.ReadAllLines(Path.Combine(Directory.GetCurrentDirectory(), "components", "zapret", "files", "flowseal-hosts.txt"));
    var telegramEntries = flowseal.Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length == 2 && HostsFileManager.IsTelegramWebFallbackHost(parts[1]))
        .ToArray();
    Require(telegramEntries.Length > 1, "Mobile Telegram fallback contains only one hosts entry");
    Require(telegramEntries.All(parts => !parts[1].EndsWith(".discord.media", StringComparison.OrdinalIgnoreCase)),
        "Discord media leaked into the Telegram fallback block");
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

static void TestDnsResponsePolicy()
{
    var usable = new byte[12];
    usable[2] = 0x81;
    usable[3] = 0x80;
    usable[7] = 1;
    var empty = usable.ToArray();
    empty[7] = 0;
    var servfail = usable.ToArray();
    servfail[3] = 0x82;

    Require(DnsResponsePolicy.HasUsableAnswer(usable), "DNS answer with one record was rejected");
    Require(!DnsResponsePolicy.HasUsableAnswer(empty), "Empty DNS response can still win the resolver race");
    Require(!DnsResponsePolicy.HasUsableAnswer(servfail), "SERVFAIL can still win the resolver race");
    Require(DnsResponsePolicy.IsNegative(empty) && DnsResponsePolicy.IsNegative(servfail),
        "Negative DNS responses are not classified for short fallback caching");
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
    Require(!game.Contains("1702-2407,2409-4499,4501-65535", StringComparison.Ordinal), "Known game profile still captures unrelated UDP traffic");
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
    var unrelatedOverrides = new[]
    {
        new HostsOverride(IPAddress.Parse("23.214.96.143"), "assets.example.test")
    };
    Require(HostsFileManager.ShouldUseTelegramStaticFallback(true, false, false, unrelatedOverrides),
        "Published home Telegram fallback was not applied");
    Require(HostsFileManager.ShouldUseTelegramStaticFallback(true, false, true, unrelatedOverrides),
        "Emergency Telegram fallback was not enabled when no measured route existed");

    var telegramOverrides = unrelatedOverrides.Append(
        new HostsOverride(IPAddress.Parse("149.154.167.233"), "web.telegram.org"));
    Require(HostsFileManager.ShouldUseTelegramStaticFallback(true, false, true, telegramOverrides),
        "Measured DNS route incorrectly suppressed the signed Telegram fallback");
    Require(!HostsFileManager.ShouldUseTelegramStaticFallback(true, true, true, unrelatedOverrides),
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
    Require(pac.Contains("PROXY 127.0.0.1:41870\'", StringComparison.Ordinal) && !pac.Contains("; DIRECT",StringComparison.Ordinal), "PAC bypassed selected proxy or omitted HTTP bridge");
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

static void TestMobileServiceStrategies()
{
    var profiles = new BypassProfileSettings { YouTube = true, Discord = true, TelegramWeb = true };
    var primary = WinwsProfileComposer.Compose(profiles, "moderate", new(), null, null, mobileCompatibilityMode: true,
        youtubeMobileStrategy: "primary", discordMobileStrategy: "primary", telegramStrategy: "strong");
    var discordFallback = WinwsProfileComposer.Compose(profiles, "moderate", new(), null, null, mobileCompatibilityMode: true,
        youtubeMobileStrategy: "primary", discordMobileStrategy: "fallback", telegramStrategy: "strong");
    Require(primary.Contains("--ip-id=zero", StringComparison.Ordinal), "Primary YouTube strategy is missing");
    Require(discordFallback.Contains("--hostlist=files\\list-discord.txt --dpi-desync=fake,multidisorder", StringComparison.Ordinal),
        "Discord did not switch to its own fallback strategy");
    Require(discordFallback.Contains("--hostlist-domains=googlevideo.com,video.google.com,redirector.googlevideo.com --ip-id=zero", StringComparison.Ordinal),
        "Changing Discord unexpectedly changed YouTube");
    Require(MobileRoutePolicy.AlternateStrategy("fallback") == "primary" &&
            MobileRoutePolicy.AlternateStrategy("primary") == "fallback",
        "Mobile advisor repeats the same saved strategy instead of testing the alternate");
}

static void TestMobileWarpFallbackScope()
{
    var domains = MobileRoutePolicy.DomainsForFallback(["youtube", "discord", "telegram"]);
    Require(domains.Contains("discord.com") && domains.Contains("web.telegram.org"), "Web fallback domains are incomplete");
    Require(!domains.Any(value => value.Contains("youtube", StringComparison.OrdinalIgnoreCase) || value.Contains("googlevideo", StringComparison.OrdinalIgnoreCase)),
        "YouTube was sent through automatic mobile WARP fallback");
    Require(!domains.Any(value => value.Contains("voice", StringComparison.OrdinalIgnoreCase)), "Discord voice was sent through SOCKS5");
    Require(MobileRoutePolicy.SystemWarpDirectDomains.Contains("youtube.com") &&
            MobileRoutePolicy.SystemWarpDirectDomains.Contains("rbxcdn.com") &&
            !MobileRoutePolicy.SystemWarpDirectDomains.Any(value => value.Contains("discord", StringComparison.OrdinalIgnoreCase) || value.Contains("telegram", StringComparison.OrdinalIgnoreCase)),
        "System WARP fallback does not preserve direct YouTube/Roblox routes or excludes its target services");
}

static void TestMobileRouteHealthPolicy()
{
    var tlsOnly = new ServiceHealthResult("youtube", ServiceHealthState.HttpUnavailable, "TLS OK; HTTP timed out");
    var workingH2 = new ServiceHealthResult("youtube", ServiceHealthState.Healthy, "video and previews returned bytes");
    var discordWebWithoutVoice = new ServiceHealthResult("discord", ServiceHealthState.Partial, "API, Gateway and CDN available; voice unconfirmed");
    Require(!MobileRoutePolicy.AcceptsDirectRoute(tlsOnly), "TLS-only YouTube was accepted");
    Require(MobileRoutePolicy.AcceptsDirectRoute(workingH2), "Working HTTP/2 YouTube was rejected");
    Require(MobileRoutePolicy.AcceptsDirectRoute(discordWebWithoutVoice), "Discord web route was tied to voice confirmation");
}

static void TestMobileRouteCachePolicy()
{
    var now = DateTimeOffset.UtcNow;
    var route = new MobileServiceRouteState
    {
        ServiceId = "discord",
        Strategy = "fallback",
        LastSuccessfulStrategy = "primary",
        State = "confirmed",
        AppliedRoute = "winws",
        LastConfirmedUtc = now.AddMinutes(-10)
    };
    Require(MobileRoutePolicy.PreferredStrategy(route) == "primary",
        "Last confirmed mobile strategy is not preferred at startup");
    Require(MobileRoutePolicy.HasFreshSuccessfulStrategy(route, now, TimeSpan.FromHours(6)),
        "Fresh direct strategy cache was rejected");
    Require(!MobileRoutePolicy.ShouldTryAlternate(1) && MobileRoutePolicy.ShouldTryAlternate(2),
        "A single transient failure can still restart mobile protection");
    Require(MobileRoutePolicy.IsT2Provider("as12958", "T2 Mobile LLC") &&
            MobileRoutePolicy.IsT2Provider("tele2", "operator") &&
            !MobileRoutePolicy.IsT2Provider("other", "Home ISP"),
        "T2 provider template detection is unsafe");
}

static void TestUpdateTransportFailure()
{
    var offline = new HttpRequestException("host unknown");
    var serverError = new HttpRequestException("server error", null, HttpStatusCode.ServiceUnavailable);
    Require(GitHubUpdateService.ClassifyTransportFailure(offline) == UpdateState.Unavailable,
        "A DNS/network outage still opens the update failure modal");
    Require(GitHubUpdateService.ClassifyTransportFailure(serverError) == UpdateState.Failed,
        "A real GitHub HTTP error was hidden as an offline state");
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
