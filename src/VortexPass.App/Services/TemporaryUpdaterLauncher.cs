using System.Diagnostics;
using System.IO;
using VortexPass.Core.Services;

namespace VortexPass.App.Services;

public sealed class TemporaryUpdaterLauncher
{
    private readonly string _applicationDirectory;

    public TemporaryUpdaterLauncher(string applicationDirectory) => _applicationDirectory = applicationDirectory;

    public void Launch(DownloadedRelease release)
    {
        var updaterDirectory = Path.Combine(_applicationDirectory, "updater");
        var updaterFiles = Directory.Exists(updaterDirectory)
            ? Directory.GetFiles(updaterDirectory, "VortexPass.Updater.*")
            : [];
        if (!updaterFiles.Any())
            throw new FileNotFoundException("В комплекте приложения нет VortexPass.Updater. Выполните установку полной версии.");

        var directory = Path.Combine(Path.GetTempPath(), "VortexPass-updater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var file in updaterFiles) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);

        var executable = Path.Combine(directory, "VortexPass.Updater.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Не найден VortexPass.Updater.exe.", executable);
        var restartPath = Path.Combine(_applicationDirectory, "VortexPass.exe");
        var arguments = $"--pid {Environment.ProcessId} --zip \"{release.ZipPath}\" --target \"{_applicationDirectory}\" --restart \"{restartPath}\"";
        Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false, WorkingDirectory = directory });
    }
}
