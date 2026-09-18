using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VortexPass.Core.Services;

public enum WarpConnectionState
{
    NotInstalled,
    Disconnected,
    Connecting,
    Connected,
    Error
}

public sealed record WarpStatus(
    WarpConnectionState State,
    string Detail,
    string? CliPath = null,
    string? AppPath = null,
    string? Version = null,
    DateTimeOffset? CheckedAt = null,
    string? Error = null,
    string? Stage = null,
    double? Progress = null,
    bool Registered = false,
    bool ServiceRunning = false,
    string? Protocol = null);

public sealed record WarpDiagnostics(
    bool WarpRouteActive,
    bool DnsAvailable,
    long? LatencyMs,
    string? ExternalIp,
    string Detail,
    DateTimeOffset CheckedAt,
    IReadOnlyDictionary<string, string> Scenarios,
    IReadOnlyDictionary<string, WarpScenarioDiagnostic>? ScenarioDetails = null,
    string? ColoCode = null,
    string? TraceLocationCode = null,
    CloudflarePopLocation? PopLocation = null);

public enum WarpProbeState
{
    Available,
    Restricted,
    Partial,
    ServiceError,
    Unavailable,
    TimedOut,
    RouteUnconfirmed,
    AvailabilityUnknown,
    RegionRestricted
}

public sealed record WarpEndpointDiagnostic(
    string Label,
    WarpProbeState State,
    string Detail,
    int? HttpStatus = null,
    long? LatencyMs = null);

public sealed record WarpScenarioDiagnostic(
    WarpProbeState State,
    string Summary,
    IReadOnlyDictionary<string, WarpEndpointDiagnostic> Endpoints);

public sealed record WarpPerformanceEndpoint(
    string Label,
    bool Available,
    long? MedianLatencyMs,
    long? JitterMs,
    string Detail);

public sealed record WarpPerformanceResult(
    string NetworkKey,
    bool WarpConnected,
    DateTimeOffset CheckedAt,
    IReadOnlyDictionary<string, WarpPerformanceEndpoint> Endpoints,
    string Summary);

public enum WarpPerformanceVerdict { BaselineSaved, Better, Similar, Worse, BaselineUnavailable }

public sealed record WarpPerformanceComparison(WarpPerformanceVerdict Verdict, string Detail);

public sealed record WarpRecoveryResult(bool Succeeded, WarpStatus Status, string Detail, bool ServiceRestarted = false);

public sealed record WarpClientConfiguration(
    string RoutingMode,
    string Protocol,
    string MasqueMode,
    string DnsMode,
    int ProxyPort = 41870);
public sealed record WarpOwnedExclusions(IReadOnlyList<string> Hosts, IReadOnlyList<string> Ips);

public sealed class WarpService : IDisposable
{
    public const string InstallerUrl = "https://1111-releases.cloudflareclient.com/windows/Cloudflare_WARP_Release-x64.msi";
    private const string ExpectedPublisher = "Cloudflare, Inc.";
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private readonly HttpClient _http;
    private readonly Func<Uri, CancellationToken, Task<WarpEndpointDiagnostic>> _endpointProbe;
    private readonly CloudflarePopDirectoryService _popDirectory;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public WarpService(
        string appDataDirectory,
        HttpClient? httpClient = null,
        Func<Uri, CancellationToken, Task<WarpEndpointDiagnostic>>? endpointProbe = null)
    {
        _cacheDirectory = Path.Combine(appDataDirectory, "warp-cache");
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VortexPass-WARP/1.0");
        _endpointProbe = endpointProbe ?? ProbeEndpointAsync;
        _popDirectory = new CloudflarePopDirectoryService(_cacheDirectory, _http);
    }

    public WarpStatus Detect()
    {
        var cli = SelectInstalledPath(CandidateCliPaths(), File.Exists);
        var app = CandidateAppPaths().FirstOrDefault(File.Exists);
        if (cli is null)
            return new(WarpConnectionState.NotInstalled, "Официальный Cloudflare WARP не установлен", AppPath: app, CheckedAt: DateTimeOffset.UtcNow);
        return new(WarpConnectionState.Disconnected, "Клиент Cloudflare WARP найден", cli, app, CheckedAt: DateTimeOffset.UtcNow);
    }

    public async Task<WarpStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var found = Detect();
        if (found.CliPath is null) return found;
        try
        {
            var result = await RunCliAsync(found.CliPath, ["status"], TimeSpan.FromSeconds(8), cancellationToken);
            var state = ParseState(result.Output);
            var version = await ReadVersionAsync(found.CliPath, cancellationToken);
            var client = await ReadClientInfoAsync(found.CliPath, cancellationToken);
            if (result.ExitCode != 0 && state != WarpConnectionState.Connected)
                return found with { State = WarpConnectionState.Error, Detail = Sanitize(result.Output), Version = version, Error = Sanitize(result.Output), CheckedAt = DateTimeOffset.UtcNow, Registered = client.Registered, ServiceRunning = client.ServiceRunning, Protocol = client.Protocol };
            return found with
            {
                State = state,
                Detail = state == WarpConnectionState.Connected ? "WARP подключён" : state == WarpConnectionState.Connecting ? "WARP подключается" : "WARP выключен",
                Version = version,
                CheckedAt = DateTimeOffset.UtcNow,
                Stage = Sanitize(result.Output),
                Progress = state == WarpConnectionState.Connected ? 1 : null,
                Registered = client.Registered,
                ServiceRunning = client.ServiceRunning,
                Protocol = client.Protocol
            };
        }
        catch (Exception ex)
        {
            return found with { State = WarpConnectionState.Error, Detail = ex.Message, Error = ex.Message, CheckedAt = DateTimeOffset.UtcNow };
        }
    }

    public async Task<WarpStatus> SetConnectedAsync(bool connected, CancellationToken cancellationToken = default, IProgress<WarpStatus>? progress = null)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var current = await GetStatusAsync(cancellationToken);
            if (current.CliPath is null) return current;
            if (connected && current.State == WarpConnectionState.Connected) return current;
            if (!connected && current.State == WarpConnectionState.Disconnected) return current;

            var action = connected ? "connect" : "disconnect";
            using var listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            string? latestStage = null;
            using var listener = TryStartStatusListener(current.CliPath, line =>
            {
                latestStage = Sanitize(line);
                var observed = ParseState(line);
                var observedState = observed == WarpConnectionState.Connected || (!connected && observed == WarpConnectionState.Disconnected)
                    ? observed
                    : WarpConnectionState.Connecting;
                progress?.Report(current with
                {
                    State = observedState,
                    Detail = latestStage,
                    Stage = latestStage,
                    Progress = ConnectionProgress(latestStage, observedState),
                    CheckedAt = DateTimeOffset.UtcNow
                });
            });
            var listenerTask = listener is null ? Task.CompletedTask : ObserveListenerAsync(listener, listenerCancellation.Token);
            var result = await RunCliAsync(current.CliPath, [action], TimeSpan.FromSeconds(20), cancellationToken);
            if (result.ExitCode != 0)
            {
                listenerCancellation.Cancel();
                await IgnoreCancellationAsync(listenerTask);
                var error = Sanitize(result.Output);
                return current with { State = WarpConnectionState.Error, Detail = error, Error = error, Stage = latestStage, CheckedAt = DateTimeOffset.UtcNow };
            }

            var wait = connected ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30);
            var deadline = DateTimeOffset.UtcNow + wait;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(750, cancellationToken);
                var probe = await ReadConnectionStateAsync(current.CliPath, cancellationToken);
                if (connected && probe.State == WarpConnectionState.Connected)
                {
                    listenerCancellation.Cancel();
                    await IgnoreCancellationAsync(listenerTask);
                    return (await GetStatusAsync(cancellationToken)) with { Progress = 1, Stage = latestStage ?? probe.Detail };
                }
                if (!connected && probe.State == WarpConnectionState.Disconnected)
                {
                    listenerCancellation.Cancel();
                    await IgnoreCancellationAsync(listenerTask);
                    return await GetStatusAsync(cancellationToken);
                }
                if (IsTerminalError(latestStage)) break;
            }
            listenerCancellation.Cancel();
            await IgnoreCancellationAsync(listenerTask);
            var detail = string.IsNullOrWhiteSpace(latestStage)
                ? $"WARP не изменил состояние за {(int)wait.TotalSeconds} секунд"
                : $"WARP не подключился. Последняя стадия: {latestStage}";
            return current with { State = WarpConnectionState.Error, Detail = detail, Error = detail, Stage = latestStage, CheckedAt = DateTimeOffset.UtcNow };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<WarpStatus> RegisterConsumerAsync(CancellationToken cancellationToken = default)
    {
        var found = Detect();
        if (found.CliPath is null) return found;
        var existing = await ReadClientInfoAsync(found.CliPath, cancellationToken);
        if (existing.Registered) return await GetStatusAsync(cancellationToken);
        var result = await RunCliAsync(found.CliPath, ["--accept-tos", "registration", "new"], TimeSpan.FromSeconds(45), cancellationToken);
        if (result.ExitCode != 0)
        {
            var error = Sanitize(result.Output);
            return found with { State = WarpConnectionState.Error, Detail = error, Error = error, ServiceRunning = existing.ServiceRunning, Protocol = existing.Protocol, CheckedAt = DateTimeOffset.UtcNow };
        }
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<WarpStatus> ApplyConfigurationAsync(WarpClientConfiguration configuration, bool reconnect, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var found = Detect();
            if (found.CliPath is null) return found;
            var beforeStatus = await ReadConnectionStateAsync(found.CliPath, cancellationToken);
            var settings = await RunCliAsync(found.CliPath, ["settings", "list"], TimeSpan.FromSeconds(8), cancellationToken);
            var previousProtocol = settings.Output.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ? "WireGuard" : "MASQUE";
            var previousMasque = settings.Output.Contains("HTTP/2 only", StringComparison.OrdinalIgnoreCase) ? "h2-only" :
                settings.Output.Contains("HTTP/3 only", StringComparison.OrdinalIgnoreCase) ? "h3-only" : "h3-with-h2-fallback";
            var previousMode = ParseMode(settings.Output);
            var shouldReconnect = reconnect || beforeStatus.State == WarpConnectionState.Connected;
            try
            {
                if (beforeStatus.State != WarpConnectionState.Disconnected)
                    await RunCliCheckedAsync(found.CliPath, ["disconnect"], cancellationToken);
                var protocol = configuration.Protocol.Equals("wireguard", StringComparison.OrdinalIgnoreCase) ? "WireGuard" : "MASQUE";
                await RunCliCheckedAsync(found.CliPath, ["tunnel", "protocol", "set", protocol], cancellationToken);
                if (protocol == "MASQUE")
                    await RunCliCheckedAsync(found.CliPath, ["tunnel", "masque-options", "set", WarpRoutingPlanner.NormalizeMasque(configuration.MasqueMode)], cancellationToken);
                var mode = CliMode(configuration.RoutingMode, configuration.DnsMode);
                await RunCliCheckedAsync(found.CliPath, ["mode", mode], cancellationToken);
                if (mode == "proxy")
                    await RunCliCheckedAsync(found.CliPath, ["proxy", "port", Math.Clamp(configuration.ProxyPort, 1024, 65535).ToString()], cancellationToken);
                if (shouldReconnect)
                {
                    await RunCliCheckedAsync(found.CliPath, ["connect"], cancellationToken);
                    await WaitForStateAsync(found.CliPath, WarpConnectionState.Connected, TimeSpan.FromSeconds(45), cancellationToken);
                }
                return await GetStatusAsync(cancellationToken);
            }
            catch
            {
                try
                {
                    await RunCliAsync(found.CliPath, ["disconnect"], TimeSpan.FromSeconds(12), CancellationToken.None);
                    await RunCliAsync(found.CliPath, ["tunnel", "protocol", "set", previousProtocol], TimeSpan.FromSeconds(12), CancellationToken.None);
                    if (previousProtocol == "MASQUE")
                        await RunCliAsync(found.CliPath, ["tunnel", "masque-options", "set", previousMasque], TimeSpan.FromSeconds(12), CancellationToken.None);
                    await RunCliAsync(found.CliPath, ["mode", previousMode], TimeSpan.FromSeconds(12), CancellationToken.None);
                    if (beforeStatus.State == WarpConnectionState.Connected)
                        await RunCliAsync(found.CliPath, ["connect"], TimeSpan.FromSeconds(12), CancellationToken.None);
                }
                catch { }
                throw;
            }
        }
        finally { _operationGate.Release(); }
    }

    public static string SelectFastestProtocol(IReadOnlyDictionary<string, long> protocolMedians, string fallback = "MASQUE") =>
        protocolMedians.Where(item => item.Value > 0).OrderBy(item => item.Value).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key).FirstOrDefault() ?? fallback;

    public async Task<WarpOwnedExclusions> SyncSplitTunnelExclusionsAsync(
        IEnumerable<string> desiredHosts,
        IEnumerable<string> desiredIps,
        IEnumerable<string> previouslyOwnedHosts,
        IEnumerable<string> previouslyOwnedIps,
        CancellationToken cancellationToken = default)
    {
        var found = Detect();
        if (found.CliPath is null) return new([], []);
        var hostList = await RunCliAsync(found.CliPath, ["tunnel", "host", "list"], TimeSpan.FromSeconds(10), cancellationToken);
        var ipList = await RunCliAsync(found.CliPath, ["tunnel", "ip", "list"], TimeSpan.FromSeconds(10), cancellationToken);
        var existingHosts = ParseListedValues(hostList.Output);
        var existingIps = ParseListedValues(ipList.Output);
        var desiredHostSet = desiredHosts.Select(WarpRoutingPlanner.NormalizeDomain).Where(value => value is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredIpSet = desiredIps.Where(WarpRoutingPlanner.IsIpOrCidr).Select(value => value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedHosts = previouslyOwnedHosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedIps = previouslyOwnedIps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var value in ownedHosts.Where(value => !desiredHostSet.Contains(value)).ToArray())
        {
            await RunCliCheckedAsync(found.CliPath, ["tunnel", "host", "remove", value], cancellationToken);
            ownedHosts.Remove(value);
        }
        foreach (var value in ownedIps.Where(value => !desiredIpSet.Contains(value)).ToArray())
        {
            await RunCliCheckedAsync(found.CliPath, ["tunnel", "ip", value.Contains('/') ? "remove-range" : "remove", value], cancellationToken);
            ownedIps.Remove(value);
        }
        foreach (var value in desiredHostSet.Where(value => !existingHosts.Contains(value)))
        {
            await RunCliCheckedAsync(found.CliPath, ["tunnel", "host", "add", value], cancellationToken);
            ownedHosts.Add(value);
        }
        foreach (var value in desiredIpSet.Where(value => !existingIps.Contains(value)))
        {
            await RunCliCheckedAsync(found.CliPath, ["tunnel", "ip", value.Contains('/') ? "add-range" : "add", value], cancellationToken);
            ownedIps.Add(value);
        }
        return new(ownedHosts.Order().ToArray(), ownedIps.Order().ToArray());
    }

    internal static HashSet<string> ParseListedValues(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Trim().TrimStart('-', ' ', '\t'))
        .Where(line => WarpRoutingPlanner.NormalizeDomain(line) is not null || WarpRoutingPlanner.IsIpOrCidr(line))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string CliMode(string routingMode, string dnsMode)
    {
        routingMode = WarpRoutingPlanner.NormalizeRoutingMode(routingMode);
        dnsMode = WarpRoutingPlanner.NormalizeDnsMode(dnsMode);
        if (routingMode is "smart" or "proxy") return "proxy";
        if (routingMode == "dnsOnly") return dnsMode == "dot" ? "dot" : "doh";
        return dnsMode switch { "doh" => "warp+doh", "dot" => "warp+dot", "system" => "tunnel_only", _ => "warp" };
    }

    private static string ParseMode(string output)
    {
        var modeLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains("Mode:", StringComparison.OrdinalIgnoreCase));
        if (modeLine?.Contains("proxy", StringComparison.OrdinalIgnoreCase) == true) return "proxy";
        if (modeLine?.Contains("warp+doh", StringComparison.OrdinalIgnoreCase) == true) return "warp+doh";
        if (modeLine?.Contains("warp+dot", StringComparison.OrdinalIgnoreCase) == true) return "warp+dot";
        if (modeLine?.Contains("tunnel", StringComparison.OrdinalIgnoreCase) == true) return "tunnel_only";
        if (modeLine?.Contains("doh", StringComparison.OrdinalIgnoreCase) == true) return "doh";
        if (modeLine?.Contains("dot", StringComparison.OrdinalIgnoreCase) == true) return "dot";
        return "warp";
    }

    private static async Task RunCliCheckedAsync(string cliPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunCliAsync(cliPath, arguments, TimeSpan.FromSeconds(20), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException(Sanitize(result.Output));
    }

    private static async Task WaitForStateAsync(string cliPath, WarpConnectionState expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await ReadConnectionStateAsync(cliPath, cancellationToken);
            if (state.State == expected) return;
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"WARP не перешёл в состояние {expected}.");
    }

    public async Task<WarpRecoveryResult> RecoverOrdinaryRouteAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var found = Detect();
            if (found.CliPath is null)
                return new(true, found, "Клиент WARP не установлен; системный маршрут не изменён.");

            var initial = await ReadConnectionStateAsync(found.CliPath, cancellationToken);
            if (initial.State == WarpConnectionState.Disconnected)
                return new(true, await GetStatusAsync(cancellationToken), "WARP уже выключен; обычный маршрут активен.");

            var disconnected = await TryDisconnectAndVerifyAsync(found.CliPath, TimeSpan.FromSeconds(18), cancellationToken);
            if (disconnected.State == WarpConnectionState.Disconnected)
                return new(true, await GetStatusAsync(cancellationToken), "WARP отключён, обычный маршрут восстановлен.");

            // A failed tunnel setup can leave the daemon's DNS/WFP path in a
            // transitional state. Restart only the official service, preserve
            // its startup configuration, then explicitly disconnect once more.
            await RunSystemCommandAsync("sc.exe", ["stop", "CloudflareWARP"], TimeSpan.FromSeconds(15), cancellationToken);
            await WaitForServiceProcessAsync(expectedRunning: false, TimeSpan.FromSeconds(10), cancellationToken);
            await RunSystemCommandAsync("sc.exe", ["start", "CloudflareWARP"], TimeSpan.FromSeconds(15), cancellationToken);
            await WaitForServiceProcessAsync(expectedRunning: true, TimeSpan.FromSeconds(10), cancellationToken);
            disconnected = await TryDisconnectAndVerifyAsync(found.CliPath, TimeSpan.FromSeconds(18), cancellationToken);
            if (disconnected.State == WarpConnectionState.Disconnected)
                return new(true, await GetStatusAsync(cancellationToken), "Служба WARP перезапущена; обычный маршрут восстановлен.", true);

            var error = $"WARP не подтвердил отключение: {Sanitize(disconnected.Detail)}";
            return new(false, disconnected with { State = WarpConnectionState.Error, Detail = error, Error = error }, error, true);
        }
        catch (Exception ex)
        {
            var status = Detect() with { State = WarpConnectionState.Error, Detail = Sanitize(ex.Message), Error = Sanitize(ex.Message), CheckedAt = DateTimeOffset.UtcNow };
            return new(false, status, $"Аварийное восстановление WARP не завершено: {Sanitize(ex.Message)}");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public static bool NeedsEmergencyRecovery(WarpConnectionState state, bool requestedConnected, bool bootstrapActive) =>
        state == WarpConnectionState.Connecting ||
        (state == WarpConnectionState.Error && (requestedConnected || bootstrapActive));

    public static string BuildSafeDiagnosticReport(WarpStatus status, WarpDiagnostics? diagnostics, bool bootstrapActive)
    {
        var lines = new List<string>
        {
            "VortexPass WARP diagnostic",
            $"Time: {status.CheckedAt:O}",
            $"State: {status.State}",
            $"Stage: {Sanitize(status.Stage)}",
            $"Error: {Sanitize(status.Error)}",
            $"Version: {Sanitize(status.Version)}",
            $"Protocol: {Sanitize(status.Protocol)}",
            $"Service running: {status.ServiceRunning}",
            $"Registered: {status.Registered}",
            $"Bootstrap active: {bootstrapActive}"
        };
        if (diagnostics is not null)
        {
            lines.Add($"Route active: {diagnostics.WarpRouteActive}");
            lines.Add($"DNS available: {diagnostics.DnsAvailable}");
            lines.Add($"Latency: {diagnostics.LatencyMs?.ToString() ?? "n/a"} ms");
            lines.Add($"Cloudflare PoP: {Sanitize(diagnostics.PopLocation?.DisplayName ?? diagnostics.ColoCode)}");
            lines.Add($"Trace location: {Sanitize(diagnostics.TraceLocationCode)}");
            foreach (var scenario in diagnostics.ScenarioDetails ?? new Dictionary<string, WarpScenarioDiagnostic>())
                lines.Add($"Scenario {Sanitize(scenario.Key)}: {scenario.Value.State} - {Sanitize(scenario.Value.Summary)}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public async Task<string> DownloadInstallerAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = new Uri(InstallerUrl);
        if (!IsOfficialInstallerUri(source))
            throw new InvalidOperationException("Недоверенный источник установщика Cloudflare WARP.");

        Directory.CreateDirectory(_cacheDirectory);
        var path = Path.Combine(_cacheDirectory, "Cloudflare_WARP_Release-x64.msi");
        using var response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                if (total is > 0) progress?.Report((double)written / total.Value);
            }
        }
        if (!IsTrustedInstaller(path, out var signer))
        {
            File.Delete(path);
            throw new InvalidDataException($"Установщик WARP не прошёл проверку цифровой подписи Cloudflare ({signer}).");
        }
        return path;
    }

    public async Task<int> InstallAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        if (!IsTrustedInstaller(installerPath, out var signer))
            throw new InvalidDataException($"Установщик WARP не подписан Cloudflare ({signer}).");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("msiexec.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"/i \"{Path.GetFullPath(installerPath)}\""
            }
        };
        if (!process.Start()) throw new InvalidOperationException("Windows не запустила установщик Cloudflare WARP.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    public void OpenClient()
    {
        var status = Detect();
        if (status.AppPath is null) throw new FileNotFoundException("Приложение Cloudflare WARP не найдено.");
        Process.Start(new ProcessStartInfo(status.AppPath) { UseShellExecute = true });
    }

    public async Task<WarpDiagnostics> DiagnoseAsync(
        bool telegram,
        bool games,
        CancellationToken cancellationToken = default,
        bool telegramProxyRunning = false,
        bool telegramProxyHasClientActivity = false,
        bool fullTelegramProbe = false,
        Uri? proxyUri = null)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var routeActive = false;
        string? externalIp = null;
        string? coloCode = null;
        string? traceLocationCode = null;
        CloudflarePopLocation? popLocation = null;
        string routeDetail;
        try
        {
            using var proxyClient = proxyUri is null ? null : CreateProxyClient(proxyUri);
            var routeClient = proxyClient ?? _http;
            using var routeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            routeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await routeClient.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", routeTimeout.Token);
            var trace = await response.Content.ReadAsStringAsync(routeTimeout.Token);
            var values = ParseTrace(trace);
            routeActive = values.GetValueOrDefault("warp")?.Equals("on", StringComparison.OrdinalIgnoreCase) == true ||
                          values.GetValueOrDefault("warp")?.Equals("plus", StringComparison.OrdinalIgnoreCase) == true;
            externalIp = values.GetValueOrDefault("ip");
            coloCode = values.GetValueOrDefault("colo")?.ToUpperInvariant();
            traceLocationCode = values.GetValueOrDefault("loc")?.ToUpperInvariant();
            popLocation = await _popDirectory.ResolveAsync(coloCode, cancellationToken);
            var pop = popLocation?.DisplayName ?? coloCode;
            routeDetail = routeActive
                ? string.IsNullOrWhiteSpace(pop) ? "Cloudflare подтвердил WARP-маршрут" : $"Cloudflare подтвердил WARP-маршрут через {pop}"
                : "Cloudflare не подтвердил WARP-маршрут";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            routeDetail = $"Проверка маршрута не выполнена: {Short(ex.Message)}";
        }

        var dnsAvailable = false;
        try { dnsAvailable = (await Dns.GetHostAddressesAsync("one.one.one.one", cancellationToken)).Length > 0; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }

        long? latency = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync("one.one.one.one", 443, timeout.Token);
            stopwatch.Stop();
            latency = stopwatch.ElapsedMilliseconds;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }

        var details = await DiagnoseScenariosAsync(telegram, games, routeActive, latency,
            telegramProxyRunning, telegramProxyHasClientActivity, cancellationToken, fullTelegramProbe);
        var scenarios = details.ToDictionary(item => item.Key, item => item.Value.Summary, StringComparer.OrdinalIgnoreCase);
        return new(routeActive, dnsAvailable, latency, externalIp, routeDetail, checkedAt, scenarios, details,
            coloCode, traceLocationCode, popLocation);
    }

    public static IReadOnlyDictionary<string, string> ParseTrace(string? trace)
    {
        if (string.IsNullOrWhiteSpace(trace)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return trace.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('=', 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, WarpScenarioDiagnostic>> DiagnoseScenariosAsync(
        bool telegram,
        bool games,
        bool warpRouteActive,
        long? warpLatencyMs = null,
        bool telegramProxyRunning = false,
        bool telegramProxyHasClientActivity = false,
        CancellationToken cancellationToken = default,
        bool fullTelegramProbe = false)
    {
        var scenarios = new Dictionary<string, WarpScenarioDiagnostic>(StringComparer.OrdinalIgnoreCase);
        var tasks = new Dictionary<string, Task<WarpScenarioDiagnostic>>(StringComparer.OrdinalIgnoreCase);

        if (telegram)
            tasks["telegram"] = ProbeTelegramScenarioAsync(warpRouteActive, telegramProxyRunning,
                telegramProxyHasClientActivity, cancellationToken, fullTelegramProbe);

        if (games)
        {
            var summary = warpRouteActive
                ? warpLatencyMs.HasValue ? $"Системный WARP-туннель: {warpLatencyMs} мс до Cloudflare" : "Системный WARP-туннель подтверждён"
                : "Cloudflare не подтвердил WARP-маршрут";
            scenarios["games"] = new(warpRouteActive ? WarpProbeState.Available : WarpProbeState.RouteUnconfirmed,
                summary, new Dictionary<string, WarpEndpointDiagnostic>());
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks.Values);
            foreach (var task in tasks) scenarios[task.Key] = await task.Value;
        }

        return scenarios;
    }

    public async Task<WarpPerformanceResult> MeasurePerformanceAsync(bool warpConnected, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, Uri>? additionalEndpoints = null, Uri? proxyUri = null)
    {
        var endpoints = new Dictionary<string, Uri>
        {
            ["cloudflare"] = new("https://www.cloudflare.com/cdn-cgi/trace"),
            ["telegram"] = new("https://web.telegram.org/"),
            ["youtube"] = new("https://www.youtube.com/generate_204"),
            ["discord"] = new("https://discord.com/api/v10/gateway")
        };
        if (additionalEndpoints is not null)
            foreach (var endpoint in additionalEndpoints) endpoints[endpoint.Key] = endpoint.Value;
        using var limiter = new SemaphoreSlim(3, 3);
        var tasks = endpoints.ToDictionary(item => item.Key, async item =>
        {
            await limiter.WaitAsync(cancellationToken);
            try { return await MeasureEndpointAsync(item.Value, cancellationToken, proxyUri); }
            finally { limiter.Release(); }
        }, StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(tasks.Values);
        var results = tasks.ToDictionary(item => item.Key, item => item.Value.Result, StringComparer.OrdinalIgnoreCase);
        var available = results.Count(item => item.Value.Available);
        var summary = warpConnected
            ? $"WARP: измерено {available}/{results.Count} направлений"
            : $"Прямой маршрут: измерено {available}/{results.Count} направлений";
        return new(GetNetworkFingerprint(), warpConnected, DateTimeOffset.UtcNow, results, summary);
    }

    public async Task<WarpPerformanceEndpoint> ProbeWebRouteAsync(Uri uri, Uri? proxyUri, CancellationToken cancellationToken = default)
    {
        using var directClient = proxyUri is null
            ? new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(6) }) { Timeout = Timeout.InfiniteTimeSpan }
            : null;
        using var proxyClient = proxyUri is null ? null : CreateProxyClient(proxyUri);
        var client = proxyClient ?? directClient!;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 VortexPass-RouteProbe/1.0");
            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            watch.Stop();
            return (int)response.StatusCode < 500
                ? new(uri.Host, true, watch.ElapsedMilliseconds, 0, $"HTTP {(int)response.StatusCode}")
                : new(uri.Host, false, null, null, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(uri.Host, false, null, null, ex is OperationCanceledException ? "тайм-аут" : Short(ex.Message));
        }
    }

    public static WarpPerformanceComparison ComparePerformance(
        IReadOnlyDictionary<string, WarpPerformanceEndpoint> current,
        IReadOnlyDictionary<string, long>? baseline)
    {
        if (baseline is null || baseline.Count == 0)
            return new(WarpPerformanceVerdict.BaselineUnavailable, "Сначала выполните проверку с выключенным WARP, чтобы сохранить прямой baseline.");

        var deltas = current
            .Where(item => item.Value.Available && item.Value.MedianLatencyMs.HasValue && baseline.TryGetValue(item.Key, out var value) && value > 0)
            .Select(item => (Current: item.Value.MedianLatencyMs!.Value, Baseline: baseline[item.Key]))
            .ToArray();
        if (deltas.Length == 0)
            return new(WarpPerformanceVerdict.BaselineUnavailable, "Нет сопоставимых измерений с прямым маршрутом.");

        var worse = deltas.Count(item => item.Current - item.Baseline > Math.Max(20, item.Baseline / 4));
        var better = deltas.Count(item => item.Baseline - item.Current > Math.Max(20, item.Baseline / 4));
        if (worse > better)
            return new(WarpPerformanceVerdict.Worse, "WARP заметно медленнее прямого маршрута. Для игр и браузера лучше отключить WARP.");
        if (better > worse)
            return new(WarpPerformanceVerdict.Better, "WARP улучшает измеренный маршрут относительно прямого подключения.");
        return new(WarpPerformanceVerdict.Similar, "WARP и прямой маршрут показывают сопоставимую задержку.");
    }

    public static string GetNetworkFingerprint()
    {
        var parts = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Where(network => !network.Name.Contains("WARP", StringComparison.OrdinalIgnoreCase) && !network.Description.Contains("WARP", StringComparison.OrdinalIgnoreCase))
            .Select(network => $"{network.Id}|{network.GetIPProperties().GatewayAddresses.FirstOrDefault()?.Address}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", parts)));
        return Convert.ToHexString(bytes)[..16];
    }

    public static WarpConnectionState ParseState(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return WarpConnectionState.Error;
        if (output.Contains("Disconnected", StringComparison.OrdinalIgnoreCase) || output.Contains("Отключ", StringComparison.OrdinalIgnoreCase))
            return WarpConnectionState.Disconnected;
        if (output.Contains("Connecting", StringComparison.OrdinalIgnoreCase) || output.Contains("Подключение", StringComparison.OrdinalIgnoreCase))
            return WarpConnectionState.Connecting;
        if (output.Contains("Connected", StringComparison.OrdinalIgnoreCase) || output.Contains("Подключено", StringComparison.OrdinalIgnoreCase))
            return WarpConnectionState.Connected;
        return WarpConnectionState.Error;
    }

    public static bool IsOfficialInstallerUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("1111-releases.cloudflareclient.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

    public static string? SelectInstalledPath(IEnumerable<string> candidates, Func<string, bool> exists) =>
        candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && exists(path));

    public static bool IsTrustedInstaller(string path, out string signer)
    {
        signer = "подпись отсутствует";
        if (!File.Exists(path) || !path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || !VerifyEmbeddedSignature(path)) return false;
        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            signer = certificate.Subject;
            return certificate.Subject.Contains(ExpectedPublisher, StringComparison.OrdinalIgnoreCase) && DateTime.Now >= certificate.NotBefore && DateTime.Now <= certificate.NotAfter;
        }
        catch (CryptographicException ex)
        {
            signer = ex.Message;
            return false;
        }
    }

    private static IEnumerable<string> CandidateCliPaths()
    {
        var fromRegistry = ReadAppPath("warp-cli.exe");
        if (!string.IsNullOrWhiteSpace(fromRegistry)) yield return fromRegistry;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) yield return Path.Combine(programFiles, "Cloudflare", "Cloudflare WARP", "warp-cli.exe");
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(x86)) yield return Path.Combine(x86, "Cloudflare", "Cloudflare WARP", "warp-cli.exe");
        var serviceExecutable = ReadServiceExecutablePath();
        if (!string.IsNullOrWhiteSpace(serviceExecutable)) yield return Path.Combine(Path.GetDirectoryName(serviceExecutable)!, "warp-cli.exe");
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.Combine(directory, "warp-cli.exe");
    }

    private static IEnumerable<string> CandidateAppPaths()
    {
        var fromRegistry = ReadAppPath("Cloudflare WARP.exe");
        if (!string.IsNullOrWhiteSpace(fromRegistry)) yield return fromRegistry;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) yield return Path.Combine(programFiles, "Cloudflare", "Cloudflare WARP", "Cloudflare WARP.exe");
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(x86)) yield return Path.Combine(x86, "Cloudflare", "Cloudflare WARP", "Cloudflare WARP.exe");
    }

    private static string? ReadAppPath(string executable)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}");
            if (key?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path)) return path.Trim('"');
        }
        return null;
    }

    private static string? ReadServiceExecutablePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\CloudflareWARP");
        if (key?.GetValue("ImagePath") is not string raw || string.IsNullOrWhiteSpace(raw)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (expanded.StartsWith('"'))
        {
            var endQuote = expanded.IndexOf('"', 1);
            return endQuote > 1 ? expanded[1..endQuote] : null;
        }
        var executableEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? expanded[..(executableEnd + 4)] : expanded;
    }

    public static WarpProbeState ClassifyHttpStatus(int statusCode) => statusCode switch
    {
        451 => WarpProbeState.RegionRestricted,
        401 or 403 or 429 => WarpProbeState.Restricted,
        >= 200 and < 500 => WarpProbeState.Available,
        >= 500 => WarpProbeState.ServiceError,
        _ => WarpProbeState.Unavailable
    };

    public static WarpProbeState AggregateScenarioState(IEnumerable<WarpProbeState> states)
    {
        var values = states.ToArray();
        if (values.Length == 0) return WarpProbeState.Unavailable;
        if (values.All(IsRouteReachable))
        {
            if (values.Any(value => value == WarpProbeState.RegionRestricted)) return WarpProbeState.RegionRestricted;
            if (values.Any(value => value == WarpProbeState.ServiceError)) return WarpProbeState.ServiceError;
            if (values.Any(value => value == WarpProbeState.AvailabilityUnknown)) return WarpProbeState.AvailabilityUnknown;
            return values.Any(value => value == WarpProbeState.Restricted) ? WarpProbeState.Restricted : WarpProbeState.Available;
        }
        if (values.Any(IsRouteReachable)) return WarpProbeState.Partial;
        if (values.All(value => value == WarpProbeState.RouteUnconfirmed)) return WarpProbeState.RouteUnconfirmed;
        return values.Any(value => value == WarpProbeState.TimedOut) ? WarpProbeState.TimedOut : WarpProbeState.Unavailable;
    }

    private static bool IsRouteReachable(WarpProbeState state) =>
        state is WarpProbeState.Available or WarpProbeState.Restricted or WarpProbeState.ServiceError or
            WarpProbeState.AvailabilityUnknown or WarpProbeState.RegionRestricted;

    private async Task<WarpScenarioDiagnostic> ProbeScenarioAsync(
        string label,
        IReadOnlyDictionary<string, Uri> endpoints,
        bool warpRouteActive,
        CancellationToken cancellationToken)
    {
        var tasks = endpoints.ToDictionary(item => item.Key, item => _endpointProbe(item.Value, cancellationToken), StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(tasks.Values);
        var results = tasks.ToDictionary(item => item.Key, item => item.Value.Result, StringComparer.OrdinalIgnoreCase);
        var state = AggregateScenarioState(results.Values.Select(item => item.State));
        var reachable = results.Count(item => IsRouteReachable(item.Value.State));
        var summary = state switch
        {
            WarpProbeState.Available => $"{label}: маршрут доступен ({reachable}/{results.Count})",
            WarpProbeState.Restricted => $"{label}: маршрут доступен, отдельные узлы требуют вход или ограничивают запрос ({reachable}/{results.Count})",
            WarpProbeState.AvailabilityUnknown => $"{label}: маршрут до сайта доступен; доступность продукта для региона и аккаунта не подтверждена",
            WarpProbeState.RegionRestricted => $"{label}: маршрут доступен, но сервис сообщил об ограничении для текущего региона",
            WarpProbeState.Partial => $"{label}: доступен частично ({reachable}/{results.Count})",
            WarpProbeState.ServiceError => $"{label}: маршрут доступен, сервис вернул серверную ошибку",
            WarpProbeState.TimedOut => $"{label}: проверка не завершилась — тайм-аут",
            _ => $"{label}: сетевой маршрут недоступен"
        };
        if (!warpRouteActive)
            return new(WarpProbeState.RouteUnconfirmed, $"{summary}; WARP-маршрут не подтверждён Cloudflare", results);
        return new(state, summary, results);
    }

    private async Task<WarpScenarioDiagnostic> ProbeTelegramScenarioAsync(
        bool warpRouteActive,
        bool proxyRunning,
        bool proxyHasClientActivity,
        CancellationToken cancellationToken,
        bool fullTelegramProbe)
    {
        var webTask = ProbeEndpointsAsync(new Dictionary<string, Uri>
        {
            ["web"] = new("https://web.telegram.org/"),
            ["webK"] = new("https://webk.telegram.org/"),
            ["webZ"] = new("https://webz.telegram.org/")
        }, cancellationToken);
        var mediaEndpoints = Enumerable.Range(0, fullTelegramProbe ? 7 : 2)
            .ToDictionary(index => index == 0 ? "media" : $"media{index}",
                index => new Uri($"https://{(index == 0 ? "cdn" : $"cdn{index}")}.telesco.pe/"),
                StringComparer.OrdinalIgnoreCase);
        var mediaTask = ProbeEndpointsAsync(mediaEndpoints, cancellationToken);
        var socketTask = ProbeEndpointsAsync(new Dictionary<string, Uri>
        {
            ["websocket"] = new("wss://kws2.web.telegram.org/apiws"),
            ["websocket4"] = new("wss://kws4.web.telegram.org/apiws")
        }, cancellationToken);
        await Task.WhenAll(webTask, mediaTask, socketTask);

        var web = BuildTelegramGroup("Web-интерфейс", webTask.Result);
        var media = BuildTelegramGroup("Медиа и стикеры CDN", mediaTask.Result);
        var websocket = BuildTelegramGroup("Сообщения / WebSocket", socketTask.Result);
        var desktop = new WarpEndpointDiagnostic(
            "Telegram Desktop proxy",
            proxyRunning ? WarpProbeState.Available : WarpProbeState.Unavailable,
            proxyRunning
                ? proxyHasClientActivity
                    ? $"Локальный прокси 127.0.0.1:{TelegramDesktopProxyService.Port} работает; Telegram Desktop подключался"
                    : $"Локальный прокси 127.0.0.1:{TelegramDesktopProxyService.Port} работает, ожидает Telegram Desktop"
                : "Локальный Telegram Desktop-прокси не запущен");
        var result = new Dictionary<string, WarpEndpointDiagnostic>(StringComparer.OrdinalIgnoreCase)
        {
            ["webInterface"] = web,
            ["messagesWebSocket"] = websocket,
            ["mediaCdn"] = media,
            ["desktopProxy"] = desktop
        };

        var requiredGroups = new[] { web.State, media.State, websocket.State };
        var networkState = AggregateTelegramGroups(requiredGroups);
        var state = !warpRouteActive ? WarpProbeState.RouteUnconfirmed : networkState;
        var proxy = proxyRunning ? proxyHasClientActivity ? "Desktop-прокси активен" : "Desktop-прокси ожидает клиента" : "Desktop-прокси выключен";
        var summary = !warpRouteActive
            ? $"WARP-маршрут для Telegram не подтверждён; {proxy}"
            : networkState == WarpProbeState.Available
                ? $"Telegram Web, сообщения и медиа доступны через WARP; {proxy}"
                : IsRouteReachable(web.State)
                    ? $"Telegram Web работает, часть резервных маршрутов не подтверждена; {proxy}"
                    : $"Telegram Web-маршрут не подтверждён; {proxy}";
        return new(state, summary, result);
    }

    private async Task<IReadOnlyDictionary<string, WarpEndpointDiagnostic>> ProbeEndpointsAsync(
        IReadOnlyDictionary<string, Uri> endpoints,
        CancellationToken cancellationToken)
    {
        using var limiter = new SemaphoreSlim(2, 2);
        var tasks = endpoints.ToDictionary(item => item.Key, async item =>
        {
            await limiter.WaitAsync(cancellationToken);
            try { return await _endpointProbe(item.Value, cancellationToken); }
            finally { limiter.Release(); }
        }, StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(tasks.Values);
        return tasks.ToDictionary(item => item.Key, item => item.Value.Result, StringComparer.OrdinalIgnoreCase);
    }

    private static WarpEndpointDiagnostic BuildTelegramGroup(string label, IReadOnlyDictionary<string, WarpEndpointDiagnostic> endpoints)
    {
        var reachable = endpoints.Values.Where(item => IsRouteReachable(item.State)).ToArray();
        var state = reachable.Length > 0 ? WarpProbeState.Available : AggregateScenarioState(endpoints.Values.Select(item => item.State));
        var failures = endpoints.Where(item => !IsRouteReachable(item.Value.State))
            .Select(item => $"{item.Key}: {Short(item.Value.Detail)}").ToArray();
        var detail = reachable.Length > 0
            ? failures.Length == 0 ? $"{reachable.Length}/{endpoints.Count} маршрута подтверждены" : $"{reachable.Length}/{endpoints.Count} маршрута подтверждены; резерв: {string.Join("; ", failures)}"
            : string.Join("; ", endpoints.Select(item => $"{item.Key}: {Short(item.Value.Detail)}"));
        var latency = reachable.Where(item => item.LatencyMs.HasValue).Select(item => item.LatencyMs!.Value).DefaultIfEmpty().Min();
        return new(label, state, detail, LatencyMs: latency == 0 ? null : latency);
    }

    private static WarpProbeState AggregateTelegramGroups(IEnumerable<WarpProbeState> states)
    {
        var values = states.ToArray();
        if (values.All(IsRouteReachable)) return WarpProbeState.Available;
        return values.Any(IsRouteReachable) ? WarpProbeState.Partial : AggregateScenarioState(values);
    }

    private async Task<WarpPerformanceEndpoint> MeasureEndpointAsync(Uri uri, CancellationToken cancellationToken, Uri? proxyUri)
    {
        using var proxyClient = proxyUri is null ? null : CreateProxyClient(proxyUri);
        var http = proxyClient ?? _http;
        var samples = new List<long>();
        string? lastError = null;
        for (var sample = 0; sample < 3; sample++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("VortexPass-WARP-Performance/1.0");
                var watch = Stopwatch.StartNew();
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                watch.Stop();
                if ((int)response.StatusCode < 500) samples.Add(watch.ElapsedMilliseconds);
                else lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                lastError = ex is OperationCanceledException ? "тайм-аут" : Short(ex.Message);
            }
        }
        if (samples.Count == 0) return new(uri.Host, false, null, null, lastError ?? "нет ответа");
        samples.Sort();
        var median = samples[samples.Count / 2];
        var jitter = samples.Count < 2 ? 0 : samples[^1] - samples[0];
        return new(uri.Host, true, median, jitter, $"{samples.Count}/3 ответов");
    }

    private static HttpClient CreateProxyClient(Uri proxyUri) => new(new HttpClientHandler
    {
        UseProxy = true,
        Proxy = new WebProxy(proxyUri)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private async Task<WarpEndpointDiagnostic> ProbeEndpointAsync(Uri uri, CancellationToken cancellationToken)
    {
        var stages = new List<string>();
        IPAddress[] addresses;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            addresses = await Dns.GetHostAddressesAsync(uri.Host, timeout.Token);
            if (addresses.Length == 0) return new(uri.Host, WarpProbeState.Unavailable, "DNS не вернул адресов");
            stages.Add($"DNS OK ({addresses.Length})");
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(uri.Host, WarpProbeState.TimedOut, "DNS: тайм-аут");
        }
        catch (SocketException ex)
        {
            return new(uri.Host, WarpProbeState.Unavailable, $"DNS: {Short(ex.Message)}");
        }

        long latency;
        try
        {
            var watch = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 443, timeout.Token);
            watch.Stop();
            latency = watch.ElapsedMilliseconds;
            stages.Add($"TCP {latency} мс");
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(uri.Host, WarpProbeState.TimedOut, $"{string.Join("; ", stages)}; TCP: тайм-аут");
        }
        catch (SocketException ex)
        {
            return new(uri.Host, WarpProbeState.Unavailable, $"{string.Join("; ", stages)}; TCP: {Short(ex.Message)}");
        }

        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 443, timeout.Token);
            using var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, timeout.Token);
            stages.Add($"TLS {ssl.SslProtocol}");
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(uri.Host, WarpProbeState.TimedOut, $"{string.Join("; ", stages)}; TLS: тайм-аут", LatencyMs: latency);
        }
        catch (Exception ex) when (ex is SocketException or AuthenticationException or IOException)
        {
            return new(uri.Host, WarpProbeState.Unavailable, $"{string.Join("; ", stages)}; TLS: {Short(ex.Message)}", LatencyMs: latency);
        }

        if (uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
            return await ProbeWebSocketAsync(uri, stages, latency, cancellationToken);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            var state = ClassifyHttpStatus(status);
            var body = IsAiProductSite(uri.Host) ? await ReadLimitedBodyAsync(response, timeout.Token) : string.Empty;
            if (IsAiProductSite(uri.Host) && DetectRegionalRestriction(body))
                state = WarpProbeState.RegionRestricted;
            else if (IsAiProductSite(uri.Host) && state == WarpProbeState.Available)
                state = WarpProbeState.AvailabilityUnknown;
            var qualifier = state == WarpProbeState.RegionRestricted ? "сервис ограничен для текущего региона" :
                state == WarpProbeState.AvailabilityUnknown ? "маршрут доступен, регион и аккаунт не проверены" :
                state == WarpProbeState.Restricted ? "маршрут отвечает, требуется вход или запрос ограничен" :
                state == WarpProbeState.ServiceError ? "сервис вернул серверную ошибку" : "доступен";
            return new(uri.Host, state, $"{string.Join("; ", stages)}; HTTP {status}: {qualifier}", status, latency);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(uri.Host, WarpProbeState.TimedOut, $"{string.Join("; ", stages)}; HTTP: тайм-аут", LatencyMs: latency);
        }
        catch (HttpRequestException ex)
        {
            return new(uri.Host, WarpProbeState.Unavailable, $"{string.Join("; ", stages)}; HTTP: {Short(ex.Message)}", LatencyMs: latency);
        }
    }

    public static bool DetectRegionalRestriction(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var markers = new[]
        {
            "not available in your country", "not available in your region", "not available in your location",
            "isn't currently available in your country", "is not currently available in your country",
            "unsupported country", "unsupported region", "country is not supported", "region is not supported",
            "недоступен в вашей стране", "недоступно в вашей стране", "не поддерживается в вашей стране",
            "в вашей стране приостановлен", "в вашей стране приостановлены"
        };
        return markers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAiProductSite(string host) =>
        host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("gemini.google.com", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadLimitedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int limit = 128 * 1024;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (output.Length < limit)
        {
            var remaining = (int)Math.Min(buffer.Length, limit - output.Length);
            var read = await input.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<WarpEndpointDiagnostic> ProbeWebSocketAsync(
        Uri uri,
        List<string> stages,
        long latency,
        CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new ClientWebSocket();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await socket.ConnectAsync(uri, timeout.Token);
            return new(uri.Host, WarpProbeState.Available, $"{string.Join("; ", stages)}; WebSocket подключён", LatencyMs: latency);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(uri.Host, WarpProbeState.TimedOut, $"{string.Join("; ", stages)}; WebSocket: тайм-аут", LatencyMs: latency);
        }
        catch (WebSocketException ex)
        {
            var message = Short(ex.Message);
            var statusCode = FindHttpStatus(ex);
            var state = statusCode.HasValue
                ? ClassifyHttpStatus(statusCode.Value) switch
                {
                    WarpProbeState.Available => WarpProbeState.Restricted,
                    var classified => classified
                }
                : WarpProbeState.Unavailable;
            return new(uri.Host, state,
                $"{string.Join("; ", stages)}; WebSocket: {message}", LatencyMs: latency);
        }
    }

    private static int? FindHttpStatus(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: not null } http) return (int)http.StatusCode.Value;
            var match = Regex.Match(current.Message, @"\b(?<status>[45]\d{2})\b");
            if (match.Success && int.TryParse(match.Groups["status"].Value, out var status)) return status;
        }
        return null;
    }

    private static async Task<string?> ReadVersionAsync(string cliPath, CancellationToken cancellationToken)
    {
        var result = await RunCliAsync(cliPath, ["--version"], TimeSpan.FromSeconds(5), cancellationToken);
        return result.ExitCode == 0 ? Short(result.Output) : null;
    }

    private static async Task<(bool Registered, bool ServiceRunning, string? Protocol)> ReadClientInfoAsync(string cliPath, CancellationToken cancellationToken)
    {
        var serviceProcesses = Process.GetProcessesByName("warp-svc");
        var serviceRunning = serviceProcesses.Length > 0;
        foreach (var process in serviceProcesses) process.Dispose();
        var registration = await RunCliAsync(cliPath, ["registration", "show"], TimeSpan.FromSeconds(8), cancellationToken);
        // Registration output contains identifiers and keys. Deliberately inspect
        // only success markers and never return or log the raw text.
        var registered = registration.ExitCode == 0 &&
                         registration.Output.Contains("Account type", StringComparison.OrdinalIgnoreCase) &&
                         !registration.Output.Contains("not registered", StringComparison.OrdinalIgnoreCase);
        string? protocol = null;
        var settings = await RunCliAsync(cliPath, ["settings", "list"], TimeSpan.FromSeconds(8), cancellationToken);
        if (settings.ExitCode == 0)
        {
            var line = settings.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(value => value.Contains("tunnel protocol", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(line)) protocol = Sanitize(line[(line.LastIndexOf(':') + 1)..]);
        }
        return (registered, serviceRunning, protocol);
    }

    private static async Task<(WarpConnectionState State, string Detail)> ReadConnectionStateAsync(string cliPath, CancellationToken cancellationToken)
    {
        var result = await RunCliAsync(cliPath, ["--no-ansi", "status"], TimeSpan.FromSeconds(8), cancellationToken);
        return (ParseState(result.Output), Sanitize(result.Output));
    }

    private static async Task<WarpStatus> TryDisconnectAndVerifyAsync(string cliPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var command = await RunCliAsync(cliPath, ["disconnect"], TimeSpan.FromSeconds(12), cancellationToken);
        var deadline = DateTimeOffset.UtcNow + timeout;
        (WarpConnectionState State, string Detail) probe = (WarpConnectionState.Error, Sanitize(command.Output));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500, cancellationToken);
            probe = await ReadConnectionStateAsync(cliPath, cancellationToken);
            if (probe.State == WarpConnectionState.Disconnected)
                return new(WarpConnectionState.Disconnected, "WARP выключен", CliPath: cliPath, CheckedAt: DateTimeOffset.UtcNow, Stage: probe.Detail);
        }
        return new(WarpConnectionState.Error, probe.Detail, CliPath: cliPath, CheckedAt: DateTimeOffset.UtcNow, Error: probe.Detail, Stage: probe.Detail);
    }

    private static async Task RunSystemCommandAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException($"Windows не запустила {Path.GetFileName(executable)}.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        await process.WaitForExitAsync(timeoutSource.Token);
        var output = $"{await stdout} {await stderr}".Trim();
        // sc.exe returns 1062 when the service is already stopped. That is safe
        // during route recovery; every other non-zero result is actionable.
        if (process.ExitCode != 0 && !(arguments.FirstOrDefault()?.Equals("stop", StringComparison.OrdinalIgnoreCase) == true && output.Contains("1062", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{Path.GetFileName(executable)} завершилась с кодом {process.ExitCode}: {Sanitize(output)}");
    }

    private static async Task WaitForServiceProcessAsync(bool expectedRunning, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var processes = Process.GetProcessesByName("warp-svc");
            var running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (running == expectedRunning) return;
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException(expectedRunning ? "Служба WARP не запустилась вовремя." : "Служба WARP не остановилась вовремя.");
    }

    private static Process StartStatusListener(string cliPath, Action<string> onLine)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(cliPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.ArgumentList.Add("--listen");
        process.StartInfo.ArgumentList.Add("--no-ansi");
        process.StartInfo.ArgumentList.Add("status");
        process.OutputDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) onLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) onLine(args.Data); };
        if (!process.Start()) throw new InvalidOperationException("Windows не запустила прослушивание статуса warp-cli.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static Process? TryStartStatusListener(string cliPath, Action<string> onLine)
    {
        try { return StartStatusListener(cliPath, onLine); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            onLine($"Прослушивание статуса недоступно, используется опрос: {ex.Message}");
            return null;
        }
    }

    private static async Task ObserveListenerAsync(Process process, CancellationToken cancellationToken)
    {
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    public static double ConnectionProgress(string? stage, WarpConnectionState state)
    {
        if (state == WarpConnectionState.Connected) return 1;
        var value = stage ?? string.Empty;
        if (value.Contains("registration", StringComparison.OrdinalIgnoreCase)) return .12;
        if (value.Contains("settings", StringComparison.OrdinalIgnoreCase)) return .26;
        if (value.Contains("network", StringComparison.OrdinalIgnoreCase)) return .42;
        if (value.Contains("handshake", StringComparison.OrdinalIgnoreCase) || value.Contains("tunnel", StringComparison.OrdinalIgnoreCase)) return .68;
        if (value.Contains("connectivity", StringComparison.OrdinalIgnoreCase) || value.Contains("route", StringComparison.OrdinalIgnoreCase)) return .86;
        return .18;
    }

    public static bool IsTerminalError(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("error", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("unable", StringComparison.OrdinalIgnoreCase));

    private static async Task<(int ExitCode, string Output)> RunCliAsync(string cliPath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(cliPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Windows не запустила warp-cli.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        await process.WaitForExitAsync(timeoutSource.Token);
        return (process.ExitCode, $"{await stdout} {await stderr}".Trim());
    }

    private static string Short(string? value)
    {
        var text = string.Join(' ', (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= 280 ? text : text[..280];
    }

    public static string Sanitize(string? value)
    {
        var text = Short(value);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = Regex.Replace(text, @"(?i)\b(device|account)?\s*id\s*:\s*\S+", "$1 ID: [скрыто]");
        text = Regex.Replace(text, @"(?i)\b(public\s+key|private\s+key|license|token)\s*:\s*\S+", "$1: [скрыто]");
        text = Regex.Replace(text, @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "[uuid скрыт]");
        return text;
    }

    private static bool VerifyEmbeddedSignature(string filePath)
    {
        var fileInfo = new WinTrustFileInfo(filePath);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf(fileInfo));
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData(fileInfoPointer);
            return WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
        public WinTrustFileInfo(string path) { StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(); FilePath = path; FileHandle = IntPtr.Zero; KnownSubject = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public string? UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>(); PolicyCallbackData = IntPtr.Zero; SipClientData = IntPtr.Zero;
            UiChoice = 2; RevocationChecks = 0; UnionChoice = 1; FileInfo = fileInfo; StateAction = 0;
            StateData = IntPtr.Zero; UrlReference = null; ProviderFlags = 0x00000080; UiContext = 0;
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, ref WinTrustData data);

    public void Dispose()
    {
        _operationGate.Dispose();
        _http.Dispose();
    }
}
