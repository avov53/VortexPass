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
        ApplyAsync(Array.Empty<HostsOverride>(), cancellationToken);

    public async Task<HostsFileStatus> ApplyAsync(IEnumerable<HostsOverride> overrides, CancellationToken cancellationToken = default)
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
        var additionalEntries = overrides
            .Where(entry => IsAllowedHost(entry.Host))
            .Select(entry => (Address: entry.Address, Host: entry.Host.TrimEnd('.').ToLowerInvariant()))
            .ToArray();
        var entries = flowsealEntries.Concat(additionalEntries).Distinct().ToArray();

        foreach (var address in entries.Select(x => x.Address).Distinct())
            await EnsureReachableAsync(address, cancellationToken);

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
        return GetStatus();
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
            if (File.Exists(_hostsPath)) File.Replace(temporary, _hostsPath, null, true);
            else File.Move(temporary, _hostsPath);
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
        host.EndsWith(".discord.media", StringComparison.Ordinal) || host == RobloxThumbnailResolver.ThumbnailHost;

    private static async Task EnsureReachableAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, 443, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            throw new IOException($"IP {address} из hosts-набора сейчас недоступен по TCP 443; системный hosts не изменён.", ex);
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
