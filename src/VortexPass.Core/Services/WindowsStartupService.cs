using System.Diagnostics;
using Microsoft.Win32;

namespace VortexPass.Core.Services;

public sealed class WindowsStartupService
{
    private const string TaskName = "VortexPass";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VortexPass";

    public bool IsEnabled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
            if (process?.ExitCode == 0) return true;
        }
        catch
        {
            // fallback
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        // 1. Always clean up legacy HKCU Run registry value if present
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // ignore
        }

        // 2. Manage elevated Task Scheduler task
        if (enabled)
        {
            // /SC ONLOGON /RL HIGHEST /F ensures it runs with Administrator privileges on login without UAC suppression
            var taskRun = $"\\\"{executablePath}\\\" --startup";
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments = $"/Create /TN \"{TaskName}\" /TR \"{taskRun}\" /SC ONLOGON /RL HIGHEST /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить schtasks.exe");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                throw new InvalidOperationException($"schtasks не удалось создать задачу автозапуска: {stderr}");
            }
        }
        else
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments = $"/Delete /TN \"{TaskName}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit();
        }
    }
}
