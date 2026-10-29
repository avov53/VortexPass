using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VortexPass.Core.Services;

public sealed record DnsProbeResult(string Mode, string Provider, string Host, string RecordType, string State,
    long DurationMs, string[] Addresses, string Detail);
public sealed record DnsResolverChoice(string Id, string Mode, string Provider, bool Working, string[] MissingHosts);
public sealed record PlainDnsReply(byte[] Payload, string Transport, string? UdpState = null);

public static class DnsDiagnosticsService
{
    public const string ControlHost = "example.com";
    public static byte[] Question(string host, ushort type = 1)
    {
        using var stream = new MemoryStream(); var header = new byte[12];
        RandomNumberGenerator.Fill(header.AsSpan(0,2)); header[2]=1; header[5]=1; stream.Write(header);
        foreach(var label in new System.Globalization.IdnMapping().GetAscii(host).Split('.'))
        { var bytes=Encoding.ASCII.GetBytes(label); if(bytes.Length is <1 or >63)throw new ArgumentException("Invalid DNS label");stream.WriteByte((byte)bytes.Length);stream.Write(bytes); }
        stream.WriteByte(0);stream.WriteByte((byte)(type>>8));stream.WriteByte((byte)type);stream.WriteByte(0);stream.WriteByte(1);
        return stream.ToArray();
    }
    public static string Classify(DnsWireResult wire) => wire.ResponseCode switch
    { 3=>"nxdomain",2=>"servfail",0 when wire.HasAddress=>"ok",0=>"nodata",_=>"dnsError" };

    public static IReadOnlyList<DnsResolverChoice> Choices(IEnumerable<DnsProbeResult> results, IEnumerable<string> hosts) => results
        .GroupBy(r=>(r.Mode,r.Provider)).Select(g=>
        {
            var missing=hosts.Distinct(StringComparer.OrdinalIgnoreCase).Where(h=>!g.Any(r=>r.Host.Equals(h,StringComparison.OrdinalIgnoreCase)&&r.State=="ok")).ToArray();
            var control=g.Any(r=>r.Host==ControlHost&&r.State=="ok");
            return new DnsResolverChoice(Guid.NewGuid().ToString("N"),g.Key.Mode,g.Key.Provider,control&&missing.Length==0,missing);
        }).ToArray();

    public static async Task<IReadOnlyList<DnsProbeResult>> CheckAsync(IEnumerable<string> hosts, CancellationToken token)
    {
        var names=new[]{ControlHost}.Concat(hosts).Where(h=>Uri.CheckHostName(h)==UriHostNameType.Dns).Distinct(StringComparer.OrdinalIgnoreCase).Take(65).ToArray();
        var resolvers=new (string Mode,string Name,string? Address,string? Url)[]
        { ("system","system",null,null),("google-udp","8.8.8.8","8.8.8.8",null),("google-udp","8.8.4.4","8.8.4.4",null),
          ("doh","cloudflare","1.1.1.1","https://cloudflare-dns.com/dns-query"),("doh","google","8.8.8.8","https://dns.google/dns-query"),("doh","quad9","9.9.9.9","https://dns.quad9.net/dns-query") };
        using var overall=CancellationTokenSource.CreateLinkedTokenSource(token);overall.CancelAfter(TimeSpan.FromSeconds(40));
        using var gate=new SemaphoreSlim(8);
        var clients=resolvers.Where(r=>r.Url is not null).ToDictionary(r=>r.Name,r=>new HttpClient(TelegramRouteProbeService.PinnedHandler(IPAddress.Parse(r.Address!))));
        var all=new List<Task<DnsProbeResult>>();
        foreach(var host in names)
        foreach(var resolver in resolvers)
        foreach(var type in new ushort[]{1,28})
            all.Add(Probe(resolver,host,type));
        try {return await Task.WhenAll(all);}finally{foreach(var client in clients.Values)client.Dispose();}

        async Task<DnsProbeResult> Probe((string Mode,string Name,string? Address,string? Url) resolver,string host,ushort type)
        {
            var watch=Stopwatch.StartNew();var record=type==1?"A":"AAAA";
            try {await gate.WaitAsync(overall.Token);}catch(OperationCanceledException){token.ThrowIfCancellationRequested();return new(resolver.Mode,resolver.Name,host,record,"timeout",watch.ElapsedMilliseconds,[],"Общий бюджет диагностики исчерпан");}
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(overall.Token);deadline.CancelAfter(TimeSpan.FromSeconds(4));
                if(resolver.Mode=="system")
                {
                    var addresses=(await Dns.GetHostAddressesAsync(host,deadline.Token)).Where(a=>a.AddressFamily==(type==1?AddressFamily.InterNetwork:AddressFamily.InterNetworkV6)).Select(a=>a.ToString()).ToArray();
                    return new(resolver.Mode,resolver.Name,host,record,addresses.Length>0?"ok":"nodata",watch.ElapsedMilliseconds,addresses,"Windows resolver");
                }
                var query=Question(host,type);byte[] response;string? transportDetail=null;
                if(resolver.Url is null)
                {
                    var plain=await QueryPlainAsync(IPAddress.Parse(resolver.Address!),query,deadline.Token);
                    response=plain.Payload;transportDetail=$"{plain.Transport}; UDP={plain.UdpState??"ok"}";
                }
                else
                {
                    var client=clients[resolver.Name];
                    using var request=new HttpRequestMessage(HttpMethod.Post,resolver.Url){Content=new ByteArrayContent(query),Version=HttpVersion.Version20,VersionPolicy=HttpVersionPolicy.RequestVersionOrLower};
                    request.Content.Headers.ContentType=new("application/dns-message");
                    request.Headers.Accept.ParseAdd("application/dns-message");
                    using var reply=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);reply.EnsureSuccessStatusCode();
                    await using var body=await reply.Content.ReadAsStreamAsync(deadline.Token);using var memory=new MemoryStream();
                    var buffer=new byte[4096];while(memory.Length<=65535){var n=await body.ReadAsync(buffer,deadline.Token);if(n==0)break;memory.Write(buffer,0,n);}
                    if(memory.Length>65535)throw new InvalidDataException("Oversized DNS response");response=memory.ToArray();
                }
                var wire=DnsWirePolicy.Inspect(query,response);
                return new(resolver.Mode,resolver.Name,host,record,Classify(wire),watch.ElapsedMilliseconds,wire.Truncated?[]:DnsWirePolicy.AddressRecords(query,response),wire.Truncated?"truncated":$"RCODE={wire.ResponseCode}"+(transportDetail is null?"":$"; {transportDetail}"));
            }
            catch(OperationCanceledException){token.ThrowIfCancellationRequested();return Result("timeout","DNS deadline");}
            catch(InvalidDataException){return Result("invalidResponse","DNS packet validation");}
            catch(SocketException ex){return Result(ex.SocketErrorCode==SocketError.HostNotFound?"nxdomain":ex.SocketErrorCode==SocketError.NoData?"nodata":"transport",ex.SocketErrorCode.ToString());}
            catch(Exception ex)when(ex is IOException or HttpRequestException){return Result("transport",ex.GetType().Name);}
            finally{gate.Release();}
            DnsProbeResult Result(string state,string detail)=>new(resolver.Mode,resolver.Name,host,record,state,watch.ElapsedMilliseconds,[],detail);
        }
    }

    public static async Task<byte[]> QueryUdpAsync(IPAddress address,byte[] query,CancellationToken token,int port=53)
        => (await QueryPlainAsync(address,query,token,port)).Payload;

    public static async Task<PlainDnsReply> QueryPlainAsync(IPAddress address,byte[] query,CancellationToken token,int port=53)
    {
        byte[]? negative=null;string? udpState=null;
        using var udp=new UdpClient(address.AddressFamily);
        try
        {
            udp.Connect(address,port);await udp.SendAsync(query,token);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(1200);
            var payload=(await udp.ReceiveAsync(deadline.Token)).Buffer;
            var wire=DnsWirePolicy.Inspect(query,payload);
            udpState=wire.Truncated?"truncated":Classify(wire);
            // A forged negative UDP reply can arrive before the genuine answer.
            // Confirm negative A replies and NXDOMAIN/SERVFAIL via TCP to the same server.
            if(!wire.Truncated&&wire.ResponseCode==0&&(wire.HasAddress||wire.Type!=1))return new(payload,"udp");
            if(!wire.Truncated)negative=payload;
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){udpState="timeout";}
        catch(SocketException){udpState="transport";}
        catch(InvalidDataException){udpState="invalidResponse";}
        try
        {
        using var tcpDeadline=CancellationTokenSource.CreateLinkedTokenSource(token);tcpDeadline.CancelAfter(TimeSpan.FromMilliseconds(1800));
        var tcpToken=tcpDeadline.Token;
        using var tcp=new TcpClient(address.AddressFamily);await tcp.ConnectAsync(address,port,tcpToken);var stream=tcp.GetStream();
        var prefix=new byte[2];BinaryPrimitives.WriteUInt16BigEndian(prefix,(ushort)query.Length);await stream.WriteAsync(prefix,tcpToken);await stream.WriteAsync(query,tcpToken);
        await stream.ReadExactlyAsync(prefix,tcpToken);var reply=new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];await stream.ReadExactlyAsync(reply,tcpToken);
        if(DnsWirePolicy.Inspect(query,reply).Truncated)throw new InvalidDataException("Truncated TCP DNS response");
        return new(reply,"tcp",udpState);
        }
        catch(Exception ex)when(negative is not null&&!token.IsCancellationRequested&&(ex is IOException or SocketException or OperationCanceledException))
        {return new(negative,"udp; TCP unavailable",udpState);}
    }
}
