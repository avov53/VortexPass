using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace VortexPass.Core.Services;

public sealed record StageLatency(
    long DnsMs,
    long TcpMs,
    long TlsMs,
    long? HttpMs = null,
    long? VoiceRttMs = null,
    string? Protocol = null,
    string? ResolvedIp = null,
    string? DnsProvider = null);

public sealed record LatencyStats(
    long MinMs,
    long MaxMs,
    long AvgMs,
    long JitterMs,
    long LastMs,
    bool HasSpike,
    string? SpikeReason = null);

public sealed class NetworkDiagnosticsService
{
    private sealed class HostHistory
    {
        public readonly List<long> Samples = new(32);
        public long PreviousLatency = -1;
    }

    private static readonly ConcurrentDictionary<string, HostHistory> Histories = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<(StageLatency Latency, LatencyStats Stats)> DiagnoseHostAsync(
        string host,
        string? url = null,
        string? preferredDns = "auto",
        CancellationToken cancellationToken = default)
    {
        var dnsWatch = Stopwatch.StartNew();
        IPAddress? address = null;
        string dnsProvider = "System DNS";
        
        try
        {
            if (preferredDns is "google")
            {
                dnsProvider = "Google DoH";
                var doh = await new DnsOverrideService().ResolveBestAsync(host, verifyTls: false, preferredProvider: "google", cancellationToken: cancellationToken);
                address = doh.Address;
            }
            else if (preferredDns is "cloudflare")
            {
                dnsProvider = "Cloudflare DoH";
                var doh = await new DnsOverrideService().ResolveBestAsync(host, verifyTls: false, preferredProvider: "cloudflare", cancellationToken: cancellationToken);
                address = doh.Address;
            }
            else
            {
                var addrs = await Dns.GetHostAddressesAsync(host, cancellationToken);
                address = addrs.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                          ?? addrs.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetworkV6);
                if (address is null)
                {
                    dnsProvider = "Cloudflare DoH (Fallback)";
                    var doh = await new DnsOverrideService().ResolveBestAsync(host, verifyTls: false, preferredProvider: "cloudflare", cancellationToken: cancellationToken);
                    address = doh.Address;
                }
            }
        }
        catch
        {
            dnsProvider = "Cloudflare DoH (Fallback)";
            var doh = await new DnsOverrideService().ResolveBestAsync(host, verifyTls: false, preferredProvider: "cloudflare", cancellationToken: cancellationToken);
            address = doh.Address;
        }
        dnsWatch.Stop();
        var dnsMs = dnsWatch.ElapsedMilliseconds;

        if (address is null)
        {
            var failedStage = new StageLatency(dnsMs, -1, -1, null, null, null, null, dnsProvider);
            return (failedStage, new LatencyStats(-1, -1, -1, 0, -1, false, "DNS резолвинг не вернул IP адрес"));
        }

        long tcpMs = -1;
        long tlsMs = -1;
        string? protocol = null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

            using var client = new TcpClient(address.AddressFamily);
            var tcpWatch = Stopwatch.StartNew();
            await client.ConnectAsync(address, 443, timeoutCts.Token);
            tcpWatch.Stop();
            tcpMs = tcpWatch.ElapsedMilliseconds;

            await using var sslStream = new SslStream(client.GetStream(), false);
            var tlsWatch = Stopwatch.StartNew();
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, timeoutCts.Token);
            tlsWatch.Stop();
            tlsMs = tlsWatch.ElapsedMilliseconds;
            protocol = sslStream.SslProtocol.ToString();
        }
        catch (Exception ex)
        {
            var errStage = new StageLatency(dnsMs, tcpMs, tlsMs, null, null, protocol, address.ToString(), dnsProvider);
            return (errStage, new LatencyStats(tcpMs, tcpMs, tcpMs, 0, tcpMs, true, $"Сбой транспорта: {ex.Message}"));
        }

        long? httpMs = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                using var httpTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                httpTimeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
                var httpWatch = Stopwatch.StartNew();
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, httpTimeoutCts.Token);
                httpWatch.Stop();
                httpMs = httpWatch.ElapsedMilliseconds;
            }
            catch { }
        }

        var totalLatency = tcpMs + (tlsMs > 0 ? tlsMs : 0);
        var stats = RecordAndAnalyze(host, totalLatency, dnsMs, tcpMs, tlsMs, httpMs);
        var stage = new StageLatency(dnsMs, tcpMs, tlsMs, httpMs, null, protocol, address.ToString(), dnsProvider);

        return (stage, stats);
    }

    public static async Task<long?> MeasureDiscordMediaRttAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync("discord.media", cancellationToken);
            var addr = addrs.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
            if (addr is null) return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

            using var client = new TcpClient(addr.AddressFamily);
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(addr, 443, timeoutCts.Token);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    private static LatencyStats RecordAndAnalyze(string host, long currentMs, long dnsMs, long tcpMs, long tlsMs, long? httpMs)
    {
        var history = Histories.GetOrAdd(host, _ => new HostHistory());
        long jitter = 0;
        bool hasSpike = false;
        string? spikeReason = null;

        lock (history.Samples)
        {
            if (history.PreviousLatency >= 0)
            {
                jitter = Math.Abs(currentMs - history.PreviousLatency);
            }

            var avgBefore = history.Samples.Count > 0 ? (long)history.Samples.Average() : currentMs;

            if (history.Samples.Count >= 3)
            {
                if (currentMs > avgBefore * 2.2 && currentMs > 100)
                {
                    hasSpike = true;
                    if (dnsMs > 100)
                        spikeReason = $"Задержка DNS ответа ({dnsMs} ms) от провайдера";
                    else if (tcpMs > avgBefore * 1.8)
                        spikeReason = $"Задержка TCP Handshake ({tcpMs} ms) — сетевой буфер/ретрай пакетов у провайдера";
                    else if (tlsMs > 150)
                        spikeReason = $"Задержка согласования TLS ({tlsMs} ms) — дроп пакетов рукопожатия ТСПУ";
                    else
                        spikeReason = $"Скачок сетевой задержки маршрута (+{currentMs - avgBefore} ms, джиттер: {jitter} ms)";
                }
            }

            history.Samples.Add(currentMs);
            if (history.Samples.Count > 30) history.Samples.RemoveAt(0);
            history.PreviousLatency = currentMs;

            var min = history.Samples.Min();
            var max = history.Samples.Max();
            var avg = (long)history.Samples.Average();

            return new LatencyStats(min, max, avg, jitter, currentMs, hasSpike, spikeReason);
        }
    }
}
