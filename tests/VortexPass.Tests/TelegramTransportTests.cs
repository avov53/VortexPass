using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using VortexPass.Core.Services;

internal static class TelegramTransportTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task Run()
    {
        // NIST SP 800-38A AES-256 CTR vector: verifies key order and counter
        // endianness independently of the local fixture implementation.
        var key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        var iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var clear = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
        var expected = "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5";
        using (var cipher = new TelegramWebSocketProbe.CounterCipher(key, iv))
        {
            var result = cipher.Transform(clear[..7]).Concat(cipher.Transform(clear[7..])).ToArray();
            Check(Convert.ToHexString(result).Equals(expected, StringComparison.OrdinalIgnoreCase), "AES CTR vector/fragment state invalid");
        }
        foreach (var corruptNonce in new[] { false, true })
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                using var clientTcp = new TcpClient();
                await clientTcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, lifetime.Token);
                using var serverTcp = await listener.AcceptTcpClientAsync(lifetime.Token);
                using var client = WebSocket.CreateFromStream(clientTcp.GetStream(), false, "binary", TimeSpan.FromSeconds(30));
                using var server = WebSocket.CreateFromStream(serverTcp.GetStream(), true, "binary", TimeSpan.FromSeconds(30));
                var fixture = Serve(server, corruptNonce, lifetime.Token);
                var rejected = false;
                try { await TelegramWebSocketProbe.ExchangeAsync(client, !corruptNonce, lifetime.Token); }
                catch (IOException) { rejected = true; }
                Check(rejected == corruptNonce, "Foreign nonce accepted or valid fragmented MTProto rejected");
                await fixture;
            }
            finally { listener.Stop(); }
        }
        Check(DnsModePolicy.Normalize("invalid") == "google-udp" && DnsModePolicy.Normalize("doh")=="doh" && !DnsModePolicy.UsesGuard("system") && DnsModePolicy.UsesGuard("google-udp"), "DNS modes unsafe");
    }

    private static async Task Serve(WebSocket socket, bool corrupt, CancellationToken token)
    {
        async Task<byte[]> Receive()
        {
            using var memory = new MemoryStream(); var buffer = new byte[1024];
            WebSocketReceiveResult part;
            do { part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token); memory.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
            return memory.ToArray();
        }
        var header = await Receive();
        Check(header.Length == 64, "Obfuscation header length invalid");
        var reverse = header.Reverse().ToArray();
        using var decrypt = new TelegramWebSocketProbe.CounterCipher(header[8..40], header[40..56]);
        using var encrypt = new TelegramWebSocketProbe.CounterCipher(reverse[8..40], reverse[40..56]);
        var decryptedHeader = decrypt.Transform(header);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(decryptedHeader.AsSpan(56)) == 0xeeeeeeee, "Intermediate protocol header invalid");
        for (var i = 0; i < (corrupt ? 1 : 3); i++)
        {
            var request = decrypt.Transform(await Receive());
            Check(BinaryPrimitives.ReadInt32LittleEndian(request) == 40, "Intermediate request framing invalid");
            var nonce = request[28..44];
            var response = new byte[84];
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(16), 64);
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(20), 0x05162463);
            nonce.CopyTo(response, 24); if (corrupt) response[24] ^= 1;
            response[56] = 8;
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(68), 0x1cb5c415);
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(72), 1);
            var frame = new byte[88]; BinaryPrimitives.WriteInt32LittleEndian(frame, 84); response.CopyTo(frame, 4);
            var encrypted = encrypt.Transform(frame);
            // Split inside both the length prefix and payload across WS messages.
            await socket.SendAsync(encrypted[..2].AsMemory(), WebSocketMessageType.Binary, true, token);
            await socket.SendAsync(encrypted[2..17].AsMemory(), WebSocketMessageType.Binary, false, token);
            await socket.SendAsync(encrypted[17..].AsMemory(), WebSocketMessageType.Binary, true, token);
        }
    }
}
