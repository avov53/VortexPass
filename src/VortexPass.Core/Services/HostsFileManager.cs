using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VortexPass.Core.Services;

public sealed record HostsFileStatus(bool Applied, int EntryCount, DateTimeOffset? UpdatedAt, string Detail);
public sealed record HostsOverride(IPAddress Address, string Host);

public sealed class HostsFileManager
{
    public const string BeginMarker = "# VortexPass begin";
    public const string EndMarker = "# VortexPass end";
    public const string HostsAssetSha256 = "ca810989b7e9ac87fb1ddb5786f01a80d862a6ff8f3c1cc883bb09baeab1c34d";
    private static readonly HashSet<string> AllowedDynamicHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        RobloxThumbnailResolver.ThumbnailHost,
        "www.youtube.com",
        "youtube.com",
        "suggestqueries.google.com",
        "suggestqueries-clients6.youtube.com",
        "youtubei.googleapis.com",
        "youtube.googleapis.com",
        "jnn-pa.googleapis.com",
        "i.ytimg.com",
        "s.ytimg.com",
        "yt3.ggpht.com",
        "yt4.ggpht.com",
        "yt5.ggpht.com",
        "yt6.ggpht.com",
        "yt3.googleusercontent.com",
        "yt4.googleusercontent.com",
        "yt5.googleusercontent.com",
        "yt6.googleusercontent.com",
        "youtube-pa.googleapis.com",
        "youtubeembeddedplayer.googleapis.com",
        "clients1.google.com",
        "clients2.google.com",
        "clients3.google.com",
        "clients4.google.com",
        "clients5.google.com",
        "clients6.google.com",
        "web.telegram.org",
        "webk.telegram.org",
        "webz.telegram.org",
        "pluto.web.telegram.org",
        "venus.web.telegram.org",
        "aurora.web.telegram.org",
        "vesta.web.telegram.org",
        "kws.web.telegram.org",
        "zws.web.telegram.org",
        "kws2.web.telegram.org",
        "kws2-1.web.telegram.org",
        "kws4.web.telegram.org",
        "kws4-1.web.telegram.org",
        "zws2.web.telegram.org",
        "zws2-1.web.telegram.org",
        "zws4.web.telegram.org",
        "zws4-1.web.telegram.org",
        "telegram.org",
        "t.me",
        "telegram.me",
        "telegram-cdn.org",
        "cdn-telegram.org",
        "telegramusercontent.com",
        "telesco.pe",
        "cdn.telesco.pe",
        "cdn1.telesco.pe",
        "cdn2.telesco.pe",
        "cdn3.telesco.pe",
        "cdn4.telesco.pe",
        "cdn5.telesco.pe",
        "cdn6.telesco.pe"
    };

    private readonly string _assetPath;
    private readonly string _hostsPath;
    private readonly string _backupPath;

    public HostsFileManager(string baseDirectory, string stateDirectory, string? hostsPath = null)
    {
        _assetPath = Path.Combine(baseDirectory, "bin", "files", "flowseal-hosts.txt");
        _hostsPath = hostsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        _backupPath = Path.Combine(stateDirectory, "hosts.before-vortexpass.bak");
    }

    public HostsFileStatus GetStatus()
    {
        try
        {
            if (!File.Exists(_hostsPath)) return new(false, 0, null, "Системный hosts не найден");
            var lines = File.ReadAllLines(_hostsPath);
            var inside = false;
            var found = false;
            var count = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith(BeginMarker, StringComparison.Ordinal)) { inside = true; found = true; continue; }
                if (inside && line.StartsWith(EndMarker, StringComparison.Ordinal)) { inside = false; continue; }
                if (inside && TryParseEntry(line, out _, out _)) count++;
            }
            return new(found && !inside, count, found ? File.GetLastWriteTimeUtc(_hostsPath) : null,
                found && !inside ? "hosts применён" : found ? "Блок VortexPass повреждён" : "hosts не применён");
        }
        catch (Exception ex) { return new(false, 0, null, ex.Message); }
    }

    public Task<HostsFileStatus> ApplyAsync(CancellationToken cancellationToken = default) =>
        ApplyAsync(Array.Empty<HostsOverride>(), includeStaticFallbacks: false, cancellationToken);

    public async Task<HostsFileStatus> ApplyAsync(IEnumerable<HostsOverride> overrides, CancellationToken cancellationToken = default)
        => await ApplyAsync(overrides, includeStaticFallbacks: false, cancellationToken);

    public async Task<HostsFileStatus> ApplyAsync(
        IEnumerable<HostsOverride> overrides,
        bool includeStaticFallbacks,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_assetPath)) throw new FileNotFoundException("Не найден закреплённый hosts-набор Flowseal.", _assetPath);
        if (!await HashVerifier.MatchesAsync(_assetPath, HostsAssetSha256, cancellationToken))
            throw new InvalidDataException("SHA-256 hosts-набора Flowseal не совпадает с утверждённым значением.");

        var flowsealEntries = (await File.ReadAllLinesAsync(_assetPath, cancellationToken))
            .Select(line => TryParseEntry(line, out var address, out var host) && address is not null && host is not null
                ? new HostsOverride(address, host)
                : null)
            .Where(entry => entry is not null && IsAllowedHost(entry.Host))
            .Select(entry => (Address: entry!.Address, Host: entry.Host))
            .Distinct()
            .ToArray();
        if (flowsealEntries.Length < 20) throw new InvalidDataException("Закреплённый hosts-набор не содержит ожидаемые Telegram Web и Discord voice записи.");
        var stableTelegramWebAddress = flowsealEntries
            .FirstOrDefault(entry => entry.Host.Equals("web.telegram.org", StringComparison.OrdinalIgnoreCase))
            .Address;
        var telegramWebAliases = stableTelegramWebAddress is null
            ? Array.Empty<(IPAddress Address, string Host)>()
            : new (IPAddress Address, string Host)[]
            {
                (stableTelegramWebAddress, "webk.telegram.org"),
                (stableTelegramWebAddress, "webz.telegram.org")
            };
        var additionalEntries = overrides
            .Where(entry => IsAllowedHost(entry.Host))
            .Select(entry => (Address: entry.Address, Host: entry.Host.TrimEnd('.').ToLowerInvariant()))
            .ToArray();
        // The signed Flowseal entries are SNI-aware routes. A raw TCP probe to their IP
        // does not contain a hostname and can be blocked by the very DPI rules that winws
        // is meant to bypass, so it must not be used to discard the trusted Telegram block.
        // A Telegram fallback must never install the bundled discord.media block.
        // Those hostnames are region-specific CDN/voice routes and pinning hundreds
        // of them to one remote address can break an otherwise healthy Discord.
        var trustedEntries = includeStaticFallbacks
            ? flowsealEntries
                .Where(entry => IsTelegramWebFallbackHost(entry.Host))
                .Concat(telegramWebAliases)
                .Distinct()
                .ToArray()
            : Array.Empty<(IPAddress Address, string Host)>();
        var candidateEntries = trustedEntries.Concat(additionalEntries).Distinct().ToArray();
        var reachableAddresses = new HashSet<IPAddress>();
        var skippedAddresses = new List<IPAddress>();
        var reachability = await Task.WhenAll(additionalEntries.Select(x => x.Address).Distinct().Select(async address =>
            (Address: address, Reachable: await IsReachableAsync(address, cancellationToken))));
        foreach (var result in reachability)
        {
            if (result.Reachable)
                reachableAddresses.Add(result.Address);
            else
                skippedAddresses.Add(result.Address);
        }

        var entries = candidateEntries
            .Where(entry => trustedEntries.Contains(entry) || reachableAddresses.Contains(entry.Address))
            .ToArray();
        if (entries.Length == 0)
            throw new IOException("Все IP из hosts-набора сейчас недоступны по TCP 443; системный hosts не изменён.");

        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        var original = File.Exists(_hostsPath) ? await File.ReadAllTextAsync(_hostsPath, cancellationToken) : string.Empty;
        await File.WriteAllTextAsync(_backupPath, original, new UTF8Encoding(false), cancellationToken);
        var clean = RemoveOwnedBlock(original).TrimEnd('\r', '\n');
        var block = new StringBuilder()
            .AppendLine($"{BeginMarker} source=Flowseal commit={WinwsProfileComposer.FlowsealCommit}");
        foreach (var entry in entries) block.AppendLine($"{entry.Address} {entry.Host}");
        block.AppendLine(EndMarker);
        var updated = string.IsNullOrEmpty(clean) ? block.ToString() : $"{clean}{Environment.NewLine}{Environment.NewLine}{block}";
        await AtomicWriteAsync(updated, cancellationToken);
        await FlushDnsAsync(cancellationToken);
        var status = GetStatus();
        return skippedAddresses.Count == 0
            ? status
            : status with { Detail = $"{status.Detail}; пропущено недоступных IP: {string.Join(", ", skippedAddresses.Distinct())}" };
    }

    public async Task<HostsFileStatus> RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_hostsPath)) return GetStatus();
        var original = await File.ReadAllTextAsync(_hostsPath, cancellationToken);
        var restored = RemoveOwnedBlock(original).TrimEnd('\r', '\n');
        if (!string.IsNullOrEmpty(restored)) restored += Environment.NewLine;
        if (!string.Equals(original, restored, StringComparison.Ordinal))
        {
            await AtomicWriteAsync(restored, cancellationToken);
            await FlushDnsAsync(cancellationToken);
        }
        return GetStatus();
    }

    private async Task AtomicWriteAsync(string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_hostsPath) ?? throw new InvalidOperationException("Не удалось определить папку hosts.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"hosts.vortexpass.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(_hostsPath)) File.Replace(temporary, _hostsPath, null, true);
                    else File.Move(temporary, _hostsPath);
                    break;
                }
                catch (IOException) when (attempt < 8)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(180 * attempt), cancellationToken);
                }
                catch (UnauthorizedAccessException) when (attempt < 8)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(180 * attempt), cancellationToken);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string RemoveOwnedBlock(string content)
    {
        var output = new List<string>();
        var inside = false;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith(BeginMarker, StringComparison.Ordinal)) { inside = true; continue; }
            if (inside && line.StartsWith(EndMarker, StringComparison.Ordinal)) { inside = false; continue; }
            if (!inside) output.Add(line);
        }
        return string.Join(Environment.NewLine, output);
    }

    private static bool TryParseEntry(string line, out IPAddress? address, out string? host)
    {
        address = null;
        host = null;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out address)) return false;
        host = parts[1].TrimEnd('.').ToLowerInvariant();
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    private static bool IsAllowedHost(string host) =>
        host == "web.telegram.org" || host == "api.telegram.org" || host.EndsWith(".web.telegram.org", StringComparison.Ordinal) ||
        host.EndsWith(".discord.media", StringComparison.Ordinal) || AllowedDynamicHosts.Contains(host);

    public static bool IsTelegramWebFallbackHost(string host) =>
        host.Equals("telegram.org", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("telesco.pe", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".telegram.org", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".web.telegram.org", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".telesco.pe", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldUseTelegramStaticFallback(
        bool telegramWebEnabled,
        bool warpCompatibilityActive,
        bool emergencyRequested,
        IEnumerable<HostsOverride> dynamicOverrides)
    {
        if (!telegramWebEnabled || warpCompatibilityActive) return false;

        // DNS resolution alone does not prove that the returned Telegram edge is
        // reachable through the current provider. Keep the signed, SNI-aware
        // Telegram Web block installed while the direct profile is active and use
        // measured dynamic entries only as additional candidates. Previously a
        // DNS answer such as 149.154.167.99 disabled this block before the later
        // TCP probe rejected the address, leaving browsers with a single unrelated
        // hosts entry and Telegram Web stuck at "Waiting for network".
        _ = emergencyRequested;
        _ = dynamicOverrides;
        return true;
    }

    internal static bool IsAllowedDynamicHostForTests(string host) => IsAllowedHost(host);

    private static async Task<bool> IsReachableAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, 443, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static async Task FlushDnsAsync(CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Не удалось запустить очистку DNS-кэша.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Очистка DNS-кэша завершилась с кодом {process.ExitCode}.");
    }
}
