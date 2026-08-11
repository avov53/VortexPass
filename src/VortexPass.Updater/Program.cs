using System.Diagnostics;
using System.IO.Compression;

namespace VortexPass.Updater;

internal static class Program
{
    private sealed record Arguments(int ProcessId, string ZipPath, string TargetPath, string RestartPath);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var input = ParseArguments(args);
            await WaitForProcessExitAsync(input.ProcessId);
            EnsureTargetIsWritable(input.TargetPath);
            var staging = Path.Combine(Path.GetTempPath(), "VortexPass-install", Guid.NewGuid().ToString("N"));
            try
            {
                ExtractSafely(input.ZipPath, staging);
                if (!File.Exists(Path.Combine(staging, "VortexPass.exe")))
                    throw new InvalidDataException("В ZIP нет VortexPass.exe в корневой папке.");
                InstallWithRollback(staging, input.TargetPath);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            Process.Start(new ProcessStartInfo(input.RestartPath) { WorkingDirectory = input.TargetPath, UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show($"VortexPass не удалось обновить:\n{ex.Message}", "VortexPass", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
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
        return new Arguments(pid, Path.GetFullPath(zip), Path.GetFullPath(target), Path.GetFullPath(restart));
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync();
        }
        catch (ArgumentException) { }
    }

    private static void EnsureTargetIsWritable(string target)
    {
        Directory.CreateDirectory(target);
        var probe = Path.Combine(target, $".vortexpass-write-{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(probe, "probe"); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
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

    private static void InstallWithRollback(string staging, string target)
    {
        var backup = Path.Combine(Path.GetTempPath(), "VortexPass-backup", Guid.NewGuid().ToString("N"));
        var installed = new List<(string Destination, bool Existed)>();
        try
        {
            foreach (var source in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(staging, source);
                var destination = Path.Combine(target, relative);
                var existed = File.Exists(destination);
                if (existed)
                {
                    var backupFile = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(destination, backupFile, overwrite: true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                installed.Add((destination, existed));
            }
        }
        catch
        {
            foreach (var (destination, existed) in installed.AsEnumerable().Reverse())
            {
                var relative = Path.GetRelativePath(target, destination);
                var backupFile = Path.Combine(backup, relative);
                if (existed && File.Exists(backupFile)) File.Copy(backupFile, destination, overwrite: true);
                else if (!existed && File.Exists(destination)) File.Delete(destination);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
    }
}
