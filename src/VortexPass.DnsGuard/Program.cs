using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

namespace VortexPass.DnsGuard;

internal static class Program
{
    private sealed record Provider(string Name, Uri Uri, IPAddress[] BootstrapAddresses, HttpClient Client);
    private sealed record CachedResponse(byte[] Payload, DateTimeOffset ExpiresAt);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        var snapshotPath = Value(args, "--snapshot") ?? throw new ArgumentException("Missing --snapshot");
        if (args.Contains("--recover", StringComparer.OrdinalIgnoreCase))
            return await RestoreSnapshotAsync(snapshotPath) ? 0 : 2;
        if (!int.TryParse(Value(args, "--parent"), out var parentPid)) throw new ArgumentException("Missing --parent");

        using var lifetime = new CancellationTokenSource();
        DnsAdapterSnapshot? snapshot = null;
        var applied = false;
        try
        {
            var providers = CreateProviders();
            await using var server = new DnsServer(providers);
            await server.BindAsync(lifetime.Token);
            var selected = await server.SelectProviderAsync(lifetime.Token);
            if (selected is null)
            {
                WriteStatus(new("dohUnavailable", "Ни один защищённый DNS не ответил", CheckedAt: DateTimeOffset.UtcNow));
                return 3;
            }
            var serverTask = server.RunAsync(lifetime.Token);

            snapshot = await CaptureActiveAdapterAsync();
            if (snapshot is null)
            {
                WriteStatus(new("error", "Не найден активный адаптер с интернет-шлюзом", selected.Name, CheckedAt: DateTimeOffset.UtcNow));
                return 4;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            await WriteAtomicAsync(snapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions));

            applied = true;
            await SetLoopbackDnsAsync(snapshot.InterfaceIndex);
            await FlushDnsAsync();
            if (!await VerifyWindowsResolutionAsync())
                throw new IOException("Windows не подтвердил запрос через локальный DNS");

            WriteStatus(new("ready", "Защищённый DNS готов", selected.Name, snapshot.InterfaceAlias, snapshot.InterfaceIndex, DateTimeOffset.UtcNow));
            var parentTask = MonitorParentAsync(parentPid, lifetime.Token);
            var inputTask = MonitorInputAsync(lifetime.Token);
            await Task.WhenAny(serverTask, parentTask, inputTask);
            lifetime.Cancel();
            try { await Task.WhenAll(serverTask, parentTask, inputTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            return 0;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            WriteStatus(new("portBusy", "Порт DNS 53 уже занят; настройки сети не изменены", CheckedAt: DateTimeOffset.UtcNow));
            return 5;
        }
        catch (Exception ex)
        {
            WriteStatus(new("error", ex.Message, CheckedAt: DateTimeOffset.UtcNow));
            return 1;
        }
        finally
        {
            lifetime.Cancel();
            if (applied && snapshot is not null)
            {
                try
                {
                    await RestoreAsync(snapshot);
                    await FlushDnsAsync();
                    if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
                    WriteStatus(new("restored", "DNS восстановлен", Adapter: snapshot.InterfaceAlias, InterfaceIndex: snapshot.InterfaceIndex, CheckedAt: DateTimeOffset.UtcNow));
                }
                catch (Exception ex) { WriteStatus(new("restoreError", $"Не удалось восстановить DNS: {ex.Message}", CheckedAt: DateTimeOffset.UtcNow)); }
            }
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static Provider[] CreateProviders() =>
    [
        CreateProvider("Cloudflare", "https://cloudflare-dns.com/dns-query", "1.1.1.1", "1.0.0.1"),
        CreateProvider("Google", "https://dns.google/dns-query", "8.8.8.8", "8.8.4.4"),
        CreateProvider("Quad9", "https://dns.quad9.net/dns-query", "9.9.9.9", "149.112.112.112")
    ];

    private static Provider CreateProvider(string name, string uri, params string[] addresses)
    {
        var parsed = addresses.Select(IPAddress.Parse).ToArray();
        var next = -1;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(3),
            ConnectCallback = async (_, token) =>
            {
                var address = parsed[(Interlocked.Increment(ref next) & int.MaxValue) % parsed.Length];
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, 443), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new(name, new Uri(uri), parsed, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });
    }

    private static async Task MonitorParentAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (ArgumentException) { }
    }

    private static async Task MonitorInputAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(cancellationToken);
            if (line is null || line.Equals("stopAndRestore", StringComparison.OrdinalIgnoreCase)) return;
        }
    }

    private static async Task<DnsAdapterSnapshot?> CaptureActiveAdapterAsync()
    {
        const string script = "$c=Get-NetIPConfiguration | Where-Object {$_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway} | ForEach-Object {$i=Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4; $d=Get-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex; [pscustomobject]@{InterfaceIndex=$_.InterfaceIndex;InterfaceAlias=$_.InterfaceAlias;InterfaceGuid=$_.NetAdapter.InterfaceGuid.Guid;Metric=$i.InterfaceMetric;DnsServersV4=@(($d|Where-Object AddressFamily -eq 2).ServerAddresses);DnsServersV6=@(($d|Where-Object AddressFamily -eq 23).ServerAddresses)}} | Sort-Object Metric | Select-Object -First 1; if($null -ne $c){$r4=Get-ItemProperty -LiteralPath ('HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\'+$c.InterfaceGuid) -ErrorAction SilentlyContinue; $r6=Get-ItemProperty -LiteralPath ('HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters\\Interfaces\\'+$c.InterfaceGuid) -ErrorAction SilentlyContinue; $c|Add-Member -NotePropertyName AutomaticV4 -NotePropertyValue ([string]::IsNullOrWhiteSpace([string]$r4.NameServer)); $c|Add-Member -NotePropertyName AutomaticV6 -NotePropertyValue ([string]::IsNullOrWhiteSpace([string]$r6.NameServer)); $c|ConvertTo-Json -Compress}";
        var output = await RunPowerShellAsync(script);
        if (string.IsNullOrWhiteSpace(output)) return null;
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        static string[] Servers(JsonElement root, string name) =>
            root.TryGetProperty(name, out var dns) && dns.ValueKind == JsonValueKind.Array
                ? dns.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray()
                : [];
        return new DnsAdapterSnapshot
        {
            InterfaceIndex = root.GetProperty("InterfaceIndex").GetInt32(),
            InterfaceAlias = root.GetProperty("InterfaceAlias").GetString() ?? string.Empty,
            InterfaceGuid = root.GetProperty("InterfaceGuid").GetString() ?? string.Empty,
            DnsServersV4 = Servers(root, "DnsServersV4"),
            DnsServersV6 = Servers(root, "DnsServersV6"),
            AutomaticV4 = root.TryGetProperty("AutomaticV4", out var automatic4) && automatic4.GetBoolean(),
            AutomaticV6 = root.TryGetProperty("AutomaticV6", out var automatic6) && automatic6.GetBoolean(),
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static Task SetLoopbackDnsAsync(int index) => RunPowerShellAsync(
        $"$d=Get-DnsClientServerAddress -InterfaceIndex {index}; $d|Where-Object AddressFamily -eq 2|Set-DnsClientServerAddress -ServerAddresses @('127.0.0.1'); $d|Where-Object AddressFamily -eq 23|Set-DnsClientServerAddress -ServerAddresses @('::1')", requireOutput: false);

    private static Task RestoreAsync(DnsAdapterSnapshot snapshot)
    {
        static string Values(IEnumerable<string> values) => string.Join(',', values.Where(value => IPAddress.TryParse(value, out _)).Select(value => $"'{value}'"));
        var v4 = snapshot.AutomaticV4 || snapshot.DnsServersV4.Length == 0
            ? "$v4|Set-DnsClientServerAddress -ResetServerAddresses"
            : $"$v4|Set-DnsClientServerAddress -ServerAddresses @({Values(snapshot.DnsServersV4)})";
        var v6 = snapshot.AutomaticV6 || snapshot.DnsServersV6.Length == 0
            ? "$v6|Set-DnsClientServerAddress -ResetServerAddresses"
            : $"$v6|Set-DnsClientServerAddress -ServerAddresses @({Values(snapshot.DnsServersV6)})";
        return RunPowerShellAsync($"$d=Get-DnsClientServerAddress -InterfaceIndex {snapshot.InterfaceIndex};$v4=$d|Where-Object AddressFamily -eq 2;$v6=$d|Where-Object AddressFamily -eq 23;{v4};{v6}", requireOutput: false);
    }

    private static async Task<bool> RestoreSnapshotAsync(string path)
    {
        if (!File.Exists(path)) return true;
        try
        {
            var snapshot = JsonSerializer.Deserialize<DnsAdapterSnapshot>(await File.ReadAllTextAsync(path), JsonOptions);
            if (snapshot is null) return false;
            await RestoreAsync(snapshot);
            await FlushDnsAsync();
            File.Delete(path);
            WriteStatus(new("restored", "DNS восстановлен после предыдущего завершения", Adapter: snapshot.InterfaceAlias, InterfaceIndex: snapshot.InterfaceIndex, CheckedAt: DateTimeOffset.UtcNow));
            return true;
        }
        catch (Exception ex) { WriteStatus(new("restoreError", ex.Message, CheckedAt: DateTimeOffset.UtcNow)); return false; }
    }

    private static async Task<string> RunPowerShellAsync(string script, bool requireOutput = true)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';" + script));
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Не удалось запустить PowerShell");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? $"PowerShell exit {process.ExitCode}" : error.Trim());
        if (requireOutput && string.IsNullOrWhiteSpace(output)) return string.Empty;
        return output.Trim();
    }

    private static async Task FlushDnsAsync()
    {
        using var process = Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns") { UseShellExecute = false, CreateNoWindow = true });
        if (process is not null) await process.WaitForExitAsync();
    }

    private static async Task<bool> VerifyWindowsResolutionAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return (await Dns.GetHostAddressesAsync("www.youtube.com", timeout.Token)).Length > 0;
        }
        catch { return false; }
    }

    private static async Task WriteAtomicAsync(string path, string content)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static void WriteStatus(DnsGuardStatus status) => Console.Out.WriteLine(JsonSerializer.Serialize(status, JsonOptions));

    private sealed class DnsServer : IAsyncDisposable
    {
        private readonly Provider[] _providers;
        private readonly ConcurrentDictionary<string, CachedResponse> _cache = new(StringComparer.Ordinal);
        private readonly List<UdpClient> _udp = [];
        private readonly List<TcpListener> _tcp = [];
        private Provider? _selected;

        public DnsServer(Provider[] providers) => _providers = providers;

        public Task BindAsync(CancellationToken cancellationToken)
        {
            _udp.Add(new UdpClient(new IPEndPoint(IPAddress.Loopback, 53)));
            _tcp.Add(new TcpListener(IPAddress.Loopback, 53));
            _tcp[^1].Start();
            try
            {
                _udp.Add(new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 53)));
                _tcp.Add(new TcpListener(IPAddress.IPv6Loopback, 53));
                _tcp[^1].Server.DualMode = false;
                _tcp[^1].Start();
            }
            catch (SocketException) when (!Socket.OSSupportsIPv6) { }
            return Task.CompletedTask;
        }

        public async Task<Provider?> SelectProviderAsync(CancellationToken cancellationToken)
        {
            var required = new[]
            {
                "www.youtube.com", "i.ytimg.com",
                "discord.com", "updates.discord.com", "stable.dl2.discordapp.net", "gateway.discord.gg",
                "web.telegram.org", "kws2.web.telegram.org"
            };
            var attempts = await Task.WhenAll(_providers.Select(async provider =>
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    // Check the service domains concurrently. A blocked or slow
                    // auxiliary hostname must not add its full timeout to app
                    // startup, while the threshold still prevents selecting
                    // a resolver that only answers a generic control domain.
                    var checks = await Task.WhenAll(required.Select(async host =>
                    {
                        try
                        {
                            var response = await QueryAsync(provider, BuildQuery(host), cancellationToken);
                            return IsUsableAnswer(response);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch { return false; }
                    }));
                    var succeeded = checks.Count(result => result);
                    // All three primary services and most auxiliary routes must
                    // resolve before Windows is switched to this provider.
                    return succeeded >= 7
                        ? (Provider: provider, Ms: watch.ElapsedMilliseconds)
                        : (Provider: (Provider?)null, Ms: long.MaxValue);
                }
                catch { return (Provider: (Provider?)null, Ms: long.MaxValue); }
            }));
            _selected = attempts.Where(item => item.Provider is not null).OrderBy(item => item.Ms).Select(item => item.Provider).FirstOrDefault();
            return _selected;
        }

        public Task RunAsync(CancellationToken cancellationToken) => Task.WhenAll(
            _udp.Select(client => RunUdpAsync(client, cancellationToken))
                .Concat(_tcp.Select(listener => RunTcpAsync(listener, cancellationToken))));

        private async Task RunUdpAsync(UdpClient client, CancellationToken cancellationToken)
        {
            using var sendLock = new SemaphoreSlim(1, 1);
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await client.ReceiveAsync(cancellationToken); }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var response = await ResolveAsync(packet.Buffer, cancellationToken);
                        await sendLock.WaitAsync(cancellationToken);
                        try { await client.SendAsync(response, packet.RemoteEndPoint, cancellationToken); }
                        finally { sendLock.Release(); }
                    }
                    catch { }
                }, cancellationToken);
            }
        }

        private async Task RunTcpAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cancellationToken); }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(() => HandleTcpAsync(client, cancellationToken), cancellationToken);
            }
        }

        private async Task HandleTcpAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                // Windows may reuse one TCP DNS connection for several queries.
                // Serve it until the client closes instead of answering only the
                // first request and turning the following ones into HOST_NOT_FOUND.
                while (!cancellationToken.IsCancellationRequested)
                {
                    var lengthBytes = new byte[2];
                    if (!await ReadExactAsync(stream, lengthBytes, cancellationToken)) return;
                    var length = (lengthBytes[0] << 8) | lengthBytes[1];
                    if (length is < 12 or > 65535) return;
                    var query = new byte[length];
                    if (!await ReadExactAsync(stream, query, cancellationToken)) return;
                    var response = await ResolveAsync(query, cancellationToken);
                    await stream.WriteAsync(new byte[] { (byte)(response.Length >> 8), (byte)response.Length }, cancellationToken);
                    await stream.WriteAsync(response, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }
        }

        private async Task<byte[]> ResolveAsync(byte[] query, CancellationToken cancellationToken)
        {
            if (query.Length < 12) throw new InvalidDataException("Short DNS query");
            var key = Convert.ToHexString(query.AsSpan(2));
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return WithId(cached.Payload, query[0], query[1]);
            var ordered = _selected is null ? _providers : [_selected, .. _providers.Where(provider => provider != _selected)];
            using var attemptsLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var attempts = ordered.Select(async provider =>
            {
                try
                {
                    var response = await QueryAsync(provider, query, attemptsLifetime.Token);
                    return (Provider: provider, Response: response);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { return (Provider: (Provider?)null, Response: (byte[]?)null); }
            }).ToList();
            (Provider? Provider, byte[]? Response) negative = (null, null);
            while (attempts.Count > 0)
            {
                var completed = await Task.WhenAny(attempts);
                attempts.Remove(completed);
                var result = await completed;
                if (result.Provider is null || result.Response is null) continue;
                // A successful HTTPS exchange may still contain SERVFAIL,
                // NXDOMAIN or an empty DNS answer. Keep waiting for another
                // resolver instead of poisoning Windows' cache with whichever
                // negative packet happened to arrive first.
                if (!DnsResponsePolicy.HasUsableAnswer(result.Response))
                {
                    if (negative.Response is null) negative = result;
                    continue;
                }
                attemptsLifetime.Cancel();
                _selected = result.Provider;
                var normalized = WithId(result.Response, 0, 0);
                _cache[key] = new(normalized, DateTimeOffset.UtcNow.AddSeconds(30));
                return WithId(normalized, query[0], query[1]);
            }
            if (negative.Provider is not null && negative.Response is not null)
            {
                _selected = negative.Provider;
                var normalized = WithId(negative.Response, 0, 0);
                _cache[key] = new(normalized, DateTimeOffset.UtcNow.AddSeconds(5));
                return WithId(normalized, query[0], query[1]);
            }
            throw new IOException("DoH unavailable");
        }

        private static async Task<byte[]> QueryAsync(Provider provider, byte[] query, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, provider.Uri);
            request.Headers.Accept.ParseAdd("application/dns-message");
            request.Content = new ByteArrayContent(query);
            request.Content.Headers.ContentType = new("application/dns-message");
            using var response = await provider.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (payload.Length < 12) throw new InvalidDataException("Short DoH response");
            return payload;
        }

        private static byte[] WithId(byte[] payload, byte high, byte low)
        {
            var copy = payload.ToArray(); copy[0] = high; copy[1] = low; return copy;
        }

        private static bool IsUsableAnswer(byte[] response) => DnsResponsePolicy.HasUsableAnswer(response);

        private static byte[] BuildQuery(string host)
        {
            using var stream = new MemoryStream();
            stream.Write(new byte[] { 0x47, 0x21, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            foreach (var label in host.Split('.')) { var bytes = Encoding.ASCII.GetBytes(label); stream.WriteByte((byte)bytes.Length); stream.Write(bytes); }
            stream.Write(new byte[] { 0, 0, 1, 0, 1 });
            return stream.ToArray();
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var client in _udp) client.Dispose();
            foreach (var listener in _tcp) listener.Stop();
            foreach (var provider in _providers) provider.Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
