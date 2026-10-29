using System.Diagnostics;
using Microsoft.Win32;

namespace VortexPass.Core.Services;

public sealed record WarpGuiStartupEntry(string Hive,string View,string Value,RegistryValueKind Kind=RegistryValueKind.String);
public static class WarpGuiPolicy
{
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool OwnsCommand(string command,string executable)=>
        Environment.ExpandEnvironmentVariables(command.Trim()).Equals(executable,StringComparison.OrdinalIgnoreCase)||
        Environment.ExpandEnvironmentVariables(command.Trim()).Equals('"'+executable+'"',StringComparison.OrdinalIgnoreCase);
    private static RegistryKey OpenHive(WarpGuiStartupEntry entry)=>RegistryKey.OpenBaseKey(entry.Hive=="HKLM"?RegistryHive.LocalMachine:RegistryHive.CurrentUser,entry.View=="64"?RegistryView.Registry64:RegistryView.Registry32);
    public static List<WarpGuiStartupEntry> Read(string executable)
    {
        if(!WarpService.IsTrustedCloudflareExecutable(executable))throw new InvalidDataException("Не подтверждена подпись интерфейса Cloudflare");
        var result=new List<WarpGuiStartupEntry>();
        foreach(var hive in new[]{"HKLM","HKCU"})foreach(var view in new[]{"64","32"})
        {
            using var root=OpenHive(new(hive,view,""));using var key=root.OpenSubKey(RunKey);
            if(key?.GetValue("CloudflareWARP",null,RegistryValueOptions.DoNotExpandEnvironmentNames) is string value&&OwnsCommand(value,executable))result.Add(new(hive,view,value,key.GetValueKind("CloudflareWARP")));
        }
        return result;
    }
    public static void Disable(IEnumerable<WarpGuiStartupEntry> entries)
    {
        foreach(var entry in entries)
        {using var root=OpenHive(entry);using var key=root.OpenSubKey(RunKey,true);if(key?.GetValue("CloudflareWARP",null,RegistryValueOptions.DoNotExpandEnvironmentNames) is string value&&value==entry.Value)key.DeleteValue("CloudflareWARP",false);}
    }
    public static void Restore(IEnumerable<WarpGuiStartupEntry> entries,string executable)
    {
        if(!WarpService.IsTrustedCloudflareExecutable(executable))throw new InvalidDataException("Не подтверждена подпись интерфейса Cloudflare");
        foreach(var entry in entries)
        {if(entry.Hive is not ("HKLM" or "HKCU")||entry.View is not ("64" or "32")||entry.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)||!OwnsCommand(entry.Value,executable))continue;using var root=OpenHive(entry);using var key=root.CreateSubKey(RunKey);if(key.GetValue("CloudflareWARP") is null)key.SetValue("CloudflareWARP",entry.Value,entry.Kind);}
    }
    public static void CloseWindows(string executable)
    {
        foreach(var process in Process.GetProcessesByName("Cloudflare WARP"))using(process)
        {
            try{if(process.MainModule?.FileName.Equals(executable,StringComparison.OrdinalIgnoreCase)==true&&process.MainWindowHandle!=IntPtr.Zero)process.CloseMainWindow();}
            catch(System.ComponentModel.Win32Exception){}
            catch(InvalidOperationException){}
        }
    }
}
