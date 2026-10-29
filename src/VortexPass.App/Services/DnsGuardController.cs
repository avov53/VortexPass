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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _configuration = string.Empty;

    public DnsGuardStatus Status { get; private set; } = new("idle", "Защищённый DNS выключен");
    public bool IsRunning => _process is { HasExited: false } && Status.Ready;
    public bool DetailedDiagnostics {get;set;}
    public event Action<DnsGuardStatus>? StatusChanged;
    public event Action<string>? DiagnosticReceived;

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

    public async Task<DnsGuardStatus> StartAsync(CancellationToken cancellationToken, string mode = "doh", IEnumerable<string>? hosts = null, string preferredProvider = "auto")
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await StartCoreAsync(cancellationToken, mode, hosts, preferredProvider); }
        finally { _gate.Release(); }
    }

    private async Task<DnsGuardStatus> StartCoreAsync(CancellationToken cancellationToken, string mode, IEnumerable<string>? hosts, string preferredProvider)
    {
        var checkHosts = string.Join(",", (hosts ?? ["example.com"]).Where(h => Uri.CheckHostName(h) == UriHostNameType.Dns)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(64));
        var provider = preferredProvider is "google" or "cloudflare" or "quad9" or "8.8.8.8" or "8.8.4.4" ? preferredProvider : "auto";
        var normalizedMode = VortexPass.Core.Services.DnsModePolicy.Normalize(mode);
        var configuration = $"{normalizedMode}:{provider}:{checkHosts}";
        if (IsRunning && _configuration == configuration) return Status;
        await StopCoreAsync(CancellationToken.None);
        if (!File.Exists(_executable))
            return Update(new("error", "Не найден VortexPass.DnsGuard.exe", CheckedAt: DateTimeOffset.UtcNow));

        Update(new("starting", "Подготовка защищённого DNS", CheckedAt: DateTimeOffset.UtcNow));
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _configuration = configuration;
        _process = StartProcess($"--parent {Environment.ProcessId} --snapshot \"{_snapshotPath}\" --mode {normalizedMode} --hosts \"{checkHosts}\" --provider {provider}"+(DetailedDiagnostics?" --diagnostic-details":""));
        _process.OutputDataReceived += OutputReceived;
        _process.ErrorDataReceived += ErrorReceived;
        _process.EnableRaisingEvents = true;
        _process.Exited += (sender, _) =>
        {
            if (!ReferenceEquals(sender, _process)) return;
            DiagnosticReceived?.Invoke(JsonSerializer.Serialize(new {type="dns.diagnostic",layer="process",reason="DNS Guard exited",exitCode=((Process)sender!).ExitCode}));
            if (File.Exists(_snapshotPath))
                Update(new("restoreError", "DNS Guard завершён; требуется восстановление сохранённых DNS-настроек", CheckedAt: DateTimeOffset.UtcNow));
            else if (Status.State is not ("restored" or "idle" or "portBusy" or "dohUnavailable" or "error" or "restoreError"))
                Update(new("restored", "DNS Guard завершён; восстановление подтверждено", CheckedAt: DateTimeOffset.UtcNow));
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        // The startup/trial token may end while protection continues. Parent
        // monitoring and explicit Stop own the lifetime of a ready guard.
        _heartbeat = new CancellationTokenSource();
        _ = HeartbeatAsync(_heartbeat.Token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try { return await _ready.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopCoreAsync(CancellationToken.None);
            return Update(new("error", "Защищённый DNS не запустился за 35 секунд", CheckedAt: DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException)
        {
            await StopCoreAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await StopCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _configuration = string.Empty;
        _heartbeat?.Cancel();
        _heartbeat?.Dispose();
        _heartbeat = null;
        var process = _process;
        _process = null;
        if (process is null) { await RecoverStaleAsync(cancellationToken); return; }
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
            // Do not start a second guard or restore its live snapshot while
            // the previous process may still be restoring the same adapter.
            _process = process;
            throw;
        }
        finally { if (!ReferenceEquals(_process, process)) process.Dispose(); }
        await RecoverStaleAsync(cancellationToken);
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
                await _process.StandardInput.WriteLineAsync(DetailedDiagnostics?"heartbeat details=1":"heartbeat details=0");
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
            using var document=JsonDocument.Parse(args.Data);
            if(document.RootElement.TryGetProperty("type",out var type)&&type.GetString()=="dns.diagnostic")
            { DiagnosticReceived?.Invoke(args.Data);return; }
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
