using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace VortexPass.Core.Services;

public sealed record TelegramRouteCandidate(string Host, IPAddress Address, bool Confirmed, string Group, string Detail, long? LatencyMs = null);

public sealed class TelegramRouteProbeService
{
    private static readonly SemaphoreSlim ProbeGate = new(4);
    public static bool IsWebSocketHost(string host) => TelegramEndpointRegistry.IsTransport(host);
    public static SocketsHttpHandler PinnedHandler(IPAddress address) => new()
    {
        UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3),
        ConnectCallback = async (context, token) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        }
    };

    public async Task<TelegramRouteCandidate> ProbeAsync(string host, IPAddress address, CancellationToken token)
    {
        var group = TelegramEndpointRegistry.IsMediaTransport(host) ? "mediaTransport" : IsWebSocketHost(host) ? "messages" : host.Contains("telesco.pe") ? "media" : "web";
        var watch = Stopwatch.StartNew();
        await ProbeGate.WaitAsync(token);
        try
        {
            // Both attempts use the original hostname for Host, SNI and certificate validation.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                using var handler = PinnedHandler(address);
                if (IsWebSocketHost(host))
                {
                    var result = await TelegramWebSocketProbe.CheckAsync(host, timeout.Token, address);
                    if (!result.Confirmed) return new(host, address, false, group, result.Detail);
                }
                else
                {
                    using var client = new HttpClient(handler, false);
                    using var response = await client.GetAsync($"https://{host}/" + (host == "web.telegram.org" ? "k/" : ""), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                    var buffer = new byte[512]; var received = await body.ReadAsync(buffer, timeout.Token);
                    var confirmed = group == "web"
                        ? RouteProbePolicy.IsUsablePage((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, received)
                        : RouteProbePolicy.IsImage((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, buffer.AsSpan(0, received));
                    if (!confirmed) return new(host, address, false, group, "Ответ не подтвердил требуемый контент; DNS-маршрут сохранён");
                }
            }
            return new(host, address, true, group, "TLS и маршрут подтверждены дважды", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or SocketException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); return new(host, address, false, group,
            ex is OperationCanceledException ? "Тайм-аут TLS/HTTP/WebSocket" : $"TLS/HTTP/WebSocket: {ex.GetType().Name}"); }
        finally { ProbeGate.Release(); }
    }

    public static IReadOnlyList<TelegramRouteCandidate> SelectConfirmed(IEnumerable<TelegramRouteCandidate> candidates) => candidates
        .Where(c => c.Confirmed).GroupBy(c => c.Host, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderBy(c => c.LatencyMs ?? long.MaxValue).First()).ToArray();
}
