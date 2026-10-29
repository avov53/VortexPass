namespace VortexPass.Core.Services;

public static class WarpSettingsTransaction
{
    public static async Task<bool> SaveAndRetryAsync(Func<Task<bool>> update,Func<Task> retry)
    {if(!await update())return false;await retry();return true;}
    public static async Task ExecuteAsync(Func<CancellationToken,Task> apply,Func<CancellationToken,Task> save,
        Func<CancellationToken,Task> restore,CancellationToken token)
    {
        try{await apply(token);await save(token);}
        catch(Exception original)
        {
            using var recovery=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try{await restore(recovery.Token);}
            catch(Exception rollback){throw new AggregateException("Настройки WARP отменены; восстановление требует проверки",original,rollback);}
            throw;
        }
    }
}
