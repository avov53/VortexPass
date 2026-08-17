using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VortexPass.Core.Services;

internal sealed record WinDivertCleanupResult(bool Owned, bool Stopped, bool Deleted, string Detail);

internal sealed class WinDivertDriverService
{
    private const string ServiceName = "WinDivert";
    private readonly string _expectedDriverPath;

    public WinDivertDriverService(string baseDirectory)
    {
        _expectedDriverPath = Path.GetFullPath(Path.Combine(baseDirectory, "bin", "WinDivert64.sys"));
    }

    public async Task<WinDivertCleanupResult> StopAndDeleteOwnedAsync(CancellationToken cancellationToken = default)
    {
        var registeredPath = ReadRegisteredDriverPath();
        if (registeredPath is null)
            return new(false, true, true, "служба WinDivert уже не зарегистрирована");

        if (!PathsEqual(registeredPath, _expectedDriverPath))
            return new(false, false, false, $"служба WinDivert принадлежит другому приложению ({registeredPath}); она не изменена");

        var query = await RunScAsync("query", cancellationToken);
        if (!IsStopped(query.Output))
        {
            var stop = await RunScAsync("stop", cancellationToken);
            if (stop.ExitCode != 0 && !IsStopped(stop.Output))
                return new(true, false, false, $"не удалось остановить драйвер: {ShortOutput(stop.Output)}");

            var stopped = false;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await Task.Delay(100, cancellationToken);
                query = await RunScAsync("query", cancellationToken);
                if (query.ExitCode != 0 || IsStopped(query.Output))
                {
                    stopped = true;
                    break;
                }
            }

            if (!stopped)
                return new(true, false, false, "драйвер не перешёл в состояние STOPPED за 5 секунд");
        }

        var delete = await RunScAsync("delete", cancellationToken);
        var deleted = delete.ExitCode == 0 || ReadRegisteredDriverPath() is null;
        return deleted
            ? new(true, true, true, "драйвер остановлен и служба удалена")
            : new(true, true, false, $"драйвер остановлен, но службу удалить не удалось: {ShortOutput(delete.Output)}");
    }

    private string? ReadRegisteredDriverPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        var raw = key?.GetValue("ImagePath") as string;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        if (value.StartsWith(@"\??\", StringComparison.Ordinal)) value = value[4..];
        if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), value[12..]);
        try { return Path.GetFullPath(value); }
        catch { return value; }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsStopped(string output) =>
        Regex.IsMatch(output, @"STATE\s*:\s*1\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string ShortOutput(string output)
    {
        var value = string.Join(" ", output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(value) ? "sc.exe не вернул описание" : value;
    }

    private static async Task<(int ExitCode, string Output)> RunScAsync(string action, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("sc.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(action);
        process.StartInfo.ArgumentList.Add(ServiceName);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, $"{await stdout} {await stderr}".Trim());
    }
}
