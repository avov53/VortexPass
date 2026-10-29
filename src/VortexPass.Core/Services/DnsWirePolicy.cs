using System.Buffers.Binary;
using System.Text;

namespace VortexPass.Core.Services;

public sealed record DnsWireResult(string Name, ushort Type, int ResponseCode, bool Truncated, bool HasAddress, int CacheSeconds);
public static class DnsWirePolicy
{
    public static string QuestionName(byte[] query) => Question(query).Name;
    public static string[] AddressRecords(byte[] query, byte[] response)
    {
        Inspect(query,response);
        var question=Question(response);
        return Records(response,question.Offset).Where(r=>r.Type is 1 or 28).Select(r=>new System.Net.IPAddress(response.AsSpan(r.Data,r.Length)).ToString()).Distinct().ToArray();
    }
    private sealed record Record(string Name, ushort Type, ushort Class,uint Ttl, int TtlOffset, int Data, int Length);
    private static ushort U16(byte[] p,int i) => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(i,2));
    private static uint U32(byte[] p,int i) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(i,4));
    private static string Name(byte[] p,ref int offset)
    {
        var position=offset;var end=-1;var labels=new List<string>();var visited=new HashSet<int>();
        for(var count=0;count<128;count++)
        {
            if(position>=p.Length||!visited.Add(position))throw new InvalidDataException("DNS name compression invalid");
            var size=p[position++];
            if(size==0){offset=end<0?position:end;return string.Join('.',labels).ToLowerInvariant();}
            if((size&0xc0)==0xc0){if(position>=p.Length)throw new InvalidDataException("DNS pointer truncated");end=end<0?position+1:end;position=((size&0x3f)<<8)|p[position];continue;}
            if(size>63||position+size>p.Length)throw new InvalidDataException("DNS label invalid");
            labels.Add(Encoding.ASCII.GetString(p,position,size));position+=size;
        }
        throw new InvalidDataException("DNS name too long");
    }
    private static (string Name,ushort Type,ushort Class,int Offset) Question(byte[] p)
    {
        if(p.Length<12||U16(p,4)!=1)throw new InvalidDataException("DNS question count invalid");
        var offset=12;var name=Name(p,ref offset);if(offset+4>p.Length)throw new InvalidDataException("DNS question truncated");
        return(name,U16(p,offset),U16(p,offset+2),offset+4);
    }
    private static List<Record> Records(byte[] p,int offset)
    {
        var records=new List<Record>();var count=U16(p,6)+U16(p,8)+U16(p,10);
        if(count>4096)throw new InvalidDataException("DNS record count invalid");
        for(var i=0;i<count;i++)
        {
            var name=Name(p,ref offset);if(offset+10>p.Length)throw new InvalidDataException("DNS record truncated");
            var type=U16(p,offset);var length=U16(p,offset+8);var data=offset+10;
            if(data+length>p.Length)throw new InvalidDataException("DNS record data truncated");
            if(type==1&&length!=4||type==28&&length!=16)throw new InvalidDataException("DNS address length invalid");
            records.Add(new(name,type,U16(p,offset+2),U32(p,offset+4),offset+4,data,length));offset=data+length;
        }
        if(offset!=p.Length)throw new InvalidDataException("DNS trailing bytes invalid");
        return records;
    }
    public static DnsWireResult Inspect(byte[] query,byte[] response)
    {
        var expected=Question(query);var actual=Question(response);
        if((response[2]&0x80)==0||(response[2]&0x78)!=0||U16(query,0)!=U16(response,0)||expected.Name!=actual.Name||expected.Type!=actual.Type||expected.Class!=actual.Class)
            throw new InvalidDataException("DNS response does not match question");
        var rcode=response[3]&15;var truncated=(response[2]&2)!=0;
        if(truncated)return new(actual.Name,actual.Type,rcode,true,false,0);
        var records=Records(response,actual.Offset);var answerCount=U16(response,6);var answers=records.Take(answerCount).ToArray();
        if(answers.Any(r=>r.Class!=actual.Class))throw new InvalidDataException("DNS answer class does not match question");
        var reachable=new HashSet<string>(StringComparer.OrdinalIgnoreCase){actual.Name};
        for(var i=0;i<answers.Length;i++)
            foreach(var record in answers.Where(r=>r.Type==5&&reachable.Contains(r.Name)))
            {var pos=record.Data;var alias=Name(response,ref pos);if(pos!=record.Data+record.Length)throw new InvalidDataException("DNS CNAME invalid");reachable.Add(alias);}
        var address=rcode==0&&answers.Any(r=>r.Type==actual.Type&&r.Type is 1 or 28&&reachable.Contains(r.Name));
        var ttl=address?answers.Where(r=>r.Type is 1 or 28 or 5&&reachable.Contains(r.Name)).Select(r=>r.Ttl).DefaultIfEmpty(0u).Min():0u;
        if(!address&&rcode is 0 or 3)
            foreach(var soa in records.Skip(answerCount).Take(U16(response,8)).Where(r=>r.Type==6))
            {var pos=soa.Data;Name(response,ref pos);Name(response,ref pos);if(pos+20!=soa.Data+soa.Length)throw new InvalidDataException("DNS SOA invalid");ttl=Math.Min(soa.Ttl,U32(response,pos+16));}
        return new(actual.Name,actual.Type,rcode,false,address,(int)Math.Min(ttl, address?3600u:60u));
    }
    public static byte[] Age(byte[] payload,TimeSpan elapsed)
    {
        var copy=payload.ToArray();var q=Question(copy);var seconds=(uint)Math.Max(0,elapsed.TotalSeconds);
        foreach(var record in Records(copy,q.Offset).Where(r=>r.Type!=41))
            BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(record.TtlOffset,4),record.Ttl>seconds?record.Ttl-seconds:0);
        return copy;
    }
    public static byte[] ServFail(byte[] query)
    {
        var question=Question(query);var response=query[..question.Offset];response[2]=(byte)((response[2]&1)|0x80);response[3]=0x82;
        Array.Clear(response,6,6);return response;
    }
    public static byte[] ForUdp(byte[] query,byte[] response)
    {
        var question=Question(query);var size=Records(query,question.Offset).FirstOrDefault(r=>r.Type==41)?.Class??512;
        var limit=Math.Clamp((int)size,512,4096);if(response.Length<=limit)return response;
        var answer=Question(response);var truncated=response[..answer.Offset];truncated[2]|=2;Array.Clear(truncated,6,6);return truncated;
    }
}
