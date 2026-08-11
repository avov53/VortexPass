using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public sealed class ExternalComponentService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _baseDirectory;
    private readonly Dictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServiceStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private ExternalComponentManifest _manifest = new();

    public ExternalComponentService(string baseDirectory) => _baseDirectory = baseDirectory;

    public event EventHandler<ServiceStatus>? StatusChanged;
    public event EventHandler<string>? DiagnosticReceived;

    public async Task LoadManifestAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_baseDirectory, "assets", "external-components.json");
        if (!File.Exists(path)) return;
        await using var stream = File.OpenRead(path);
        _manifest = await JsonSerializer.DeserializeAsync<ExternalComponentManifest>(stream, JsonOptions, cancellationToken) ?? new();
        foreach (var component in _manifest.Components)
            _statuses.TryAdd(component.Id, new ServiceStatus(component.Id, component.DisplayName, ServiceState.Stopped));
    }

    public IReadOnlyList<ServiceStatus> GetStatuses() => _manifest.Components.Select(component =>
    {
        var running = _processes.TryGetValue(component.Id, out var process) && !process.HasExited;
        if (running) return new ServiceStatus(component.Id, component.DisplayName, ServiceState.Running);
        return _statuses.GetValueOrDefault(component.Id) ?? new ServiceStatus(component.Id, component.DisplayName, ServiceState.Stopped);
    }).ToList();

    public void SetArguments(string id, string arguments)
    {
        var component = _manifest.Components.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (component is null) throw new InvalidOperationException($"Компонент '{id}' не найден в манифесте.");
        component.Arguments = arguments;
    }

    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        if (_manifest.Components.Count == 0)
            throw new InvalidOperationException("Манифест компонентов пуст или имеет неверный формат.");
        foreach (var component in _manifest.Components)
            await StartAsync(component.Id, cancellationToken);
    }

    public async Task StartAsync(string id, CancellationToken cancellationToken = default)
    {
        var component = _manifest.Components.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (component is null) return;
        if (_processes.TryGetValue(id, out var current) && !current.HasExited) return;
        PublishDiagnostic($"[{component.DisplayName}] Проверка комплекта компонентов.");
        PublishStatus(new ServiceStatus(id, component.DisplayName, ServiceState.Starting));

        foreach (var required in component.RequiredFiles)
        {
            var requiredPath = Path.Combine(_baseDirectory, required.Path);
            if (!File.Exists(requiredPath))
            {
                PublishError(component, $"Не найден файл: {required.Path}");
                return;
            }
            if (string.IsNullOrWhiteSpace(required.Sha256))
            {
                PublishError(component, $"Для файла не указан утверждённый SHA-256: {required.Path}");
                return;
            }
            if (!await HashVerifier.MatchesAsync(requiredPath, required.Sha256, cancellationToken))
            {
                PublishError(component, $"Не пройдена SHA-256 проверка: {required.Path}");
                return;
            }
            PublishDiagnostic($"[{component.DisplayName}] SHA-256 OK: {required.Path}");
        }

        var executable = Path.Combine(_baseDirectory, component.Executable);
        if (!File.Exists(executable))
        {
            PublishError(component, $"Не найден исполняемый файл: {component.Executable}");
            return;
        }
        if (string.IsNullOrWhiteSpace(component.Arguments))
        {
            PublishError(component, "Не настроены аргументы запуска компонента.");
            return;
        }

        try
        {
            var diagnostics = new ConcurrentQueue<string>();
            var process = new Process
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
            if (!process.Start()) throw new InvalidOperationException("Windows не запустила процесс.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await Task.Delay(350, cancellationToken);
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                var output = diagnostics.IsEmpty ? string.Empty : $" Вывод: {string.Join(" | ", diagnostics)}";
                process.Dispose();
                throw new InvalidOperationException($"Компонент завершился сразу после запуска (код {exitCode}).{output}");
            }
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                _processes.Remove(component.Id);
                var exitCode = process.ExitCode;
                var output = diagnostics.IsEmpty ? string.Empty : $" Вывод: {string.Join(" | ", diagnostics)}";
                PublishStatus(new ServiceStatus(component.Id, component.DisplayName, ServiceState.Stopped, $"Процесс завершился (код {exitCode}).{output}"));
            };
            _processes[component.Id] = process;
            PublishStatus(new ServiceStatus(component.Id, component.DisplayName, ServiceState.Running));
            PublishDiagnostic($"[{component.DisplayName}] Процесс запущен, PID {process.Id}.");
        }
        catch (Exception ex)
        {
            PublishError(component, ex.Message);
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var (id, process) in _processes.ToArray())
        {
            var component = _manifest.Components.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (component is null || process.HasExited) continue;
            PublishStatus(new ServiceStatus(id, component.DisplayName, ServiceState.Stopping));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            _processes.Remove(id);
            PublishStatus(new ServiceStatus(id, component.DisplayName, ServiceState.Stopped));
        }
    }

    private void PublishError(ExternalComponentDefinition component, string detail) =>
        PublishStatus(new ServiceStatus(component.Id, component.DisplayName, ServiceState.Error, detail));

    private void PublishStatus(ServiceStatus status)
    {
        _statuses[status.Id] = status;
        StatusChanged?.Invoke(this, status);
    }

    private void PublishDiagnostic(string message) => DiagnosticReceived?.Invoke(this, message);

    public void Dispose()
    {
        foreach (var process in _processes.Values)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
        }
        _processes.Clear();
    }
}
