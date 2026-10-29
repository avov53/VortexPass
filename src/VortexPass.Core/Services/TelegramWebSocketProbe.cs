using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace VortexPass.Core.Services;

public sealed record TelegramTransportResult(bool Confirmed, string Detail, long? LatencyMs = null);

public static class TelegramWebSocketProbe
{
    // Obfuscated intermediate transport, per core.telegram.org/mtproto/mtproto-transports.
    // No credentials, authorization key or account data are involved.
    public static async Task<TelegramTransportResult> CheckAsync(string host, CancellationToken token,
        IPAddress? address = null, bool sustained = false)
    {
        if (!TelegramEndpointRegistry.IsTransport(host)) throw new ArgumentException("Unknown Telegram endpoint");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(sustained ? 7 : 4));
            using var socket = new ClientWebSocket();
            socket.Options.Proxy = null;
            socket.Options.AddSubProtocol("binary");
            using var handler = address is null ? null : TelegramRouteProbeService.PinnedHandler(address);
            using var invoker = handler is null ? null : new HttpMessageInvoker(handler, false);
            if (invoker is null) await socket.ConnectAsync(TelegramEndpointRegistry.WebSocket(host), timeout.Token);
            else await socket.ConnectAsync(TelegramEndpointRegistry.WebSocket(host), invoker, timeout.Token);
            var latency = await ExchangeAsync(socket, sustained, timeout.Token);
            return new(true, $"{host}: MTProto resPQ {(sustained ? "3/3" : "1/1")}; аккаунт не проверялся", latency);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return new(false, $"{host}: MTProto {(ex is OperationCanceledException ? "тайм-аут" : ex.GetType().Name)}");
        }
    }

    internal static async Task<long> ExchangeAsync(WebSocket socket, bool sustained, CancellationToken token)
    {
        var header = CreateHeader();
        var reverse = header.Reverse().ToArray();
        using var outgoing = new CounterCipher(header[8..40], header[40..56]);
        using var incoming = new CounterCipher(reverse[8..40], reverse[40..56]);
        var encryptedHeader = outgoing.Transform(header);
        encryptedHeader.AsSpan(56, 8).CopyTo(header.AsSpan(56));
        await socket.SendAsync(header.AsMemory(), WebSocketMessageType.Binary, true, token);
        var pending = new Queue<byte>();
        async Task<byte[]> Read(int count)
        {
            while (pending.Count < count)
            {
                var buffer = new byte[4096];
                var received = await socket.ReceiveAsync(buffer.AsMemory(), token);
                if (received.MessageType != WebSocketMessageType.Binary || received.Count == 0)
                    throw new IOException("WebSocket closed before MTProto response");
                foreach (var b in incoming.Transform(buffer[..received.Count])) pending.Enqueue(b);
                if (pending.Count > 8192) throw new IOException("MTProto frame too large");
            }
            var result = new byte[count];
            for (var i = 0; i < count; i++) result[i] = pending.Dequeue();
            return result;
        }
        var samples = new List<long>();
        for (var i = 0; i < (sustained ? 3 : 1); i++)
        {
            if (i > 0) await Task.Delay(500, token);
            var nonce = RandomNumberGenerator.GetBytes(16);
            var request = TelegramHttpsProbe.Request(nonce, DateTimeOffset.UtcNow);
            var frame = new byte[4 + request.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, request.Length);
            request.CopyTo(frame, 4);
            var watch = Stopwatch.StartNew();
            await socket.SendAsync(outgoing.Transform(frame).AsMemory(), WebSocketMessageType.Binary, true, token);
            var lengthBytes = await Read(4);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is < 68 or > 4096) throw new IOException("MTProto invalid frame length");
            var response = await Read(length);
            if (!TelegramHttpsProbe.IsResPq(response, nonce)) throw new IOException("MTProto nonce/response mismatch");
            samples.Add(watch.ElapsedMilliseconds);
        }
        socket.Abort();
        return LocalStrategyAdvisor.Median(samples)!.Value;
    }

    internal static byte[] CreateHeader()
    {
        byte[] header;
        do { header = RandomNumberGenerator.GetBytes(64); }
        while (header[0] == 0xef || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) == 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) is 0xdddddddd or 0xeeeeeeee or 0x54534f50 or 0x20544547 or 0x44414548 or 0x4954504f);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), 0xeeeeeeee);
        return header;
    }

    // AES CTR is a byte stream: keep the counter and unused keystream across
    // arbitrary WebSocket fragments. Encrypting the header consumes 64 bytes.
    internal sealed class CounterCipher : IDisposable
    {
        private readonly Aes _aes = Aes.Create();
        private readonly byte[] _counter;
        private readonly byte[] _block = new byte[16];
        private int _offset = 16;
        public CounterCipher(byte[] key, byte[] iv) { _aes.Key = key; _counter = iv.ToArray(); }
        public byte[] Transform(byte[] bytes)
        {
            var output = new byte[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                if (_offset == 16)
                {
                    _aes.EncryptEcb(_counter, _block, PaddingMode.None);
                    for (var n = 15; n >= 0 && ++_counter[n] == 0; n--) { }
                    _offset = 0;
                }
                output[i] = (byte)(bytes[i] ^ _block[_offset++]);
            }
            return output;
        }
        public void Dispose() => _aes.Dispose();
    }
}
