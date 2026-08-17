using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VortexPass.Core.Services;

public sealed record DnsOverrideResult(string Host, IPAddress? Address, string Source, string Detail, long? LatencyMs = null)
{
    public bool Succeeded => Address is not null;
}

public sealed class DnsOverrideService
{
    private sealed record CachedResolution(DnsOverrideResult Result, DateTimeOffset ExpiresAt);
    private sealed record DnsProvider(string Name, string Url);

    private sealed class DnsJsonResponse
    {
        public int Status { get; init; }
        public List<DnsJsonAnswer>? Answer { get; init; }
    }

    private sealed class DnsJsonAnswer
    {
        [JsonPropertyName("type")]
        public int Type { get; init; }

        [JsonPropertyName("data")]
        public string Data { get; init; } = string.Empty;
    }

    private static readonly DnsProvider[] Providers =
    [
        new("Cloudflare DNS", "https://1.1.1.1/dns-query"),
        new("Google DNS", "https://dns.google/resolve"),
        new("OpenDNS", "https://doh.opendns.com/dns-query")
    ];
    private static readonly ConcurrentDictionary<string, CachedResolution> Cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DnsOverrideResult> ResolveBestAsync(
        string host,
        bool verifyTls = true,
        string? httpProbeUrl = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        var timeoutValue = timeout ?? TimeSpan.FromSeconds(5);
        var errors = new List<string>();

        var cacheKey = $"{normalizedHost}|{httpProbeUrl ?? string.Empty}|{verifyTls}";
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Result;

        var system = await TrySystemDnsAsync(normalizedHost, verifyTls, httpProbeUrl, timeoutValue, cancellationToken);
        if (system.Succeeded)
        {
            Cache[cacheKey] = new(system, DateTimeOffset.UtcNow.AddMinutes(10));
            return system;
        }
        errors.Add(system.Detail);

        var providerTasks = Providers
            .Select(provider => TryProviderAsync(provider, normalizedHost, verifyTls, httpProbeUrl, timeoutValue, cancellationToken))
            .ToArray();
        var providerResults = await Task.WhenAll(providerTasks);
        foreach (var result in providerResults)
        {
            if (result.Succeeded)
            {
                Cache[cacheKey] = new(result, DateTimeOffset.UtcNow.AddMinutes(10));
                return result;
            }
            errors.Add(result.Detail);
        }

        return new(normalizedHost, null, "none", string.Join("; ", errors.Take(4)));
    }

    private static async Task<DnsOverrideResult> TrySystemDnsAsync(
        string host,
        bool verifyTls,
        string? httpProbeUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            var addresses = await Dns.GetHostAddressesAsync(host, linked.Token);
            return await ValidateAddressesAsync(host, addresses, "System DNS", verifyTls, httpProbeUrl, timeout, linked.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(host, null, "System DNS", $"System DNS failed for {host}: {ShortError(ex)}");
        }
    }

    private static async Task<DnsOverrideResult> TryProviderAsync(
        DnsProvider provider,
        string host,
        bool verifyTls,
        string? httpProbeUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout + TimeSpan.FromSeconds(2));
            using var client = new HttpClient { Timeout = timeout + TimeSpan.FromSeconds(2) };
            var separator = provider.Url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{provider.Url}{separator}name={Uri.EscapeDataString(host)}&type=A");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
                return new(host, null, provider.Name, $"{provider.Name} returned HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            var payload = await JsonSerializer.DeserializeAsync<DnsJsonResponse>(stream, cancellationToken: linked.Token);
            if (payload?.Status != 0 || payload.Answer is null)
                return new(host, null, provider.Name, $"{provider.Name} returned no successful A response");

            var addresses = payload.Answer
                .Where(answer => answer.Type == 1 && IPAddress.TryParse(answer.Data, out _))
                .Select(answer => IPAddress.Parse(answer.Data))
                .ToArray();
            return await ValidateAddressesAsync(host, addresses, provider.Name, verifyTls, httpProbeUrl, timeout, linked.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or AuthenticationException or JsonException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(host, null, provider.Name, $"{provider.Name} failed for {host}: {ShortError(ex)}");
        }
    }

    private static async Task<DnsOverrideResult> ValidateAddressesAsync(
        string host,
        IEnumerable<IPAddress> addresses,
        string source,
        bool verifyTls,
        string? httpProbeUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        foreach (var address in addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork))
        {
            var watch = Stopwatch.StartNew();
            try
            {
                if (verifyTls)
                    await VerifyTlsAsync(host, address, timeout, cancellationToken);
                if (!string.IsNullOrWhiteSpace(httpProbeUrl))
                    await VerifyHttpAsync(host, address, httpProbeUrl, timeout, cancellationToken);
                watch.Stop();
                return new(host, address, source, $"{source} OK: {address}", watch.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is SocketException or AuthenticationException or HttpRequestException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return new(host, null, source, $"{source} returned no reachable IPv4 for {host}");
    }

    private static async Task VerifyTlsAsync(string host, IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, 443, linked.Token);
        await using var stream = new SslStream(client.GetStream(), false);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        }, linked.Token);
    }

    private static async Task VerifyHttpAsync(string host, IPAddress address, string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = timeout,
            ConnectCallback = async (context, token) =>
            {
                var client = new TcpClient(address.AddressFamily);
                try
                {
                    await client.ConnectAsync(address, context.DnsEndPoint.Port, token);
                    return client.GetStream();
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout + TimeSpan.FromSeconds(2));
        using var client = new HttpClient(handler) { Timeout = timeout + TimeSpan.FromSeconds(2) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Host = host;
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 VortexPass/0.3");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
    }

    private static string ShortError(Exception exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        return string.IsNullOrWhiteSpace(message) ? exception.GetType().Name : message.Replace(Environment.NewLine, " ");
    }
}
