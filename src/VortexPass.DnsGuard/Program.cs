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
    internal sealed record Provider(string Name, Uri Uri, IPAddress[] BootstrapAddresses, HttpClient Client, bool PlainDns = false);
    private sealed record CachedResponse(byte[] Payload, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        if(args.Contains("--self-check",StringComparer.Ordinal))
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await using var testServer=new DnsServer(CreateProviders(Value(args,"--mode")));await testServer.BindAsync(timeout.Token,0);
            var selected=await testServer.SelectProviderAsync(timeout.Token);
            if(selected is null){Console.WriteLine("DNS self-check: DoH unavailable; adapter settings unchanged");return 3;}
            var task=testServer.RunAsync(timeout.Token);var ready=await testServer.CheckLoopbackAsync(timeout.Token);
            foreach(var host in (Value(args,"--hosts")??"").Split(',',StringSplitOptions.RemoveEmptyEntries).Where(h=>Uri.CheckHostName(h)==UriHostNameType.Dns).Take(16))
            {
                var query=DnsDiagnosticsService.Question(host);
                var response=await DnsDiagnosticsService.QueryUdpAsync(IPAddress.Loopback,query,timeout.Token,testServer.Port);
                var wire=DnsWirePolicy.Inspect(query,response);ready&=wire.HasAddress;
                Console.WriteLine($"DNS self-check target: {host}: {DnsDiagnosticsService.Classify(wire)}");
            }
            timeout.Cancel();try{await task;}catch(OperationCanceledException){}
            Console.WriteLine($"DNS self-check: resolver={selected.Name}, UDP/TCP={(ready?"confirmed":"unconfirmed")}; adapter settings unchanged");return ready?0:1;
        }
        var snapshotPath = Value(args, "--snapshot") ?? throw new ArgumentException("Missing --snapshot");
        if (args.Contains("--recover", StringComparer.OrdinalIgnoreCase))
            return await RestoreSnapshotAsync(snapshotPath) ? 0 : 2;
        if (!int.TryParse(Value(args, "--parent"), out var parentPid)) throw new ArgumentException("Missing --parent");

        using var lifetime = new CancellationTokenSource();
        DnsAdapterSnapshot? snapshot = null;
        var applied = false;
        try
        {
            var providers = CreateProviders(Value(args, "--mode"));
            var requiredHosts = (Value(args, "--hosts") ?? "example.com").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(h => Uri.CheckHostName(h) == UriHostNameType.Dns).Distinct().Take(64).ToArray();
            if (requiredHosts.Length == 0) requiredHosts = ["example.com"];
            await using var server = new DnsServer(providers, requiredHosts, Value(args, "--provider")) {DetailedDiagnostics=args.Contains("--diagnostic-details")};
            await server.BindAsync(lifetime.Token);
            var selected = await server.SelectProviderAsync(lifetime.Token);
            if (selected is null)
            {
                WriteStatus(new("dohUnavailable", "Ни один выбранный DNS не ответил", CheckedAt: DateTimeOffset.UtcNow));
                return 3;
            }
            var serverTask = server.RunAsync(lifetime.Token);
            if(!await server.CheckLoopbackAsync(lifetime.Token))throw new IOException("Локальный DNS не подтвердился по UDP/TCP; настройки адаптера не изменены.");

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
            var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> missing = [];
            while (true)
            {
                missing = await RouteProbePolicy.MissingWindowsDnsAsync(lifetime.Token, [DnsDiagnosticsService.ControlHost]);
                if (missing.Count == 0) break;
                rejected.Add(selected.Name);
                selected = await server.SelectProviderAsync(lifetime.Token, rejected);
                if (selected is null) throw new IOException($"Windows DNS не подтверждён: {string.Join(", ", missing)}");
                server.ClearCache();
                await FlushDnsAsync();
            }

            WriteStatus(new("ready", "Выбранный DNS готов", selected.Name, snapshot.InterfaceAlias, snapshot.InterfaceIndex, DateTimeOffset.UtcNow));
            server.ResolverChanged = resolver => Console.Out.WriteLine(JsonSerializer.Serialize(new {type="dns.diagnostic",layer="resolver",resolver,reason="resolver changed; Windows validation pending"},JsonOptions));
            var parentTask = MonitorParentAsync(parentPid, lifetime.Token);
            var inputTask = MonitorInputAsync(server,lifetime.Token);
            var healthTask=MonitorDnsAsync(server,snapshot,lifetime.Token);
            var finished=await Task.WhenAny(serverTask, parentTask, inputTask,healthTask);
            Console.Out.WriteLine(JsonSerializer.Serialize(new {type="dns.diagnostic",layer="lifecycle",reason=finished==serverTask?"listener stopped":finished==healthTask?"health monitor stopped":finished==parentTask?"parent exited":"stop requested"},JsonOptions));
            // Surface listener/monitor faults instead of silently restoring DNS.
            if(finished==serverTask||finished==healthTask)await finished;
            lifetime.Cancel();
            try { await Task.WhenAll(serverTask, parentTask, inputTask,healthTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
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

    private static Provider[] CreateProviders(string? mode = null) => mode == "google-udp" ?
    [
        new("Google UDP 8.8.8.8", new Uri("https://dns.google/dns-query"), [IPAddress.Parse("8.8.8.8")], new HttpClient(), true),
        new("Google UDP 8.8.4.4", new Uri("https://dns.google/dns-query"), [IPAddress.Parse("8.8.4.4")], new HttpClient(), true)
    ] :
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
            // Bootstrap connects to the resolver itself, never to an OS HTTP proxy.
            // Match the read-only diagnostic route.
            UseProxy = false,
            AllowAutoRedirect = false,
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

    private static async Task MonitorDnsAsync(DnsServer server,DnsAdapterSnapshot snapshot,CancellationToken token)
    {
        var consecutiveFailures = 0;
        while(!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15),token);
            var watch=Stopwatch.StartNew();
            var current=await CaptureActiveAdapterAsync();
            if(current is null||current.InterfaceGuid!=snapshot.InterfaceGuid)
            {WriteStatus(new("error","Активный адаптер изменился; DNS прежнего адаптера восстанавливается",CheckedAt:DateTimeOffset.UtcNow));return;}
            var local=await server.CheckLoopbackAsync(token);
            var missing=await RouteProbePolicy.MissingWindowsDnsAsync(token, [DnsDiagnosticsService.ControlHost]);
            Console.Out.WriteLine(JsonSerializer.Serialize(new {type="dns.diagnostic",layer="windows",durationMs=watch.ElapsedMilliseconds,missingDomains=missing.Count,loopback=local},JsonOptions));
            if(local && missing.Count==0) { consecutiveFailures = 0; continue; }
            if (++consecutiveFailures < 3) continue;
            WriteStatus(new("recovering",$"DNS перепроверяется: loopback {(local?"OK":"отказ")}; Windows: не разрешены {missing.Count} контрольных доменов",CheckedAt:DateTimeOffset.UtcNow));
            server.ClearCache();
            server.ResetProviderRejections();
            var resolver=await server.SelectProviderAsync(token,server.CurrentResolver is { } previous?[previous]:[]);
            await FlushDnsAsync();
            if(resolver is not null && await server.CheckLoopbackAsync(token) && (await RouteProbePolicy.MissingWindowsDnsAsync(token,[DnsDiagnosticsService.ControlHost])).Count==0)
            {
                consecutiveFailures = 0;
                WriteStatus(new("ready",$"DNS восстановлен и подтверждён за {watch.ElapsedMilliseconds} мс",resolver.Name,snapshot.InterfaceAlias,snapshot.InterfaceIndex,DateTimeOffset.UtcNow));
                continue;
            }
            WriteStatus(new("error","Локальный DNS или Windows DNS не подтвердился после смены resolver; исходные DNS восстанавливаются",CheckedAt:DateTimeOffset.UtcNow));
            return;
        }
    }

    private static async Task MonitorInputAsync(DnsServer server,CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(cancellationToken);
            if (line is null || line.Equals("stopAndRestore", StringComparison.OrdinalIgnoreCase)) return;
            if(line=="heartbeat details=1")server.DetailedDiagnostics=true;
            if(line=="heartbeat details=0")server.DetailedDiagnostics=false;
        }
    }

    private static async Task<DnsAdapterSnapshot?> CaptureActiveAdapterAsync()
    {
        const string script = "$c=Get-NetIPConfiguration | Where-Object {$_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway} | ForEach-Object {$i=Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4;$r=Get-NetRoute -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1; $d=Get-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex; [pscustomobject]@{InterfaceIndex=$_.InterfaceIndex;InterfaceAlias=$_.InterfaceAlias;InterfaceGuid=([guid][string]$_.NetAdapter.InterfaceGuid).ToString('D');Metric=([int]$i.InterfaceMetric+[int]$r.RouteMetric);DnsServersV4=@(($d|Where-Object AddressFamily -eq 2).ServerAddresses);DnsServersV6=@(($d|Where-Object AddressFamily -eq 23).ServerAddresses)}} | Sort-Object Metric | Select-Object -First 1; if($null -ne $c){$r4=Get-ItemProperty -LiteralPath ('HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\'+'{'+$c.InterfaceGuid+'}') -ErrorAction SilentlyContinue; $r6=Get-ItemProperty -LiteralPath ('HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters\\Interfaces\\'+$c.InterfaceGuid) -ErrorAction SilentlyContinue; $c|Add-Member -NotePropertyName AutomaticV4 -NotePropertyValue ([string]::IsNullOrWhiteSpace([string]$r4.NameServer)); $c|Add-Member -NotePropertyName AutomaticV6 -NotePropertyValue ([string]::IsNullOrWhiteSpace([string]$r6.NameServer)); $c|ConvertTo-Json -Compress}";
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

    private static async Task RestoreAsync(DnsAdapterSnapshot snapshot)
    {
        if(!Guid.TryParse(snapshot.InterfaceGuid,out var interfaceGuid))
        {
            if(snapshot.InterfaceIndex<=0||string.IsNullOrWhiteSpace(snapshot.InterfaceAlias))throw new InvalidDataException("Старый снимок DNS не содержит идентификатор адаптера.");
            var alias=snapshot.InterfaceAlias.Replace("'","''",StringComparison.Ordinal);
            var legacy=await RunPowerShellAsync($"$a=Get-NetAdapter -InterfaceIndex {snapshot.InterfaceIndex} -IncludeHidden -ErrorAction Stop;$d=Get-DnsClientServerAddress -InterfaceIndex {snapshot.InterfaceIndex};if($a.Name -ne '{alias}' -or -not(@($d.ServerAddresses)|Where-Object {{$_ -eq '127.0.0.1' -or $_ -eq '::1'}})){{throw 'Legacy adapter ownership could not be confirmed'}};([guid][string]$a.InterfaceGuid).ToString('D')");
            if(!Guid.TryParse(legacy.Trim(),out interfaceGuid))throw new InvalidDataException("Не удалось определить GUID старого снимка.");
            // The old writer did not reliably record DHCP/manual mode. Preserve
            // known addresses rather than silently replacing manual DNS by DHCP.
            snapshot.AutomaticV4=snapshot.DnsServersV4.Length==0;
            snapshot.AutomaticV6=snapshot.DnsServersV6.Length==0;
            Console.Out.WriteLine(JsonSerializer.Serialize(new {type="dns.diagnostic",layer="restore",reason="legacy snapshot: saved DNS addresses restored; original DHCP mode was not recorded reliably"},JsonOptions));
        }
        static string Values(IEnumerable<string> values) => string.Join(',', values.Where(value => IPAddress.TryParse(value, out _)).Select(value => $"'{value}'"));
        var v4 = snapshot.AutomaticV4 || snapshot.DnsServersV4.Length == 0
            ? "$v4|Set-DnsClientServerAddress -ResetServerAddresses"
            : $"$v4|Set-DnsClientServerAddress -ServerAddresses @({Values(snapshot.DnsServersV4)})";
        var v6 = snapshot.AutomaticV6 || snapshot.DnsServersV6.Length == 0
            ? "$v6|Set-DnsClientServerAddress -ResetServerAddresses"
            : $"$v6|Set-DnsClientServerAddress -ServerAddresses @({Values(snapshot.DnsServersV6)})";
        var offlineV4=snapshot.AutomaticV4?"":string.Join(',',snapshot.DnsServersV4.Where(v=>IPAddress.TryParse(v,out _)));
        var offlineV6=snapshot.AutomaticV6?"":string.Join(',',snapshot.DnsServersV6.Where(v=>IPAddress.TryParse(v,out _)));
        await RunPowerShellAsync($"$a=Get-NetAdapter -IncludeHidden | Where-Object {{[guid]$_.InterfaceGuid -eq '{interfaceGuid:D}'}} | Select-Object -First 1; if($a){{$d=Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex;$v4=$d|Where-Object AddressFamily -eq 2;$v6=$d|Where-Object AddressFamily -eq 23;{v4};{v6}}}else{{$p4='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\{{{interfaceGuid:D}}}';$p6='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters\\Interfaces\\{{{interfaceGuid:D}}}';if(Test-Path -LiteralPath $p4){{Set-ItemProperty -LiteralPath $p4 -Name NameServer -Value '{offlineV4}'}};if(Test-Path -LiteralPath $p6){{Set-ItemProperty -LiteralPath $p6 -Name NameServer -Value '{offlineV6}'}}}}", requireOutput: false);
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

    internal sealed class DnsServer : IAsyncDisposable
    {
        private readonly Provider[] _providers;
        private readonly ConcurrentDictionary<string,byte> _robloxReports=new();
        private void ReportRoblox(object detail)
        {
            var text=JsonSerializer.Serialize(detail,JsonOptions);
            if(_robloxReports.Count<128&&_robloxReports.TryAdd(text,0))Console.Out.WriteLine(text);
        }
        private readonly ConcurrentDictionary<string, CachedResponse> _cache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string,DateTimeOffset> _knownIpv4=new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _requests=new(96);
        private readonly List<UdpClient> _udp = [];
        private readonly List<TcpListener> _tcp = [];
        private Provider? _selected;

        public IReadOnlyList<string> RequiredHosts { get; }
        private readonly string? _preferredProvider;
        public DnsServer(Provider[] providers, IReadOnlyList<string>? requiredHosts = null, string? preferredProvider = null)
        {
            _providers = providers;
            _preferredProvider = preferredProvider;
            RequiredHosts = requiredHosts is { Count: > 0 } ? requiredHosts : ["example.com"];
        }

        public int Port=>((IPEndPoint)_udp[0].Client.LocalEndPoint!).Port;
        public Task BindAsync(CancellationToken cancellationToken,int port=53)
        {
            _udp.Add(new UdpClient(new IPEndPoint(IPAddress.Loopback, port)));
            port=Port;
            _tcp.Add(new TcpListener(IPAddress.Loopback, port));
            _tcp[^1].Start();
            try
            {
                _udp.Add(new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, port)));
                _tcp.Add(new TcpListener(IPAddress.IPv6Loopback, port));
                _tcp[^1].Server.DualMode = false;
                _tcp[^1].Start();
            }
            catch (SocketException) when (!Socket.OSSupportsIPv6) { }
            return Task.CompletedTask;
        }

        public Action<string>? ResolverChanged { get; set; }
        public bool DetailedDiagnostics {get;set;}
        private long _queryCount,_errorCount;
        private long _lastSummaryTicks=Environment.TickCount64;
        private void ReportQuery(object detail,bool failed=false)
        {
            Interlocked.Increment(ref _queryCount);if(failed)Interlocked.Increment(ref _errorCount);
            if(DetailedDiagnostics){Console.Out.WriteLine(JsonSerializer.Serialize(detail,JsonOptions));return;}
            var now=Environment.TickCount64;var previous=Interlocked.Read(ref _lastSummaryTicks);
            if(now-previous>=30000&&Interlocked.CompareExchange(ref _lastSummaryTicks,now,previous)==previous)
                Console.Out.WriteLine(JsonSerializer.Serialize(new{type="dns.diagnostic",layer="summary",queries=Interlocked.Exchange(ref _queryCount,0),transportErrors=Interlocked.Exchange(ref _errorCount,0)},JsonOptions));
        }
        public void ClearCache() { _cache.Clear();_knownIpv4.Clear(); }
        public void ResetProviderRejections()=>_rejectedProviders.Clear();
        private readonly ConcurrentDictionary<string,bool> _rejectedProviders = new(StringComparer.OrdinalIgnoreCase);
        public string? CurrentResolver => _selected?.Name;
        public async Task<Provider?> SelectProviderAsync(CancellationToken cancellationToken, IEnumerable<string>? rejected = null)
        {
            if (rejected is not null) foreach (var name in rejected) _rejectedProviders.TryAdd(name,true);
            IReadOnlyList<string> required = [DnsDiagnosticsService.ControlHost];
            var attempts = await Task.WhenAll(_providers.Where(p => !_rejectedProviders.ContainsKey(p.Name)).Select(async provider =>
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
                            var query=BuildQuery(host);
                            var response = await QueryAsync(provider, query, cancellationToken);
                            return DnsWirePolicy.Inspect(query,response).HasAddress;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch { return false; }
                    }));
                    var succeeded = checks.Count(result => result);
                    // All three primary services and most auxiliary routes must
                    // resolve before Windows is switched to this provider.
                    return succeeded == required.Count
                        ? (Provider: provider, Ms: watch.ElapsedMilliseconds)
                        : (Provider: (Provider?)null, Ms: long.MaxValue);
                }
                catch { return (Provider: (Provider?)null, Ms: long.MaxValue); }
            }));
            _selected = attempts.Where(item => item.Provider is not null)
                .OrderByDescending(item => item.Provider!.Name.Equals(_preferredProvider, StringComparison.OrdinalIgnoreCase) || _preferredProvider is "8.8.8.8" or "8.8.4.4" && item.Provider.Name.EndsWith(" "+_preferredProvider,StringComparison.Ordinal))
                .ThenBy(item => item.Ms).Select(item => item.Provider).FirstOrDefault();
            return _selected;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var listeners=_udp.Select(client=>RunUdpAsync(client,cancellationToken))
                .Concat(_tcp.Select(listener=>RunTcpAsync(listener,cancellationToken))).ToArray();
            // One dead listener must not remain hidden behind the other listeners.
            var completed=await Task.WhenAny(listeners);
            await completed;
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("DNS listener stopped unexpectedly");
        }

        private async Task RunUdpAsync(UdpClient client, CancellationToken cancellationToken)
        {
            using var sendLock = new SemaphoreSlim(1, 1);
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await client.ReceiveAsync(cancellationToken); }
                catch (OperationCanceledException) { break; }
                // Windows can report an ICMP error from a client which has already
                // closed its UDP port. Keep serving subsequent DNS requests.
                catch(SocketException ex)when(ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.MessageSize){continue;}
                if(!_requests.Wait(0))
                {try {await client.SendAsync(DnsWirePolicy.ServFail(packet.Buffer),packet.RemoteEndPoint,cancellationToken);}catch{}continue;}
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var response = DnsWirePolicy.ForUdp(packet.Buffer,await ResolveAsync(packet.Buffer, cancellationToken));
                        await sendLock.WaitAsync(cancellationToken);
                        try { await client.SendAsync(response, packet.RemoteEndPoint, cancellationToken); }
                        finally { sendLock.Release(); }
                    }
                    catch { }
                    finally {_requests.Release();}
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
                if(!_requests.Wait(0)){client.Dispose();continue;}
                _ = Task.Run(async () => { try { await HandleTcpAsync(client, cancellationToken); } catch { client.Dispose(); } finally {_requests.Release();} }, cancellationToken);
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
                    using var idle=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idle.CancelAfter(TimeSpan.FromSeconds(15));
                    if (!await ReadExactAsync(stream, lengthBytes, idle.Token)) return;
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
                return WithId(DnsWirePolicy.Age(cached.Payload, DateTimeOffset.UtcNow - cached.CreatedAt), query[0], query[1]);
            var normalizedQuery=WithId(query,0,0);
            var lazy=_inflight.GetOrAdd(key,_=>new Lazy<Task<byte[]>>(()=>ResolveUncachedAsync(normalizedQuery,key,cancellationToken)));
            var task=lazy.Value;
            try { return WithId(await task,query[0],query[1]); }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException) { return DnsWirePolicy.ServFail(query); }
            finally { if(task.IsCompleted)_inflight.TryRemove(new KeyValuePair<string,Lazy<Task<byte[]>>>(key,lazy)); }
        }

        private readonly ConcurrentDictionary<string,Lazy<Task<byte[]>>> _inflight = new();
        private readonly SemaphoreSlim _queryGate = new(16);
        private async Task<byte[]> ResolveUncachedAsync(byte[] query,string key,CancellationToken cancellationToken)
        {
            var eligible = _providers.Where(p => !_rejectedProviders.ContainsKey(p.Name)).ToArray();
            var selected=_selected;
            var ordered = selected is null ? eligible : [selected, .. eligible.Where(provider => provider != selected)];
            var result=await StableDnsFallback.QueryAsync(ordered,(provider,token)=>QueryAsync(provider,query,token),cancellationToken);
            var payload=result.Payload;
            var robloxResolver=result.Provider.Name;
            if(RobloxDnsRecovery.Needed(query,payload))
            {
                var reserve=ordered.FirstOrDefault(p=>p!=result.Provider&&p.PlainDns);
                if(reserve is not null)payload=await RobloxDnsRecovery.ResolveAsync(query,payload,t=>QueryAsync(reserve,query,t),cancellationToken);
                if(!ReferenceEquals(payload,result.Payload)&&reserve is not null)robloxResolver=reserve.Name;
                if(RobloxDnsRecovery.Needed(query,payload))
                {
                    using var recovery=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);recovery.CancelAfter(TimeSpan.FromSeconds(4));
                    try
                    {
                        async Task<byte[]> Direct(byte[] q,CancellationToken t)=>(await DnsDiagnosticsService.QueryPlainAsync(IPAddress.Parse("9.9.9.9"),q,t)).Payload;
                        var alternate=await Direct(query,recovery.Token);
                        if(DnsWirePolicy.Inspect(query,alternate).HasAddress)payload=alternate;
                        else payload=await RobloxDnsRecovery.FollowAliasesAsync(query,alternate,Direct,recovery.Token);
                        if(!DnsWirePolicy.Inspect(query,payload).HasAddress)payload=await RobloxDnsRecovery.FollowAliasesAsync(query,result.Payload,Direct,recovery.Token);
                        if(DnsWirePolicy.Inspect(query,payload).HasAddress)robloxResolver="Quad9 UDP/TCP 9.9.9.9 (Roblox only)";
                    }
                    catch(Exception ex)when(!cancellationToken.IsCancellationRequested&&ex is IOException or SocketException or OperationCanceledException)
                    {ReportRoblox(new {type="dns.diagnostic",layer="roblox-direct-reserve",state="failed",reason=YoutubeRouteProbe.Error(ex)});}
                }
                if(!DnsWirePolicy.Inspect(query,payload).HasAddress)payload=result.Payload;
                ReportRoblox(new {type="dns.diagnostic",layer="roblox-direct",host=DnsWirePolicy.QuestionName(query),state=DnsDiagnosticsService.Classify(DnsWirePolicy.Inspect(query,payload)),resolver=robloxResolver,recovered=!ReferenceEquals(payload,result.Payload),alias=DnsWirePolicy.LastAlias(query,result.Payload).Target});
            }
            if(RobloxDnsRecovery.IsScoped(query))payload=DnsWirePolicy.CompactAddressReply(query,payload);
            var wire=DnsWirePolicy.Inspect(query,payload);
            if(_cache.Count>4096)
                foreach(var entry in _cache.OrderBy(p=>p.Value.ExpiresAt).Take(1024))_cache.TryRemove(entry.Key,out _);
            var normalized=WithId(payload,0,0);
            if(wire.CacheSeconds>0)_cache[key]=new(normalized,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddSeconds(wire.CacheSeconds));
            return WithId(normalized,query[0],query[1]);
        }
        private async Task<byte[]> QueryAsync(Provider provider, byte[] query, CancellationToken cancellationToken)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _queryGate.WaitAsync(timeout.Token);
            var watch=Stopwatch.StartNew();
            int? responseCode=null;
            try
            {
            if (provider.PlainDns)
            {
                var exchange=await DnsDiagnosticsService.QueryPlainAsync(provider.BootstrapAddresses[0],query,timeout.Token);
                var plain=exchange.Payload;
                var inspected=DnsWirePolicy.Inspect(query,plain);
                ReportQuery(new {type="dns.diagnostic",layer=exchange.Transport,udpState=exchange.UdpState,resolver=provider.Name,host=inspected.Name,recordType=inspected.Type,state=DnsDiagnosticsService.Classify(inspected),durationMs=watch.ElapsedMilliseconds});
                return plain;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, provider.Uri)
            { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            request.Headers.Accept.ParseAdd("application/dns-message");
            request.Content = new ByteArrayContent(query);
            request.Content.Headers.ContentType = new("application/dns-message");
            using var response = await provider.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            var wire=DnsWirePolicy.Inspect(query,payload);
            responseCode=wire.ResponseCode;
            ReportQuery(new {type="dns.diagnostic",layer="doh",resolver=provider.Name,host=wire.Name,recordType=wire.Type,state=DnsDiagnosticsService.Classify(wire),durationMs=watch.ElapsedMilliseconds});
            if(wire.Truncated)throw new IOException("DoH returned truncated response");
            return payload;
            }
            catch(Exception ex) when(ex is IOException or HttpRequestException or OperationCanceledException)
            {
                if(!cancellationToken.IsCancellationRequested)ReportQuery(new {type="dns.diagnostic",layer=provider.PlainDns?"udp/tcp":"doh",resolver=provider.Name,responseCode,durationMs=watch.ElapsedMilliseconds,host=DnsWirePolicy.QuestionName(query),reason=ex is OperationCanceledException?"timeout":ex is HttpRequestException http?http.HttpRequestError.ToString():ex.GetType().Name},true);
                throw;
            }
            finally {_queryGate.Release();}
        }

        internal static async Task<byte[]> QueryPlainDnsAsync(IPAddress address, byte[] query, CancellationToken token, int port = 53)
            => await DnsDiagnosticsService.QueryUdpAsync(address,query,token,port);

        private static byte[] WithId(byte[] payload, byte high, byte low)
        {
            var copy = payload.ToArray(); copy[0] = high; copy[1] = low; return copy;
        }

        private static bool IsUsableAnswer(byte[] response) => DnsResponsePolicy.HasUsableAnswer(response);

        public async Task<bool> CheckLoopbackAsync(CancellationToken token)
        {
            try
            {
              foreach(var address in _udp.Select(c=>((IPEndPoint)c.Client.LocalEndPoint!).Address))
              {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var query=BuildQuery(DnsDiagnosticsService.ControlHost);
                var watch=Stopwatch.StartNew();using var udp=new UdpClient(address.AddressFamily);
                await udp.SendAsync(query,new IPEndPoint(address,Port),timeout.Token);
                var reply=await udp.ReceiveAsync(timeout.Token);
                if(!DnsWirePolicy.Inspect(query,reply.Buffer).HasAddress)return false;
                Console.Out.WriteLine(JsonSerializer.Serialize(new{type="dns.diagnostic",layer="loopback-udp",family=address.AddressFamily.ToString(),durationMs=watch.ElapsedMilliseconds,responseCode=0},JsonOptions));
                watch.Restart();using var tcp=new TcpClient(address.AddressFamily);await tcp.ConnectAsync(address,Port,timeout.Token);
                var stream=tcp.GetStream();await stream.WriteAsync(new byte[]{(byte)(query.Length>>8),(byte)query.Length},timeout.Token);await stream.WriteAsync(query,timeout.Token);
                var size=new byte[2];if(!await ReadExactAsync(stream,size,timeout.Token))return false;
                var data=new byte[(size[0]<<8)|size[1]];if(!await ReadExactAsync(stream,data,timeout.Token))return false;
                if(!DnsWirePolicy.Inspect(query,data).HasAddress)return false;
                Console.Out.WriteLine(JsonSerializer.Serialize(new{type="dns.diagnostic",layer="loopback-tcp",family=address.AddressFamily.ToString(),durationMs=watch.ElapsedMilliseconds,responseCode=0},JsonOptions));
              }
              return true;
            }
            catch(Exception ex)when(ex is SocketException or IOException or OperationCanceledException){token.ThrowIfCancellationRequested();return false;}
        }

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
