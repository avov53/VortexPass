namespace VortexPass.Core.Services;

// Selection belongs to the control-domain health check, never to individual user queries.
public static class StableDnsFallback
{
    public static async Task<(T Provider,byte[] Payload)> QueryAsync<T>(IReadOnlyList<T> ordered,
        Func<T,CancellationToken,Task<byte[]>> query,CancellationToken token)
    {
        Exception? last=null;
        foreach(var provider in ordered)
        {
            token.ThrowIfCancellationRequested();
            try{return(provider,await query(provider,token));}
            catch(OperationCanceledException)when(!token.IsCancellationRequested){last=new TimeoutException("DNS transport timeout");}
            catch(Exception ex)when(ex is IOException or System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException or TimeoutException){last=ex;}
        }
        token.ThrowIfCancellationRequested();throw new IOException("Все DNS-транспорты недоступны",last);
    }
}
