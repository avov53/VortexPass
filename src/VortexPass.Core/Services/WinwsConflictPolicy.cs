using System.Diagnostics;

namespace VortexPass.Core.Services;

public static class WinwsConflictPolicy
{
    public static async Task<string> ReadTcpTimestampsAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "interface tcp show global")
                { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true });
            if (process is null) return "не удалось прочитать";
            var output=process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error=process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text=await output;await error;
            return text.Split('\n').FirstOrDefault(line=>line.Contains("timestamps",StringComparison.OrdinalIgnoreCase))?.Trim()
                ?? "состояние не распознано; параметры Windows не изменены";
        }
        catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        { token.ThrowIfCancellationRequested();return "чтение не завершилось; параметры Windows не изменены"; }
    }
    public static int[] ForeignPids(IEnumerable<int> running, IEnumerable<int> owned) =>
        running.Except(owned).Order().ToArray();

    public static void EnsureExclusive(IEnumerable<int> owned)
    {
        var processes = Process.GetProcessesByName("winws").Concat(Process.GetProcessesByName("winws2")).ToArray();
        try
        {
            var foreign = ForeignPids(processes.Select(p => p.Id), owned);
            if (foreign.Length > 0)
                throw new InvalidOperationException("Обнаружен другой zapret/winws (PID " + string.Join(", ", foreign) +
                    "). Отключите его через service.bat → Remove Services или закройте его окно, затем повторите запуск. Чужие процессы не изменены.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
