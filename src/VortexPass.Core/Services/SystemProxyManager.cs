using Microsoft.Win32;

namespace VortexPass.Core.Services;

/// <summary>Owns only the AutoConfigURL value it writes, never the user's other proxy settings.</summary>
public sealed class SystemProxyManager
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private readonly string _backupPath;
    private readonly string _ownerPath;
    private string? _ownedPacUrl;

    public SystemProxyManager(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _backupPath = Path.Combine(appDataDirectory, "proxy-backup.txt");
        _ownerPath = Path.Combine(appDataDirectory, "proxy-owner.txt");
        if (File.Exists(_ownerPath)) _ownedPacUrl = File.ReadAllText(_ownerPath);
    }

    public void ApplyPac(Uri pacUrl)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, writable: true);
        var existing = key.GetValue("AutoConfigURL") as string;
        if (!string.Equals(existing, _ownedPacUrl, StringComparison.OrdinalIgnoreCase))
            File.WriteAllText(_backupPath, existing ?? string.Empty);
        _ownedPacUrl = pacUrl.AbsoluteUri;
        File.WriteAllText(_ownerPath, _ownedPacUrl);
        key.SetValue("AutoConfigURL", _ownedPacUrl, RegistryValueKind.String);
        NotifyWindows();
    }

    public void RestoreIfOwned()
    {
        if (_ownedPacUrl is null || !File.Exists(_backupPath)) return;
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, writable: true);
        var current = key.GetValue("AutoConfigURL") as string;
        if (!string.Equals(current, _ownedPacUrl, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(_backupPath);
            File.Delete(_ownerPath);
            _ownedPacUrl = null;
            return;
        }
        var previous = File.ReadAllText(_backupPath);
        if (string.IsNullOrWhiteSpace(previous)) key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        else key.SetValue("AutoConfigURL", previous, RegistryValueKind.String);
        File.Delete(_backupPath);
        File.Delete(_ownerPath);
        _ownedPacUrl = null;
        NotifyWindows();
    }

    private static void NotifyWindows()
    {
        NativeMethods.InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        NativeMethods.InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
        internal static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);
    }
}
