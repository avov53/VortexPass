using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VortexPass.Core.Models;

namespace VortexPass.App.Services;

internal sealed class DnsGuardController : IDisposable
{
    private readonly string _executable;
    private readonly string _snapshotPath;
    private Process? _process;
    private CancellationTokenSource? _heartbeat;
    private TaskCompletionSource<DnsGuardStatus>? _ready;

    public DnsGuardStatus Status { get; private set; } = new("idle", "Защищённый DNS выключен");
    public bool IsRunning => _process is { HasExited: false } && Status.Ready;
    public event Action<DnsGuardStatus>? StatusChanged;

    public DnsGuardController(string baseDirectory, string appDataDirectory)
    {
        _executable = Path.Combine(baseDirectory, "dns-guard", "VortexPass.DnsGuard.exe");
        _snapshotPath = Path.Combine(appDataDirectory, "mobile-dns-adapter.json");
    }

    public async Task RecoverStaleAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_snapshotPath)) return;
        if (!File.Exists(_executable)) throw new FileNotFoundException("Не найден модуль восстановления DNS.", _executable);
        using var process = StartProcess($"--recover --snapshot \"{_snapshotPath}\"");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new IOException("Не удалось восстановить DNS после предыдущего завершения VortexPass.");
        Update(new("restored", "DNS восстановлен после предыдущего завершения", CheckedAt: DateTimeOffset.UtcNow));
    }

    public async Task<DnsGuardStatus> StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return Status;
        await StopAsync(CancellationToken.None);
        if (!File.Exists(_executable))
            return Update(new("error", "Не найден VortexPass.DnsGuard.exe", CheckedAt: DateTimeOffset.UtcNow));

        Update(new("starting", "Подготовка защищённого DNS", CheckedAt: DateTimeOffset.UtcNow));
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _process = StartProcess($"--parent {Environment.ProcessId} --snapshot \"{_snapshotPath}\"");
        _process.OutputDataReceived += OutputReceived;
        _process.ErrorDataReceived += ErrorReceived;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            if (Status.State is not ("restored" or "idle" or "portBusy" or "dohUnavailable" or "error"))
                Update(new("restored", "DNS Guard завершён; исходный DNS восстановлен", CheckedAt: DateTimeOffset.UtcNow));
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = HeartbeatAsync(_heartbeat.Token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));
        try { return await _ready.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None);
            return Update(new("error", "Защищённый DNS не запустился за 18 секунд", CheckedAt: DateTimeOffset.UtcNow));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _heartbeat?.Cancel();
        _heartbeat?.Dispose();
        _heartbeat = null;
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("stopAndRestore");
                await process.StandardInput.FlushAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The guard also monitors the parent. Do not kill it before its
            // finally block has had a chance to restore the adapter.
        }
        finally { process.Dispose(); }
    }

    private Process StartProcess(string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(_executable, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(_executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить DNS Guard.");
        return process;
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_process is null || _process.HasExited) return;
                await _process.StandardInput.WriteLineAsync("heartbeat");
                await _process.StandardInput.FlushAsync();
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch { return; }
        }
    }

    private void OutputReceived(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        try
        {
            var status = JsonSerializer.Deserialize<DnsGuardStatus>(args.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (status is null) return;
            Update(status);
            if (status.Ready || status.State is "error" or "portBusy" or "dohUnavailable" or "restoreError")
                _ready?.TrySetResult(status);
        }
        catch { }
    }

    private void ErrorReceived(object sender, DataReceivedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
            Update(new("error", args.Data, CheckedAt: DateTimeOffset.UtcNow));
    }

    private DnsGuardStatus Update(DnsGuardStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
        return status;
    }

    public void Dispose()
    {
        _heartbeat?.Cancel();
        _heartbeat?.Dispose();
        _process?.Dispose();
    }
}
