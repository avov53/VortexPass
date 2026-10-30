using System.Net;
using System.Net.Sockets;
using System.Text;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public sealed record WarpRoutingPlan(
    IReadOnlyList<string> ProxyDomains,
    IReadOnlyList<string> DirectDomains,
    IReadOnlyList<string> DirectIpRanges,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings);

public static class WarpRoutingPlanner
{
    private static readonly Dictionary<string, string[]> BuiltInDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube"] = ["youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com", "googleusercontent.com", "youtubei.googleapis.com"],
        ["discord"] = ["discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media"],
        ["telegram"] = TelegramWebPolicy.Domains,
        ["games"] = []
    };

    public static void EnsureDefaults(WarpSettings settings)
    {
        settings.RouteProfiles ??= [];
        AddBuiltIn(settings, "youtube", "YouTube", "site", false);
        AddBuiltIn(settings, "discord", "Discord", "site", false);
        AddBuiltIn(settings, "telegram", "Telegram Web", "site", settings.Telegram);
        AddBuiltIn(settings, "games", "Игры", "game", settings.Games);
        foreach (var profile in settings.RouteProfiles)
        {
            profile.Domains ??= [];
            profile.IpRanges ??= [];
            if (BuiltInDomains.TryGetValue(profile.BuiltInId, out var domains)) profile.Domains = domains.ToList();
            profile.Connection = NormalizeConnection(profile.Connection);
            profile.Kind = profile.Kind.Equals("game", StringComparison.OrdinalIgnoreCase) ? "game" : "site";
        }
        settings.ProxyPort = settings.ProxyPort is >= 1024 and <= 65535 ? settings.ProxyPort : 41870;
        settings.RoutingMode = NormalizeRoutingMode(settings.RoutingMode);
        WarpTransportPolicy.Migrate(settings);
        settings.DnsMode = NormalizeDnsMode(settings.DnsMode);
        settings.NetworkOptimizations ??= [];
    }

    private static void AddBuiltIn(WarpSettings settings, string id, string name, string kind, bool enabled)
    {
        if (settings.RouteProfiles.Any(profile => profile.BuiltInId.Equals(id, StringComparison.OrdinalIgnoreCase))) return;
        settings.RouteProfiles.Add(new WarpRouteProfile
        {
            Id = $"builtin-{id}", BuiltInId = id, Name = name, Kind = kind, Enabled = enabled,
            Connection = "auto", Domains = BuiltInDomains[id].ToList()
        });
    }

    public static WarpRoutingPlan BuildPlan(WarpSettings settings, string networkKey)
    {
        EnsureDefaults(settings);
        var decisions = settings.NetworkOptimizations
            .Where(item => item.NetworkKey.Equals(networkKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CheckedAt).FirstOrDefault()?.RouteDecisions;
        var routes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var directIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var profile in settings.RouteProfiles.Where(profile => profile.Enabled))
        {
            var connection = NormalizeConnection(profile.Connection);
            if (connection == "auto")
                connection = decisions?.GetValueOrDefault(profile.Id) ?? "direct";
            if (profile.Kind.Equals("game", StringComparison.OrdinalIgnoreCase) && connection == "warpProxy")
            {
                warnings.Add($"{profile.Name}: выборочный SOCKS5 не поддерживает игровой UDP; используется прямой маршрут.");
                connection = "direct";
            }
            if (connection == "warpSystem")
                warnings.Add($"{profile.Name}: системный WARP влияет на весь компьютер, а не только на этот профиль.");
            var route = connection == "warpProxy" ? "proxy" : connection == "direct" ? "direct" : "system";
            foreach (var raw in profile.Domains)
            {
                var domain = NormalizeDomain(raw);
                if (domain is null) continue;
                if (!routes.TryGetValue(domain, out var set)) routes[domain] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(route);
            }
            if (route == "direct")
                foreach (var range in profile.IpRanges.Where(IsIpOrCidr)) directIps.Add(range.Trim());
        }
        var conflicts = routes.Where(item => item.Value.Count > 1)
            .Select(item => $"{item.Key}: {string.Join(" / ", item.Value.Order())}").ToArray();
        var conflicted = conflicts.Select(value => value[..value.IndexOf(':')]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(
            routes.Where(item => item.Value.Contains("proxy") && !conflicted.Contains(item.Key)).Select(item => item.Key).Order().ToArray(),
            routes.Where(item => item.Value.Contains("direct") && !conflicted.Contains(item.Key)).Select(item => item.Key).Order().ToArray(),
            directIps.Order().ToArray(), conflicts, warnings);
    }

    public static bool UsesWarpForBuiltIn(WarpSettings settings, string networkKey, string builtInId)
    {
        EnsureDefaults(settings);
        if (NormalizeRoutingMode(settings.RoutingMode) == "full") return true;
        var decisions = settings.NetworkOptimizations
            .Where(item => item.NetworkKey.Equals(networkKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CheckedAt).FirstOrDefault()?.RouteDecisions;
        var profile = settings.RouteProfiles.FirstOrDefault(item => item.Enabled &&
            item.BuiltInId.Equals(builtInId, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return false;
        var connection = NormalizeConnection(profile.Connection);
        if (connection == "auto") connection = decisions?.GetValueOrDefault(profile.Id) ?? "direct";
        return connection is "warpProxy" or "warpSystem";
    }

    public static string? SelectMeasuredWebRoute(bool directAvailable, long? directLatencyMs, bool proxyAvailable, long? proxyLatencyMs)
    {
        if (!directAvailable) return proxyAvailable ? "warpProxy" : null;
        if (!proxyAvailable) return "direct";
        return proxyLatencyMs.HasValue && directLatencyMs.HasValue && proxyLatencyMs.Value < directLatencyMs.Value
            ? "warpProxy" : "direct";
    }

    public static string? NormalizeDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim().TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
        if (Uri.TryCreate(candidate.Contains("://") ? candidate : $"https://{candidate}", UriKind.Absolute, out var uri)) candidate = uri.Host;
        if (candidate.Length is 0 or > 253 || candidate.Split('.').Any(label => label.Length is 0 or > 63 || label.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-')))) return null;
        return candidate;
    }

    public static bool IsIpOrCidr(string value)
    {
        var parts = value.Trim().Split('/');
        if (!IPAddress.TryParse(parts[0], out var address)) return false;
        if (parts.Length == 1) return true;
        return parts.Length == 2 && int.TryParse(parts[1], out var prefix) && prefix >= 0 && prefix <= (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
    }

    public static string NormalizeRoutingMode(string? value) => value is "full" or "proxy" or "dnsOnly" ? value : "smart";
    public static string NormalizeProtocol(string? value) => value?.ToLowerInvariant() is "masque" or "wireguard" ? value.ToLowerInvariant() : "auto";
    public static string NormalizeMasque(string? value) => value is "h3-only" or "h2-only" ? value : "h3-with-h2-fallback";
    public static string NormalizeDnsMode(string? value) => value is "udp" or "doh" or "dot" or "system" ? value : "auto";
    public static string NormalizeConnection(string? value) => value is "direct" or "warpProxy" or "warpSystem" ? value : "auto";
}

public sealed class WarpPacService : IDisposable
{
    private readonly WarpHttpProxy _httpProxy=new();
    public bool DiagnosticsEnabled {get=>_httpProxy.DiagnosticsEnabled;set=>_httpProxy.DiagnosticsEnabled=value;}
    public WarpProxyMetrics ProxyMetrics=>_httpProxy.Metrics;
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private string _content = string.Empty;
    public Uri? PacUri { get; private set; }
    public Uri? HttpProxyUri {get;private set;}

    public async Task<Uri> StartAsync(IEnumerable<string> domains, int socksPort, int preferredPort, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        var selected=domains.ToArray();
        var httpPort=await _httpProxy.StartAsync(selected,socksPort,cancellationToken);
        HttpProxyUri=new Uri($"http://127.0.0.1:{httpPort}");
        _content = GeneratePac(selected, httpPort);
        _listener = new TcpListener(IPAddress.Loopback, preferredPort is >=1024 and <=65535?preferredPort:0);
        try { _listener.Start(); }
        catch (SocketException)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // A changing URL forces browsers (notably Firefox in system-proxy
        // mode) to discard a cached PAC after the WARP route changes.
        PacUri = new Uri($"http://127.0.0.1:{port}/vortexpass.pac?v={Guid.NewGuid():N}");
        _lifetime = new CancellationTokenSource();
        _loop = ServeAsync(_lifetime.Token);
        return PacUri;
    }

    public async Task StopAsync()
    {
        _lifetime?.Cancel();
        _listener?.Stop();
        if (_loop is not null) try { await _loop; } catch (OperationCanceledException) { } catch (SocketException) { }
        _loop = null; _listener = null; _lifetime?.Dispose(); _lifetime = null; PacUri = null;
        await _httpProxy.StopAsync();
        HttpProxyUri=null;
    }

    public async Task VerifyPacAsync(CancellationToken token)
    {
        var uri=PacUri??throw new InvalidOperationException("PAC ещё не запущен");
        using var handler=new SocketsHttpHandler {UseProxy=false,UseCookies=false};
        using var client=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(3)};
        var content=await client.GetStringAsync(uri,token);
        if(content!=_content)throw new IOException("Браузерный PAC не прошёл проверку загрузки");
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            _ = RespondAsync(client, cancellationToken);
        }
    }

    private async Task RespondAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        try
        {
            // Consume the request before closing: closing a socket with unread
            // request bytes can reset it, discarding the PAC on Windows clients.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var stream = client.GetStream();
            var request = new List<byte>();
            var one = new byte[1];
            while (request.Count < 16384)
            {
                if (await stream.ReadAsync(one, deadline.Token) == 0) return;
                request.Add(one[0]);
                if (request.TakeLast(4).SequenceEqual(new byte[] {13,10,13,10})) break;
            }
            if (!request.TakeLast(4).SequenceEqual(new byte[] {13,10,13,10})) return;
            var first = Encoding.ASCII.GetString(request.ToArray()).Split("\r\n")[0].Split(' ');
            if (first.Length != 3 || first[0] is not ("GET" or "HEAD")) return;
            var body = Encoding.UTF8.GetBytes(_content);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/x-ns-proxy-autoconfig\r\nCache-Control: no-store, no-cache, must-revalidate\r\nPragma: no-cache\r\nExpires: 0\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, deadline.Token);
            if (first[0] != "HEAD") await stream.WriteAsync(body, deadline.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    public static string GeneratePac(IEnumerable<string> domains, int socksPort)
    {
        var values = domains.Select(WarpRoutingPlanner.NormalizeDomain).Where(value => value is not null).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var json = System.Text.Json.JsonSerializer.Serialize(values);
        return $"function FindProxyForURL(url, host) {{ var d={json}; host=host.toLowerCase(); for(var i=0;i<d.length;i++){{if(host===d[i]||dnsDomainIs(host,'.'+d[i]))return 'PROXY 127.0.0.1:{socksPort}';}} return 'DIRECT'; }}";
    }

    public void Dispose() { _lifetime?.Cancel();_listener?.Stop();_ = _httpProxy.StopAsync(); }
}
