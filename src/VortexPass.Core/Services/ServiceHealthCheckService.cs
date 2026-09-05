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
    private static readonly int[] PhotonUdpPorts = [5055, 5056, 5058, 27000, 27001, 27002];
    private static readonly int[] PhotonWebSocketPorts = [19090, 19093, 9090, 9093, 4530, 4533];

    private static readonly IReadOnlyDictionary<string, Target> Targets = new Dictionary<string, Target>
    {
        ["youtube"] = new("www.youtube.com", "https://www.youtube.com/generate_204"),
        ["discord"] = new("discord.com", "https://discord.com/api/v10/gateway", "discord.media"),
        ["telegram"] = new("web.telegram.org", "https://web.telegram.org/"),
        ["roblox"] = new("t1.rbxcdn.com", "https://t1.rbxcdn.com/")
    };
    private static readonly SecureDnsHostResolver SecureDnsResolver = new();
    private static readonly DnsOverrideService DnsOverrides = new();

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
        if (id.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase))
            return await CheckPhasmophobiaAsync(cancellationToken);
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
            var overrideResult = await DnsOverrides.ResolveBestAsync(target.Host, verifyTls: true, cancellationToken: cancellationToken);
            if (overrideResult.Succeeded && overrideResult.Address is not null)
            {
                address = overrideResult.Address;
                secureDnsFallback = !overrideResult.Source.Equals("System DNS", StringComparison.OrdinalIgnoreCase);
                dns = secureDnsFallback
                    ? $"системный DNS недоступен; {overrideResult.Source} OK: {address}"
                    : $"OK: {address}";
            }
        }

        ServiceHealthState? transportFailure = null;
        long tcpMs = -1;
        long tlsMs = -1;
        if (address is not null)
        {
            try
            {
                using var socketTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                socketTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var client = new TcpClient(address.AddressFamily);
                var tcpWatch = Stopwatch.StartNew();
                await client.ConnectAsync(address, 443, socketTimeout.Token);
                tcpWatch.Stop();
                tcpMs = tcpWatch.ElapsedMilliseconds;
                tcp = $"OK: 443 ({tcpMs} ms)";
                
                await using var stream = new SslStream(client.GetStream(), false);
                var tlsWatch = Stopwatch.StartNew();
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = target.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, socketTimeout.Token);
                tlsWatch.Stop();
                tlsMs = tlsWatch.ElapsedMilliseconds;
                tls = $"OK: {stream.SslProtocol} ({tlsMs} ms)";
                transportLatencyMs = tcpMs > 0 ? tcpMs : (tlsMs > 0 ? tlsMs : 0);
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

        long? voiceLatencyMs = null;
        if (target.VoiceHost is not null)
        {
            var voiceCheck = await CheckVoiceEndpointWithLatencyAsync(target.VoiceHost, cancellationToken);
            voice = voiceCheck.Detail;
            voiceLatencyMs = voiceCheck.LatencyMs;
        }
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
                if (id == "youtube")
                {
                    var auxiliary = await Task.WhenAll(
                        CheckYoutubeSearchEndpointAsync(cancellationToken),
                        CheckYoutubeAssetEndpointsAsync(cancellationToken));
                    var search = auxiliary[0];
                    var thumbnails = auxiliary[1];
                    http = $"{http}; Search=[{search.Detail}]; Thumbnails=[{thumbnails.Detail}]";
                    var youtubeStages = new ServiceProbeStages(dns, tcp, tls, http, voice, gateway, cdn);
                    var youtubeDetail = secureDnsFallback
                        ? "YouTube доступен через Secure DNS; поисковые подсказки и превью OK"
                        : "YouTube работает; системный DNS, поисковые подсказки и превью OK";
                    return new(id, ServiceHealthState.Healthy, youtubeDetail, transportLatencyMs, youtubeStages);
                }

                if (id == "telegram")
                {
                    var webAuxiliary = await CheckTelegramWebAuxiliaryAsync(cancellationToken);
                    http = $"{http}; Web/media=[{webAuxiliary.Detail}]";
                    var telegramStages = new ServiceProbeStages(dns, tcp, tls, http, voice, gateway, cdn);
                    var telegramDetail = appSelected
                        ? telegramProxyRunning
                            ? "Telegram Web доступен; Desktop подключён к локальному WebSocket-прокси"
                            : "Telegram Web и Desktop/DC доступны"
                        : secureDnsFallback
                            ? "Telegram Web работает через Secure DNS; Web/media маршруты доступны"
                            : "Telegram Web и Web/media маршруты доступны";
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
                var latencyToReport = (id == "discord" && voiceLatencyMs.HasValue) ? voiceLatencyMs : transportLatencyMs;
                return new(id, ServiceHealthState.Healthy, detail, latencyToReport,
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
        if (id == "telegram" && webSelected)
        {
            var webAuxiliary = await CheckTelegramWebAuxiliaryAsync(cancellationToken);
            if (webAuxiliary.Success)
            {
                var partialStages = new ServiceProbeStages(dns, tcp, tls, $"{http}; Web/media=[{webAuxiliary.Detail}]", voice, gateway, cdn);
                return new(id, ServiceHealthState.Healthy, $"Telegram Web доступен через альтернативные Web/media endpoints: {webAuxiliary.Detail}", webAuxiliary.LatencyMs, partialStages);
            }
        }
        if (id == "youtube" && transportLatencyMs.HasValue && tls.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            return new(id, ServiceHealthState.Healthy, $"YouTube доступен (TLS OK); HTTP-проверка: {http}", transportLatencyMs, stages);
        if (id == "telegram" && transportLatencyMs.HasValue && tls.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            return new(id, ServiceHealthState.Healthy, $"Telegram Web доступен (TLS OK); HTTP-проверка: {http}", transportLatencyMs, stages);
        if (id == "telegram" && appSelected && gateway?.StartsWith("OK", StringComparison.Ordinal) == true)
        {
            var detail = telegramProxyRunning
                ? "Telegram Desktop подключён к локальному WebSocket-прокси"
                : $"Telegram Desktop/DC доступен: {gateway}";
            return new(id, ServiceHealthState.Healthy, detail, transportLatencyMs ?? 2, stages);
        }
        if (address is null && dnsFailure is not null)
            return new(id, ServiceHealthState.DnsUnavailable, $"Системный DNS недоступен; Secure DNS браузера проверяется отдельно. HTTP: {http}", null, stages);
        if (transportFailure == ServiceHealthState.TcpTimeout)
            return new(id, ServiceHealthState.TcpTimeout, $"TCP 443 недоступен; HTTP: {http}", null, stages);
        if (transportFailure == ServiceHealthState.TlsFailed)
            return new(id, ServiceHealthState.TlsFailed, $"TLS не установлен; HTTP: {http}", null, stages);
        return new(id, ServiceHealthState.HttpUnavailable, $"HTTP/WebSocket-совместимая точка недоступна: {http}", null, stages);
    }

    private static async Task<EndpointCheck> CheckYoutubeSearchEndpointAsync(CancellationToken cancellationToken)
    {
        const string searchHost = "suggestqueries.google.com";
        const string searchUrl = "https://suggestqueries.google.com/complete/search?client=firefox&ds=yt&q=vortexpass";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(7));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
            using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 VortexPass/0.2");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var success = (int)response.StatusCode < 500;
            if (success)
                return new EndpointCheck(true, $"suggest=HTTP {(int)response.StatusCode}");

            var fallback = await CheckYoutubeSearchEndpointWithSecureDnsAsync(searchHost, searchUrl, cancellationToken);
            return fallback.Success
                ? new EndpointCheck(true, $"suggest=HTTP {(int)response.StatusCode}; SecureDNS=[{fallback.Detail}]")
                : new EndpointCheck(false, $"suggest=HTTP {(int)response.StatusCode}; SecureDNS=[{fallback.Detail}]");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            var fallback = await CheckYoutubeSearchEndpointWithSecureDnsAsync(searchHost, searchUrl, cancellationToken);
            return fallback.Success
                ? new EndpointCheck(true, $"suggest={ShortError(ex)}; SecureDNS=[{fallback.Detail}]")
                : new EndpointCheck(false, $"suggest={ShortError(ex)}; SecureDNS=[{fallback.Detail}]");
        }
    }

    private static async Task<EndpointCheck> CheckYoutubeSearchEndpointWithSecureDnsAsync(string host, string url, CancellationToken cancellationToken)
    {
        var resolution = await SecureDnsResolver.ResolveAsync(host, verifyTls: true, cancellationToken);
        if (!resolution.Succeeded || resolution.Address is null)
            return new EndpointCheck(false, resolution.Detail);

        try
        {
            using var handler = CreateHttpHandler(resolution.Address, usePinnedAddress: true);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 VortexPass/0.2");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var success = (int)response.StatusCode < 500;
            return new EndpointCheck(success, $"suggest=HTTP {(int)response.StatusCode} via {resolution.Address}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new EndpointCheck(false, ShortError(ex));
        }
    }

    private static async Task<EndpointCheck> CheckYoutubeAssetEndpointsAsync(CancellationToken cancellationToken)
    {
        var endpoints = new[]
        {
            ("thumb", "i.ytimg.com", "https://i.ytimg.com/vi/dQw4w9WgXcQ/default.jpg"),
            ("static", "s.ytimg.com", "https://s.ytimg.com/"),
            ("avatar", "yt3.ggpht.com", "https://yt3.ggpht.com/")
        };
        var checks = await Task.WhenAll(endpoints.Select(endpoint =>
            CheckPinnedHttpEndpointAsync(endpoint.Item1, endpoint.Item2, endpoint.Item3, TimeSpan.FromSeconds(6), cancellationToken)));
        var detail = string.Join("; ", checks.Select(check => check.Detail));
        var anySuccess = checks.Any(check => check.Success);
        var latency = checks.Where(x => x.LatencyMs.HasValue).Select(x => x.LatencyMs!.Value).DefaultIfEmpty().Max();
        if (anySuccess)
            return new EndpointCheck(true, $"thumbnails OK: {detail}", latency);
        return new EndpointCheck(false, $"thumbnails failed: {detail}");
    }

    private static async Task<EndpointCheck> CheckVoiceEndpointWithLatencyAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetworkV6);
            if (address is null) return new EndpointCheck(false, "DNS не вернул IP");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var client = new TcpClient(address.AddressFamily);
            var watch = Stopwatch.StartNew();
            await client.ConnectAsync(address, 443, timeout.Token);
            watch.Stop();
            var ms = watch.ElapsedMilliseconds;
            return new EndpointCheck(true, $"OK: {address}:443 ({ms} ms); UDP активен", ms);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return new EndpointCheck(false, $"недоступен: {ShortError(ex)}");
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

    private static async Task<EndpointCheck> CheckTelegramWebAuxiliaryAsync(CancellationToken cancellationToken)
    {
        var webEndpoints = new[]
        {
            ("main", "web.telegram.org", "https://web.telegram.org/"),
            ("webk", "webk.telegram.org", "https://webk.telegram.org/"),
            ("webz", "webz.telegram.org", "https://webz.telegram.org/")
        };
        var webTask = Task.WhenAll(webEndpoints.Select(endpoint =>
            CheckPinnedHttpEndpointWithRetryAsync(endpoint.Item1, endpoint.Item2, endpoint.Item3, TimeSpan.FromSeconds(6), cancellationToken)));
        var mediaTask = CheckPinnedHttpEndpointAsync(
            "media",
            "cdn.telesco.pe",
            "https://cdn.telesco.pe/",
            TimeSpan.FromSeconds(6),
            cancellationToken);
        var gatewayTask = Task.WhenAll(new[] { 2, 4 }.Select(dc =>
            CheckPinnedHttpEndpointAsync(
                $"kws{dc}",
                $"kws{dc}.web.telegram.org",
                $"https://kws{dc}.web.telegram.org/apiws",
                TimeSpan.FromSeconds(6),
                cancellationToken)));
        var checks = (await webTask).Append(await mediaTask).Concat(await gatewayTask).ToArray();
        var detail = string.Join("; ", checks.Select(check => check.Detail));
        var successfulWebRoutes = checks.Count(check => check.Success && (
            check.Detail.StartsWith("main=", StringComparison.OrdinalIgnoreCase) ||
            check.Detail.StartsWith("webk=", StringComparison.OrdinalIgnoreCase) ||
            check.Detail.StartsWith("webz=", StringComparison.OrdinalIgnoreCase)));
        var enoughWeb = successfulWebRoutes >= 1;
        var latency = checks.Where(x => x.LatencyMs.HasValue).Select(x => x.LatencyMs!.Value).DefaultIfEmpty().Max();
        if (enoughWeb)
            return new EndpointCheck(true, $"Web/media routes OK: {detail}", latency);
        return new EndpointCheck(false, $"Web/media routes failed: {detail}", latency);
    }

    private static async Task<EndpointCheck> CheckPinnedHttpEndpointWithRetryAsync(
        string name,
        string host,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EndpointCheck? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            last = await CheckPinnedHttpEndpointAsync(name, host, url, timeout, cancellationToken);
            if (last.Success) return last;
            if (attempt == 0) await Task.Delay(150, cancellationToken);
        }
        return last!;
    }

    private static async Task<EndpointCheck> CheckPinnedHttpEndpointAsync(
        string name,
        string host,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var resolution = await DnsOverrides.ResolveBestAsync(host, verifyTls: false, cancellationToken: cancellationToken, timeout: timeout);
        if (!resolution.Succeeded || resolution.Address is null)
            return new EndpointCheck(false, $"{name}=DNS {resolution.Detail}");

        try
        {
            using var handler = CreateHttpHandler(resolution.Address, usePinnedAddress: true);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout + TimeSpan.FromSeconds(2));
            using var client = new HttpClient(handler) { Timeout = timeout + TimeSpan.FromSeconds(2) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 VortexPass/0.3");
            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            watch.Stop();
            var success = (int)response.StatusCode < 500;
            var source = resolution.Source.Equals("System DNS", StringComparison.OrdinalIgnoreCase) ? "system" : resolution.Source;
            return new EndpointCheck(success, $"{name}=HTTP {(int)response.StatusCode} {watch.ElapsedMilliseconds} ms via {source}", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new EndpointCheck(false, $"{name}={ShortError(ex)} via {resolution.Source}");
        }
    }

    private sealed record EndpointCheck(bool Success, string Detail, long? LatencyMs = null);

    private static async Task<ServiceHealthResult> CheckPhasmophobiaAsync(CancellationToken cancellationToken)
    {
        const string photonHost = "ns.exitgames.com";
        var watch = Stopwatch.StartNew();
        var dns = await CheckDnsEndpointAsync(photonHost, cancellationToken);
        var udp = dns.Success && dns.Address is not null
            ? await CheckUdpSmokeEndpointsAsync(dns.Address, PhotonUdpPorts, cancellationToken)
            : new EndpointCheck(false, "skipped: Photon DNS returned no IP");
        var websocket = dns.Success && dns.Address is not null
            ? await CheckPhotonWebSocketPortsAsync(dns.Address, PhotonWebSocketPorts, cancellationToken)
            : new EndpointCheck(false, "skipped: Photon DNS returned no IP");
        watch.Stop();

        var stages = new ServiceProbeStages(
            $"Photon DNS=[{dns.Detail}]",
            $"Photon UDP=[{udp.Detail}]",
            "game TLS is not forced",
            $"Photon WebSocket=[{websocket.Detail}]");

        if (!dns.Success)
            return new("phasmophobia", ServiceHealthState.DnsUnavailable, $"Photon DNS недоступен: {dns.Detail}", null, stages);
        if (!udp.Success && !websocket.Success)
            return new("phasmophobia", ServiceHealthState.TcpTimeout, $"Photon UDP/WebSocket недоступны: UDP {udp.Detail}; WS {websocket.Detail}", null, stages);

        var detail = udp.Success
            ? "Photon DNS/UDP доступны; игровой UDP оставлен прямым для стабильности сессии"
            : $"Photon DNS доступен; UDP smoke не подтверждён, WebSocket: {websocket.Detail}";
        return new("phasmophobia", ServiceHealthState.Healthy, detail, watch.ElapsedMilliseconds, stages);
    }

    private static async Task<(bool Success, string Detail, IPAddress? Address)> CheckDnsEndpointAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetworkV6);
            return address is null
                ? (false, "DNS returned no IP", null)
                : (true, $"OK: {address}", address);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (false, ShortError(ex), null);
        }
    }

    private static async Task<EndpointCheck> CheckUdpSmokeEndpointsAsync(IPAddress address, IEnumerable<int> ports, CancellationToken cancellationToken)
    {
        var checks = new List<string>();
        foreach (var port in ports)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
                using var client = new UdpClient(address.AddressFamily);
                client.Connect(address, port);
                await client.SendAsync([0], 1).WaitAsync(timeout.Token);
                return new EndpointCheck(true, $"{address}:{port} send OK");
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                checks.Add($"{port} {ShortError(ex)}");
            }
        }

        return new EndpointCheck(false, string.Join("; ", checks.Take(4)));
    }

    private static async Task<EndpointCheck> CheckPhotonWebSocketPortsAsync(IPAddress address, IEnumerable<int> ports, CancellationToken cancellationToken)
    {
        var checks = await Task.WhenAll(ports.Select(port => CheckTcpEndpointAsync(address, port, cancellationToken)));
        var success = checks.FirstOrDefault(check => check.Success);
        if (success is not null) return new EndpointCheck(true, success.Detail);
        return new EndpointCheck(false, string.Join("; ", checks.Select(check => check.Detail).Take(4)));
    }

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
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
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
