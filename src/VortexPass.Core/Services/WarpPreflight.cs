using System.Diagnostics;

namespace VortexPass.Core.Services;

public sealed record WarpTransportVariant(string Protocol,string Masque,string Bootstrap);
public sealed record WarpVerifiedVariant(string NetworkDnsClientKey,WarpTransportVariant Variant,DateTimeOffset CheckedAt,string ObservedTransport="unknown")
{
    public string ProofKey=>NetworkDnsClientKey+"|"+Variant.Protocol+"|"+Variant.Masque+"|"+Variant.Bootstrap+"|observed:"+ObservedTransport;
}
public static class WarpPreflight
{
    public static string CacheKey(VortexPass.Core.Models.AppSettings settings,string? version)=>WarpService.GetNetworkFingerprint()+"|dns:"+settings.DnsMode+":"+settings.SelectedDnsProvider+":"+settings.GoogleDnsAddress+"|port:"+settings.Warp.ProxyPort+"|client:"+version+"|selection:"+WarpTransportPolicy.Selection(settings.Warp)+"|checks:"+RouteProbePolicy.Version;
    public static IReadOnlyList<WarpTransportVariant> Candidates(VortexPass.Core.Models.AppSettings settings,string? version)
    {
        var key=CacheKey(settings,version);
        // A v14 transport hint can be retried; this never validates a v14 recommendation.
        var previousKey=RouteProbePolicy.Version==15?key.Replace("|checks:15","|checks:14"):key;
        var last=settings.Warp.VerifiedVariants.Where(v=>v.NetworkDnsClientKey==key||v.NetworkDnsClientKey==previousKey).OrderByDescending(v=>v.CheckedAt).FirstOrDefault()?.Variant;
        return Candidates(settings.Warp,last);
    }
    public static IReadOnlyList<WarpTransportVariant> Candidates(VortexPass.Core.Models.WarpSettings settings,WarpTransportVariant? last=null)
    {
        if(!WarpTransportPolicy.IsAutomatic(settings))return [WarpTransportPolicy.Resolve(settings)];
        return new[]{last,new WarpTransportVariant("masque","h2-only","none"),new("masque","h3-with-h2-fallback","strong"),new("masque","h2-only","split")}
            .Where(v=>v is not null&&v.Protocol=="masque"&&v.Masque is "h2-only" or "h3-with-h2-fallback"&&v.Bootstrap is "none" or "default" or "split" or "strong")
            .Select(v=>v!).Distinct().Take(3).ToArray();
    }
    public static bool RequiresBootstrap(string mode)=>mode switch
    {"none"=>false,"default" or "split" or "disorder" or "strong"=>true,_=>throw new ArgumentException("Неизвестный вариант bootstrap WARP")};
    public static async Task<WarpTransportVariant> RunAsync(IReadOnlyList<WarpTransportVariant> variants,
        Func<WarpTransportVariant,int,CancellationToken,Task<bool>> attempt,CancellationToken token,
        TimeSpan? budget=null,TimeSpan? round=null)
    {
        using var total=CancellationTokenSource.CreateLinkedTokenSource(token);total.CancelAfter(budget??TimeSpan.FromSeconds(60));
        string reason="Маршрут через локальный прокси не подтверждён";
        for(var i=0;i<variants.Count;i++)
        {
            token.ThrowIfCancellationRequested();
            if(total.IsCancellationRequested)break;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(total.Token);deadline.CancelAfter(round??TimeSpan.FromSeconds(18));
            try{if(await attempt(variants[i],i+1,deadline.Token))return variants[i];}
            catch(OperationCanceledException)when(!token.IsCancellationRequested){reason="Истекло время проверки варианта WARP";}
            catch(Exception ex)when(ex is IOException or InvalidOperationException or System.Net.Http.HttpRequestException)
            {reason=WarpService.DescribePreparationError(ex);}
        }
        token.ThrowIfCancellationRequested();
        throw new IOException("Маршрут WARP не подтверждён: "+reason+". Подбор остановлен до запуска модели; рекомендация не создана.");
    }
}
