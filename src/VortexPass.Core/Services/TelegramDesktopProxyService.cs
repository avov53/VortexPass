using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace VortexPass.Core.Services;

public sealed class TelegramDesktopProxyService : IDisposable
{
    public const string Version = "1.11.2-headless";
    public const string ExpectedSha256 = "fd2dac969c77bc7f944c8a2a7b94d3637c3ecf6023398faaa4e2af9373fbdefa";
    public const string ManifestSha256 = "97302511ab74596d7d707ee60ad262a36f7d35a09b2a49046a1c04c89b3a8624";
    public const int Port = 1443;

    private readonly string _sourcePath;
    private readonly string _legacySourcePath;
    private readonly string _runtimeDirectory;
    private readonly string _runtimeExecutable;
    private readonly string _logPath;
    private readonly string _secretPath;
    private readonly string _configurationOfferedPath;
    private Process? _process;
    private string? _secret;

    public TelegramDesktopProxyService(string baseDirectory, string appDataDirectory)
    {
        _sourcePath = Path.Combine(baseDirectory, "bin", "TgWsProxyHeadless", "TgWsProxyHeadless.exe");
        _legacySourcePath = Path.Combine(baseDirectory, "bin", "TgWsProxy.exe");
        _runtimeDirectory = Path.Combine(appDataDirectory, "telegram-proxy");
        _runtimeExecutable = Path.Combine(_runtimeDirectory, "TgWsProxyHeadless.exe");
        _logPath = Path.Combine(_runtimeDirectory, "proxy.log");
        _secretPath = Path.Combine(_runtimeDirectory, "secret.txt");
        _configurationOfferedPath = Path.Combine(_runtimeDirectory, ".telegram-configuration-offered");
    }

    public bool IsRunning => _process is { HasExited: false };
    public bool ShouldOfferConfiguration => !File.Exists(_configurationOfferedPath);
    public bool HasTelegramClientActivity => ReadCounters().Ws > 0 || HasValidTelegramHandshake();
    public string ProxyUrl => ReadProxyUrlFromLog() ?? $"tg://proxy?server=127.0.0.1&port={Port}&secret=dd{EnsureSecret()}";

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning && await IsPortReadyAsync(cancellationToken)) return;

        StopOwnedProxyProcesses();
        StopProcess();
        await VerifyAndPrepareAsync(cancellationToken);

        var startInfo = new ProcessStartInfo(_runtimeExecutable)
        {
            WorkingDirectory = _runtimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(Port.ToString());
        startInfo.ArgumentList.Add("--secret");
        startInfo.ArgumentList.Add(EnsureSecret());
        startInfo.ArgumentList.Add("--log-file");
        startInfo.ArgumentList.Add(_logPath);
        startInfo.ArgumentList.Add("--log-max-mb");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("--log-backups");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--buf-kb");
        startInfo.ArgumentList.Add("256");
        startInfo.ArgumentList.Add("--pool-size");
        startInfo.ArgumentList.Add("8");
        startInfo.ArgumentList.Add("--verbose");

        // Do not let activity from an earlier protection session produce a false
        // "Telegram Desktop connected" status after the proxy is restarted.
        File.WriteAllText(_logPath, string.Empty);

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };

        if (!_process.Start())
            throw new InvalidOperationException("Windows не запустила локальный Telegram WS Proxy.");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                var exitCode = _process.ExitCode;
                var detail = RecentLogSummary();
                _process.Dispose();
                _process = null;
                throw new InvalidOperationException($"Telegram WS Proxy завершился сразу после запуска (код {exitCode}). {detail}");
            }

            if (await IsPortReadyAsync(cancellationToken)) return;
            await Task.Delay(200, cancellationToken);
        }

        StopProcess();
        throw new TimeoutException($"Telegram WS Proxy запущен, но порт 127.0.0.1:{Port} не открылся за 10 секунд. {RecentLogSummary()}");
    }

    public void OpenTelegramConfiguration()
    {
        Process.Start(new ProcessStartInfo(ProxyUrl) { UseShellExecute = true });
        Directory.CreateDirectory(_runtimeDirectory);
        File.WriteAllText(_configurationOfferedPath, DateTimeOffset.UtcNow.ToString("O"));
    }

    public async Task StopAsync()
    {
        if (_process is not { HasExited: false })
        {
            StopOwnedProxyProcesses();
            StopProcess();
            return;
        }

        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        StopProcess();
        StopOwnedProxyProcesses();
    }

    public async Task<bool> IsPortReadyAsync(CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
            await client.ConnectAsync("127.0.0.1", Port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string RecentLogSummary()
    {
        try
        {
            if (!File.Exists(_logPath)) return "Лог прокси ещё не создан.";
            var lines = File.ReadLines(_logPath).TakeLast(5).Select(line => line.Trim()).Where(line => line.Length > 0);
            var summary = string.Join(" | ", lines);
            return summary.Length == 0 ? "Лог прокси пуст." : summary;
        }
        catch (Exception ex)
        {
            return $"Не удалось прочитать лог прокси: {ex.Message}";
        }
    }

    private async Task VerifyAndPrepareAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_sourcePath))
            throw new FileNotFoundException("В комплекте отсутствует bin\\TgWsProxyHeadless\\TgWsProxyHeadless.exe. Пересоберите или обновите VortexPass.", _sourcePath);
        if (!await HashVerifier.MatchesAsync(_sourcePath, ExpectedSha256, cancellationToken))
            throw new InvalidDataException("Telegram WS Proxy не прошёл проверку SHA-256. Файл не будет запущен.");

        Directory.CreateDirectory(_runtimeDirectory);
        var sourceDirectory = Path.GetDirectoryName(_sourcePath)
            ?? throw new InvalidOperationException("Не удалось определить папку Telegram WS Proxy.");
        await VerifyBundleAsync(sourceDirectory, cancellationToken);
        var runtimeManifest = Path.Combine(_runtimeDirectory, "runtime-manifest.json");
        if (!File.Exists(runtimeManifest) || !await HashVerifier.MatchesAsync(runtimeManifest, ManifestSha256, cancellationToken))
        {
            // Replace only the owned interpreter bundle. Keep proxy secret,
            // configuration marker and logs in the parent runtime directory.
            var interpreter = Path.Combine(_runtimeDirectory, "_internal");
            if (Directory.Exists(interpreter)) Directory.Delete(interpreter, true);
        }
        MirrorDirectory(sourceDirectory, _runtimeDirectory);
        await VerifyBundleAsync(_runtimeDirectory, cancellationToken);

        var storedSecret = LoadSecret();
        _secret = storedSecret ?? RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        await File.WriteAllTextAsync(_secretPath, _secret, cancellationToken);

        // A previously generated 16-character secret produced an invalid tg://proxy link.
        // Offer Telegram configuration again after replacing that legacy secret.
        if (storedSecret is null && File.Exists(_configurationOfferedPath))
            File.Delete(_configurationOfferedPath);
    }

    public static async Task VerifyBundleAsync(string directory, CancellationToken cancellationToken = default)
    {
        var manifest = Path.Combine(directory, "runtime-manifest.json");
        if (!await HashVerifier.MatchesAsync(manifest, ManifestSha256, cancellationToken))
            throw new InvalidDataException("Telegram WS Proxy: манифест не прошёл проверку SHA-256.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, cancellationToken));
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = Path.GetFullPath(Path.Combine(root, file.GetProperty("path").GetString()!));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !await HashVerifier.MatchesAsync(path, file.GetProperty("sha256").GetString()!, cancellationToken))
                throw new InvalidDataException("Telegram WS Proxy: файл runtime не прошёл проверку SHA-256.");
        }
    }

    private string EnsureSecret()
    {
        _secret ??= LoadSecret();
        return _secret ?? throw new InvalidOperationException("Конфигурация Telegram WS Proxy ещё не создана. Сначала запустите защиту Telegram.");
    }

    private string? LoadSecret()
    {
        try
        {
            if (File.Exists(_secretPath))
            {
                var secret = File.ReadAllText(_secretPath).Trim();
                if (IsSecret(secret)) return secret.ToLowerInvariant();
            }

            var oldConfig = Path.Combine(_runtimeDirectory, "TgWsProxy_data", "config.json");
            if (!File.Exists(oldConfig)) return null;
            var text = File.ReadAllText(oldConfig);
            var match = Regex.Match(text, "\"secret\"\\s*:\\s*\"(?<secret>[0-9a-fA-F]{32})\"");
            return match.Success ? match.Groups["secret"].Value.ToLowerInvariant() : null;
        }
        catch
        {
            return null;
        }
    }

    private string? ReadProxyUrlFromLog()
    {
        try
        {
            if (!File.Exists(_logPath)) return null;
            return File.ReadLines(_logPath)
                .Reverse()
                .Select(line => Regex.Match(line, @"tg://proxy\?server=127\.0\.0\.1&port=\d+&secret=(dd|ee)[0-9a-fA-F]+"))
                .FirstOrDefault(match => match.Success)
                ?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static void MirrorDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var target = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool IsSecret(string value) =>
        value.Length == 32 && value.All(Uri.IsHexDigit);

    private ProxyCounters ReadCounters()
    {
        try
        {
            if (!File.Exists(_logPath)) return new ProxyCounters(0, 0, 0);
            foreach (var line in File.ReadLines(_logPath).Reverse().Take(80))
            {
                var match = Regex.Match(line, @"stats:\s+total=(?<total>\d+).*?\s+ws=(?<ws>\d+).*?\s+bad=(?<bad>\d+)");
                if (!match.Success) continue;
                return new ProxyCounters(
                    int.Parse(match.Groups["total"].Value),
                    int.Parse(match.Groups["ws"].Value),
                    int.Parse(match.Groups["bad"].Value));
            }
            return new ProxyCounters(0, 0, 0);
        }
        catch
        {
            return new ProxyCounters(0, 0, 0);
        }
    }

    private bool HasValidTelegramHandshake()
    {
        try
        {
            if (!File.Exists(_logPath)) return false;
            return File.ReadLines(_logPath).Reverse().Take(500).Any(line =>
                line.Contains("handshake ok:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("fallback closed", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private void StopOwnedProxyProcesses()
    {
        foreach (var name in new[] { "TgWsProxyHeadless", "TgWsProxy" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!IsOwnedProxyPath(path)) continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
                catch
                {
                    // Ignore inaccessible processes; StartAsync will report a port conflict if one remains.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private bool IsOwnedProxyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.Equals(_runtimeExecutable, StringComparison.OrdinalIgnoreCase)
            || path.Equals(_sourcePath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(_legacySourcePath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(_runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void StopProcess()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        StopProcess();
        StopOwnedProxyProcesses();
    }

    private sealed record ProxyCounters(int Total, int Ws, int Bad);
}
