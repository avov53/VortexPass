namespace VortexPass.Core.Services;

public static class YoutubeApplyTransaction
{
    public static bool RouteConfirmed(ServiceHealthResult result) => YoutubeOnlyPolicy.Group(result,"page") && YoutubeOnlyPolicy.Group(result,"images") &&
        result.Groups?.Any(g=>g.Id.StartsWith("tls-",StringComparison.Ordinal)&&g.State=="confirmed")==true;
    public static async Task<ServiceHealthResult> RunAsync(Func<CancellationToken,Task> apply,
        Func<CancellationToken,Task<ServiceHealthResult>> probe,Func<ServiceHealthResult,CancellationToken,Task> save,CancellationToken token)
    {
        await apply(token);token.ThrowIfCancellationRequested();
        using var check=CancellationTokenSource.CreateLinkedTokenSource(token);check.CancelAfter(TimeSpan.FromSeconds(8));
        var evidence=await probe(check.Token);token.ThrowIfCancellationRequested();
        if(!RouteConfirmed(evidence))throw new IOException("Свежая проверка TLS, страницы и картинки YouTube не прошла. Исходный блок восстанавливается.");
        await save(evidence,token);return evidence;
    }
}
