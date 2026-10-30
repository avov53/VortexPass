using VortexPass.Core.Services;
internal static class RobloxDnsRecoveryTests
{
    public static async Task Run()
    {
        var start=AdvancedRouteTests.Question("tr.rbxcdn.com");
        var cnameBytes=new byte[]{4,(byte)'e',(byte)'d',(byte)'g',(byte)'e',7,(byte)'e',(byte)'x',(byte)'a',(byte)'m',(byte)'p',(byte)'l',(byte)'e',0};
        var cnameOnly=AdvancedRouteTests.Answer(start,5,cnameBytes);
        var repaired=await RobloxDnsRecovery.FollowAliasesAsync(start,cnameOnly,(q,_)=>Task.FromResult(AdvancedRouteTests.Answer(q)),default);
        if(!DnsWirePolicy.Inspect(start,repaired).HasAddress||DnsWirePolicy.QuestionName(repaired)!="tr.rbxcdn.com")throw new Exception("Incomplete CDN alias chain was not completed for the original name");
        var missing=await RobloxDnsRecovery.FollowAliasesAsync(start,cnameOnly,(q,_)=>{var n=q.ToArray();n[2]=0x81;n[3]=0x80;return Task.FromResult(n);},default);
        if(DnsWirePolicy.Inspect(start,missing).HasAddress)throw new Exception("Missing terminal address was invented");
        foreach(var path in Directory.EnumerateFiles("tests/VortexPass.Tests/Fixtures/RobloxDns","*-query.bin"))
        {
            var q=File.ReadAllBytes(path);var r=File.ReadAllBytes(path.Replace("-query.bin","-response.bin"));
            var compact=DnsWirePolicy.CompactAddressReply(q,r);
            if(DnsWirePolicy.Inspect(q,r).HasAddress!=DnsWirePolicy.Inspect(q,compact).HasAddress||compact.Length>512)throw new Exception("Captured CDN reply lost valid addresses or requires truncation");
            var before=DnsWirePolicy.AddressRecords(q,r).Order().ToArray();var after=DnsWirePolicy.AddressRecords(q,compact).Order().ToArray();
            if(!before.SequenceEqual(after))throw new Exception("Compact reply invented or lost an address");
        }
        foreach(var host in new[]{"tr.rbxcdn.com","thumbnails.roblox.com","youtube.com","discord.com","web.telegram.org","rbxcdn.com.evil.test"})
        foreach(var type in new ushort[]{1,28})
        {
            var q=AdvancedRouteTests.Question(host,type);var negative=q.ToArray();negative[2]=0x81;negative[3]=0x80;
            if(RobloxDnsRecovery.IsScoped(q)!=(host=="tr.rbxcdn.com"||host=="thumbnails.roblox.com"))throw new Exception("Secure query scope escaped Roblox");
            var calls=0;var answer=await RobloxDnsRecovery.ResolveAsync(q,negative,_=>{calls++;return Task.FromResult(AdvancedRouteTests.Answer(q));},default);
            var expected=type==1&&(host=="tr.rbxcdn.com"||host=="thumbnails.roblox.com");
            if(calls!=(expected?1:0)||expected!=DnsWirePolicy.Inspect(q,answer).HasAddress)throw new Exception("Roblox DNS recovery escaped its scope: "+host);
            if(expected)
            {
                var failed=await RobloxDnsRecovery.ResolveAsync(q,negative,_=>throw new HttpRequestException("TLS failure"),default);
                if(!ReferenceEquals(negative,failed))throw new Exception("Failed HTTPS invented an address");
                var valid=AdvancedRouteTests.Answer(q);calls=0;
                await RobloxDnsRecovery.ResolveAsync(q,valid,_=>{calls++;return Task.FromResult(valid);},default);
                if(calls!=0)throw new Exception("Working Roblox UDP answer triggered recovery");
            }
        }
    }
}
