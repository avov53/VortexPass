using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public sealed class ExternalComponentService : IDisposable
{
    private sealed record VerifiedFile(long Length, DateTime LastWriteUtc, string Sha256);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<string, VerifiedFile> VerificationCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _baseDirectory;
    private readonly WinDivertDriverService _winDivertDriver;
    private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComponentLifecycle _lifecycle = new();
    private readonly SemaphoreSlim _operations = new(1,1);
    private ExternalComponentManifest _manifest = new();

    public ExternalComponentService(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        _winDivertDriver = new WinDivertDriverService(baseDirectory);
    }

    public event EventHandler<ServiceStatus>? StatusChanged;
    public event EventHandler<string>? DiagnosticReceived;
    public Func<IEnumerable<int>> AdditionalOwnedProcessIds { get; set; } = () => [];
    public void EnsureNoForeignProcesses() => WinwsConflictPolicy.EnsureExclusive(
        _processes.Values.Where(IsRunning).Select(p => p.Id).Concat(AdditionalOwnedProcessIds()));
    private static bool IsRunning(Process process) { try { return !process.HasExited; } catch (InvalidOperationException) { return false; } }
    public bool IsCurrent(ServiceStatus status) => _lifecycle.IsCurrent(status);

    public async Task LoadManifestAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_baseDirectory, "assets", "external-components.json");
        if (!File.Exists(path)) return;
        await using var stream = File.OpenRead(path);
        _manifest = await JsonSerializer.DeserializeAsync<ExternalComponentManifest>(stream, JsonOptions, cancellationToken) ?? new();
        foreach (var component in _manifest.Components)
            _lifecycle.Set(component.Id, component.DisplayName, 0, ServiceState.Stopped);
    }

    public IReadOnlyList<ServiceStatus> GetStatuses() => _manifest.Components.Select(component =>
    {
        var status=_lifecycle.Get(component.Id) ?? new ServiceStatus(component.Id, component.DisplayName, ServiceState.Stopped);
        if(status.State==ServiceState.Running&&(!_processes.TryGetValue(component.Id,out var process)||!IsRunning(process)))
            return status with{State=ServiceState.Stopped,Detail="Текущий процесс больше не работает"};
        return status;
    }).ToList();

    public void SetArguments(string id, string arguments)
    {
        var component = _manifest.Components.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (component is null) throw new InvalidOperationException($"Компонент '{id}' не найден в манифесте.");
        component.Arguments = arguments;
    }

    public string GetArguments(string id) => _manifest.Components.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Arguments
        ?? throw new ArgumentException("Компонент не найден", nameof(id));

    public async Task StartAllAsync(CancellationToken cancellationToken = default, bool reuseExistingOwnedDriver = false)
    {
        await _operations.WaitAsync(cancellationToken);
        try { await StartAllCoreAsync(cancellationToken,reuseExistingOwnedDriver); }
        finally { _operations.Release(); }
    }
    private async Task StartAllCoreAsync(CancellationToken cancellationToken, bool reuseExistingOwnedDriver)
    {
        EnsureNoForeignProcesses();
        PublishDiagnostic("[winws] TCP timestamps: " + await WinwsConflictPolicy.ReadTcpTimestampsAsync(cancellationToken));
        if (_manifest.Components.Count == 0)
            throw new InvalidOperationException("Манифест компонентов пуст или имеет неверный формат.");
        if (_processes.Values.All(process => !IsRunning(process)) && !reuseExistingOwnedDriver)
        {
            var cleanup = await CleanupWinDivertAsync(cancellationToken, "перед запуском");
            if (!cleanup.Owned && !cleanup.Stopped)
            {
                throw new InvalidOperationException(
                    $"Обнаружен конфликт WinDivert: {cleanup.Detail}. " +
                    "Закройте другой zapret/обход, отключите его автозапуск и повторите включение защиты.");
            }
        }
        foreach (var component in _manifest.Components)
            await StartCoreAsync(component.Id, cancellationToken);
    }

    public async Task StartAsync(string id, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try { await StartCoreAsync(id,cancellationToken); }
        finally { _operations.Release(); }
    }
    private async Task StartCoreAsync(string id, CancellationToken cancellationToken)
    {
        var component = _manifest.Components.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (component is null) return;
        if (_processes.TryGetValue(id, out var current) && IsRunning(current)) return;
        var starting = _lifecycle.Begin(id, component.DisplayName);
        var generation = starting.Generation;
        PublishDiagnostic($"[{component.DisplayName}] Проверка комплекта компонентов.");
        EmitStatus(starting);

        foreach (var required in component.RequiredFiles)
        {
            var requiredPath = Path.Combine(_baseDirectory, required.Path);
            if (!File.Exists(requiredPath))
            {
                PublishError(component, generation, $"Не найден файл: {required.Path}");
                return;
            }
            if (string.IsNullOrWhiteSpace(required.Sha256))
            {
                PublishError(component, generation, $"Для файла не указан утверждённый SHA-256: {required.Path}");
                return;
            }
            var info = new FileInfo(requiredPath);
            var cacheKey = Path.GetFullPath(requiredPath);
            var verified = VerificationCache.TryGetValue(cacheKey, out var cached) &&
                           cached.Length == info.Length &&
                           cached.LastWriteUtc == info.LastWriteTimeUtc &&
                           cached.Sha256.Equals(required.Sha256, StringComparison.OrdinalIgnoreCase);
            if (!verified && !await HashVerifier.MatchesAsync(requiredPath, required.Sha256, cancellationToken))
            {
                PublishError(component, generation, $"Не пройдена SHA-256 проверка: {required.Path}");
                return;
            }
            VerificationCache[cacheKey] = new(info.Length, info.LastWriteTimeUtc, required.Sha256);
            PublishDiagnostic($"[{component.DisplayName}] SHA-256 OK{(verified ? " (кэш)" : string.Empty)}: {required.Path}");
        }

        var executable = Path.Combine(_baseDirectory, component.Executable);
        if (!File.Exists(executable))
        {
            PublishError(component, generation, $"Не найден исполняемый файл: {component.Executable}");
            return;
        }
        if (string.IsNullOrWhiteSpace(component.Arguments))
        {
            PublishError(component, generation, "Не настроены аргументы запуска компонента.");
            return;
        }

        Process? launched=null;
        try
        {
            var diagnostics = new ConcurrentQueue<string>();
            var process = launched=new Process
            {
                StartInfo = new ProcessStartInfo(executable, component.Arguments)
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
                var clean = line.Trim();
                diagnostics.Enqueue(clean);
                while (diagnostics.Count > 6) diagnostics.TryDequeue(out _);
                PublishDiagnostic($"[{component.DisplayName}] winws: {clean}");
            }
            process.OutputDataReceived += (_, args) => Capture(args.Data);
            process.ErrorDataReceived += (_, args) => Capture(args.Data);
            PublishDiagnostic($"[{component.DisplayName}] Запуск: {Path.GetFileName(executable)} {component.Arguments}");
            PublishDiagnostic($"[{component.DisplayName}] Источник: Flowseal {WinwsProfileComposer.FlowsealVersion}, commit {WinwsProfileComposer.FlowsealCommit[..7]}; проверки v{RouteProbePolicy.Version}; command SHA-256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(component.Arguments)))[..16]}; рабочая папка — каталог проверенного winws.");
            if (!process.Start()) throw new InvalidOperationException("Windows не запустила процесс.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await Task.Delay(350, cancellationToken);
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                var output = diagnostics.IsEmpty ? string.Empty : $" Вывод: {string.Join(" | ", diagnostics)}";
                throw new InvalidOperationException($"Компонент завершился сразу после запуска (код {exitCode}).{output}");
            }
            process.Exited += (_, _) =>
            {
                if (!((ICollection<KeyValuePair<string,Process>>)_processes).Remove(new(component.Id, process))) return;
                var exitCode = process.ExitCode;
                var output = diagnostics.IsEmpty ? string.Empty : $" Вывод: {string.Join(" | ", diagnostics)}";
                EmitStatus(_lifecycle.Set(component.Id, component.DisplayName, generation, ServiceState.Stopped, $"Процесс завершился (код {exitCode}).{output}"));
                process.Dispose();
            };
            _processes[component.Id] = process;
            process.EnableRaisingEvents = true;
            if (!IsRunning(process)) throw new IOException("Компонент завершился при подтверждении запуска");
            EmitStatus(_lifecycle.Set(component.Id, component.DisplayName, generation, ServiceState.Running));
            if (!IsRunning(process)) EmitStatus(_lifecycle.Set(component.Id, component.DisplayName, generation, ServiceState.Stopped, "Компонент завершился после запуска"));
            PublishDiagnostic($"[{component.DisplayName}] Процесс запущен, PID {process.Id}.");
        }
        catch (Exception ex)
        {
            if(launched is not null)
            {
                ((ICollection<KeyValuePair<string,Process>>)_processes).Remove(new(component.Id, launched));
                try{launched.EnableRaisingEvents=false;if(IsRunning(launched)){launched.Kill(entireProcessTree:true);await launched.WaitForExitAsync(CancellationToken.None);}}catch(InvalidOperationException){}
                launched.Dispose();
            }
            PublishError(component, generation, ex.Message);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public async Task StopAllAsync(bool cleanupDriver = true)
    {
        await _operations.WaitAsync();
        try { await StopAllCoreAsync(cleanupDriver); }
        finally { _operations.Release(); }
    }
    private async Task StopAllCoreAsync(bool cleanupDriver)
    {
        foreach (var (id, process) in _processes.ToArray())
        {
            var component = _manifest.Components.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (component is null) continue;
            var generation = _lifecycle.Get(id)?.Generation ?? 0;
            ((ICollection<KeyValuePair<string,Process>>)_processes).Remove(new(id,process));
            process.EnableRaisingEvents=false;
            EmitStatus(_lifecycle.Set(id,component.DisplayName,generation,ServiceState.Stopping,planned:true));
            if(IsRunning(process)){process.Kill(entireProcessTree: true);await process.WaitForExitAsync();}
            process.Dispose();
            EmitStatus(_lifecycle.Set(id,component.DisplayName,generation,ServiceState.Stopped,planned:true));
        }
        if (cleanupDriver)
            await CleanupWinDivertAsync(CancellationToken.None, "после остановки winws");
    }

    public async Task RestartOwnedAsync(string id,string arguments,CancellationToken token)
    {
        await _operations.WaitAsync(token);
        try { await RestartOwnedCoreAsync(id,arguments,token); }
        finally { _operations.Release(); }
    }
    private async Task RestartOwnedCoreAsync(string id,string arguments,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();EnsureNoForeignProcesses();
        if(!_manifest.Components.Any(c=>c.Id==id))throw new ArgumentException("Неизвестный компонент");
        if(_processes.TryRemove(id,out var process))
        {
            process.EnableRaisingEvents=false;
            EmitStatus(_lifecycle.Set(id,_manifest.Components.First(c=>c.Id==id).DisplayName,_lifecycle.Get(id)?.Generation??0,ServiceState.Stopping,planned:true));
            if(IsRunning(process)){process.Kill(entireProcessTree:true);await process.WaitForExitAsync(CancellationToken.None);}
            process.Dispose();
        }
        SetArguments(id,arguments);await StartCoreAsync(id,token);
        if(_lifecycle.Get(id)?.State!=ServiceState.Running||!_processes.TryGetValue(id,out var owned)||!IsRunning(owned))throw new IOException("Компонент не принял конфигурацию");
    }

    private async Task<WinDivertCleanupResult> CleanupWinDivertAsync(CancellationToken cancellationToken, string stage)
    {
        try
        {
            var result = await _winDivertDriver.StopAndDeleteOwnedAsync(cancellationToken);
            PublishDiagnostic($"[WinDivert] {stage}: {result.Detail}.");
            return result;
        }
        catch (Exception ex)
        {
            PublishDiagnostic($"[WinDivert] {stage}: очистка не выполнена — {ex.Message}");
            return new(false, false, false, $"не удалось проверить службу WinDivert: {ex.Message}");
        }
    }

    private void PublishError(ExternalComponentDefinition component, long generation, string detail) =>
        EmitStatus(_lifecycle.Set(component.Id, component.DisplayName,generation,ServiceState.Error,detail));

    private void EmitStatus(ServiceStatus? status)
    {
        if(status is null||!_lifecycle.IsCurrent(status))return;
        StatusChanged?.Invoke(this, status);
    }

    private void PublishDiagnostic(string message) => DiagnosticReceived?.Invoke(this, message);

    public void Dispose()
    {
        _operations.Wait();
        try { foreach (var (id,process) in _processes.ToArray())
        {
            _processes.TryRemove(id,out _);process.EnableRaisingEvents=false;
            if (IsRunning(process)) process.Kill(entireProcessTree: true);
            process.Dispose();
        }
        } finally { _operations.Release(); }
    }
}
