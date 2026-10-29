using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace VortexPass.Core.Services;
public sealed record WarpProxyMetrics(bool Enabled,int ActiveTunnels,long Completed,long Failures,long Rejected,long SentBytes,long ReceivedBytes,long Handshakes,double? MeanHandshakeMs);

// A byte tunnel: TLS terminates at the destination, never in VortexPass.
public sealed class WarpHttpProxy : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private readonly SemaphoreSlim _clients=new(64);
    private readonly ConcurrentDictionary<long,Task> _connections=new();
    private long _next,_completed,_failures,_rejected,_sent,_received,_handshakes,_handshakeTicks;
    private int _active;
    public bool DiagnosticsEnabled {get;set;}
    public WarpProxyMetrics Metrics=>new(DiagnosticsEnabled,Volatile.Read(ref _active),Interlocked.Read(ref _completed),Interlocked.Read(ref _failures),Interlocked.Read(ref _rejected),Interlocked.Read(ref _sent),Interlocked.Read(ref _received),Interlocked.Read(ref _handshakes),Interlocked.Read(ref _handshakes) is var n&&n>0?Interlocked.Read(ref _handshakeTicks)*1000.0/Stopwatch.Frequency/n:null);
    public int Port=>((IPEndPoint)_listener!.LocalEndpoint).Port;
    public static bool Allows(string host,IEnumerable<string> domains)=>domains.Any(d=>host.Equals(d,StringComparison.OrdinalIgnoreCase)||host.EndsWith("."+d,StringComparison.OrdinalIgnoreCase));
    public async Task<int> StartAsync(IEnumerable<string> domains,int socksPort,CancellationToken token)
    {
        await StopAsync();token.ThrowIfCancellationRequested();var allowed=domains.Select(WarpRoutingPlanner.NormalizeDomain).Where(d=>d is not null).Cast<string>().ToArray();
        _listener=new(IPAddress.Loopback,0);_listener.Start();
        // Starting-operation cancellation must not kill an established proxy.
        _lifetime=new();_loop=AcceptAsync(_listener,allowed,socksPort,_lifetime.Token);return Port;
    }
    private async Task AcceptAsync(TcpListener listener,string[] allowed,int socksPort,CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            TcpClient client;
            try{client=await listener.AcceptTcpClientAsync(token);}catch(Exception ex)when(ex is OperationCanceledException or SocketException or ObjectDisposedException){break;}
            if(!_clients.Wait(0)){Interlocked.Increment(ref _rejected);client.Dispose();continue;}
            var id=Interlocked.Increment(ref _next);var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _connections[id]=completion.Task;Interlocked.Increment(ref _active);
            _=TrackAsync();
            async Task TrackAsync()
            {try{await ServeAsync(client,allowed,socksPort,token);}catch{Interlocked.Increment(ref _failures);client.Dispose();}finally{Interlocked.Decrement(ref _active);Interlocked.Increment(ref _completed);_connections.TryRemove(id,out _);completion.TrySetResult();}}
        }
    }
    private async Task ServeAsync(TcpClient client,string[] allowed,int socksPort,CancellationToken token)
    {
        using(client)
        try
        {
            var started=Stopwatch.GetTimestamp();var input=client.GetStream();using var handshake=CancellationTokenSource.CreateLinkedTokenSource(token);handshake.CancelAfter(TimeSpan.FromSeconds(8));
            var header=new List<byte>();var one=new byte[1];
            while(header.Count<16384)
            {
                if(await input.ReadAsync(one,handshake.Token)==0)return;header.Add(one[0]);
                var n=header.Count;if(n>=4&&header[n-4]==13&&header[n-3]==10&&header[n-2]==13&&header[n-1]==10)break;
            }
            var text=Encoding.ASCII.GetString(header.ToArray());var lines=text.Split("\r\n");var first=lines[0].Split(' ');
            if(first.Length!=3||!text.EndsWith("\r\n\r\n",StringComparison.Ordinal))return;
            var connect=first[0]=="CONNECT";Uri target;
            if(!Uri.TryCreate(connect?"https://"+first[1]:first[1],UriKind.Absolute,out target!)||target.UserInfo.Length>0||
                target.Scheme is not ("http" or "https")||target.Port is not (80 or 443)||!Allows(target.IdnHost,allowed))
            {await input.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),handshake.Token);return;}
            using var upstream=new TcpClient();await upstream.ConnectAsync(IPAddress.Loopback,socksPort,handshake.Token);var output=upstream.GetStream();
            await OpenSocksAsync(output,target.IdnHost,target.Port,handshake.Token);
            if(DiagnosticsEnabled){Interlocked.Increment(ref _handshakes);Interlocked.Add(ref _handshakeTicks,Stopwatch.GetTimestamp()-started);}
            if(connect)await input.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(),handshake.Token);
            else
            {
                if(lines.Any(l=>l.StartsWith("Transfer-Encoding:",StringComparison.OrdinalIgnoreCase))&&lines.Any(l=>l.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)))throw new IOException("Ambiguous HTTP framing");
                var clean=lines.Skip(1).Where(l=>l.Length>0&&!l.StartsWith("Proxy-",StringComparison.OrdinalIgnoreCase)&&!l.StartsWith("Connection:",StringComparison.OrdinalIgnoreCase)&&!l.StartsWith("Host:",StringComparison.OrdinalIgnoreCase));
                var rewritten=$"{first[0]} {target.PathAndQuery} HTTP/1.1\r\nHost: {target.Authority}\r\n"+string.Join("\r\n",clean)+"\r\nConnection: close\r\n\r\n";
                await output.WriteAsync(Encoding.ASCII.GetBytes(rewritten),handshake.Token);
            }
            using var relay=CancellationTokenSource.CreateLinkedTokenSource(token);
            var send=CopyAsync(input,output,upstream.Client,true,relay);var receive=CopyAsync(output,input,client.Client,false,relay);
            await Task.WhenAll(send,receive);
        }
        catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException){if(!token.IsCancellationRequested)Interlocked.Increment(ref _failures);/* No URLs, headers or credentials. */}
        finally{_clients.Release();}
    }
    private async Task CopyAsync(Stream source,Stream destination,Socket destinationSocket,bool upload,CancellationTokenSource relay)
    {
        try
        {
            var buffer=new byte[81920];
            while(true)
            {
                var n=await source.ReadAsync(buffer,relay.Token);if(n==0)break;
                await destination.WriteAsync(buffer.AsMemory(0,n),relay.Token);
                if(DiagnosticsEnabled){if(upload)Interlocked.Add(ref _sent,n);else Interlocked.Add(ref _received,n);}
            }
            // EOF closes only this direction. The peer may still send a complete response.
            try{destinationSocket.Shutdown(SocketShutdown.Send);}catch(SocketException ex)when(ex.SocketErrorCode is SocketError.NotConnected or SocketError.Shutdown){}
        }
        catch{relay.Cancel();throw;}
    }
    public static async Task OpenSocksAsync(Stream stream,string host,int port,CancellationToken token)
    {
        var name=Encoding.ASCII.GetBytes(new System.Globalization.IdnMapping().GetAscii(host));if(name.Length>255)throw new IOException("SOCKS host too long");
        await stream.WriteAsync(new byte[]{5,1,0},token);var response=new byte[2];await stream.ReadExactlyAsync(response,token);
        if(response[0]!=5||response[1]!=0)throw new IOException("SOCKS authentication unavailable");
        var request=new byte[7+name.Length];request[0]=5;request[1]=1;request[3]=3;request[4]=(byte)name.Length;name.CopyTo(request,5);request[^2]=(byte)(port>>8);request[^1]=(byte)port;
        await stream.WriteAsync(request,token);var result=new byte[4];await stream.ReadExactlyAsync(result,token);
        if(result[0]!=5||result[1]!=0||result[2]!=0)throw new IOException("SOCKS target unavailable");
        var size=result[3] switch{1=>4,4=>16,3=>await ReadSize(),_=>throw new IOException("Invalid SOCKS reply")};
        await stream.ReadExactlyAsync(new byte[size+2],token);
        async Task<int> ReadSize(){var n=new byte[1];await stream.ReadExactlyAsync(n,token);return n[0];}
    }
    public async Task StopAsync()
    {
        _lifetime?.Cancel();_listener?.Stop();if(_loop is not null)await _loop;
        await Task.WhenAll(_connections.Values.ToArray());
        _loop=null;_listener=null;_lifetime?.Dispose();_lifetime=null;
    }
    public async ValueTask DisposeAsync()=>await StopAsync();
}
