using System.Diagnostics;
using System.IO.Compression;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

namespace VortexPass.Updater;

internal static class Program
{
    private sealed record Arguments(
        int ProcessId,
        string ZipPath,
        string TargetPath,
        string RestartPath,
        string? Sha256,
        string? StatePath,
        string? FromVersion,
        string? ToVersion);

    private static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VortexPass");
    private static readonly string LogPath = Path.Combine(AppData, "updater.log");

    public static async Task<int> Main(string[] args)
    {
        using var window=new UpdateWindow(args.Contains("--light"),!args.Contains("--standard"));
        if(args.Length>=2&&args[0]=="--render-preview")
        {window.Stage("Проверяем комплект обновления…\nНастройки и подключения остаются без изменений.");await window.SavePreview(Path.GetFullPath(args[1]));return 0;}
        Arguments? input = null;
        try
        {
            Directory.CreateDirectory(AppData);
            Log("Updater started.");
            input = ParseArguments(args);

            window.Stage("Проверяем целостность скачанного обновления…");
            if (!string.IsNullOrWhiteSpace(input.Sha256) && !await HashVerifier.MatchesAsync(input.ZipPath, input.Sha256))
                throw new InvalidDataException("SHA-256 скачанного ZIP не совпадает с ожидаемым значением.");

            window.Stage("Ожидаем завершения VortexPass…");
            await WaitForProcessExitAsync(input.ProcessId, TimeSpan.FromSeconds(45));
            EnsureTargetIsWritable(input.TargetPath);

            var staging = Path.Combine(Path.GetTempPath(), "VortexPass-install", Guid.NewGuid().ToString("N"));
            try
            {
                window.Stage("Распаковываем и проверяем комплект…");
                ExtractSafely(input.ZipPath, staging);
                if (!File.Exists(Path.Combine(staging, "VortexPass.exe")))
                    throw new InvalidDataException("В ZIP нет VortexPass.exe в корневой папке.");

                PreflightInstall(staging, input.TargetPath);
                window.Stage("Устанавливаем файлы. Не выключайте компьютер…");
                InstallWithRollback(staging, input.TargetPath);
            }
            finally
            {
                TryDeleteDirectory(staging);
            }

            await MarkCompletedAsync(input);
            window.Stage("Обновление установлено. Открываем приложение…");
            StartUpdatedApp(input);
            Log("Updater completed.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex}");
            if (input is not null) await MarkFailedAsync(input, ex.Message);
            await window.Failure($"{ex.Message}\n\nПодробности сохранены в updater.log.");
            return 1;
        }
    }

    private static Arguments ParseArguments(string[] args)
    {
        string? Get(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        if (!int.TryParse(Get("--pid"), out var pid)) throw new ArgumentException("Не указан PID приложения.");
        var zip = Get("--zip");
        var target = Get("--target");
        var restart = Get("--restart");
        if (string.IsNullOrWhiteSpace(zip) || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(restart))
            throw new ArgumentException("Не переданы обязательные параметры обновления.");
        if (!File.Exists(zip)) throw new FileNotFoundException("Не найден скачанный ZIP-архив.", zip);
        return new Arguments(
            pid,
            Path.GetFullPath(zip),
            Path.GetFullPath(target),
            Path.GetFullPath(restart),
            Get("--sha256"),
            Get("--state"),
            Get("--from"),
            Get("--to"));
    }

    private static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Log($"Waiting for VortexPass PID {processId}.");
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                Log($"PID {processId} exited with code {process.ExitCode}.");
            }
            catch (OperationCanceledException)
            {
                throw new IOException($"VortexPass.exe не завершился за {timeout.TotalSeconds:0} секунд. Обновление не начато.");
            }
        }
        catch (ArgumentException)
        {
            Log($"PID {processId} is not running.");
        }
    }

    private static void EnsureTargetIsWritable(string target)
    {
        Directory.CreateDirectory(target);
        var probe = Path.Combine(target, $".vortexpass-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Open(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException($"Нет прав записи в папку установки: {target}. {ex.Message}", ex);
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
        }
    }

    private static void ExtractSafely(string zipPath, string staging)
    {
        Directory.CreateDirectory(staging);
        using var archive = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP содержит недопустимый путь.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void PreflightInstall(string staging, string target)
    {
        foreach (var source in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, source);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) continue;

            Retry($"проверка доступа к {destination}", () =>
            {
                using var stream = File.Open(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
        }
        Log("Preflight write access check completed.");
    }

    private static void InstallWithRollback(string staging,string target)=>
        UpdateFileInstaller.Install(staging,target,Log,(source,destination)=>Retry("замена файла",()=>File.Copy(source,destination,true)));
    private static void Retry(string action, Action work)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                work();
                if (attempt > 1) Log($"{action}: succeeded on attempt {attempt}.");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Log($"{action}: attempt {attempt} failed: {ex.Message}");
                Thread.Sleep(Math.Min(250 * attempt, 1500));
            }
        }
        throw new IOException($"{action}: файл занят или недоступен после повторных попыток. {last?.Message}", last);
    }

    private static async Task MarkFailedAsync(Arguments input, string error)
    {
        if (string.IsNullOrWhiteSpace(input.StatePath)) return;
        try
        {
            var state = await LoadStateFromPathAsync(input.StatePath) ?? new PendingUpdateState();
            state.Status = "failed";
            state.LastError = error;
            await SaveStateToPathAsync(input.StatePath, state);
        }
        catch (Exception ex)
        {
            Log($"Cannot save failed update-state: {ex.Message}");
        }
    }

    private static async Task MarkCompletedAsync(Arguments input)
    {
        if (string.IsNullOrWhiteSpace(input.StatePath)) return;
        try
        {
            var state = await LoadStateFromPathAsync(input.StatePath) ?? new PendingUpdateState();
            state.Status = "completed";
            state.LastError = null;
            await SaveStateToPathAsync(input.StatePath, state);
            File.Delete(input.StatePath);
        }
        catch (Exception ex)
        {
            Log($"Cannot clear update-state: {ex.Message}");
        }
    }

    private static async Task<PendingUpdateState?> LoadStateFromPathAsync(string path)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<PendingUpdateState>(stream);
    }

    private static async Task SaveStateToPathAsync(string path, PendingUpdateState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        await using var stream = File.Create(path);
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static void StartUpdatedApp(Arguments input)
    {
        var start = new ProcessStartInfo(input.RestartPath)
        {
            WorkingDirectory = input.TargetPath,
            UseShellExecute = true
        };
        if (!string.IsNullOrWhiteSpace(input.FromVersion) && !string.IsNullOrWhiteSpace(input.ToVersion))
        {
            start.Arguments = $"--updated-from {input.FromVersion} --updated-to {input.ToVersion}";
        }
        Process.Start(start);
    }

    private static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) { Log($"Cannot delete temp directory {directory}: {ex.Message}"); }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppData);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}


