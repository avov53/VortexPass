namespace VortexPass.Core.Services;

public static class RobloxDnsRecovery
{
    public static async Task<byte[]> FollowAliasesAsync(byte[] question,byte[] original,Func<byte[],CancellationToken,Task<byte[]>> resolve,CancellationToken token)
    {
        if(!Needed(question,original))return original;
        var q=question;var reply=original;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase){DnsWirePolicy.QuestionName(q)};var ttl=3600;
        for(var i=0;i<6;i++)
        {
            var alias=DnsWirePolicy.LastAlias(q,reply);if(alias.Target is null||!seen.Add(alias.Target))break;ttl=Math.Min(ttl,alias.Ttl);
            q=DnsDiagnosticsService.Question(alias.Target,1);reply=await resolve(q,token);
            if(DnsWirePolicy.Inspect(q,reply).HasAddress)return DnsWirePolicy.RebindAddressReply(question,q,reply,ttl);
        }
        return original;
    }
    public static bool IsScoped(byte[] question)=>new[]{"roblox.com","rbxcdn.com"}.Any(d=>
        DnsWirePolicy.QuestionName(question).Equals(d,StringComparison.OrdinalIgnoreCase)||DnsWirePolicy.QuestionName(question).EndsWith("."+d,StringComparison.OrdinalIgnoreCase))
        && DnsWirePolicy.QuestionType(question) is 1 or 28;
    public static bool Needed(byte[] question,byte[] answer)
    {
        var wire=DnsWirePolicy.Inspect(question,answer);
        return wire.Type==1&&!wire.HasAddress&&!wire.Truncated&&wire.ResponseCode is 0 or 2 or 3&&
            new[]{"roblox.com","rbxcdn.com"}.Any(d=>wire.Name.Equals(d,StringComparison.OrdinalIgnoreCase)||wire.Name.EndsWith("."+d,StringComparison.OrdinalIgnoreCase));
    }
    public static async Task<byte[]> ResolveAsync(byte[] question,byte[] original,Func<CancellationToken,Task<byte[]>> secure,CancellationToken token)
    {
        if(!Needed(question,original))return original;
        try
        {
            var candidate=await secure(token);
            // Verify transaction, name, record type and the complete CNAME chain.
            return DnsWirePolicy.Inspect(question,candidate).HasAddress?candidate:original;
        }
        catch(Exception ex)when(!token.IsCancellationRequested&&ex is IOException or HttpRequestException or OperationCanceledException)
        {return original;}
    }
}
