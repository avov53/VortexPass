using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.NetworkInformation;
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
    private sealed record DnsProvider(string Id, string Name, string Url, bool WireFormat = false);

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
        new("cloudflare", "Cloudflare DNS", "https://cloudflare-dns.com/dns-query"),
        new("google", "Google DNS", "https://dns.google/resolve"),
        new("quad9", "Quad9 DNS", "https://dns.quad9.net/dns-query", WireFormat: true)
    ];
    private static readonly ConcurrentDictionary<string, CachedResolution> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient ProviderClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        EnableMultipleHttp2Connections = true
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    static DnsOverrideService() => NetworkChange.NetworkAddressChanged += (_, _) => Cache.Clear();

    public static void InvalidateCache() => Cache.Clear();

    public async Task<DnsOverrideResult> ResolveBestAsync(
        string host,
        bool verifyTls = true,
        string? httpProbeUrl = null,
        TimeSpan? timeout = null,
        string? preferredProvider = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        var timeoutValue = timeout ?? TimeSpan.FromSeconds(5);
        var errors = new List<string>();

        var cacheKey = $"{normalizedHost}|{httpProbeUrl ?? string.Empty}|{verifyTls}|{preferredProvider ?? "auto"}";
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Result;

        var normalizedProvider = preferredProvider?.Trim().ToLowerInvariant() ?? "auto";
        var activeProviders = normalizedProvider == "auto"
            ? Providers
            : Providers.Where(provider => provider.Id == normalizedProvider).ToArray();
        if (activeProviders.Length == 0) activeProviders = Providers;

        var attempts = new List<Task<DnsOverrideResult>>();
        if (normalizedProvider == "auto")
        {
            attempts.Add(TrySystemDnsAsync(normalizedHost, verifyTls, httpProbeUrl, timeoutValue, cancellationToken));
        }
        attempts.AddRange(activeProviders.Select(provider =>
            TryProviderAsync(provider, normalizedHost, verifyTls, httpProbeUrl, timeoutValue, cancellationToken)));
        var results = await Task.WhenAll(attempts);
        var best = SelectFastest(results);
        if (best is not null)
        {
            Cache[cacheKey] = new(best, DateTimeOffset.UtcNow.AddMinutes(10));
            return best;
        }
        errors.AddRange(results.Select(result => result.Detail));
        var failed = new DnsOverrideResult(normalizedHost, null, "none", string.Join("; ", errors.Take(4)));
        Cache[cacheKey] = new(failed, DateTimeOffset.UtcNow.AddSeconds(30));
        return failed;
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
            var watch = Stopwatch.StartNew();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            var addresses = await Dns.GetHostAddressesAsync(host, linked.Token);
            var result = await ValidateAddressesAsync(host, addresses, "System DNS", verifyTls, httpProbeUrl, timeout, linked.Token);
            watch.Stop();
            return result with { LatencyMs = result.Succeeded ? watch.ElapsedMilliseconds : null };
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
            var watch = Stopwatch.StartNew();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout + TimeSpan.FromSeconds(2));
            using var request = provider.WireFormat
                ? new HttpRequestMessage(HttpMethod.Post, provider.Url)
                : new HttpRequestMessage(HttpMethod.Get, $"{provider.Url}?name={Uri.EscapeDataString(host)}&type=A");
            if (provider.WireFormat)
            {
                request.Content = new ByteArrayContent(BuildDnsWireQuery(host));
                request.Content.Headers.ContentType = new("application/dns-message");
                request.Headers.Accept.ParseAdd("application/dns-message");
            }
            else
            {
                request.Headers.Accept.ParseAdd("application/dns-json");
            }
            using var response = await ProviderClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
                return new(host, null, provider.Name, $"{provider.Name} returned HTTP {(int)response.StatusCode}");

            IPAddress[] addresses;
            if (provider.WireFormat)
            {
                addresses = ParseDnsWireAddresses(await response.Content.ReadAsByteArrayAsync(linked.Token)).ToArray();
            }
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
                var payload = await JsonSerializer.DeserializeAsync<DnsJsonResponse>(stream, cancellationToken: linked.Token);
                if (payload?.Status != 0 || payload.Answer is null)
                    return new(host, null, provider.Name, $"{provider.Name} returned no successful A response");
                addresses = payload.Answer
                    .Where(answer => answer.Type == 1 && IPAddress.TryParse(answer.Data, out _))
                    .Select(answer => IPAddress.Parse(answer.Data))
                    .ToArray();
                if (addresses.Length == 0)
                {
                    var cname = payload.Answer.FirstOrDefault(answer => answer.Type == 5)?.Data?.Trim().TrimEnd('.');
                    if (!string.IsNullOrWhiteSpace(cname) && !cname.Equals(host, StringComparison.OrdinalIgnoreCase))
                        return await TryProviderAsync(provider, cname, verifyTls, httpProbeUrl, timeout, cancellationToken);
                }
            }

            var result = await ValidateAddressesAsync(host, addresses, provider.Name, verifyTls, httpProbeUrl, timeout, linked.Token);
            watch.Stop();
            return result with { LatencyMs = result.Succeeded ? watch.ElapsedMilliseconds : null };
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
            AllowAutoRedirect = false,
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

    internal static DnsOverrideResult? SelectFastest(IEnumerable<DnsOverrideResult> results) => results
        .Where(result => result.Succeeded)
        .OrderBy(result => result.LatencyMs ?? long.MaxValue)
        .FirstOrDefault();

    internal static void ClearCacheForTests() => Cache.Clear();
    internal static int CachedEntryCountForTests => Cache.Count;

    internal static byte[] BuildDnsWireQuery(string host)
    {
        using var stream = new MemoryStream();
        // Transaction ID 0 is valid for DoH because the HTTPS request already
        // correlates the response. RD=1, QDCOUNT=1.
        stream.Write(new byte[] { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in host.Trim('.').Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Некорректное DNS-имя.", nameof(host));
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.Write(new byte[] { 0, 0, 1, 0, 1 }); // root, QTYPE=A, QCLASS=IN
        return stream.ToArray();
    }

    internal static IReadOnlyList<IPAddress> ParseDnsWireAddresses(byte[] message)
    {
        if (message.Length < 12 || (message[3] & 0x0f) != 0) return [];
        var questions = ReadUInt16(message, 4);
        var answers = ReadUInt16(message, 6);
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            if (!SkipDnsName(message, ref offset) || offset + 4 > message.Length) return [];
            offset += 4;
        }
        var result = new List<IPAddress>();
        for (var i = 0; i < answers; i++)
        {
            if (!SkipDnsName(message, ref offset) || offset + 10 > message.Length) break;
            var type = ReadUInt16(message, offset);
            var dataLength = ReadUInt16(message, offset + 8);
            offset += 10;
            if (offset + dataLength > message.Length) break;
            if (type == 1 && dataLength == 4)
                result.Add(new IPAddress(message.AsSpan(offset, 4)));
            offset += dataLength;
        }
        return result;
    }

    private static bool SkipDnsName(byte[] message, ref int offset)
    {
        while (offset < message.Length)
        {
            var length = message[offset++];
            if (length == 0) return true;
            if ((length & 0xc0) == 0xc0)
            {
                if (offset >= message.Length) return false;
                offset++;
                return true;
            }
            if (length > 63 || offset + length > message.Length) return false;
            offset += length;
        }
        return false;
    }

    private static ushort ReadUInt16(byte[] value, int offset) =>
        (ushort)((value[offset] << 8) | value[offset + 1]);
}
