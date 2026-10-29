using System.Buffers.Binary;
using System.Text;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

internal static class AdvancedRouteTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    public static byte[] Question(string host="example.org",ushort type=1)
    {
        using var data=new MemoryStream();data.Write(new byte[]{0x12,0x34,1,0,0,1,0,0,0,0,0,0});
        foreach(var label in host.Split('.')){var bytes=Encoding.ASCII.GetBytes(label);data.WriteByte((byte)bytes.Length);data.Write(bytes);}
        data.Write(new byte[]{0,(byte)(type>>8),(byte)type,0,1});return data.ToArray();
    }
    public static byte[] Answer(byte[] query,ushort type=1,byte[]? bytes=null,int count=1)
    {
        using var data=new MemoryStream();var header=query.ToArray();header[2]=0x81;header[3]=0x80;header[6]=(byte)(count>>8);header[7]=(byte)count;data.Write(header);
        bytes??=type==28?new byte[16]:new byte[]{1,2,3,4};
        for(var i=0;i<count;i++){data.Write(new byte[]{0xc0,0x0c,(byte)(type>>8),(byte)type,0,1,0,0,0,60,(byte)(bytes.Length>>8),(byte)bytes.Length});data.Write(bytes);}
        return data.ToArray();
    }
    public static Task Dns()
    {
        var query=Question();var response=Answer(query);var result=DnsWirePolicy.Inspect(query,response);
        Check(result.HasAddress&&result.CacheSeconds==60,"A record rejected");
        var cname=Answer(query,5,new byte[]{5,(byte)'a',(byte)'l',(byte)'i',(byte)'a',(byte)'s',0xc0,0x0c});
        Check(!DnsWirePolicy.Inspect(query,cname).HasAddress,"CNAME alone accepted as address");
        Check(DnsWirePolicy.Inspect(query,DnsWirePolicy.Age(response,TimeSpan.FromSeconds(17))).CacheSeconds==43,"TTL not aged");
        var v6=Question(type:28);Check(DnsWirePolicy.Inspect(v6,Answer(v6,28)).HasAddress,"AAAA rejected");
        var nodata=v6.ToArray();nodata[2]=0x81;nodata[3]=0x80;
        Check(!DnsWirePolicy.Inspect(v6,nodata).HasAddress&&DnsWirePolicy.Inspect(v6,nodata).ResponseCode==0,"Normal NODATA confused with failure");
        var large=Answer(query,count:40);Check(DnsWirePolicy.Inspect(query,DnsWirePolicy.ForUdp(query,large)).Truncated,"UDP not truncated for TCP fallback");
        Check(DnsWirePolicy.Inspect(query,large).HasAddress,"Full TCP response rejected");
        Check(DnsWirePolicy.Inspect(query,DnsWirePolicy.ServFail(query)).ResponseCode==2,"SERVFAIL invalid");
        void Reject(byte[] packet){try{DnsWirePolicy.Inspect(query,packet);}catch(InvalidDataException){return;}throw new Exception("Malformed DNS accepted");}
        var wrong=response.ToArray();wrong[0]^=1;Reject(wrong);
        wrong=response.ToArray();wrong[query.Length+5]=3;Reject(wrong);
        wrong=response[..^1];Reject(wrong);
        wrong=response.ToArray();wrong[query.Length]=0xff;wrong[query.Length+1]=0xff;Reject(wrong);
        return Task.CompletedTask;
    }
    public static Task Rules()
    {
        var rule=new SiteRule{Name="Media",Domain="media.youtube.com",Protocol="tcp",Ports="443,8443"};
        var parsed=SiteRulePolicy.Validate([rule]);var command=WinwsProfileComposer.Compose(new(){YouTube=true},"medium",new(),null,null,siteRules:parsed);
        Check(command.IndexOf("--hostlist-domains=media.youtube.com",StringComparison.Ordinal)<command.IndexOf("--hostlist=files\\list-youtube.txt",StringComparison.Ordinal),"Site rules lose priority");
        Check(command.Contains("--wf-tcp=80,443,8443"),"Rule port not captured");
        var just80=WinwsProfileComposer.Compose(new(){Custom=true},"medium",new(),null,null,siteRules:[rule with{Ports="80",Protocol="auto"}]);
        Check(!just80.Contains("--filter-udp=443 --hostlist-domains=media.youtube.com"),"Port 443 leaked into port 80 rule");
        void Reject(SiteRule input){try{SiteRulePolicy.Validate([input]);}catch(ArgumentException){return;}catch(FormatException){return;}throw new Exception("Unsafe rule accepted");}
        Reject(rule with{Protocol="udp"});Reject(rule with{Ports="1-65535"});Reject(rule with{Connection="warpProxy",Protocol="quic",Ports=""});
        Reject(rule with{Domain="example.org --unsafe"});Reject(rule with{Connection="warpProxy",Components=["voice"],Ports=""});
        var profile=new SavedConnectionProfile{Id=Guid.NewGuid().ToString(),Name="Rules",SiteRules=[rule]};
        Check(SavedConnectionProfilePolicy.ServiceFlags(SavedConnectionProfilePolicy.Validate(profile)).Custom,"Rule-only profile not enabled");
        var copy=ProfileTransferService.Parse(ProfileTransferService.Export(profile));
        Check(copy.SiteRules.Count==1&&copy.SiteRules[0].Id!=rule.Id&&copy.SiteRules[0].Ports=="443,8443","Rule import lost data or reused IDs");
        var before=DraftRecommendationPolicy.Composition(profile);profile.SiteRules=[rule with{Ports="443"}];
        Check(before!=DraftRecommendationPolicy.Composition(profile),"Proof does not bind site rule");
        return Task.CompletedTask;
    }
    public static Task Mtproto()
    {
        var nonce=Enumerable.Range(0,16).Select(i=>(byte)i).ToArray();var request=TelegramHttpsProbe.Request(nonce,DateTimeOffset.UtcNow);
        Check(BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(16))==20&&request.Length==40,"Invalid MTProto envelope");
        var response=new byte[84];BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(16),64);BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(20),0x05162463);nonce.CopyTo(response,24);
        response[56]=8;BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(68),0x1cb5c415);BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(72),1);
        Check(TelegramHttpsProbe.IsResPq(response,nonce),"Valid resPQ rejected");response[24]^=1;Check(!TelegramHttpsProbe.IsResPq(response,nonce),"Foreign nonce accepted");
        Check(!TelegramHttpsProbe.IsResPq("<html>HTTP 200</html>"u8,nonce),"HTML accepted as MTProto");return Task.CompletedTask;
    }
}
