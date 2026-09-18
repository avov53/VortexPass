using System.Diagnostics;
using Microsoft.Win32;

namespace VortexPass.Core.Services;

public sealed class WindowsStartupService
{
    private const string TaskName = "VortexPass";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VortexPass";
    private readonly Func<IReadOnlyList<string>, (int ExitCode, string Error)> _runTaskScheduler;
    private readonly Func<bool> _legacyRunValueExists;
    private readonly Action _deleteLegacyRunValue;

    public WindowsStartupService(
        Func<IReadOnlyList<string>, (int ExitCode, string Error)>? taskScheduler = null,
        Func<bool>? legacyRunValueExists = null,
        Action? deleteLegacyRunValue = null)
    {
        _runTaskScheduler = taskScheduler ?? RunTaskScheduler;
        _legacyRunValueExists = legacyRunValueExists ?? LegacyRunValueExists;
        _deleteLegacyRunValue = deleteLegacyRunValue ?? DeleteLegacyRunValue;
    }

    public bool IsEnabled()
    {
        try
        {
            if (_runTaskScheduler(["/Query", "/TN", TaskName]).ExitCode == 0) return true;
        }
        catch
        {
            // fallback
        }

        try
        {
            return _legacyRunValueExists();
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
            _deleteLegacyRunValue();
        }
        catch
        {
            // ignore
        }

        // 2. Manage elevated Task Scheduler task
        if (enabled)
        {
            // /SC ONLOGON /RL HIGHEST /F ensures it runs with Administrator privileges on login without UAC suppression
            var taskRun = $"\"{Path.GetFullPath(executablePath)}\" --startup";
            var result = _runTaskScheduler(["/Create", "/TN", TaskName, "/TR", taskRun, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"]);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"schtasks не удалось создать задачу автозапуска: {result.Error}");
            }
        }
        else
        {
            _runTaskScheduler(["/Delete", "/TN", TaskName, "/F"]);
        }
    }

    private static (int ExitCode, string Error) RunTaskScheduler(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить schtasks.exe");
        process.WaitForExit();
        return (process.ExitCode, process.StandardError.ReadToEnd().Trim());
    }

    private static bool LegacyRunValueExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    private static void DeleteLegacyRunValue()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
