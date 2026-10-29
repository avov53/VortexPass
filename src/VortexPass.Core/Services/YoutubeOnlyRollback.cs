namespace VortexPass.Core.Services;
public static class YoutubeOnlyRollback
{
    // A failed process restore must not prevent restoring the saved settings, and vice versa.
    public static async Task RestoreAsync(Func<CancellationToken,Task> restoreWire,Func<CancellationToken,Task> restoreSettings,Exception failure,CancellationToken token)
    {
        var errors=new List<Exception>();
        try{await restoreWire(token);}catch(Exception ex){errors.Add(ex);}
        try{await restoreSettings(token);}catch(Exception ex){errors.Add(ex);}
        if(errors.Count>0)throw new AggregateException("Ошибка отката YouTube; сохранена резервная копия исходной конфигурации",new[]{failure}.Concat(errors));
    }
}
