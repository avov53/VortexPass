using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VortexPass.Core.Services;

public enum ServiceHealthState { NotSelected, WinwsStopped, Checking, Healthy, Partial, DnsUnavailable, TcpTimeout, TlsFailed, HttpUnavailable }
public enum ServiceMeasurementType { RouteProbe, VoiceProbe, GameSession }

public sealed record ServiceProbeStages(
    string Dns,
    string Tcp,
    string Tls,
    string Http,
    string? Voice = null,
    string? Gateway = null,
    string? Cdn = null);

public sealed record ServiceRouteGroup(string Id, string State, string Detail, long? DurationMs=null);

public sealed record ServiceHealthResult(
    string Id,
    ServiceHealthState State,
    string Detail,
    long? LatencyMs = null,
    ServiceProbeStages? Stages = null,
    ServiceMeasurementType MeasurementType = ServiceMeasurementType.RouteProbe,
    bool? ContentConfirmed = null,
    IReadOnlyList<ServiceRouteGroup>? Groups = null,
    bool? SystemDnsReady = null)
{
    public bool IsHealthy => State is ServiceHealthState.Healthy or ServiceHealthState.Partial;
}

public sealed class ServiceHealthCheckService
{
    public static async Task<ServiceHealthResult> CheckSiteRuleAsync(VortexPass.Core.Models.SiteRule rule,Uri? proxy,CancellationToken token,bool systemWarpActive=false)
    {
        if(rule.Connection=="warpProxy"&&proxy is null||rule.Connection=="warpSystem"&&!systemWarpActive)
            return new("site-rule:"+rule.Id,ServiceHealthState.HttpUnavailable,$"{rule.Name}: выбранный WARP-маршрут не подключён",ContentConfirmed:false);
        var watch=Stopwatch.StartNew();var checks=new List<ServiceRouteGroup>();var ports=SiteRulePolicy.Ports(rule.Ports);if(ports.Length==0)ports=[443];
        foreach(var domain in SiteRulePolicy.Domains(rule))
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var handler=new SocketsHttpHandler{UseProxy=proxy is not null,Proxy=proxy is null?null:new WebProxy(proxy),AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(3)};
                using var client=new HttpClient(handler);var port=ports[0];var uri=new UriBuilder(port==80?"http":"https",domain,port).Uri;
                using var request=new HttpRequestMessage(HttpMethod.Get,uri);
                if(rule.Protocol=="quic"){request.Version=HttpVersion.Version30;request.VersionPolicy=HttpVersionPolicy.RequestVersionExact;}
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);var bytes=await stream.ReadAsync(new byte[512],timeout.Token);
                var ok=response.IsSuccessStatusCode&&bytes>0&&(rule.Protocol!="quic"||response.Version.Major==3);
                checks.Add(new(domain,ok?"confirmed":"incomplete",$"{domain}:{port}: HTTP {(int)response.StatusCode}, {bytes} B; {(proxy is null?"direct":"WARP-прокси")}"));
            }
            catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException)
            {token.ThrowIfCancellationRequested();checks.Add(new(domain,"routeUnavailable",ex is OperationCanceledException?"тайм-аут":ex.GetType().Name));}
        }
        if(ports.Length>1)checks.Add(new("ports","incomplete","Проверен первый порт; остальные порты требуют отдельной сессии сервиса."));
        if(rule.Components.Any(c=>c is "images" or "video"))checks.Add(new("media","incomplete","Для подтверждения медиа нужен реальный URL ресурса, а не корневая страница домена."));
        if(rule.Components.Any(c=>c is "voice" or "game"))checks.Add(new("session","sessionRequired","Игровая/голосовая сессия не подтверждается запросом Web; игровой UDP остаётся прямым."));
        var confirmed=checks.All(c=>c.State is "confirmed" or "sessionRequired");
        return new("site-rule:"+rule.Id,confirmed?ServiceHealthState.Healthy:ServiceHealthState.Partial,$"{rule.Name}: "+string.Join("; ",checks.Select(c=>c.Detail)),watch.ElapsedMilliseconds,ContentConfirmed:confirmed,Groups:checks);
    }
    public static async Task<ServiceHealthResult> CheckCustomDomainAsync(string domain, CancellationToken token)
    {
        var normalized = UserDomainListService.Normalize([domain]).Single();
        var watch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(normalized, token);
            if (addresses.Length == 0) return new("domain:" + domain, ServiceHealthState.DnsUnavailable, "Системный DNS не вернул адрес", SystemDnsReady: false);
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(4) };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
            using var response = await client.GetAsync("https://" + normalized + "/", HttpCompletionOption.ResponseHeadersRead, token);
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var received = await stream.ReadAsync(new byte[512], token);
            var confirmed = response.IsSuccessStatusCode && received > 0;
            return new("domain:" + domain, confirmed ? ServiceHealthState.Healthy : ServiceHealthState.Partial,
                $"{domain}: HTTP {(int)response.StatusCode}, {received} байт; проверяется только корневая HTTPS-страница",
                watch.ElapsedMilliseconds, ContentConfirmed: confirmed, SystemDnsReady: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return new("domain:" + domain, ServiceHealthState.HttpUnavailable, $"{domain}: {ShortError(ex)}", ContentConfirmed: false);
        }
    }

    private static async Task<EndpointCheck> CheckTelegramPageAsync(Uri uri, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3), AllowAutoRedirect = false };
            using var client = new HttpClient(handler);
            var watch = Stopwatch.StartNew();
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var received = await body.ReadAsync(new byte[512], timeout.Token);
            return new(RouteProbePolicy.IsUsablePage((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, received),
                $"{uri.AbsolutePath}: HTTP {(int)response.StatusCode}, {received} B через Windows DNS", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); return new(false, $"{uri.AbsolutePath}: {ShortError(ex)} через Windows DNS"); }
    }
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
        ["roblox"] = new("www.roblox.com", "https://www.roblox.com/")
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
        bool telegramProxyHasClientActivity = false,
        string discordVoiceMode = "minimal",
        bool probeOptionalTransport = true,
        bool deepTelegramProbe = false)
    {
        if (!selected) return new(id, ServiceHealthState.NotSelected, "Не выбран");
        if (!winwsRunning) return new(id, ServiceHealthState.WinwsStopped, "winws остановлен");
        if(id=="youtube")return await YoutubeRouteProbe.CheckAsync(cancellationToken);
        if(id=="roblox")
        {
            var content=await RobloxContentProbeService.CheckAsync(cancellationToken);
            return new("roblox",content.Confirmed?ServiceHealthState.Healthy:ServiceHealthState.Partial,
                string.Join("; ",content.Groups.Select(g=>g.Id+": "+g.Detail)),content.Latency,
                ContentConfirmed:content.Confirmed,Groups:content.Groups);
        }
        if (id.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase))
            return await CheckPhasmophobiaAsync(cancellationToken);
        if (!Targets.TryGetValue(id, out var target)) throw new ArgumentOutOfRangeException(nameof(id));
        if (id == "telegram" && !appSelected && !webSelected)
        {
            appSelected = true;
            webSelected = true;
        }
        if (id == "telegram" && webSelected)
            return await CheckTelegramGroupsAsync(appSelected, telegramProxyRunning, telegramProxyHasClientActivity, cancellationToken, deepTelegramProbe);
        if (id == "telegram" && appSelected && telegramProxyRunning && !telegramProxyHasClientActivity)
            return new(id, ServiceHealthState.Partial, "Desktop-прокси запущен и ожидает подключения клиента",
                Groups: [new("desktop", "sessionRequired", "Нужна активная сессия Telegram Desktop")]);

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
        var normalizedVoiceMode = WinwsProfileComposer.NormalizeDiscordVoiceMode(discordVoiceMode);
        if (target.VoiceHost is not null && normalizedVoiceMode != "off")
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
            if (response.IsSuccessStatusCode)
            {
                if (id == "youtube")
                {
                    var searchTask = CheckYoutubeSearchEndpointAsync(cancellationToken);
                    var assetsTask = CheckYoutubeAssetEndpointsAsync(cancellationToken);
                    var http3Task = probeOptionalTransport ? CheckHttp3EndpointAsync("https://www.youtube.com/generate_204", cancellationToken)
                        : Task.FromResult(new EndpointCheck(false, "Не требуется для проверки контента"));
                    await Task.WhenAll(searchTask, assetsTask, http3Task);
                    var search = await searchTask;
                    var assets = await assetsTask;
                    var http3 = await http3Task;
                    http = $"{http}; Search/API=[{search.Detail}]; Video CDN=[{assets.VideoDetail}]; Preview/avatars=[{assets.PreviewDetail}]; QUIC=[{http3.Detail}]";
                    var youtubeStages = new ServiceProbeStages(dns, tcp, tls, http, voice, gateway, cdn);
                    var baseDetail = secureDnsFallback
                        ? "Оболочка YouTube доступна через внутренний DoH; системный DNS не подтверждён"
                        : assets.VideoSuccess?"Оболочка и контрольное видео YouTube доступны":"Оболочка YouTube открывается; видео не подтверждено";
                    var verified = new List<string>();
                    var unverified = new List<string>();
                    (search.Success ? verified : unverified).Add("поиск");
                    (assets.VideoSuccess ? verified : unverified).Add("video CDN");
                    (assets.PreviewSuccess ? verified : unverified).Add("превью/аватары");
                    (http3.Success ? verified : unverified).Add("HTTP/3");
                    var verifiedDetail = verified.Count == 0 ? string.Empty : $"; подтверждены: {string.Join(", ", verified)}";
                    var unverifiedDetail = unverified.Count == 0 ? string.Empty : $"; не подтверждены: {string.Join(", ", unverified)}";
                    var youtubeDetail = $"{baseDetail}{verifiedDetail}{unverifiedDetail}";
                    // The shell/API being reachable is not enough to call
                    // YouTube healthy: thumbnails and video use separate CDN
                    // routes, which are exactly what the user sees failing.
                    var youtubeState = ClassifyYouTubeState(true, assets.VideoSuccess,
                        search.Success, assets.PreviewSuccess, http3.Success);
                    return new(id, youtubeState, youtubeDetail, transportLatencyMs, youtubeStages,
                        ContentConfirmed: search.Success && assets.VideoSuccess && assets.PreviewSuccess && !secureDnsFallback,
                        Groups: [new("web", "confirmed", http.Split(';')[0]), Group("api", search.Success, search.Detail),
                            Group("video", assets.VideoSuccess, assets.VideoDetail, true), Group("images", assets.PreviewSuccess, assets.PreviewDetail, true),
                            Group("quic", http3.Success, http3.Detail, true)], SystemDnsReady: !secureDnsFallback && address is not null);
                }

                if (id == "telegram")
                {
                    var webAuxiliaryTask = CheckTelegramWebAuxiliaryAsync(cancellationToken);
                    var http3Task = probeOptionalTransport ? CheckHttp3EndpointAsync("https://web.telegram.org/", cancellationToken)
                        : Task.FromResult(new EndpointCheck(false, "Не требуется для проверки контента"));
                    await Task.WhenAll(webAuxiliaryTask, http3Task);
                    var webAuxiliary = await webAuxiliaryTask;
                    var http3 = await http3Task;
                    http = $"{http}; Web/media=[{webAuxiliary.Detail}]; QUIC media=[{http3.Detail}]";
                    var telegramStages = new ServiceProbeStages(dns, tcp, tls, http, voice, gateway, cdn);
                    var desktopDetail = appSelected
                        ? telegramProxyRunning
                            ? telegramProxyHasClientActivity
                                ? "Desktop подключался к локальному WebSocket-прокси"
                                : "Desktop-прокси запущен и ожидает подключения клиента"
                            : "Desktop-прокси не запущен"
                        : null;
                    ServiceRouteGroup[] telegramGroups = [Group("web", webAuxiliary.InterfaceAvailable, "Интерфейс Telegram Web"),
                        Group("messages", webAuxiliary.GatewayAvailable, "WebSocket / канал обновлений"),
                        Group("media", webAuxiliary.MediaAvailable, webAuxiliary.Detail, true),
                        new("desktop", telegramProxyHasClientActivity ? "confirmed" : "sessionRequired", desktopDetail ?? "Desktop не выбран")];
                    if (!webAuxiliary.GatewayAvailable)
                    {
                        var partialDetail = $"Интерфейс открыт, канал обновлений недоступен: {webAuxiliary.Detail}";
                        if (desktopDetail is not null) partialDetail = $"{partialDetail}; {desktopDetail}";
                        return new(id, ServiceHealthState.Partial, partialDetail, transportLatencyMs, telegramStages,
                            Groups: telegramGroups, SystemDnsReady: !secureDnsFallback && address is not null);
                    }
                    if (!webAuxiliary.MediaAvailable || !webAuxiliary.InterfaceAvailable)
                    {
                        var partialDetail = $"Интерфейс Telegram Web доступен, но часть маршрутов сообщений/WebSocket или медиа не подтверждена: {webAuxiliary.Detail}";
                        if (desktopDetail is not null) partialDetail = $"{partialDetail}; {desktopDetail}";
                        return new(id, ServiceHealthState.Partial, partialDetail, transportLatencyMs, telegramStages,
                            Groups: telegramGroups, SystemDnsReady: !secureDnsFallback && address is not null);
                    }
                    var telegramDetail = appSelected
                        ? $"Telegram Web, Web/media и WebSocket доступны; {desktopDetail}"
                        : secureDnsFallback
                            ? "Telegram Web работает через Secure DNS; Web/media маршруты доступны"
                            : "Telegram Web и Web/media маршруты доступны";
                    return new(id, ServiceHealthState.Healthy, telegramDetail, transportLatencyMs, telegramStages,
                        ContentConfirmed: !secureDnsFallback, Groups: telegramGroups, SystemDnsReady: !secureDnsFallback && address is not null);
                }

                if (id == "roblox")
                {
                    var content = await CheckRobloxContentAsync(cancellationToken);
                    http = $"{http}; API/CDN=[{content.Detail}]";
                    var robloxStages = new ServiceProbeStages(dns, tcp, tls, http, voice, gateway, cdn);
                    return content.Success
                        ? new(id, ServiceHealthState.Healthy,
                            "Roblox Web, thumbnail API и реальные байты CDN доступны; игровая UDP-сессия проверяется самим клиентом",
                            transportLatencyMs, robloxStages)
                        : new(id, ServiceHealthState.Partial,
                            $"Roblox Web открывается, но контент API/CDN не подтверждён: {content.Detail}",
                            transportLatencyMs, robloxStages);
                }

                var discordGatewayAvailable = gateway?.StartsWith("OK", StringComparison.Ordinal) == true;
                var discordCdnAvailable = cdn?.StartsWith("OK", StringComparison.Ordinal) == true ||
                    cdn?.Contains("cdn=HTTP 200", StringComparison.OrdinalIgnoreCase) == true;
                var discordAuxiliaryFailed = id == "discord" && (!discordGatewayAvailable || !discordCdnAvailable);
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
                var discordState = id == "discord" && voice?.StartsWith("OK", StringComparison.Ordinal) != true
                    ? ServiceHealthState.Partial
                    : ServiceHealthState.Healthy;
                return new(id, discordState, detail, latencyToReport,
                    successStages, ContentConfirmed: id == "discord" && !discordAuxiliaryFailed && !secureDnsFallback);
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
            if (webAuxiliary.InterfaceAvailable || webAuxiliary.GatewayAvailable)
            {
                var partialStages = new ServiceProbeStages(dns, tcp, tls, $"{http}; Web/media=[{webAuxiliary.Detail}]", voice, gateway, cdn);
                // The auxiliary request downloads the Web K shell and can take
                // seconds even when the network path itself is healthy.  Keep
                // the displayed ping tied to TCP/TLS transport, not page load.
                return new(id, ClassifyTelegramWebState(primaryHttpAvailable: false, auxiliaryAvailable: webAuxiliary.GatewayAvailable),
                    webAuxiliary.GatewayAvailable
                        ? $"Основной интерфейс не ответил, но канал обновлений WebSocket доступен: {webAuxiliary.Detail}"
                        : $"Интерфейс открыт, канал обновлений недоступен: {webAuxiliary.Detail}",
                    SelectTelegramReportedLatency(transportLatencyMs, webAuxiliary.LatencyMs), partialStages);
            }
        }
        // A completed TLS handshake proves only that the edge accepted a
        // socket. It must never turn a timed-out HTTP/CDN route green.
        if (id == "telegram" && appSelected && gateway?.StartsWith("OK", StringComparison.Ordinal) == true)
        {
            var detail = telegramProxyRunning
                ? "Telegram Desktop подключён к локальному WebSocket-прокси"
                : $"Telegram Desktop/DC доступен: {gateway}";
            return new(id, ServiceHealthState.Healthy, detail, transportLatencyMs ?? 2, stages);
        }
        if (id == "discord" && gateway?.StartsWith("OK", StringComparison.Ordinal) == true &&
            (cdn?.StartsWith("OK", StringComparison.Ordinal) == true ||
             cdn?.Contains("cdn=HTTP 200", StringComparison.OrdinalIgnoreCase) == true))
            return new(id, ServiceHealthState.Partial,
                $"Discord работает: Gateway и CDN доступны; отдельная API/TCP-проверка не завершилась ({http})",
                null, stages);
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

    private sealed record YoutubeAssetCheck(bool PreviewSuccess, bool VideoSuccess, string PreviewDetail, string VideoDetail);

    private static async Task<YoutubeAssetCheck> CheckYoutubeAssetEndpointsAsync(CancellationToken cancellationToken)
    {
        var thumbnail = CheckPinnedHttpEndpointAsync("thumb", "i.ytimg.com", "https://i.ytimg.com/vi/dQw4w9WgXcQ/default.jpg", TimeSpan.FromSeconds(6), cancellationToken);
        var avatar = PublicMediaProbeService.CheckAsync("avatar", cancellationToken);
        var video = PublicMediaProbeService.CheckAsync("video", cancellationToken);
        await Task.WhenAll(thumbnail, avatar, video);
        return new YoutubeAssetCheck(
            thumbnail.Result.Success && avatar.Result.Confirmed,
            video.Result.Confirmed,
            $"{thumbnail.Result.Detail}; {avatar.Result.Detail}",
            video.Result.Detail);
    }

    public static ServiceHealthState ClassifyYouTubeState(
        bool siteAvailable,
        bool videoCdnAvailable,
        bool searchAvailable,
        bool previewsAvailable,
        bool http3Available)
    {
        if (!siteAvailable) return ServiceHealthState.HttpUnavailable;
        // HTTP/2 is a valid, often faster fallback on mobile operators. QUIC is
        // reported independently and is not required for a fully working site.
        return videoCdnAvailable && searchAvailable && previewsAvailable
            ? ServiceHealthState.Healthy
            : ServiceHealthState.Partial;
    }

    private static async Task<EndpointCheck> CheckVoiceEndpointWithLatencyAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetworkV6);
            if (address is null) return new EndpointCheck(false, "DNS не вернул IP");
            var stunBindingRequest = new byte[]
            {
                0x00, 0x01, 0x00, 0x00, 0x21, 0x12, 0xA4, 0x42,
                0x56, 0x50, 0x41, 0x53, 0x53, 0x56, 0x4F, 0x49,
                0x43, 0x45, 0x30, 0x31
            };
            var samples = new List<long>();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
                    using var client = new UdpClient(address.AddressFamily);
                    client.Connect(address, 50000);
                    var watch = Stopwatch.StartNew();
                    await client.SendAsync(stunBindingRequest, timeout.Token);
                    var response = await client.ReceiveAsync(timeout.Token);
                    watch.Stop();
                    if (response.Buffer.Length >= 20) samples.Add(watch.ElapsedMilliseconds);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            if (samples.Count == 0)
                return new EndpointCheck(false, "вход возможен, но UDP voice не подтверждён без активной голосовой сессии");
            var median = samples.Order().ElementAt(samples.Count / 2);
            var jitter = samples.Max() - samples.Min();
            return samples.Count >= 2 && jitter <= 250
                ? new EndpointCheck(true, $"OK: UDP подтверждён {samples.Count}/3, медиана {median} ms, jitter {jitter} ms", median)
                : new EndpointCheck(false, $"UDP частично подтверждён {samples.Count}/3; высокий jitter/потери (jitter {jitter} ms)", median, Partial: true);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return new EndpointCheck(false, $"не подтверждён без активной голосовой сессии: {ShortError(ex)}");
        }
    }

    private static async Task<EndpointCheck> CheckHttp3EndpointAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(4) };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            watch.Stop();
            return new EndpointCheck(true, $"HTTP/3 {(int)response.StatusCode}, {watch.ElapsedMilliseconds} ms", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or PlatformNotSupportedException)
        {
            return new EndpointCheck(false, $"HTTP/3 недоступен: {ShortError(ex)}");
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
        return checks.All(check => check.Success&&!check.Partial) ? $"OK: {detail}" :
            checks.All(check=>check.Success)?$"Транспорт доступен; часть ресурсов требует доступа: {detail}":$"часть проверок не подтверждена: {detail}";
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
            if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new EndpointCheck(true,$"{name}=HTTP {(int)response.StatusCode}: доступ ограничен; сетевой отказ не доказан",Partial:true);
            var received = 0;
            if ((int)response.StatusCode < 500)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                received = await stream.ReadAsync(new byte[256], cancellationToken);
            }
            var success = (int)response.StatusCode < 500 && received > 0;
            return new EndpointCheck(success, $"{name}=HTTP {(int)response.StatusCode}, {received} B");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new EndpointCheck(false, $"{name}={ShortError(ex)}");
        }
    }

    private static async Task<EndpointCheck> CheckRobloxContentAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(7));
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 VortexPass/0.0.7");
            using var apiResponse = await client.GetAsync(
                "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=1&size=48x48&format=Png&isCircular=false",
                HttpCompletionOption.ResponseContentRead,
                timeout.Token);
            if (!apiResponse.IsSuccessStatusCode)
                return new EndpointCheck(false, $"thumbnail API HTTP {(int)apiResponse.StatusCode}");

            using var document = JsonDocument.Parse(await apiResponse.Content.ReadAsStringAsync(timeout.Token));
            var imageUrl = document.RootElement.TryGetProperty("data", out var data) &&
                           data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
                           data[0].TryGetProperty("imageUrl", out var image)
                ? image.GetString()
                : null;
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri))
                return new EndpointCheck(false, "thumbnail API не вернул CDN URL");

            using var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 511);
            using var imageResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)imageResponse.StatusCode >= 500)
                return new EndpointCheck(false, $"CDN HTTP {(int)imageResponse.StatusCode}");
            await using var stream = await imageResponse.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[512];
            var received = await stream.ReadAtLeastAsync(buffer, 16, false, timeout.Token);
            return RouteProbePolicy.IsImage((int)imageResponse.StatusCode, imageResponse.Content.Headers.ContentType?.MediaType, buffer.AsSpan(0, received))
                ? new EndpointCheck(true, $"thumbnail API HTTP {(int)apiResponse.StatusCode}; CDN HTTP {(int)imageResponse.StatusCode}, {received} B")
                : new EndpointCheck(false, $"CDN HTTP {(int)imageResponse.StatusCode}, 0 B");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new EndpointCheck(false, ShortError(ex));
        }
    }

    private static async Task<TelegramWebCheck> CheckTelegramWebAuxiliaryAsync(CancellationToken cancellationToken)
    {
        // Periodic health checks deliberately use only primary and reserve routes.
        // The complete CDN inventory is available from the manual WARP diagnostic.
        // Every endpoint owns its timeout, so one timeout cannot cancel siblings.
        // The shared token is still observed so a WARP/network transition can stop
        // the whole obsolete diagnostic instead of loading the new route.
        var webEndpoints = new[] { ("webk", "webk.telegram.org", "https://webk.telegram.org/") };
        var webTask = Task.WhenAll(webEndpoints.Select(endpoint =>
            CheckPinnedHttpEndpointWithRetryAsync(endpoint.Item1, endpoint.Item2, endpoint.Item3, TimeSpan.FromSeconds(6), cancellationToken)));
        var mediaTask = PublicMediaProbeService.CheckAsync("telegram-media", cancellationToken);
        var gatewayTask = Task.WhenAll(new[] { 2, 4 }.Select(dc =>
            CheckTelegramWebSocketEndpointAsync(
                $"kws{dc}",
                new Uri($"wss://kws{dc}.web.telegram.org/apiws"),
                TimeSpan.FromSeconds(7),
                cancellationToken)));
        var webChecks = await webTask;
        var media = await mediaTask;
        var gatewayChecks = await gatewayTask;
        var checks = webChecks.Concat(gatewayChecks).ToArray();
        var detail = string.Join("; ", checks.Select(check => check.Detail)) + "; " + media.Detail;
        var successfulWebRoutes = webChecks.Count(check => check.Success);
        var mediaAvailable = media.Confirmed;
        var gatewayAvailable = gatewayChecks.Any(check => check.Success);
        // Auxiliary timings describe page/CDN response time and are useful in
        // details, but the slowest optional endpoint is not a representative
        // network ping. Prefer the primary Web route when a caller needs one.
        var latency = webChecks.Where(x => x.Success && x.LatencyMs.HasValue)
            .Select(x => x.LatencyMs!.Value).DefaultIfEmpty().Min();
        return new TelegramWebCheck(
            successfulWebRoutes > 0,
            gatewayAvailable,
            mediaAvailable,
            $"Web {successfulWebRoutes}/{webChecks.Length}; медиа {(mediaAvailable ? "подтверждено" : "проверка не завершилась")}; WebSocket {gatewayChecks.Count(x => x.Success)}/{gatewayChecks.Length}: {detail}",
            latency);
    }

    private static async Task<ServiceHealthResult> CheckTelegramGroupsAsync(bool desktopSelected, bool proxyRunning, bool clientActivity, CancellationToken token, bool deep)
    {
        // Periodic checks use two canonical routes. Selection tests all DCs with
        // bounded concurrency, exchanging real MTProto rather than just Upgrade.
        using var gate = new SemaphoreSlim(3);
        var names = deep ? TelegramEndpointRegistry.NamedDataCenters : new[] { "venus", "vesta" };
        async Task<TelegramTransportResult> Transport(string host)
        {
            await gate.WaitAsync(token);
            try { return await TelegramWebSocketProbe.CheckAsync(host, token, sustained: deep); }
            finally { gate.Release(); }
        }
        var webTask = Task.WhenAll(TelegramEndpointRegistry.Pages.Take(deep ? 2 : 1).Select(uri => CheckTelegramPageAsync(uri, token)));
        var messagesTask = Task.WhenAll(names.Select(name => Transport(name + ".web.telegram.org")));
        var mediaTask = deep ? Task.WhenAll(names.Select(name => Transport(name + "-1.web.telegram.org")))
            : Task.FromResult(Array.Empty<TelegramTransportResult>());
        var dnsTask = CheckDnsEndpointAsync("web.telegram.org", token);
        await Task.WhenAll(webTask, messagesTask, mediaTask, dnsTask);
        var interfaceOk = webTask.Result.Any(x => x.Success);
        var messagesOk = messagesTask.Result.All(x => x.Confirmed);
        var mediaOk = deep && mediaTask.Result.All(x => x.Confirmed);
        ServiceRouteGroup[] groups = [
            Group("web", interfaceOk, string.Join("; ", webTask.Result.Select(x => x.Detail))),
            Group("messages", messagesOk, string.Join("; ", messagesTask.Result.Select(x => x.Detail))),
            new("mediaTransport", !deep ? "notTested" : mediaOk ? "confirmed" : "routeUnavailable",
                !deep ? "Глубокая проверка выполняется при подборе" : string.Join("; ", mediaTask.Result.Select(x => x.Detail))),
            new("browserSession", "sessionRequired", "Транспорт MTProto проверен без входа. Синхронизация аккаунта и личные вложения требуют проверки в браузере."),
            new("desktop", !desktopSelected ? "notSelected" : !proxyRunning ? "routeUnavailable" : clientActivity ? "confirmed" : "sessionRequired",
                !desktopSelected ? "Не выбран" : !proxyRunning ? "Прокси не запущен" : clientActivity ? "Клиент подключался к прокси" : "Прокси ожидает клиента")];
        // HTTPS is a distinct opt-in browser transport; it must not mark a broken
        // WebSocket profile as working or add timeout to every successful check.
        if (deep && !messagesOk)
        {
            var https = await Task.WhenAll(names.Select(async name =>
            {
                await gate.WaitAsync(token);
                try { return await TelegramHttpsProbe.CheckAsync(name + ".web.telegram.org", token); }
                finally { gate.Release(); }
            }));
            groups = [..groups, Group("httpsTransport", https.All(r => r.Confirmed), string.Join("; ", https.Select(r => r.Detail)))];
        }
        if (FirefoxDnsPolicy.ReadHint() is { } hint) groups = [..groups, new("browserDns", "incomplete", hint)];
        var usable = interfaceOk && messagesOk && dnsTask.Result.Success && (!deep || mediaOk);
        var detail = usable ? deep
            ? "MTProto сообщений и медиатранспорта подтверждён на пяти DC; аккаунт проверяется в браузере"
            : "Контрольные MTProto-маршруты отвечают; полная проверка доступна при подборе"
            : "Telegram доступен частично: " + (!interfaceOk ? "интерфейс; " : "") + (!messagesOk ? "MTProto сообщений; " : "") + (deep && !mediaOk ? "медиатранспорт; " : "");
        return new("telegram", usable ? ServiceHealthState.Healthy : interfaceOk || messagesOk ? ServiceHealthState.Partial : ServiceHealthState.HttpUnavailable,
            detail, LocalStrategyAdvisor.Median(messagesTask.Result.Where(x => x.Confirmed && x.LatencyMs.HasValue).Select(x => x.LatencyMs!.Value)),
            new ServiceProbeStages(dnsTask.Result.Detail, "WSS/TLS", "Сертификаты проверены", groups[0].Detail, Gateway: groups[1].Detail, Cdn: groups[2].Detail),
            ContentConfirmed: deep && usable, Groups: groups, SystemDnsReady: dnsTask.Result.Success);
    }

    public static long? SelectTelegramReportedLatency(long? transportLatencyMs, long? webLoadLatencyMs) =>
        transportLatencyMs;

    public static ServiceHealthState ClassifyTelegramWebState(bool primaryHttpAvailable, bool auxiliaryAvailable) =>
        primaryHttpAvailable && auxiliaryAvailable
            ? ServiceHealthState.Healthy
            : primaryHttpAvailable || auxiliaryAvailable
                ? ServiceHealthState.Partial
            : ServiceHealthState.HttpUnavailable;

    private static async Task<EndpointCheck> CheckTelegramWebSocketEndpointAsync(
        string name,
        Uri uri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            using var socket = new ClientWebSocket();
            socket.Options.Proxy = null;
            socket.Options.AddSubProtocol("binary");
            socket.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 VortexPass/0.0.7");
            var watch = Stopwatch.StartNew();
            await socket.ConnectAsync(uri, linked.Token);
            watch.Stop();
            var opened = socket.State == WebSocketState.Open;
            // This is only a reachability probe. Waiting for Telegram to echo a
            // graceful close can stall the whole periodic health check.
            if (opened) socket.Abort();
            return new EndpointCheck(opened,
                opened ? $"{name}=WebSocket OK {watch.ElapsedMilliseconds} ms" : $"{name}=WebSocket {socket.State}",
                watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new EndpointCheck(false, $"{name}=WebSocket {ShortError(ex)}");
        }
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
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 VortexPass/0.3");
            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var received = 0;
            var buffer = new byte[512];
            if ((int)response.StatusCode is 200 or 206)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
                received = await stream.ReadAtLeastAsync(buffer, 16, false, linked.Token);
            }
            watch.Stop();
            var success = name.StartsWith("thumb") || name.StartsWith("avatar") || name.StartsWith("media")
                ? RouteProbePolicy.IsImage((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, buffer.AsSpan(0, received))
                : RouteProbePolicy.IsUsablePage((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, received);
            var source = resolution.Source.Equals("System DNS", StringComparison.OrdinalIgnoreCase) ? "system" : resolution.Source;
            return new EndpointCheck(success, $"{name}=HTTP {(int)response.StatusCode}, {received} B {watch.ElapsedMilliseconds} ms via {source}", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new EndpointCheck(false, $"{name}={ShortError(ex)} via {resolution.Source}");
        }
    }

    private sealed record EndpointCheck(bool Success, string Detail, long? LatencyMs = null, bool Partial = false);
    private static ServiceRouteGroup Group(string id, bool confirmed, string detail, bool incomplete = false) =>
        new(id, confirmed ? "confirmed" : incomplete ? "incomplete" : "routeUnavailable", detail);
    private sealed record TelegramWebCheck(
        bool InterfaceAvailable,
        bool GatewayAvailable,
        bool MediaAvailable,
        string Detail,
        long? LatencyMs = null);

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
