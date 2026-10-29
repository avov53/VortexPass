using VortexPass.Core.Models;
namespace VortexPass.Core.Services;
public static class StartupConnectionPolicy
{
    public static void Select(AppSettings settings,string id)
    {
        if(!settings.StartWithWindows)throw new InvalidOperationException("Сначала включите запуск VortexPass с Windows");
        if(!settings.SavedConnections.Any(p=>p.Id==id))throw new InvalidOperationException("Профиль автоподключения не найден");
        settings.StartupConnectionId=id;settings.StartProtectionOnStartup=true;settings.StartupProfileChoiceRequired=false;
    }
    public static string Reason(AppSettings settings,bool startup)=>!startup?"обычный запуск, без --startup":!settings.StartWithWindows?"запуск с Windows отключён":!settings.StartProtectionOnStartup?"автоподключение выключено":!settings.SavedConnections.Any(p=>p.Id==settings.StartupConnectionId)?"профиль не найден":"подключаем выбранный профиль";
    public static async Task WaitReadyAsync(Func<bool> ready,TimeSpan timeout,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(timeout);
        try{while(!ready()){deadline.Token.ThrowIfCancellationRequested();await Task.Delay(200,deadline.Token);}}
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new TimeoutException("Приложение ещё занято подготовкой соединения");}
        token.ThrowIfCancellationRequested();
    }
}
