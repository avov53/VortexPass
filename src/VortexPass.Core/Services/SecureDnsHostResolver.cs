using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VortexPass.Core.Services;

public sealed record SecureDnsHostResolution(string Host, IPAddress? Address, string Detail)
{
    public bool Succeeded => Address is not null;
}

public sealed class SecureDnsHostResolver
{
    private sealed class SecureDnsResponse
    {
        public int Status { get; init; }
        public List<SecureDnsAnswer>? Answer { get; init; }
    }

    private sealed class SecureDnsAnswer
    {
        [JsonPropertyName("type")]
        public int Type { get; init; }

        [JsonPropertyName("data")]
        public string Data { get; init; } = string.Empty;
    }

    public async Task<SecureDnsHostResolution> ResolveAsync(string host, bool verifyTls = true, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A");
            request.Headers.Accept.ParseAdd("application/dns-json");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new(host, null, $"Cloudflare DNS returned HTTP {(int)response.StatusCode}");

            await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
            var result = await JsonSerializer.DeserializeAsync<SecureDnsResponse>(content, cancellationToken: timeout.Token);
            if (result?.Status != 0 || result.Answer is null)
                return new(host, null, "Cloudflare DNS did not return a successful A record response");

            foreach (var answer in result.Answer)
            {
                if (answer.Type != 1 || !IPAddress.TryParse(answer.Data, out var address))
                    continue;

                if (verifyTls)
                    await VerifyTlsAsync(host, address, timeout.Token);

                return new(host, address, $"Cloudflare DNS and TLS OK: {address}");
            }

            return new(host, null, "Cloudflare DNS returned no IPv4 address");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or AuthenticationException or OperationCanceledException or JsonException)
        {
            return new(host, null, ex.InnerException?.Message ?? ex.Message);
        }
    }

    private static async Task VerifyTlsAsync(string host, IPAddress address, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, 443, cancellationToken);
        await using var stream = new SslStream(client.GetStream(), false);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        }, cancellationToken);
    }
}
