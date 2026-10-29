using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace VortexPass.Core.Services;

public sealed record WarpBootstrapSnapshot(bool ProtectionWasRunning, DateTimeOffset StartedAt);

public sealed class WarpBootstrapService : IDisposable
{
    private const string ExpectedWinwsSha256 = "affb4f69d2ea302a7abccd5325d81826e140ddae014f1e070bc4a6c0dd555188";
    private readonly string? _baseDirectory;
    private WarpBootstrapSnapshot? _snapshot;
    private Process? _process;

    public WarpBootstrapService(string? baseDirectory = null) => _baseDirectory = baseDirectory;

    public bool IsActive => _snapshot is not null;
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;
    public WarpBootstrapSnapshot? Snapshot => _snapshot;

    public void Begin(bool protectionWasRunning)
    {
        _snapshot ??= new WarpBootstrapSnapshot(protectionWasRunning, DateTimeOffset.UtcNow);
    }

    public WarpBootstrapSnapshot? End()
    {
        var snapshot = _snapshot;
        _snapshot = null;
        return snapshot;
    }

    public async Task StartAsync(string arguments, bool protectionWasRunning, CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false })
        {
            Begin(protectionWasRunning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_baseDirectory))
            throw new InvalidOperationException("Не задан каталог компонентов WARP bootstrap.");

        var executable = Path.Combine(_baseDirectory, "bin", "winws.exe");
        var required = new[]
        {
            executable,
            Path.Combine(_baseDirectory, "bin", "WinDivert.dll"),
            Path.Combine(_baseDirectory, "bin", "WinDivert64.sys"),
            Path.Combine(_baseDirectory, "bin", "files", "list-warp.txt"),
            Path.Combine(_baseDirectory, "bin", "files", "quic_initial_www_google_com.bin"),
            Path.Combine(_baseDirectory, "bin", "files", "tls_clienthello_www_google_com.bin")
        };
        var missing = required.FirstOrDefault(path => !File.Exists(path));
        if (missing is not null)
            throw new FileNotFoundException($"Не найден компонент WARP bootstrap: {Path.GetFileName(missing)}", missing);
        string hash;
        await using (var executableStream = File.OpenRead(executable))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(executableStream, cancellationToken)).ToLowerInvariant();
        if (!hash.Equals(ExpectedWinwsSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("winws.exe не прошёл проверку SHA-256 для WARP bootstrap.");

        var diagnostics = new ConcurrentQueue<string>();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        void Capture(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            diagnostics.Enqueue(line.Trim());
            while (diagnostics.Count > 6) diagnostics.TryDequeue(out _);
        }
        process.OutputDataReceived += (_, args) => Capture(args.Data);
        process.ErrorDataReceived += (_, args) => Capture(args.Data);
        if (!process.Start()) throw new InvalidOperationException("Windows не запустила отдельный WARP bootstrap.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { await Task.Delay(400, cancellationToken); }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            process.Dispose();
            throw;
        }
        if (process.HasExited)
        {
            var detail = diagnostics.IsEmpty ? string.Empty : $" Вывод: {string.Join(" | ", diagnostics)}";
            var code = process.ExitCode;
            process.Dispose();
            throw new InvalidOperationException($"WARP bootstrap завершился сразу после запуска (код {code}).{detail}");
        }
        _process = process;
        Begin(protectionWasRunning);
    }

    public async Task<WarpBootstrapSnapshot?> StopAsync()
    {
        var snapshot = End();
        var process = _process;
        _process = null;
        if (process is null) return snapshot;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        finally { process.Dispose(); }
        return snapshot;
    }

    public void Dispose()
    {
        var process = _process;
        _process = null;
        End();
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
        process.Dispose();
    }
}
