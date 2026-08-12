using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VortexPass.Core.Services;

public enum ServiceHealthState { NotSelected, WinwsStopped, Checking, Healthy, DnsUnavailable, TcpTimeout, TlsFailed, HttpUnavailable }

public sealed record ServiceProbeStages(
    string Dns,
    string Tcp,
    string Tls,
    string Http,
    string? Voice = null,
    string? Gateway = null,
    string? Cdn = null);

public sealed record ServiceHealthResult(
    string Id,
    ServiceHealthState State,
    string Detail,
    long? LatencyMs = null,
    ServiceProbeStages? Stages = null)
{
    public bool IsHealthy => State == ServiceHealthState.Healthy;
}

public sealed class ServiceHealthCheckService
{
    private sealed record Target(string Host, string Url, string? VoiceHost = null);
    private sealed record TcpEndpoint(IPAddress Address, int Port);

    private static readonly TcpEndpoint[] TelegramDcEndpoints =
    [
        new(IPAddress.Parse("149.154.167.51"), 443),
        new(IPAddress.Parse("149.154.167.91"), 443),
        new(IPAddress.Parse("91.108.56.130"), 443),
        new(IPAddress.Parse("149.154.167.51"), 5222),
        new(IPAddress.Parse("149.154.167.91"), 5222)
    ];

    private static readonly IReadOnlyDictionary<string, Target> Targets = new Dictionary<string, Target>
    {
        ["youtube"] = new("www.youtube.com", "https://www.youtube.com/generate_204"),
        ["discord"] = new("discord.com", "https://discord.com/api/v10/gateway", "discord.media"),
        ["telegram"] = new("web.telegram.org", "https://web.telegram.org/"),
        ["roblox"] = new("thumbnails.roblox.com", "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=1&size=48x48&format=Png&isCircular=false")
    };

    public async Task<ServiceHealthResult> CheckAsync(
        string id,
        bool selected,
        bool winwsRunning,
        CancellationToken cancellationToken,
        bool appSelected = false,
        bool webSelected = false,
        bool telegramProxyRunning = false,
        bool telegramProxyHasClientActivity = false)
    {
        if (!selected) return new(id, ServiceHealthState.NotSelected, "Не выбран");
        if (!winwsRunning) return new(id, ServiceHealthState.WinwsStopped, "winws остановлен");
        if (!Targets.TryGetValue(id, out var target)) throw new ArgumentOutOfRangeException(nameof(id));
        if (id == "telegram" && !appSelected && !webSelected)
        {
            appSelected = true;
            webSelected = true;
        }

        var dns = "не проверен";
        var tcp = "не проверен";
        var tls = "не проверен";
        var http = "не проверен";
        var voice = target.VoiceHost is null ? null : "не проверен";
        string? gateway = id is "discord" or "telegram" ? "не проверен" : null;
        string? cdn = id == "discord" ? "не проверен" : null;
        IPAddress? address = null;
        Exception? dnsFailure = null;
        var secureDnsFallback = false;
        long? transportLatencyMs = null;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target.Host, cancellationToken);
            address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetworkV6);
            dns = address is null ? "DNS не вернул IP" : $"OK: {address}";
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dnsFailure = ex;
            dns = $"недоступен: {ShortError(ex)}";
        }

        if (address is null && dnsFailure is not null)
        {
            address = await ResolveWithSecureDnsAsync(target.Host, cancellationToken);
            if (address is not null)
            {
                secureDnsFallback = true;
                dns = $"системный DNS недоступен; Secure DNS OK: {address}";
            }
        }

        ServiceHealthState? transportFailure = null;
        if (address is not null)
        {
            try
            {
                using var socketTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                socketTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var client = new TcpClient(address.AddressFamily);
                var transportWatch = Stopwatch.StartNew();
                await client.ConnectAsync(address, 443, socketTimeout.Token);
                transportWatch.Stop();
                transportLatencyMs = transportWatch.ElapsedMilliseconds;
                tcp = "OK: 443";
                await using var stream = new SslStream(client.GetStream(), false);
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = target.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, socketTimeout.Token);
                tls = $"OK: {stream.SslProtocol}";
            }
            catch (OperationCanceledException ex)
            {
                tcp = $"тайм-аут: {ShortError(ex)}";
                transportFailure = ServiceHealthState.TcpTimeout;
            }
            catch (SocketException ex)
            {
                tcp = $"ошибка: {ex.SocketErrorCode}";
                transportFailure = ServiceHealthState.TcpTimeout;
            }
            catch (AuthenticationException ex)
            {
                tls = $"ошибка: {ShortError(ex)}";
                transportFailure = ServiceHealthState.TlsFailed;
            }
            catch (Exception ex)
            {
                tls = $"ошибка: {ShortError(ex)}";
                transportFailure = ServiceHealthState.TlsFailed;
            }
        }
        else
        {
            tcp = "пропущен: системный DNS не вернул адрес";
            tls = "пропущен: нет адреса";
        }

        if (target.VoiceHost is not null)
            voice = await CheckVoiceEndpointAsync(target.VoiceHost, cancellationToken);
        if (id == "discord")
        {
            var auxiliary = await Task.WhenAll(
                CheckGatewayEndpointAsync(cancellationToken),
                CheckCdnEndpointAsync(cancellationToken));
            gateway = auxiliary[0];
            cdn = auxiliary[1];
        }
        if (id == "telegram" && appSelected)
        {
            gateway = telegramProxyRunning
                ? await CheckTelegramLocalProxyAsync(telegramProxyHasClientActivity, cancellationToken)
                : await CheckTelegramDcEndpointAsync(cancellationToken);
            if (appSelected && !webSelected)
            {
                http = "пропущен: выбран Telegram Desktop";
                var desktopStages = new ServiceProbeStages(dns, tcp, tls, http, voice, gateway, cdn);
                return gateway.StartsWith("OK", StringComparison.Ordinal)
                    ? new(id, ServiceHealthState.Healthy, telegramProxyRunning ? "Telegram Desktop подключён к локальному WebSocket-прокси" : $"Telegram Desktop/DC доступен: {gateway}", null, desktopStages)
                    : new(id, ServiceHealthState.TcpTimeout, $"Telegram Desktop недоступен: {gateway}", null, desktopStages);
            }
        }

        try
        {
            using var handler = CreateHttpHandler(address, secureDnsFallback);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(9) };
            using var response = await client.GetAsync(target.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            http = $"OK: HTTP {(int)response.StatusCode}";
            if ((int)response.StatusCode < 500)
            {
                if (id == "telegram")
                {
                    var telegramStages = new ServiceProbeStages(dns, tcp, tls, http, voice, gateway, cdn);
                    if (webSelected && appSelected && gateway?.StartsWith("OK", StringComparison.Ordinal) != true)
                        return new(id, ServiceHealthState.HttpUnavailable, $"Telegram Web доступен; Telegram Desktop не подключён: {gateway}", transportLatencyMs, telegramStages);

                    var telegramDetail = appSelected
                        ? telegramProxyRunning
                            ? "Telegram Web доступен; Desktop подключён к локальному WebSocket-прокси"
                            : "Telegram Web и Desktop/DC доступны"
                        : secureDnsFallback
                            ? "Работает через Secure DNS; системный DNS Windows недоступен"
                            : "Telegram Web доступен";
                    return new(id, ServiceHealthState.Healthy, telegramDetail, transportLatencyMs, telegramStages);
                }

                var discordAuxiliaryFailed = id == "discord" &&
                    (gateway?.StartsWith("OK", StringComparison.Ordinal) != true || cdn?.StartsWith("OK", StringComparison.Ordinal) != true);
                var detail = id == "discord"
                    ? discordAuxiliaryFailed
                        ? $"API работает; Gateway: {gateway}; CDN: {cdn}"
                        : voice?.StartsWith("OK", StringComparison.Ordinal) == true
                            ? "API, Gateway, CDN и voice доступны"
                            : $"API, Gateway и CDN доступны; voice: {voice}"
                    : secureDnsFallback
                        ? "Работает через Secure DNS; системный DNS Windows недоступен"
                        : "Работает";
                var successStages = new ServiceProbeStages(dns, tcp, tls, http, voice, gateway, cdn);
                if (discordAuxiliaryFailed)
                    return new(id, ServiceHealthState.HttpUnavailable, detail, null, successStages);
                return new(id, ServiceHealthState.Healthy, detail, transportLatencyMs,
                    successStages);
            }
        }
        catch (TaskCanceledException ex)
        {
            http = $"тайм-аут: {ShortError(ex)}";
        }
        catch (HttpRequestException ex)
        {
            http = $"ошибка: {ShortError(ex)}";
        }
        catch (Exception ex)
        {
            http = $"ошибка: {ShortError(ex)}";
        }

        var stages = new ServiceProbeStages(dns, tcp, tls, http, voice, gateway, cdn);
        if (id == "telegram" && appSelected && gateway?.StartsWith("OK", StringComparison.Ordinal) == true)
            return new(id, ServiceHealthState.HttpUnavailable, $"Telegram Desktop/DC доступен; Telegram Web недоступен: {http}", null, stages);
        if (dnsFailure is not null)
            return new(id, ServiceHealthState.DnsUnavailable, $"Системный DNS недоступен; Secure DNS браузера проверяется отдельно. HTTP: {http}", null, stages);
        if (transportFailure == ServiceHealthState.TcpTimeout)
            return new(id, ServiceHealthState.TcpTimeout, $"TCP 443 недоступен; HTTP: {http}", null, stages);
        if (transportFailure == ServiceHealthState.TlsFailed)
            return new(id, ServiceHealthState.TlsFailed, $"TLS не установлен; HTTP: {http}", null, stages);
        return new(id, ServiceHealthState.HttpUnavailable, $"HTTP/WebSocket-совместимая точка недоступна: {http}", null, stages);
    }

    private static async Task<string> CheckVoiceEndpointAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetworkV6);
            if (address is null) return "DNS не вернул IP";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, 443, timeout.Token);
            return $"OK: {address}:443; UDP захватывается winws";
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return $"недоступен: {ShortError(ex)}";
        }
    }

    private static async Task<string> CheckTelegramLocalProxyAsync(bool hasClientActivity, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, TelegramDesktopProxyService.Port, timeout.Token);
            return hasClientActivity
                ? $"OK: WS Proxy 127.0.0.1:{TelegramDesktopProxyService.Port}; Telegram подключался"
                : $"локальный WS Proxy запущен на 127.0.0.1:{TelegramDesktopProxyService.Port}, но Telegram Desktop ещё не подключался";
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return $"локальный WS Proxy недоступен: {ShortError(ex)}";
        }
    }

    private static async Task<string> CheckGatewayEndpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(7));
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("User-Agent", "VortexPass/0.1");
            await socket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), timeout.Token);
            return socket.State == WebSocketState.Open ? "OK: WebSocket" : $"состояние: {socket.State}";
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            return $"недоступен: {ShortError(ex)}";
        }
    }

    private static async Task<string> CheckCdnEndpointAsync(CancellationToken cancellationToken)
    {
        var endpoints = new[]
        {
            ("cdn", "https://cdn.discordapp.com/embed/avatars/0.png"),
            ("media", "https://media.discordapp.net/embed/avatars/0.png?size=64"),
            ("proxy-1", "https://images-ext-1.discordapp.net/"),
            ("proxy-2", "https://images-ext-2.discordapp.net/")
        };
        var checks = await Task.WhenAll(endpoints.Select(endpoint =>
            CheckDiscordAssetEndpointAsync(endpoint.Item1, endpoint.Item2, cancellationToken)));
        var detail = string.Join("; ", checks.Select(check => check.Detail));
        return checks.All(check => check.Success) ? $"OK: {detail}" : $"частично недоступен: {detail}";
    }

    private static async Task<EndpointCheck> CheckDiscordAssetEndpointAsync(
        string name,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var success = (int)response.StatusCode < 500;
            return new EndpointCheck(success, $"{name}=HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new EndpointCheck(false, $"{name}={ShortError(ex)}");
        }
    }

    private sealed record EndpointCheck(bool Success, string Detail);

    private static async Task<string> CheckTelegramDcEndpointAsync(CancellationToken cancellationToken)
    {
        var checks = await Task.WhenAll(TelegramDcEndpoints.Select(endpoint =>
            CheckTcpEndpointAsync(endpoint.Address, endpoint.Port, cancellationToken)));
        var success = checks.FirstOrDefault(check => check.Success);
        if (success is not null) return $"OK: {success.Detail}";
        return string.Join("; ", checks.Select(check => check.Detail).Take(5));
    }

    private static async Task<EndpointCheck> CheckTcpEndpointAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(2500));
            using var client = new TcpClient(address.AddressFamily);
            var watch = Stopwatch.StartNew();
            await client.ConnectAsync(address, port, timeout.Token);
            watch.Stop();
            return new(true, $"{address}:{port} {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return new(false, $"{address}:{port} {ShortError(ex)}");
        }
    }

    private static HttpMessageHandler CreateHttpHandler(IPAddress? address, bool usePinnedAddress)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };

        if (usePinnedAddress && address is not null)
        {
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var client = new TcpClient(address.AddressFamily);
                try
                {
                    await client.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                    return client.GetStream();
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            };
        }

        return handler;
    }

    private static async Task<IPAddress?> ResolveWithSecureDnsAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync<SecureDnsResponse>(content, cancellationToken: cancellationToken);
            if (result?.Status != 0 || result.Answer is null) return null;

            foreach (var answer in result.Answer)
            {
                if (answer.Type == 1 && IPAddress.TryParse(answer.Data, out var address))
                    return address;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

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

    private static string ShortError(Exception exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        return string.IsNullOrWhiteSpace(message) ? exception.GetType().Name : message.Replace(Environment.NewLine, " ");
    }
}
