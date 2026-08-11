using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace VortexPass.Core.Services;

public sealed record RobloxThumbnailResolution(IPAddress? Address, string Detail)
{
    public bool Succeeded => Address is not null;
}

public sealed class RobloxThumbnailResolver
{
    public const string ThumbnailHost = "tr.rbxcdn.com";
    private const string ResolverUrl = "https://doh.opendns.com/dns-query";

    public async Task<RobloxThumbnailResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var query = Convert.ToBase64String(BuildQuery(ThumbnailHost)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ResolverUrl}?dns={query}");
            request.Headers.Accept.ParseAdd("application/dns-message");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new(null, $"OpenDNS вернул HTTP {(int)response.StatusCode}");

            var payload = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            var address = ReadFirstARecord(payload);
            if (address is null)
                return new(null, "OpenDNS не вернул IPv4-адрес для tr.rbxcdn.com");

            await VerifyTlsAsync(address, timeout.Token);
            return new(address, $"OpenDNS и TLS OK: {address}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or AuthenticationException or OperationCanceledException or InvalidDataException)
        {
            return new(null, ex.InnerException?.Message ?? ex.Message);
        }
    }

    private static byte[] BuildQuery(string host)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x56);
        stream.WriteByte(0x50);
        stream.WriteByte(0x01);
        stream.WriteByte(0x00);
        stream.WriteByte(0x00);
        stream.WriteByte(0x01);
        stream.Write(new byte[6]);
        foreach (var label in host.Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0x00);
        stream.WriteByte(0x00);
        stream.WriteByte(0x01);
        stream.WriteByte(0x00);
        stream.WriteByte(0x01);
        return stream.ToArray();
    }

    private static IPAddress? ReadFirstARecord(byte[] message)
    {
        if (message.Length < 12) throw new InvalidDataException("Неполный DNS-ответ OpenDNS.");
        var questions = ReadUInt16(message, 4);
        var answers = ReadUInt16(message, 6);
        var offset = 12;
        for (var index = 0; index < questions; index++)
        {
            offset = SkipName(message, offset) + 4;
            if (offset > message.Length) throw new InvalidDataException("Некорректный DNS-вопрос OpenDNS.");
        }
        for (var index = 0; index < answers; index++)
        {
            offset = SkipName(message, offset);
            if (offset + 10 > message.Length) throw new InvalidDataException("Некорректный DNS-ответ OpenDNS.");
            var type = ReadUInt16(message, offset);
            var recordClass = ReadUInt16(message, offset + 2);
            var length = ReadUInt16(message, offset + 8);
            offset += 10;
            if (offset + length > message.Length) throw new InvalidDataException("Некорректная длина DNS-записи OpenDNS.");
            if (type == 1 && recordClass == 1 && length == 4)
                return new IPAddress(message.AsSpan(offset, 4));
            offset += length;
        }
        return null;
    }

    private static int SkipName(byte[] message, int offset)
    {
        while (offset < message.Length)
        {
            var length = message[offset++];
            if (length == 0) return offset;
            if ((length & 0xC0) == 0xC0)
            {
                if (offset >= message.Length) throw new InvalidDataException("Неполное DNS-имя OpenDNS.");
                return offset + 1;
            }
            if ((length & 0xC0) != 0 || offset + length > message.Length)
                throw new InvalidDataException("Некорректное DNS-имя OpenDNS.");
            offset += length;
        }
        throw new InvalidDataException("DNS-имя OpenDNS не завершено.");
    }

    private static ushort ReadUInt16(byte[] message, int offset) => (ushort)((message[offset] << 8) | message[offset + 1]);

    private static async Task VerifyTlsAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, 443, cancellationToken);
        await using var stream = new SslStream(client.GetStream(), false);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = ThumbnailHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, cancellationToken);
    }
}
