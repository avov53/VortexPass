using System.Windows;
using System.Windows.Threading;
using System.IO;

namespace VortexPass.App;

public partial class App : System.Windows.Application
{
    private System.Threading.Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        var user=System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value??Environment.UserName;
        _instance=new System.Threading.Mutex(true,"Local\\VortexPass-"+user,out var created);
        if(!created){Shutdown();return;}
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try{_instance?.ReleaseMutex();}catch(ApplicationException){}
        _instance?.Dispose();base.OnExit(e);
    }
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            WriteCrashLog("Unobserved task", eventArgs.Exception);
            eventArgs.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        WriteCrashLog("UI dispatcher", eventArgs.Exception);
        eventArgs.Handled = true;
        System.Windows.MessageBox.Show("VortexPass обработал внутреннюю ошибку. Окно остаётся открытым; подробности записаны в crash.log.",
            "VortexPass", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void WriteCrashLog(string source, Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VortexPass");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"),
                $"{DateTimeOffset.Now:O} [{source}] {exception}{Environment.NewLine}");
        }
        catch { }
    }
}
