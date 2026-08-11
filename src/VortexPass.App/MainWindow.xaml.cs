using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using VortexPass.Core.Models;
using VortexPass.Core.Services;
using Forms = System.Windows.Forms;

namespace VortexPass.App;

public partial class MainWindow : Window
{
    private sealed record AdvisorCandidate(string Level, string Label, int Healthy, int Total, long? LatencyMs, string Summary, long Score);

    private readonly AppSettingsStore _settingsStore;
    private readonly WindowsStartupService _startupService = new();
    private readonly ExternalComponentService _components;
    private readonly ServiceHealthCheckService _healthChecks = new();
    private readonly SystemProxyManager _proxyManager;
    private readonly HostsFileManager _hostsManager;
    private readonly RobloxThumbnailResolver _robloxThumbnailResolver = new();
    private readonly UserDomainListService _domainLists;
    private readonly GitHubUpdateService _updates = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayProtectionItem;
    private readonly HttpClient _advisorHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _applicationLogPath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _protectionGate = new(1, 1);
    private readonly Dictionary<string, ServiceStatus> _serviceStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServiceHealthResult> _healthStatuses = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings = new();
    private BypassProfileSettings _activeProfiles = new();
    private HostsFileStatus _hostsStatus = new(false, 0, null, "hosts не применён");
    private UpdateCheckResult? _lastUpdate;
    private bool _allowClose;
    private bool _shutdownStarted;
    private bool _webReady;
    private bool _protectionRequested;
    private bool _protectionTransitioning;
    private DateTimeOffset? _protectionStarted;
    private long _baselineBytes;
    private long _baselinePackets;
    private DateTimeOffset _nextHealthCheckUtc = DateTimeOffset.MinValue;
    private int _healthGeneration;
    private bool _advisorRunning;

    public MainWindow()
    {
        InitializeComponent();
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VortexPass");
        _settingsStore = new AppSettingsStore(appData);
        _applicationLogPath = Path.Combine(appData, "app.log");
        _components = new ExternalComponentService(AppContext.BaseDirectory);
        _proxyManager = new SystemProxyManager(appData);
        _hostsManager = new HostsFileManager(AppContext.BaseDirectory, appData);
        _domainLists = new UserDomainListService(appData);
        _components.StatusChanged += ComponentStatusChanged;
        _components.DiagnosticReceived += ComponentDiagnosticReceived;

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Text = "VortexPass",
            Visible = false,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.Add("Открыть", null, (_, _) => RestoreWindow());
        _trayProtectionItem = new Forms.ToolStripMenuItem("Запустить защиту");
        _trayProtectionItem.Click += async (_, _) =>
        {
            if (_advisorRunning || _protectionTransitioning) return;
            await SetProtectionAsync(!_protectionRequested);
        };
        _trayIcon.ContextMenuStrip.Items.Add(_trayProtectionItem);
        _trayIcon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add("Выход", null, async (_, _) => await ShutdownAsync());
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();
        _advisorHttp.DefaultRequestHeaders.UserAgent.ParseAdd("VortexPass/0.1 (+https://github.com/avov53/VortexPass)");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ResetApplicationLogAsync();
        _settings = await _settingsStore.LoadAsync(_lifetime.Token);
        _settings.Profiles ??= new BypassProfileSettings();
        _settings.GameFilter ??= new GameFilterSettings();
        _settings.GameFilter.SelectedGames ??= [];
        _settings.GameFilter.Protocol = WinwsProfileComposer.NormalizeGameProtocol(_settings.GameFilter.Protocol);
        _settings.CustomBypassDomains ??= [];
        _settings.ExcludedDomains ??= DefaultDomainLists.CreateExclusions();
        _hostsStatus = _hostsManager.GetStatus();
        if (_hostsStatus.Applied)
        {
            try { _hostsStatus = await _hostsManager.RestoreAsync(_lifetime.Token); }
            catch { _hostsStatus = _hostsManager.GetStatus(); }
        }
        if (_settings.ProfileSettingsVersion < 2)
        {
            _settings.Profiles.YouTube = false;
            _settings.Profiles.Discord = false;
            _settings.Profiles.Telegram = false;
            _settings.ProfileSettingsVersion = 2;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        if (!_settings.StartWithWindows)
            _startupService.SetEnabled(false, Path.Combine(AppContext.BaseDirectory, "VortexPass.exe"));
        _trayIcon.Visible = true;
        await _components.LoadManifestAsync(_lifetime.Token);

        var webDirectory = Path.Combine(AppContext.BaseDirectory, "web");
        if (!File.Exists(Path.Combine(webDirectory, "index.html")))
        {
            System.Windows.MessageBox.Show("Не найдены файлы интерфейса. Запустите dist.bat заново.", "VortexPass", MessageBoxButton.OK, MessageBoxImage.Error);
            await ShutdownAsync();
            return;
        }

        try
        {
            await DesignView.EnsureCoreWebView2Async();
            DesignView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            DesignView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            DesignView.CoreWebView2.WebMessageReceived += WebViewMessageReceived;
            DesignView.CoreWebView2.NavigationCompleted += WebViewNavigationCompleted;

            var currentVersion = CurrentVersion().ToString(3);
            var initialJson = JsonSerializer.Serialize(new
            {
                settings = new { autostart = _settings.StartWithWindows, tray = _settings.MinimizeToTray },
                profiles = new
                {
                    youtube = _settings.Profiles.YouTube,
                    discord = _settings.Profiles.Discord,
                    telegram = _settings.Profiles.Telegram,
                    web = _settings.Profiles.Web,
                    gameFilter = _settings.Profiles.GameFilter
                },
                gameFilter = new
                {
                    selectedGames = _settings.GameFilter.SelectedGames,
                    protocol = WinwsProfileComposer.NormalizeGameProtocol(_settings.GameFilter.Protocol)
                },
                domains = new
                {
                    bypass = _settings.CustomBypassDomains,
                    exclusions = _settings.ExcludedDomains
                },
                bypassLevel = WinwsProfileComposer.NormalizeLevel(_settings.BypassLevel),
                app = new { version = currentVersion, platform = Environment.OSVersion.VersionString }
            });
            await DesignView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.__vortexSettings={initialJson}.settings;window.__vortexRuntime={initialJson};");
            DesignView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "vortexpass.local", webDirectory, CoreWebView2HostResourceAccessKind.Allow);
            DesignView.CoreWebView2.Navigate("https://vortexpass.local/index.html");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось запустить интерфейс VortexPass.\n{ex.Message}", "VortexPass", MessageBoxButton.OK, MessageBoxImage.Error);
            await ShutdownAsync();
        }
    }

    private async void WebViewNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            System.Windows.MessageBox.Show($"Интерфейс не загрузился: {e.WebErrorStatus}", "VortexPass", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _webReady = true;
        await SendServiceSnapshotAsync();
        _ = RunMetricsLoopAsync(_lifetime.Token);
        _ = CheckForUpdatesAsync();
    }

    private async void WebViewMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            var type = root.GetProperty("type").GetString();
            var value = root.TryGetProperty("value", out var rawValue) && rawValue.ValueKind is JsonValueKind.True or JsonValueKind.False && rawValue.GetBoolean();

            switch (type)
            {
                case "window.drag":
                    BeginNativeDrag();
                    break;
                case "window.minimize":
                    WindowState = WindowState.Minimized;
                    break;
                case "window.maximize":
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    break;
                case "window.close":
                    if (_settings.MinimizeToTray) Hide();
                    else await ShutdownAsync();
                    break;
                case "settings.autostart":
                    await SetAutostartAsync(value);
                    break;
                case "settings.tray":
                    _settings.MinimizeToTray = value;
                    await _settingsStore.SaveAsync(_settings);
                    await PublishSettingsStatusAsync();
                    break;
                case "protection.set":
                    await SetProtectionAsync(value);
                    break;
                case "service.set":
                    var serviceId = root.TryGetProperty("serviceId", out var rawServiceId) ? rawServiceId.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(serviceId)) await SetProfileAsync(serviceId, value);
                    break;
                case "profile.set":
                    var bypassLevel = root.TryGetProperty("level", out var rawLevel) ? rawLevel.GetString() : null;
                    await SetBypassLevelAsync(bypassLevel);
                    break;
                case "advisor.run":
                    await RunProfileAdvisorAsync();
                    break;
                case "gamefilter.set":
                    await SetGameFilterAsync(root);
                    break;
                case "domains.set":
                    await SetDomainsAsync(root);
                    break;
                case "hosts.restore":
                    await RestoreHostsAsync();
                    break;
                case "update.check":
                    await CheckForUpdatesAsync();
                    break;
                case "update.install":
                    await InstallUpdateAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            await PostUiAsync(new { type = "log", level = "error", message = ex.Message });
        }
    }

    private async Task SetProtectionAsync(bool enabled)
    {
        if (_advisorRunning)
        {
            await LogToUiAsync("warn", "Дождитесь завершения автоматического подбора профиля.");
            return;
        }

        if (enabled)
        {
            if (!_settings.Profiles.AnyEnabled)
            {
                await LogToUiAsync("warn", "Сначала выберите хотя бы один профиль: YouTube, Discord, Telegram, Web или Game Filter.");
                await SendServiceSnapshotAsync();
                return;
            }
            if (_settings.Profiles.GameFilter && !_settings.GameFilter.HasSelectedGames)
            {
                await LogToUiAsync("warn", "Game Filter выбран, но игра не отмечена. Откройте вкладку Game Filter и выберите Roblox или Phasmophobia.");
                await SendServiceSnapshotAsync();
                return;
            }
            await ApplyProfilesAsync();
            return;
        }

        await StopProtectionAsync();
    }

    private async Task SetAutostartAsync(bool enabled)
    {
        var previous = _settings.StartWithWindows;
        try
        {
            _startupService.SetEnabled(enabled, Path.Combine(AppContext.BaseDirectory, "VortexPass.exe"));
            _settings.StartWithWindows = enabled;
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            _settings.StartWithWindows = previous;
            await LogToUiAsync("error", $"Не удалось изменить автозапуск: {ex.Message}");
        }
        await PublishSettingsStatusAsync();
    }

    private async Task RunProfileAdvisorAsync()
    {
        if (_advisorRunning) return;
        if (!_settings.Profiles.AnyEnabled)
        {
            await PostUiAsync(new { type = "advisor.status", state = "failed", error = "Сначала выберите хотя бы один сервис на дашборде." });
            return;
        }
        if (_settings.Profiles.GameFilter && !_settings.GameFilter.HasSelectedGames)
        {
            await PostUiAsync(new { type = "advisor.status", state = "failed", error = "Для Game Filter сначала выберите Roblox или Phasmophobia." });
            return;
        }

        _advisorRunning = true;
        var savedLevel = WinwsProfileComposer.NormalizeLevel(_settings.BypassLevel);
        var wasRunning = _activeProfiles.AnyEnabled && _components.GetStatuses().Any(x => x.Id == "dpi" && x.State == ServiceState.Running);
        var candidates = new List<AdvisorCandidate>();
        string? provider = null;
        try
        {
            await PostUiAsync(new { type = "advisor.status", state = "detecting", progress = 0, message = "Определяем провайдера…" });
            provider = await DetectProviderAsync(_lifetime.Token);
            await LogToUiAsync("info", provider is null
                ? "Провайдер не определён. Подбор продолжится по сетевым проверкам."
                : $"Автоподбор: определён провайдер {provider}.");

            if (wasRunning) await StopProtectionAsync();

            var levels = new[] { "light", "moderate", "medium", "complex", "aggressive" };
            for (var index = 0; index < levels.Length; index++)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var level = levels[index];
                _settings.BypassLevel = level;
                await PostUiAsync(new
                {
                    type = "advisor.status",
                    state = "testing",
                    provider,
                    level,
                    progress = index,
                    total = levels.Length,
                    message = $"Проверяем профиль «{LevelDisplayName(level)}»…"
                });

                await ApplyProfilesAsync();
                var running = _components.GetStatuses().Any(x => x.Id == "dpi" && x.State == ServiceState.Running);
                var results = new List<ServiceHealthResult>();
                if (running)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(18));
                    try
                    {
                        var checks = AdvisorServiceIds()
                            .Select(id => _healthChecks.CheckAsync(id, true, true, timeout.Token));
                        results.AddRange(await Task.WhenAll(checks));
                    }
                    catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { }
                }

                var healthy = results.Count(x => x.IsHealthy);
                var total = results.Count;
                var latency = results.Where(x => x.IsHealthy && x.LatencyMs.HasValue).Select(x => x.LatencyMs!.Value).DefaultIfEmpty().Average();
                var latencyValue = latency > 0 ? (long?)Math.Round(latency) : null;
                var score = running ? 100 + healthy * 1000 - (latencyValue ?? 500) : -10000;
                var summary = !running
                    ? "winws не запустился"
                    : total == 0
                        ? "winws запускается; для выбранного Game Filter нет HTTP-проверки"
                        : $"Доступно сервисов: {healthy} из {total}";
                candidates.Add(new AdvisorCandidate(level, LevelDisplayName(level), healthy, total, latencyValue, summary, score));

                if (running || _protectionRequested) await StopProtectionAsync();
            }

            var best = candidates.OrderByDescending(item => item.Score).Take(3).Select(item => new
            {
                level = item.Level,
                label = item.Label,
                healthy = item.Healthy,
                total = item.Total,
                latencyMs = item.LatencyMs,
                summary = item.Summary
            }).ToArray();
            await PostUiAsync(new { type = "advisor.status", state = "complete", provider, candidates = best });
            await LogToUiAsync("ok", $"Автоматический подбор завершён{(provider is null ? "" : $" для {provider}")}. Выберите один из предложенных уровней вручную.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await PostUiAsync(new { type = "advisor.status", state = "failed", provider, error = ex.Message });
            await LogToUiAsync("error", $"Автоматический подбор не завершён: {ex.Message}");
        }
        finally
        {
            _settings.BypassLevel = savedLevel;
            if (!_shutdownStarted && wasRunning)
            {
                try { await ApplyProfilesAsync(); }
                catch (Exception ex) { await LogToUiAsync("error", $"Не удалось восстановить прежний профиль после диагностики: {ex.Message}"); }
            }
            _advisorRunning = false;
            await PostUiAsync(new { type = "profile.status", level = _settings.BypassLevel });
            await SendServiceSnapshotAsync();
        }
    }

    private IEnumerable<string> AdvisorServiceIds()
    {
        if (_settings.Profiles.YouTube || _settings.Profiles.Web) yield return "youtube";
        if (_settings.Profiles.Discord || _settings.Profiles.Web) yield return "discord";
        if (_settings.Profiles.Telegram || _settings.Profiles.Web) yield return "telegram";
    }

    private async Task<string?> DetectProviderAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _advisorHttp.GetAsync("https://ipapi.co/json/", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var organization = root.TryGetProperty("org", out var org) ? org.GetString() : null;
            var asn = root.TryGetProperty("asn", out var rawAsn) ? rawAsn.GetString() : null;
            var result = string.Join(" · ", new[] { organization, asn }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task StopProtectionAsync()
    {
        await _protectionGate.WaitAsync();
        try
        {
            _protectionTransitioning = true;
            _protectionRequested = false;
            Interlocked.Increment(ref _healthGeneration);
            await SendServiceSnapshotAsync();
            await _components.StopAllAsync();
            await RestoreHostsCoreAsync();
            _proxyManager.RestoreIfOwned();
            _activeProfiles = new BypassProfileSettings();
            _protectionStarted = null;
            await LogToUiAsync("warn", "Защита остановлена. Выбранные профили сохранены.");
        }
        finally
        {
            _protectionTransitioning = false;
            await SendServiceSnapshotAsync();
            _protectionGate.Release();
        }
    }

    private async Task SetProfileAsync(string id, bool enabled)
    {
        switch (id.ToLowerInvariant())
        {
            case "youtube": _settings.Profiles.YouTube = enabled; break;
            case "discord": _settings.Profiles.Discord = enabled; break;
            case "telegram": _settings.Profiles.Telegram = enabled; break;
            case "web": _settings.Profiles.Web = enabled; break;
            case "gamefilter": _settings.Profiles.GameFilter = enabled; break;
            default: throw new ArgumentException($"Неизвестный профиль: {id}");
        }
        await _settingsStore.SaveAsync(_settings);
        await LogToUiAsync("info", $"Профиль {id} {(enabled ? "выбран" : "выключен")}. Нажмите главную кнопку, чтобы применить изменения.");
        await SendServiceSnapshotAsync();
    }

    private async Task SetBypassLevelAsync(string? level)
    {
        _settings.BypassLevel = WinwsProfileComposer.NormalizeLevel(level);
        await _settingsStore.SaveAsync(_settings);
        await LogToUiAsync("info", $"Выбран общий уровень обхода: {LevelDisplayName(_settings.BypassLevel)}.");
        await PostUiAsync(new { type = "profile.status", level = _settings.BypassLevel });
    }

    private async Task SetGameFilterAsync(JsonElement root)
    {
        var games = root.TryGetProperty("selectedGames", out var rawGames) && rawGames.ValueKind == JsonValueKind.Array
            ? rawGames.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "")
                .Where(x => x.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase) || x.Equals("roblox", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var protocol = root.TryGetProperty("protocol", out var rawProtocol) ? rawProtocol.GetString() : null;
        _settings.GameFilter.SelectedGames = games;
        _settings.GameFilter.Protocol = WinwsProfileComposer.NormalizeGameProtocol(protocol);
        await _settingsStore.SaveAsync(_settings);
        var gameNames = games.Count == 0
            ? "игры не выбраны"
            : string.Join(", ", games.Select(x => x.Equals("roblox", StringComparison.OrdinalIgnoreCase) ? "Roblox" : "Phasmophobia"));
        await LogToUiAsync("info", $"Game Filter сохранён: {gameNames}, протокол {_settings.GameFilter.Protocol.ToUpperInvariant()}. Будет применено при следующем запуске защиты.");
        await PublishConfigurationAsync();
    }

    private async Task SetDomainsAsync(JsonElement root)
    {
        static IEnumerable<string> Read(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty)
                : [];

        _settings.CustomBypassDomains = UserDomainListService.Normalize(Read(root, "bypass")).ToList();
        _settings.ExcludedDomains = UserDomainListService.Normalize(Read(root, "exclusions")).ToList();
        await _settingsStore.SaveAsync(_settings);
        await LogToUiAsync("info", $"Пользовательские домены сохранены: обход — {_settings.CustomBypassDomains.Count}, исключения — {_settings.ExcludedDomains.Count}. Будут применены при следующем запуске защиты.");
        await PublishConfigurationAsync();
    }

    private Task PublishConfigurationAsync() => PostUiAsync(new
    {
        type = "configuration.status",
        gameFilter = new { selectedGames = _settings.GameFilter.SelectedGames, protocol = _settings.GameFilter.Protocol },
        domains = new { bypass = _settings.CustomBypassDomains, exclusions = _settings.ExcludedDomains }
    });

    private async Task ApplyProfilesAsync()
    {
        if (!await _protectionGate.WaitAsync(0))
        {
            await SendServiceSnapshotAsync();
            return;
        }

        try
        {
            _protectionTransitioning = true;
            _protectionRequested = _settings.Profiles.AnyEnabled;
            await SendServiceSnapshotAsync();

            if (_settings.Profiles.AnyEnabled)
            {
                _serviceStatuses.Clear();
                Interlocked.Increment(ref _healthGeneration);
                var counters = ReadNetworkCounters();
                _baselineBytes = counters.Bytes;
                _baselinePackets = counters.Packets;
                _protectionStarted = DateTimeOffset.UtcNow;
                var runtimeLists = await _domainLists.WriteAsync(_settings.CustomBypassDomains, _settings.ExcludedDomains, _lifetime.Token);
                var arguments = WinwsProfileComposer.Compose(
                    _settings.Profiles,
                    _settings.BypassLevel,
                    _settings.GameFilter,
                    runtimeLists.BypassPath,
                    runtimeLists.ExclusionPath);
                await LogToUiAsync("info", $"Применение уровня {LevelDisplayName(_settings.BypassLevel)}. Сервисы: {string.Join(", ", ActiveProfileNames())}.");
                await LogToUiAsync("info", $"Пользовательские списки: обход — {runtimeLists.BypassCount}, исключения — {runtimeLists.ExclusionCount}.");
                await _components.StopAllAsync();
                _components.SetArguments("dpi", arguments);
                await PostUiAsync(new { type = "log", level = "info", message = "Запуск компонентов обхода…" });

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await _components.StartAllAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                {
                    _serviceStatuses["dpi"] = new ServiceStatus("dpi", "YouTube, Discord и Telegram", ServiceState.Error, "Запуск превысил 15 секунд.");
                    await PostUiAsync(new { type = "log", level = "error", message = "Не удалось запустить winws: превышено время ожидания 15 секунд." });
                }
                catch (Exception ex)
                {
                    _serviceStatuses["dpi"] = new ServiceStatus("dpi", "YouTube, Discord и Telegram", ServiceState.Error, ex.Message);
                    await LogToUiAsync("error", $"Не удалось запустить компонент: {ex.Message}");
                }

                var componentStatuses = _components.GetStatuses();
                var failed = componentStatuses.FirstOrDefault(x => x.State == ServiceState.Error)
                    ?? _serviceStatuses.Values.FirstOrDefault(x => x.State == ServiceState.Error);
                if (failed is not null || !componentStatuses.Any(x => x.State == ServiceState.Running))
                {
                    await _components.StopAllAsync();
                    await RestoreHostsCoreAsync();
                    _proxyManager.RestoreIfOwned();
                    _activeProfiles = new BypassProfileSettings();
                    _protectionRequested = false;
                    _protectionStarted = null;
                    var reason = failed?.Detail ?? "winws не перешёл в состояние «Работает».";
                    await PostUiAsync(new { type = "log", level = "error", message = $"Обход не запущен: {reason}" });
                }
                else
                {
                    _activeProfiles = CloneProfiles(_settings.Profiles);
                    var hostsOverrides = await CreateHostsOverridesAsync(_activeProfiles);
                    if (RequiresHosts(_activeProfiles) || hostsOverrides.Count != 0)
                    {
                        try
                        {
                            _hostsStatus = await _hostsManager.ApplyAsync(hostsOverrides, _lifetime.Token);
                            var robloxDetail = hostsOverrides.Count == 0 ? string.Empty : "; Roblox CDN DNS применён";
                            await LogToUiAsync("ok", $"Системный hosts применён: {_hostsStatus.EntryCount} записей, источник Flowseal {WinwsProfileComposer.FlowsealCommit[..7]}{robloxDetail}.");
                        }
                        catch (Exception ex)
                        {
                            _hostsStatus = _hostsManager.GetStatus();
                            await LogToUiAsync("error", $"winws запущен, но hosts не применён: {ex.Message}");
                        }
                    }
                    else
                    {
                        await RestoreHostsCoreAsync();
                    }
                    _nextHealthCheckUtc = DateTimeOffset.MinValue;
                    await PostUiAsync(new { type = "log", level = "ok", message = $"Профили активны: {string.Join(", ", ActiveProfileNames())}." });
                }
            }
            else
            {
                await _components.StopAllAsync();
                await RestoreHostsCoreAsync();
                _proxyManager.RestoreIfOwned();
                _activeProfiles = new BypassProfileSettings();
                _protectionStarted = null;
                await PostUiAsync(new { type = "log", level = "warn", message = "Все профили выключены. Сетевые настройки восстановлены." });
            }
        }
        finally
        {
            _protectionTransitioning = false;
            await SendServiceSnapshotAsync();
            _protectionGate.Release();
        }
    }

    private IEnumerable<string> ActiveProfileNames()
    {
        if (_settings.Profiles.YouTube) yield return "YouTube";
        if (_settings.Profiles.Discord) yield return "Discord";
        if (_settings.Profiles.Telegram) yield return "Telegram";
        if (_settings.Profiles.Web) yield return "Web";
        if (_settings.Profiles.GameFilter) yield return $"Game Filter ({_settings.GameFilter.Protocol.ToUpperInvariant()})";
    }

    private async Task<IReadOnlyList<HostsOverride>> CreateHostsOverridesAsync(BypassProfileSettings profiles)
    {
        var robloxSelected = profiles.GameFilter && _settings.GameFilter.SelectedGames
            .Any(game => game.Equals("roblox", StringComparison.OrdinalIgnoreCase));
        if (!robloxSelected) return Array.Empty<HostsOverride>();

        var resolution = await _robloxThumbnailResolver.ResolveAsync(_lifetime.Token);
        if (!resolution.Succeeded)
        {
            await LogToUiAsync("warn", $"Roblox CDN: не удалось получить временный DNS для {RobloxThumbnailResolver.ThumbnailHost}: {resolution.Detail}. Правила winws всё равно применены.");
            return Array.Empty<HostsOverride>();
        }

        await LogToUiAsync("ok", $"Roblox CDN: временный DNS для {RobloxThumbnailResolver.ThumbnailHost} подтверждён ({resolution.Detail}).");
        return [new HostsOverride(resolution.Address!, RobloxThumbnailResolver.ThumbnailHost)];
    }

    private async Task RestoreHostsAsync()
    {
        try
        {
            _hostsStatus = await _hostsManager.RestoreAsync(_lifetime.Token);
            await LogToUiAsync("ok", "Блок VortexPass удалён из системного hosts; остальные записи сохранены.");
        }
        catch (Exception ex)
        {
            _hostsStatus = _hostsManager.GetStatus();
            await LogToUiAsync("error", $"Не удалось восстановить hosts: {ex.Message}");
        }
        await PublishHostsStatusAsync();
    }

    private async Task RestoreHostsCoreAsync()
    {
        try { _hostsStatus = await _hostsManager.RestoreAsync(_lifetime.Token); }
        catch (Exception ex) { _hostsStatus = _hostsManager.GetStatus(); await LogToUiAsync("error", $"Не удалось удалить блок VortexPass из hosts: {ex.Message}"); }
        await PublishHostsStatusAsync();
    }

    private Task PublishHostsStatusAsync() => PostUiAsync(new
    {
        type = "hosts.status",
        applied = _hostsStatus.Applied,
        entryCount = _hostsStatus.EntryCount,
        updatedAt = _hostsStatus.UpdatedAt,
        detail = _hostsStatus.Detail,
        sourceCommit = WinwsProfileComposer.FlowsealCommit
    });

    private async void ComponentStatusChanged(object? sender, ServiceStatus status)
    {
        try
        {
            _serviceStatuses[status.Id] = status;
            await PostUiAsync(new
            {
                type = "service.status",
                id = status.Id,
                state = status.State.ToString().ToLowerInvariant(),
                detail = status.Detail
            });
            var level = status.State == ServiceState.Error ? "error" : status.State == ServiceState.Running ? "ok" : "info";
            var detail = string.IsNullOrWhiteSpace(status.Detail) ? string.Empty : $" {status.Detail}";
            await LogToUiAsync(level, $"[{status.DisplayName}] {status.State}.{detail}");
            if (status.State is ServiceState.Running or ServiceState.Error or ServiceState.Stopped)
                await SendServiceSnapshotAsync();
            if (status.Id.Equals("dpi", StringComparison.OrdinalIgnoreCase) &&
                status.State == ServiceState.Stopped && _protectionRequested && !_protectionTransitioning)
            {
                _protectionRequested = false;
                _activeProfiles = new BypassProfileSettings();
                _protectionStarted = null;
                await RestoreHostsCoreAsync();
                await LogToUiAsync("error", "winws неожиданно завершился. Блок VortexPass удалён из hosts, сетевые настройки восстановлены.");
                await SendServiceSnapshotAsync();
            }
        }
        catch (Exception ex)
        {
            await AppendBackgroundErrorAsync("ComponentStatusChanged", ex);
        }
    }

    private async void ComponentDiagnosticReceived(object? sender, string message)
    {
        try { await LogToUiAsync("info", message); }
        catch (Exception ex) { await AppendBackgroundErrorAsync("ComponentDiagnosticReceived", ex); }
    }


    private async Task LogToUiAsync(string level, string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} [{level.ToUpperInvariant()}] {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(_applicationLogPath, line, _lifetime.Token);
        }
        catch { }
        await PostUiAsync(new { type = "log", level, message });
    }

    private async Task SendServiceSnapshotAsync()
    {
        var statuses = _components.GetStatuses();
        foreach (var status in statuses)
            _serviceStatuses[status.Id] = status;

        var winwsRunning = _serviceStatuses.GetValueOrDefault("dpi")?.State == ServiceState.Running;
        var enabled = _activeProfiles.AnyEnabled && winwsRunning;
        await PostUiAsync(new
        {
            type = "protection.status",
            enabled,
            pending = _protectionTransitioning || _advisorRunning,
            youtube = winwsRunning && _activeProfiles.YouTube,
            discord = winwsRunning && _activeProfiles.Discord,
            telegram = winwsRunning && _activeProfiles.Telegram,
            web = winwsRunning && _activeProfiles.Web,
            gameFilter = winwsRunning && _activeProfiles.GameFilter,
            requestedYoutube = _settings.Profiles.YouTube,
            requestedDiscord = _settings.Profiles.Discord,
            requestedTelegram = _settings.Profiles.Telegram,
            requestedWeb = _settings.Profiles.Web,
            requestedGameFilter = _settings.Profiles.GameFilter
        });
        UpdateTrayState(enabled);
        await PublishSettingsStatusAsync();
        await PublishConfigurationAsync();
        await PublishHostsStatusAsync();
        await PublishHealthSnapshotAsync(winwsRunning);
    }

    private Task PublishSettingsStatusAsync() => PostUiAsync(new
    {
        type = "settings.status",
        autostart = _settings.StartWithWindows,
        tray = _settings.MinimizeToTray
    });

    private void UpdateTrayState(bool protectionEnabled)
    {
        _trayProtectionItem.Text = protectionEnabled ? "Остановить защиту" : "Запустить защиту";
        _trayProtectionItem.Enabled = !_protectionTransitioning && !_advisorRunning;
        _trayIcon.Text = protectionEnabled ? "VortexPass — защита включена" : "VortexPass — защита выключена";
    }

    private async Task RunMetricsLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var counters = ReadNetworkCounters();
            var active = _protectionStarted.HasValue;
            if (active && DateTimeOffset.UtcNow >= _nextHealthCheckUtc)
            {
                _nextHealthCheckUtc = DateTimeOffset.UtcNow.AddSeconds(15);
                await RunHealthChecksAsync(cancellationToken);
            }
            var pings = _healthStatuses.ToDictionary(x => x.Key, x => x.Value.IsHealthy ? x.Value.LatencyMs : null, StringComparer.OrdinalIgnoreCase);
            await PostUiAsync(new
            {
                type = "metrics",
                uptimeSeconds = active ? Math.Max(0, (long)(DateTimeOffset.UtcNow - _protectionStarted!.Value).TotalSeconds) : 0,
                trafficBytes = active ? Math.Max(0, counters.Bytes - _baselineBytes) : 0,
                packets = active ? Math.Max(0, counters.Packets - _baselinePackets) : 0,
                youtubePing = pings.GetValueOrDefault("youtube"),
                discordPing = pings.GetValueOrDefault("discord"),
                telegramPing = pings.GetValueOrDefault("telegram")
            });
        }
    }

    private async Task RunHealthChecksAsync(CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _healthGeneration);
        var winwsRunning = _components.GetStatuses().Any(x => x.Id == "dpi" && x.State == ServiceState.Running);
        var checks = new[]
        {
            _healthChecks.CheckAsync("youtube", IsServiceSelected("youtube"), winwsRunning, cancellationToken),
            _healthChecks.CheckAsync("discord", IsServiceSelected("discord"), winwsRunning, cancellationToken),
            _healthChecks.CheckAsync("telegram", IsServiceSelected("telegram"), winwsRunning, cancellationToken)
        };
        var results = await Task.WhenAll(checks);
        if (generation != Volatile.Read(ref _healthGeneration)) return;
        foreach (var result in results)
        {
            var changed = !_healthStatuses.TryGetValue(result.Id, out var previous) || previous.State != result.State || previous.Detail != result.Detail;
            _healthStatuses[result.Id] = result;
            if (changed)
            {
                var level = result.IsHealthy ? "ok" : result.State is ServiceHealthState.NotSelected or ServiceHealthState.WinwsStopped ? "info" : "error";
                var latency = result.LatencyMs is null ? string.Empty : $" ({result.LatencyMs} ms)";
                var stages = result.Stages is null ? string.Empty :
                    $" DNS=[{result.Stages.Dns}]; TCP=[{result.Stages.Tcp}]; TLS=[{result.Stages.Tls}]; HTTP=[{result.Stages.Http}]" +
                    (result.Stages.Voice is null ? string.Empty : $"; Voice=[{result.Stages.Voice}]") +
                    (result.Stages.Gateway is null ? string.Empty : $"; Gateway=[{result.Stages.Gateway}]") +
                    (result.Stages.Cdn is null ? string.Empty : $"; CDN=[{result.Stages.Cdn}]");
                await LogToUiAsync(level, $"Проверка {ServiceDisplayName(result.Id)}: {result.Detail}{latency}.{stages}");
            }
        }
        await PublishHealthSnapshotAsync(winwsRunning);
    }

    private async Task PublishHealthSnapshotAsync(bool winwsRunning)
    {
        foreach (var id in new[] { "youtube", "discord", "telegram" })
        {
            if (!IsServiceSelected(id))
                _healthStatuses[id] = new ServiceHealthResult(id, ServiceHealthState.NotSelected, "Не выбран");
            else if (!winwsRunning)
                _healthStatuses[id] = new ServiceHealthResult(id, ServiceHealthState.WinwsStopped, "winws остановлен");
            else if (!_healthStatuses.ContainsKey(id))
                _healthStatuses[id] = new ServiceHealthResult(id, ServiceHealthState.Checking, "Ожидается сетевая проверка");
        }
        await PostUiAsync(new
        {
            type = "health.status",
            services = _healthStatuses.ToDictionary(x => x.Key, x => new
            {
                state = x.Value.State.ToString().ToLowerInvariant(),
                detail = x.Value.Detail,
                latencyMs = x.Value.LatencyMs,
                stages = x.Value.Stages
            })
        });
    }

    private bool IsServiceSelected(string id)
    {
        var profiles = _activeProfiles.AnyEnabled ? _activeProfiles : _settings.Profiles;
        return id switch
        {
            "youtube" => profiles.YouTube || profiles.Web,
            "discord" => profiles.Discord || profiles.Web,
            "telegram" => profiles.Telegram || profiles.Web,
            _ => false
        };
    }

    private static string ServiceDisplayName(string id) => id switch { "youtube" => "YouTube", "discord" => "Discord", "telegram" => "Telegram", _ => id };

    private static bool RequiresHosts(BypassProfileSettings profiles) => profiles.Web || profiles.Discord;

    private static BypassProfileSettings CloneProfiles(BypassProfileSettings profiles) => new()
    {
        YouTube = profiles.YouTube,
        Discord = profiles.Discord,
        Telegram = profiles.Telegram,
        Web = profiles.Web,
        GameFilter = profiles.GameFilter
    };

    private static string LevelDisplayName(string level) => level switch
    {
        "light" => "Лёгкий", "moderate" => "Умеренный", "medium" => "Средний", "complex" => "Сложный", "aggressive" => "Агрессивный", _ => "Средний"
    };

    private static (long Bytes, long Packets) ReadNetworkCounters()
    {
        long bytes = 0, packets = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            try
            {
                var stats = nic.GetIPv4Statistics();
                bytes += stats.BytesReceived + stats.BytesSent;
                packets += stats.UnicastPacketsReceived + stats.UnicastPacketsSent + stats.NonUnicastPacketsReceived + stats.NonUnicastPacketsSent;
            }
            catch (NetworkInformationException) { }
        }
        return (bytes, packets);
    }


    private async Task CheckForUpdatesAsync()
    {
        await PostUiAsync(new { type = "update.status", state = "checking", currentVersion = CurrentVersion().ToString(3) });
        _lastUpdate = await _updates.CheckAsync(CurrentVersion(), _lifetime.Token);
        _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        await _settingsStore.SaveAsync(_settings);
        await PostUiAsync(new
        {
            type = "update.status",
            state = _lastUpdate.State switch { UpdateState.Current => "current", UpdateState.Available => "available", _ => "failed" },
            currentVersion = CurrentVersion().ToString(3),
            availableVersion = _lastUpdate.Release?.Version.ToString(3),
            checkedAt = _settings.LastUpdateCheckUtc,
            error = _lastUpdate.Error
        });
    }

    private async Task InstallUpdateAsync()
    {
        if (_lastUpdate?.State != UpdateState.Available || _lastUpdate.Release is null)
        {
            await CheckForUpdatesAsync();
            return;
        }
        await PostUiAsync(new { type = "update.status", state = "downloading", currentVersion = CurrentVersion().ToString(3), availableVersion = _lastUpdate.Release.Version.ToString(3) });
        try
        {
            var downloaded = await _updates.DownloadAsync(_lastUpdate.Release, cancellationToken: _lifetime.Token);
            var updaterSource = Path.Combine(AppContext.BaseDirectory, "updater");
            var updaterTemp = Path.Combine(Path.GetTempPath(), "VortexPass-updater", Guid.NewGuid().ToString("N"));
            if (!File.Exists(Path.Combine(updaterSource, "VortexPass.Updater.exe"))) throw new FileNotFoundException("Не найден VortexPass.Updater.exe.");
            CopyDirectory(updaterSource, updaterTemp);
            var updaterExe = Path.Combine(updaterTemp, "VortexPass.Updater.exe");
            var appExe = Path.Combine(AppContext.BaseDirectory, "VortexPass.exe");
            Process.Start(new ProcessStartInfo(updaterExe)
            {
                UseShellExecute = true,
                WorkingDirectory = updaterTemp,
                Arguments = $"--pid {Environment.ProcessId} --zip \"{downloaded.ZipPath}\" --target \"{AppContext.BaseDirectory}\" --restart \"{appExe}\""
            });
            await ShutdownAsync();
        }
        catch (Exception ex)
        {
            await PostUiAsync(new { type = "update.status", state = "failed", currentVersion = CurrentVersion().ToString(3), error = ex.Message });
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), true);
    }

    private async Task PostUiAsync(object message)
    {
        if (!_webReady || _shutdownStarted) return;
        var json = JsonSerializer.Serialize(message);
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_webReady || _shutdownStarted || DesignView.CoreWebView2 is null) return;
                DesignView.CoreWebView2.PostWebMessageAsJson(json);
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or ObjectDisposedException)
        {
            // The window can be closing while winws emits a final line; the process must stay alive.
        }
    }

    private async Task AppendBackgroundErrorAsync(string source, Exception exception)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} [ERROR] {source}: {exception}{Environment.NewLine}";
            await File.AppendAllTextAsync(_applicationLogPath, line);
        }
        catch { }
        await PostUiAsync(new { type = "log", level = "error", message = $"{source}: {exception.Message}" });
    }

    private async Task ResetApplicationLogAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_applicationLogPath)!);
            await File.WriteAllTextAsync(_applicationLogPath, $"{DateTimeOffset.Now:O} [INFO] VortexPass started.{Environment.NewLine}", _lifetime.Token);
        }
        catch { }
    }

    private static Version CurrentVersion()
    {
        var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        return Version.TryParse(informational, out var version) ? version : new Version(0, 0, 0);
    }

    private void BeginNativeDrag()
    {
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _lifetime.Cancel();
            return;
        }
        e.Cancel = true;
        if (_settings.MinimizeToTray) Hide();
        else await ShutdownAsync();
    }

    private void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async Task ShutdownAsync()
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        try
        {
            await _components.StopAllAsync();
            await RestoreHostsCoreAsync();
            _proxyManager.RestoreIfOwned();
        }
        finally
        {
            _components.Dispose();
            _advisorHttp.Dispose();
            _allowClose = true;
            Close();
        }
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
