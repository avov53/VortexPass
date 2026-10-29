using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
namespace VortexPass.Core.Services;

public sealed record TelegramHttpsResult(bool Confirmed,string Detail,long? LatencyMs=null);
public static class TelegramHttpsProbe
{
    // Only req_pq_multi/resPQ: no account login, API credentials or key creation.
    public static byte[] Request(byte[] nonce,DateTimeOffset now)
    {
        if(nonce.Length!=16)throw new ArgumentException("MTProto nonce must have 16 bytes");
        var packet=new byte[40];var seconds=(ulong)now.ToUnixTimeSeconds();var fraction=(ulong)(now.ToUnixTimeMilliseconds()%1000)*4294967296UL/1000;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8),((seconds<<32)|fraction)&~3UL);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16),20);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20),0xbe7e8ef1);
        nonce.CopyTo(packet,24);return packet;
    }
    public static bool IsResPq(ReadOnlySpan<byte> packet,ReadOnlySpan<byte> nonce)
    {
        if(packet.Length<68||nonce.Length!=16||packet[..8].IndexOfAnyExcept((byte)0)>=0||BinaryPrimitives.ReadInt32LittleEndian(packet[16..])!=packet.Length-20||
            BinaryPrimitives.ReadUInt32LittleEndian(packet[20..])!=0x05162463||!packet.Slice(24,16).SequenceEqual(nonce))return false;
        var length=packet[56];if(length is <1 or >8)return false;
        var offset=57+length;offset+= (4-((1+length)%4))%4;
        if(offset+8>packet.Length||BinaryPrimitives.ReadUInt32LittleEndian(packet[offset..])!=0x1cb5c415)return false;
        var count=BinaryPrimitives.ReadInt32LittleEndian(packet[(offset+4)..]);return count is >0 and <=32&&offset+8+count*8==packet.Length;
    }
    public static async Task<TelegramHttpsResult> CheckAsync(string host,CancellationToken token)
    {
        if(!TelegramEndpointRegistry.TransportHosts.Contains(host,StringComparer.OrdinalIgnoreCase))throw new ArgumentException("Untrusted Telegram endpoint");
        var watch=Stopwatch.StartNew();
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(3)};
            using var client=new HttpClient(handler);var nonce=RandomNumberGenerator.GetBytes(16);
            using var request=new HttpRequestMessage(HttpMethod.Post,$"https://{host}/apiw"){Content=new ByteArrayContent(Request(nonce,DateTimeOffset.UtcNow))};
            request.Content.Headers.ContentType=new("application/octet-stream");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);using var memory=new MemoryStream();var buffer=new byte[512];
            while(memory.Length<=4096){var n=await stream.ReadAsync(buffer,timeout.Token);if(n==0)break;memory.Write(buffer,0,n);}
            var confirmed=response.IsSuccessStatusCode&&memory.Length<=4096&&IsResPq(memory.ToArray(),nonce);
            return new(confirmed,confirmed?$"{host}: req_pq_multi/resPQ подтверждён; сессия аккаунта не проверялась":$"{host}: HTTP {(int)response.StatusCode}, ответ MTProto не подтверждён",watch.ElapsedMilliseconds);
        }
        catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException)
        {token.ThrowIfCancellationRequested();return new(false,$"{host}: {(ex is OperationCanceledException?"тайм-аут":ex.GetType().Name)}",watch.ElapsedMilliseconds);}
    }
}
