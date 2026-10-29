using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using VortexPass.Core.Models;
using VortexPass.Core.Services;
using VortexPass.App.Services;
using Forms = System.Windows.Forms;

namespace VortexPass.App;

public partial class MainWindow : Window
{
    private sealed record AdvisorCandidate(string Level, int Tuning, string Label, int Healthy, int Total, long? LatencyMs, string Summary, long Score, string Source, string Mode = "standard");
    private sealed record AdvisorStrategy(string Level, int Tuning);
    private sealed record ProviderInfo(string DisplayName, string Key);

    private readonly AppSettingsStore _settingsStore;
    private readonly WindowsStartupService _startupService = new();
    private readonly ExternalComponentService _components;
    private readonly TelegramDesktopProxyService _telegramProxy;
    private readonly ServiceHealthCheckService _healthChecks = new();
    private readonly SystemProxyManager _proxyManager;
    private readonly WarpPacService _warpPac = new();
    private readonly HostsFileManager _hostsManager;
    private readonly DnsOverrideService _dnsOverrides = new();
    private readonly DnsGuardController _dnsGuard;
    private readonly UserDomainListService _domainLists;
    private readonly GitHubUpdateService _updates = new();
    private readonly WarpService _warpService;
    private readonly WarpBootstrapService _warpBootstrap = new(AppContext.BaseDirectory);
    private readonly PendingUpdateStore _pendingUpdates;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly TrayMenuWindow _trayMenu;
    private readonly string _applicationLogPath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _protectionGate = new(1, 1);
    private readonly Dictionary<string, ServiceStatus> _serviceStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServiceHealthResult> _healthStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _healthFailureStreaks = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _mobileOptimizationCts;
    private int _mobileOptimizationGeneration;
    private int _mobileTelegramRouteCount;
    private int _telegramFallbackHostCount;
    private int _telegramDnsHostCount;
    private int _robloxHostCount;
    private AppSettings _settings = new();
    private BypassProfileSettings _activeProfiles = new();
    private HostsFileStatus _hostsStatus = new(false, 0, null, "hosts не применён");
    private UpdateCheckResult? _lastUpdate;
    private string? _startupUpdateError;
    private bool _updatePromptDismissedThisSession;
    private int _updateCheckRunning;
    private bool _allowClose;
    private bool _shutdownStarted;
    private bool _webReady;
    private bool _settingsLoaded;
    private bool _protectionRequested;
    private bool _protectionTransitioning;
    private DateTimeOffset? _protectionStarted;
    private long _baselineBytes;
    private long _baselinePackets;
    private DateTimeOffset _nextHealthCheckUtc = DateTimeOffset.MinValue;
    private int _healthGeneration;
    private int _healthCheckRunning;
    private bool _advisorRunning;
    private CancellationTokenSource? _advisorCts;
    private CancellationTokenSource? _profileApplyCts;
    private CancellationTokenSource _healthCheckCts = new();
    private bool _advisorStopProtectionRequested;
    private int _startupAddedDefaultExclusions;
    private bool _startupFallbackApplied;
    private WarpStatus _warpStatus = new(WarpConnectionState.NotInstalled, "Официальный Cloudflare WARP не установлен");
    private WarpDiagnostics? _warpDiagnostics;
    private WarpPerformanceResult? _warpPerformance;
    private WarpPerformanceComparison? _warpPerformanceComparison;
    private bool _warpTransitioning;
    private bool _warpTelegramCompatibilityActive;
    private bool _mobileWarpFallbackActive;
    private bool _mobileWarpWasConnected;
    private readonly HashSet<string> _mobileWarpFallbackServices = new(StringComparer.OrdinalIgnoreCase);
    private string? _warpAppliedRoutingMode;
    private string _currentNetworkKey = string.Empty;
    private int _networkChangeGeneration;

    public MainWindow()
    {
        InitializeComponent();
        if (Environment.GetCommandLineArgs().Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase)))
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Visibility = Visibility.Hidden;
        }
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VortexPass");
        _settingsStore = new AppSettingsStore(appData);
        _dnsGuard = new DnsGuardController(AppContext.BaseDirectory, appData);
        _dnsGuard.StatusChanged += status => _ = Dispatcher.InvokeAsync(() => _ = HandleDnsGuardStatusAsync(status));
        _dnsGuard.DiagnosticReceived += detail => _ = Dispatcher.InvokeAsync(() => _ = LogToUiAsync("info","DNS: "+detail));
        _pendingUpdates = new PendingUpdateStore(appData);
        _warpService = new WarpService(appData);
        _applicationLogPath = Path.Combine(appData, "app.log");
        _components = new ExternalComponentService(AppContext.BaseDirectory);
        _components.AdditionalOwnedProcessIds = () => _warpBootstrap.ProcessId is { } pid ? new[] { pid } : [];
        _telegramProxy = new TelegramDesktopProxyService(AppContext.BaseDirectory, appData);
        _proxyManager = new SystemProxyManager(appData);
        _hostsManager = new HostsFileManager(AppContext.BaseDirectory, appData);
        _domainLists = new UserDomainListService(appData);
        _components.StatusChanged += ComponentStatusChanged;
        _components.DiagnosticReceived += ComponentDiagnosticReceived;

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "VortexPass",
            Visible = false
        };
        _trayMenu = new TrayMenuWindow(
            RestoreWindow,
            async () =>
            {
                if ((_advisorRunning || _protectionTransitioning) && !_protectionRequested) return;
                if (_protectionRequested) await SetProtectionAsync(false);
                else if (_settings.SavedConnections.Any(p => p.Id == _settings.SelectedConnectionId))
                    await LaunchConnectionAsync(_settings.SelectedConnectionId);
                else RestoreWindow();
            },
            async () => await SetWarpConnectedAsync(_warpStatus.State != WarpConnectionState.Connected),
            ShutdownAsync);
        _trayMenu.UpdateState(false, false, false, false, false, false);
        _trayIcon.MouseUp += TrayIconMouseUp;
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();
    }

    private void TrayIconMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => TrayIconMouseUp(sender, e));
            return;
        }

        if (e.Button == Forms.MouseButtons.Left)
        {
            RestoreWindow();
            return;
        }
        if (e.Button != Forms.MouseButtons.Right || _shutdownStarted) return;

        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        var cursorPoint = transform?.Transform(new System.Windows.Point(cursor.X, cursor.Y)) ?? new System.Windows.Point(cursor.X, cursor.Y);
        var area = screen.WorkingArea;
        var workAreaTopLeft = transform?.Transform(new System.Windows.Point(area.Left, area.Top)) ?? new System.Windows.Point(area.Left, area.Top);
        var workAreaBottomRight = transform?.Transform(new System.Windows.Point(area.Right, area.Bottom)) ?? new System.Windows.Point(area.Right, area.Bottom);
        var workArea = new Rect(workAreaTopLeft, workAreaBottomRight);
        _trayMenu.UpdateState(_protectionRequested, _protectionTransitioning || _advisorRunning,
            _startupFallbackApplied, HasProtectionError(), _warpStatus.State == WarpConnectionState.Connected, _warpTransitioning);
        _trayMenu.SetAppearance(_settings.Theme == "light", _settings.ReduceMotion);
        _trayMenu.ShowAt(cursorPoint, workArea);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ResetApplicationLogAsync();
        await LogUpdateCommandLineAsync();
        if (await TryInstallPendingUpdateOnStartupAsync()) return;
        try { await _dnsGuard.RecoverStaleAsync(_lifetime.Token); }
        catch (Exception ex) { await AppendApplicationLogLineAsync($"[ERROR] DNS recovery: {ex.Message}"); }
        _settings = await _settingsStore.LoadAsync(_lifetime.Token);
        _settingsLoaded = true;
        _settings.Profiles ??= new BypassProfileSettings();
        _settings.GameFilter ??= new GameFilterSettings();
        _settings.Warp ??= new WarpSettings();
        WarpRoutingPlanner.EnsureDefaults(_settings.Warp);
        await AppendApplicationLogLineAsync($"[INFO] WARP транспорт: {WarpTransportPolicy.Label(_settings.Warp)}; источник {_settings.Warp.TransportSelectionSource}.");
        if(_settings.Warp.LegacyTransportBackup is not null)
        {
            try{await _settingsStore.SaveAsync(_settings,_lifetime.Token);}
            catch(Exception ex)when(ex is System.IO.IOException or UnauthorizedAccessException)
            {await AppendApplicationLogLineAsync("[WARN] Миграция транспорта WARP применена в памяти, но не сохранена: "+ex.Message);}
        }
        // Recover an owned PAC left by a crash before deciding whether the
        // current session should enable selective WARP routing again.
        _proxyManager.RestoreIfOwned();
        _settings.GameFilter.SelectedGames ??= [];
        _settings.GameFilter.Protocol = WinwsProfileComposer.NormalizeGameProtocol(_settings.GameFilter.Protocol);
        _settings.CustomBypassDomains ??= [];
        _settings.CurrentSiteRules ??= [];
        _settings.ExcludedDomains ??= DefaultDomainLists.CreateExclusions();
        _startupAddedDefaultExclusions = DefaultDomainLists.MergeDefaultExclusions(_settings.ExcludedDomains);
        _settings.FavoriteProfiles ??= [];
        _settings.FavoriteProfiles = NormalizeFavorites(_settings.FavoriteProfiles);
        _settings.AdvisorHistory ??= [];
        _settings.LocalAdvisor ??= new();
        _settings.LocalAdvisor.History ??= [];
        MigrateSavedConnections();
        _settings.Theme = _settings.Theme == "light" ? "light" : "dark";
        DesignView.DefaultBackgroundColor = _settings.Theme == "light" ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(24, 24, 24);
        _settings.NetworkCompatibilityProfiles ??= [];
        foreach (var profile in _settings.NetworkCompatibilityProfiles)
        {
            profile.TelegramRoutes ??= new(StringComparer.OrdinalIgnoreCase);
            profile.ServiceRoutes ??= new(StringComparer.OrdinalIgnoreCase);
            profile.MobileDns ??= new MobileDnsState();
            profile.TelegramStrategy = WinwsProfileComposer.NormalizeTelegramStrategy(profile.TelegramStrategy);
            foreach (var route in profile.ServiceRoutes.Values)
            {
                route.Strategy = WinwsProfileComposer.NormalizeMobileServiceStrategy(route.Strategy);
                if (string.IsNullOrWhiteSpace(route.LastSuccessfulStrategy) && route.State == "confirmed")
                    route.LastSuccessfulStrategy = route.Strategy;
                if (!string.IsNullOrWhiteSpace(route.LastSuccessfulStrategy))
                    route.Strategy = WinwsProfileComposer.NormalizeMobileServiceStrategy(route.LastSuccessfulStrategy);
            }
            if (WinwsProfileComposer.NormalizeDiscordVoiceMode(profile.DiscordVoiceMode) == "auto")
                profile.DiscordVoiceResolvedMode = "minimal";
        }
        var currentCompatibility = _settings.NetworkCompatibilityProfiles
            .FirstOrDefault(profile => profile.NetworkKey.Equals(CurrentNetworkKey(), StringComparison.Ordinal));
        _settings.CompatibilityMode = WinwsProfileComposer.NormalizeCompatibilityMode(
            currentCompatibility?.Mode ?? _settings.CompatibilityMode);
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        _warpStatus = _warpService.Detect();
        if(_settings.PendingConnectionWarpRestore is not null)
        {
            try {await RestoreConnectionWarpAsync();}
            catch(Exception ex){await LogToUiAsync("error","Восстановление WARP-профиля: "+ex.Message);}
        }
        var staleMobileWarp = _settings.NetworkCompatibilityProfiles.Where(item => item.MobileWarpFallbackOwned).ToArray();
        if (staleMobileWarp.Length > 0)
        {
            try
            {
                _warpStatus = await _warpService.GetStatusAsync(_lifetime.Token);
                if (_warpStatus.State == WarpConnectionState.Connected && staleMobileWarp.All(item => !item.MobileWarpWasConnected))
                    _warpStatus = await _warpService.SetConnectedAsync(false, _lifetime.Token);
                else if (_warpStatus.State == WarpConnectionState.Connected)
                    _warpStatus = await ApplyWarpClientPreferencesAsync(reconnect: true, _lifetime.Token);
                foreach (var item in staleMobileWarp) item.MobileWarpFallbackOwned = false;
                await _settingsStore.SaveAsync(_settings, _lifetime.Token);
                await AppendApplicationLogLineAsync("[INFO] Восстановлено состояние WARP/PAC после незавершённого мобильного fallback.");
            }
            catch (Exception ex)
            {
                await AppendApplicationLogLineAsync($"[WARN] Mobile WARP stale recovery: {ex.Message}");
            }
        }
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
        if (_settings.ProfileSettingsVersion < 3)
        {
            _settings.FavoriteProfiles.Clear();
            _settings.ProfileSettingsVersion = 3;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        if (_settings.ProfileSettingsVersion < 4)
        {
            // In v0.6.0 the Telegram Web switch was backed by the broad Web flag.
            // Preserve effective coverage while separating both settings.
            _settings.Profiles.TelegramWeb = _settings.Profiles.Web;
            _settings.ProfileSettingsVersion = 4;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        if (_settings.ProfileSettingsVersion < 5)
        {
            _settings.CompatibilityMode = "standard";
            _settings.ProfileSettingsVersion = 5;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        if (_settings.ProfileSettingsVersion < 6)
        {
            foreach (var profile in _settings.NetworkCompatibilityProfiles)
            {
                profile.TelegramStrategy = "strong";
                profile.OptimizationState = "idle";
                profile.OptimizationDetail = string.Empty;
            }
            _settings.ProfileSettingsVersion = 6;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        if (MobileDnsPolicy.MustDiscardLegacyRoutes(_settings.ProfileSettingsVersion))
        {
            // v6 briefly cached TCP-only YouTube/Discord addresses and replayed
            // them through hosts. They are intentionally discarded: TCP reachability
            // does not prove that a browser TLS/HTTP route is usable.
            foreach (var profile in _settings.NetworkCompatibilityProfiles)
                profile.MobileDns = new MobileDnsState { NetworkKey = profile.NetworkKey, State = "idle" };
            _settings.ProfileSettingsVersion = 7;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        if (_settings.ProfileSettingsVersion < 8)
        {
            foreach (var profile in _settings.NetworkCompatibilityProfiles)
            {
                profile.ServiceRoutes ??= new(StringComparer.OrdinalIgnoreCase);
                profile.MobileWarpFallbackOwned = false;
                profile.MobileWarpWasConnected = false;
            }
            _settings.ProfileSettingsVersion = 8;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        if (_settings.ProfileSettingsVersion < 9)
        {
            foreach (var profile in _settings.NetworkCompatibilityProfiles)
            {
                profile.ServiceRoutes ??= new(StringComparer.OrdinalIgnoreCase);
                foreach (var route in profile.ServiceRoutes.Values)
                {
                    route.Strategy = WinwsProfileComposer.NormalizeMobileServiceStrategy(route.Strategy);
                    if (route.State == "confirmed" && string.IsNullOrWhiteSpace(route.LastSuccessfulStrategy))
                        route.LastSuccessfulStrategy = route.Strategy;
                }

                // Older builds could leave T2 on the failed Discord fallback.
                // This proven starter remains narrow and can still fall back
                // after two consecutive content-check failures.
                if (MobileRoutePolicy.IsT2Provider(profile.ProviderKey, profile.ProviderName))
                {
                    var discord = EnsureServiceRoute(profile, "discord");
                    discord.Strategy = "primary";
                    discord.LastSuccessfulStrategy = "primary";
                    var youtube = EnsureServiceRoute(profile, "youtube");
                    youtube.Strategy = "primary";
                    youtube.LastSuccessfulStrategy = "primary";
                    var telegram = EnsureServiceRoute(profile, "telegram");
                    telegram.Strategy = "primary";
                    telegram.LastSuccessfulStrategy = "primary";
                    profile.TelegramStrategy = "strong";
                    profile.DiscordVoiceMode = "auto";
                    profile.DiscordVoiceResolvedMode = "minimal";
                }
            }
            _settings.ProfileSettingsVersion = 9;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        if (_startupAddedDefaultExclusions > 0)
        {
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        _currentNetworkKey = CurrentNetworkKey();
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        var isStartup = Environment.GetCommandLineArgs().Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));
        if (isStartup)
        {
            WindowState = WindowState.Minimized;
            Hide();
        }
        try{_startupService.SetEnabled(_settings.StartWithWindows,Path.Combine(AppContext.BaseDirectory,"VortexPass.exe"));}catch(Exception ex){await LogToUiAsync("error","Проверьте задачу автозапуска: "+ex.Message);}
        _trayIcon.Visible = true;
        await _components.LoadManifestAsync(_lifetime.Token);
        await LogToUiAsync("info","Автозапуск: "+StartupConnectionPolicy.Reason(_settings,isStartup)+$"; профиль выбран={!string.IsNullOrEmpty(_settings.StartupConnectionId)}; существует={_settings.SavedConnections.Any(p=>p.Id==_settings.StartupConnectionId)}.");
        if (isStartup && _settings.StartWithWindows && _settings.StartProtectionOnStartup)
            _startupTask = StartProtectionOnWindowsStartupAsync();

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
                settings = new
                {
                    theme = _settings.Theme,
                    reduceMotion = _settings.ReduceMotion,
                    autostart = _settings.StartWithWindows,
                    tray = _settings.MinimizeToTray,
                    selectedDnsProvider = _settings.SelectedDnsProvider,
                    dnsMode = DnsModePolicy.Normalize(_settings.DnsMode),
                    startProtectionOnStartup = _settings.StartProtectionOnStartup,
                    startupStrategy = _settings.StartupStrategy, startupConnectionError = _startupConnectionError, startupConnectionId = _settings.StartupConnectionId, startupProfileChoiceRequired = _settings.StartupProfileChoiceRequired,
                    gameFilterIpsetMode = _settings.GameFilter.IpsetMode
                },
                profiles = new
                {
                    youtube = _settings.Profiles.YouTube,
                    discord = _settings.Profiles.Discord,
                    telegram = _settings.Profiles.Telegram,
                    telegramWeb = _settings.Profiles.TelegramWeb,
                    web = _settings.Profiles.Web,
                    gameFilter = _settings.Profiles.GameFilter
                },
                gameFilter = new
                {
                    selectedGames = _settings.GameFilter.SelectedGames,
                    protocol = WinwsProfileComposer.NormalizeGameProtocol(_settings.GameFilter.Protocol)
                },
                warp = new
                {
                    telegram = _settings.Warp.Telegram,
                    games = _settings.Warp.Games,
                    requestedConnected = _settings.Warp.RequestedConnected,
                    state = WarpStateName(_warpStatus.State),
                    detail = _warpStatus.Detail,
                    installed = _warpStatus.CliPath is not null,
                    registered = _warpStatus.Registered,
                    serviceRunning = _warpStatus.ServiceRunning,
                    protocol = _warpStatus.Protocol,
                    routingMode = _settings.Warp.RoutingMode,
                    transportSelection = WarpTransportPolicy.Selection(_settings.Warp),
                    transportSelectionSource = _settings.Warp.TransportSelectionSource,
                    transportCandidates = CurrentWarpCandidates(),
                    protocolPreference = WarpTransportPolicy.IsAutomatic(_settings.Warp)?"auto":WarpTransportPolicy.Resolve(_settings.Warp).Protocol,
                    masquePreference = WarpTransportPolicy.Resolve(_settings.Warp).Masque,
                    dnsMode = _settings.Warp.DnsMode,
                    proxyPort = _settings.Warp.ProxyPort,
                    profiles = _settings.Warp.RouteProfiles.Select(profile => new
                    {
                        id = profile.Id, name = profile.Name, kind = profile.Kind, builtInId = profile.BuiltInId,
                        enabled = profile.Enabled, connection = profile.Connection, domains = profile.Domains,
                        ipRanges = profile.IpRanges, executablePath = profile.ExecutablePath
                    })
                },
                domains = new
                {
                    bypass = _settings.CustomBypassDomains,
                    exclusions = _settings.ExcludedDomains
                },
                bypassLevel = WinwsProfileComposer.NormalizeLevel(_settings.BypassLevel),
                bypassTuning = WinwsProfileComposer.NormalizeTuning(_settings.BypassTuning),
                compatibility = CurrentCompatibilityStatus(),
                favoriteProfiles = _settings.FavoriteProfiles.Select(profile => new
                {
                    level = WinwsProfileComposer.NormalizeLevel(profile.Level),
                    tuning = WinwsProfileComposer.NormalizeTuning(profile.Tuning),
                    mode = WinwsProfileComposer.NormalizeCompatibilityMode(profile.Mode)
                }),
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
        await PublishLocalAdvisorStatusAsync();
        await SendServiceSnapshotAsync();
        _ = RefreshWarpAsync(runDiagnostics: false);
        if (!string.IsNullOrWhiteSpace(_startupUpdateError))
            await PostUiAsync(new { type = "update.status", state = "failed", currentVersion = CurrentVersion().ToString(3), error = _startupUpdateError });
        if (_startupAddedDefaultExclusions > 0)
            await LogToUiAsync("info", $"Исключения обновлены: добавлено {_startupAddedDefaultExclusions} служебных доменов для совместимости приложений и WARP.");
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

            if (_advisorRunning && type is not null && !type.StartsWith("window.", StringComparison.Ordinal) &&
                type is not "advisor.stop" and not "protection.set" and not "logs.export" and not "settings.appearance") return;

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
                case "window.quit":
                    await ShutdownAsync();
                    break;
                case "settings.autostart":
                    await SetAutostartAsync(value);
                    break;
                case "settings.tray":
                    _settings.MinimizeToTray = value;
                    await _settingsStore.SaveAsync(_settings);
                    await PublishSettingsStatusAsync();
                    break;
                case "settings.seasonalIcon":
                    _seasonalIcon=value;
                    var iconPath=Path.Combine(AppContext.BaseDirectory,"Assets",value?"Halloween.ico":"VortexPass.ico");
                    if(File.Exists(iconPath)){
                        Icon=System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(iconPath));
                        var oldIcon=_trayIcon.Icon;_trayIcon.Icon=new System.Drawing.Icon(iconPath);oldIcon?.Dispose();
                    }
                    break;
                case "settings.appearance":
                    _settings.Theme = root.TryGetProperty("theme", out var appearanceTheme) && appearanceTheme.GetString() == "light" ? "light" : "dark";
                    _settings.ReduceMotion = root.TryGetProperty("reduceMotion", out var reduced) && reduced.ValueKind == JsonValueKind.True;
                    await _settingsStore.SaveAsync(_settings);
                    await PublishSettingsStatusAsync();
                    break;
                case "settings.startProtectionOnStartup":
                    if(value && !_settings.SavedConnections.Any(p=>p.Id==_settings.StartupConnectionId))throw new InvalidOperationException("Сначала выберите профиль автозапуска");
                    var previousAutoConnect=_settings.StartProtectionOnStartup;
                    _settings.StartProtectionOnStartup = value;
                    try{await _settingsStore.SaveAsync(_settings);}catch{_settings.StartProtectionOnStartup=previousAutoConnect;throw;}
                    await PublishSettingsStatusAsync();
                    break;
                case "settings.startupConnection":
                    var startupId=root.GetProperty("id").GetString()??"";
                    if(!_settings.SavedConnections.Any(p=>p.Id==startupId))throw new InvalidOperationException("Профиль не найден");
                    var previousStartup=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
                    StartupConnectionPolicy.Select(_settings,startupId);
                    try{await _settingsStore.SaveAsync(_settings);}
                    catch{_settings.StartupConnectionId=previousStartup.StartupConnectionId;_settings.StartProtectionOnStartup=previousStartup.StartProtectionOnStartup;_settings.StartupProfileChoiceRequired=previousStartup.StartupProfileChoiceRequired;throw;}
                    await LogToUiAsync("info","Автоподключение сохранено: выбранный профиль будет подключён при входе в Windows.");                    await PublishSettingsStatusAsync();
                    break;
                case "settings.dnsProvider":
                    var dns = root.TryGetProperty("provider", out var rawDns) ? rawDns.GetString() : null;
                    if (!_advisorRunning && !_protectionTransitioning && dns is "auto" or "google" or "cloudflare" or "quad9")
                    {
                        _settings.SelectedDnsProvider = dns;
                        if (DnsModePolicy.Normalize(_settings.DnsMode) == "doh")
                            await RestartSelectedDnsAsync();
                        await _settingsStore.SaveAsync(_settings);
                        await PublishSettingsStatusAsync();
                        await LogToUiAsync("info", $"Выбран DNS-сервер: {dns}");
                    }
                    break;
                case "settings.dnsMode":
                    if (_advisorRunning || _protectionTransitioning) break;
                    var dnsMode = root.TryGetProperty("mode", out var rawDnsMode) ? rawDnsMode.GetString() : null;
                    if (dnsMode is not ("system" or "google-udp" or "doh")) break;
                    _settings.DnsMode = dnsMode;
                    if(dnsMode=="google-udp")_settings.GoogleDnsAddress="auto";
                    await RestartSelectedDnsAsync();
                    await PublishSettingsStatusAsync();
                    break;
                case "gamefilter.ipsetMode":
                    var ipsetMode = root.TryGetProperty("mode", out var rawMode) ? rawMode.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(ipsetMode))
                    {
                        _settings.GameFilter.IpsetMode = ipsetMode;
                        await _settingsStore.SaveAsync(_settings);
                        await PublishSettingsStatusAsync();
                        if (_protectionStarted.HasValue)
                        {
                            await ApplyProfilesAsync();
                        }
                    }
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
                    var bypassTuning = root.TryGetProperty("tuning", out var rawTuning) && rawTuning.TryGetInt32(out var tuning) ? tuning : 0;
                    var bypassMode = root.TryGetProperty("mode", out var rawProfileMode) ? rawProfileMode.GetString() : null;
                    await SetProfileModeOnlyAsync(bypassMode);
                    await SetBypassLevelAsync(bypassLevel, bypassTuning);
                    break;
                case "profile.favorite.set":
                    // Legacy clients may send this command; all saved profiles are now visible.
                    await PublishConnectionsAsync();
                    break;
                case "profile.launch":
                    var launchLevel = root.TryGetProperty("level", out var rawLaunchLevel) ? rawLaunchLevel.GetString() : null;
                    var launchTuning = root.TryGetProperty("tuning", out var rawLaunchTuning) && rawLaunchTuning.TryGetInt32(out var launchTuningValue) ? launchTuningValue : 0;
                    var launchMode = root.TryGetProperty("mode", out var rawLaunchMode) ? rawLaunchMode.GetString() : null;
                    await LaunchProfileAsync(launchLevel, launchTuning, launchMode);
                    break;
                case "connections.save":
                    await SaveConnectionAsync(root);
                    break;
                case "connections.select":
                    await SelectConnectionAsync(root.GetProperty("id").GetString());
                    break;
                case "connections.launch":
                    await LaunchConnectionAsync(root.GetProperty("id").GetString());
                    break;
                case "connections.delete":
                    await DeleteConnectionAsync(root.GetProperty("id").GetString());
                    break;
                case "compatibility.set":
                    await PublishProfileStatusAsync();
                    break;
                case "compatibility.voice.set":
                    await PublishProfileStatusAsync();
                    break;
                case "compatibility.route.refresh":
                    if (IsMobileCompatibilityMode()) await RunMobileCompatibilityAdvisorAsync();
                    break;
                case "advisor.dns.check":
                    try {await DiagnoseAdvisorDnsAsync(root);}
                    catch(Exception ex){await PostUiAsync(new {type="advisor.dns.status",state="failed",error=ex.Message});await LogToUiAsync("error",ex.Message);}
                    break;
                case "advisor.dns.apply":
                    try {await ApplyAdvisorDnsAsync(root);}
                    catch(Exception ex){await PostUiAsync(new {type="advisor.local.status",state="failed",error=ex.Message});await LogToUiAsync("error",ex.Message);}
                    break;
                case "advisor.dns.cancel":
                    _dnsDiagnosticCts?.Cancel();
                    break;
                case "roblox.diagnose":
                    await DiagnoseRobloxPlayerAsync();
                    break;
                case "roblox.recheck":
                    await OptimizeRobloxConnectionAsync(forceCheck:true);
                    break;
                case "advisor.run":
                    try { await RunConnectionAdvisorAsync(root); }
                    catch (Exception ex)
                    {
                        await PostUiAsync(new { type = "advisor.local.status", state = "failed", error = ex.Message });
                        throw;
                    }
                    break;
                case "advisor.warp.useAutoAndRetry":
                    try{await UseAutoWarpAndRetryAdvisorAsync(root);}
                    catch(Exception ex){await PostUiAsync(new{type="advisor.local.status",state="failed",error=ex.Message});throw;}
                    break;
                case "connections.export":
                case "connections.import.preview":
                case "connections.import":
                    await TransferProfileAsync(root);
                    break;
                case "advisor.stop":
                    StopProfileAdvisor();
                    break;
                case "telegram.https.open":
                    Process.Start(new ProcessStartInfo(TelegramEndpointRegistry.HttpsFallback) { UseShellExecute = true });
                    break;
                case "advisor.provider.set":
                    await SetAdvisorProviderAsync(root.GetProperty("provider").GetString());
                    break;
                case "advisor.apply":
                    await ApplyAdvisorRecommendationAsync(root.GetProperty("id").GetString());
                    break;
                case "youtube.advisor.apply":
                    try{await ApplyYoutubeOnlyAsync(root);}
                    catch(Exception ex){await PublishYoutubeErrorAsync(root,ex);await LogToUiAsync("error","Применение YouTube: "+YoutubeRouteProbe.Error(ex));}
                    break;
                case "youtube.browser.confirm":
                    await ConfirmYoutubeBrowserAsync(root);
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
                case "telegram.proxy.configure":
                    await ConfigureTelegramProxyAsync();
                    break;
                case "warp.toggle":
                    if(!value && (_connectionOriginalWarp is not null) && _protectionRequested)await SetProtectionAsync(false); else await SetWarpConnectedAsync(value);
                    break;
                case "warp.prepare":
                    await PrepareWarpAsync(root);
                    break;
                case "warp.prepare.cancel":
                    _warpPreparationCts?.Cancel();
                    break;
                case "warp.guiStartup.restore":
                    await RestoreWarpGuiStartupAsync();
                    break;
                case "warp.install":
                    await InstallWarpAsync();
                    break;
                case "warp.refresh":
                    await RefreshWarpAsync(runDiagnostics: true);
                    break;
                case "warp.performance.refresh":
                    await RefreshWarpPerformanceAsync();
                    break;
                case "diagnostics.details.update":
                    await SetNetworkDiagnosticDetailsAsync(root);
                    break;
                case "warp.media.compare":
                    await CompareWarpMediaAsync();
                    break;
                case "warp.media.cancel":
                    _warpMediaCts?.Cancel();
                    break;
                case "warp.proxy.snapshot":
                    await PublishWarpStatusAsync();
                    break;
                case "diagnostics.youtube.compare":
                    await CompareYoutubeAsync(root.TryGetProperty("browser",out var browser)?browser.GetString():null);
                    break;
                case "diagnostics.youtube.open":
                    Process.Start(new ProcessStartInfo("https://www.youtube.com/"){UseShellExecute=true});
                    break;
                case "warp.open":
                    _warpService.OpenClient();
                    break;
                case "warp.restore":
                    await SetWarpConnectedAsync(false);
                    break;
                case "warp.retry":
                    await SetWarpConnectedAsync(true);
                    break;
                case "warp.register":
                    await RegisterWarpAsync();
                    break;
                case "warp.copyDiagnostics":
                    CopyWarpDiagnostics();
                    break;
                case "warp.scenarios":
                    await SetWarpScenariosAsync(root);
                    break;
                case "warp.settings.update":
                    await UpdateWarpSettingsAsync(root);
                    break;
                case "warp.optimize":
                    await OptimizeWarpAsync();
                    break;
                case "warp.profile.create":
                case "warp.profile.update":
                    await UpsertWarpProfileAsync(root);
                    break;
                case "warp.profile.delete":
                    await DeleteWarpProfileAsync(root);
                    break;
                case "warp.profile.toggle":
                    await ToggleWarpProfileAsync(root);
                    break;
                case "update.check":
                    await CheckForUpdatesAsync();
                    break;
                case "update.install":
                case "update.installNow":
                    await InstallUpdateNowAsync();
                    break;
                case "update.defer":
                    await DeferUpdateAsync();
                    break;
                case "update.dismissError":
                    await _pendingUpdates.ClearAsync();
                    await CheckForUpdatesAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            await PostUiAsync(new { type = "log", level = "error", message = ex.Message });
        }
    }

    private async Task SetWarpConnectedAsync(bool connected, CancellationToken cancellationToken=default, bool runDiagnostics=true)
    {
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,cancellationToken);
        var operationToken=operation.Token;
        if (_warpTransitioning) return;
        if (connected && _warpStatus.CliPath is null)
        {
            await LogToUiAsync("warn", "Cloudflare WARP не установлен. Откройте вкладку WARP и нажмите «Установить».");
            await PublishWarpStatusAsync();
            return;
        }

        _warpTransitioning = true;
        ResetHealthChecksAfterRouteChange();
        DnsOverrideService.InvalidateCache();
        _warpStatus = _warpStatus with { State = WarpConnectionState.Connecting, Detail = connected ? "Подготовка обхода WARP" : "WARP отключается", Progress = connected ? .05 : null };
        await PublishWarpStatusAsync();
        UpdateTrayState(_protectionRequested);
        try
        {
            if (!connected)
                await StopWarpRoutingAsync(operationToken);
            if (connected)
            {
                if (GetEffectiveWarpRoutingMode() is "full" or "dnsOnly")
                    await _dnsGuard.StopAsync(CancellationToken.None);
                if (!_warpStatus.Registered)
                    _warpStatus = await _warpService.GetStatusAsync(operationToken);
                if (!_warpStatus.Registered)
                    throw new InvalidOperationException("Сначала выполните бесплатную регистрацию Consumer WARP во вкладке WARP.");
                if(GetEffectiveWarpRoutingMode() is not ("smart" or "proxy"))
                {
                    await SetWarpBootstrapAsync(true, operationToken, telegramCompatibility: ShouldUseWarpForTelegram());
                    _warpStatus = await ApplyWarpClientPreferencesAsync(reconnect: false, operationToken);
                }
                await LogToUiAsync("info", GetEffectiveWarpRoutingMode() is "smart" or "proxy" ? "WARP: проверяем транспорт и фактический локальный прокси перед применением PAC." : "WARP bootstrap активен: применены узкие правила TCP/443, QUIC/443 и UDP/2408.");
            }

            var progress = new Progress<WarpStatus>(status =>
            {
                _warpStatus = status;
                _ = PublishWarpStatusAsync();
                UpdateTrayState(_protectionRequested);
            });
            _warpStatus = connected&&GetEffectiveWarpRoutingMode() is ("smart" or "proxy")
                ?await ConnectSelectiveWarpAsync(operationToken)
                :await _warpService.SetConnectedAsync(connected, operationToken, progress);
            if(!connected)_warpRouteConfirmed=false;
            _settings.Warp.RequestedConnected = connected && _warpStatus.State == WarpConnectionState.Connected;
            _settings.Warp.DetectedCliPath = _warpStatus.CliPath ?? string.Empty;
            _settings.Warp.LastCheckedUtc = _warpStatus.CheckedAt;
            _settings.Warp.LastError = _warpStatus.Error ?? string.Empty;
            if (_warpStatus.State == WarpConnectionState.Connected)
            {
                await SetWarpBootstrapAsync(true, operationToken, telegramCompatibility: ShouldUseWarpForTelegram());
                await ApplyWarpRoutingAsync(operationToken);
                await EnsureTelegramSmartRouteAsync(operationToken);
                DnsOverrideService.InvalidateCache();
                if(runDiagnostics) _warpDiagnostics = await _warpService.DiagnoseAsync(
                    _settings.Warp.Telegram,
                    _settings.Warp.Games,
                    operationToken,
                    telegramProxyRunning: _telegramProxy.IsRunning,
                    telegramProxyHasClientActivity: _telegramProxy.HasTelegramClientActivity,
                    fullTelegramProbe: false,
                    proxyUri: GetWarpProxyUri());
            }
            else if (!connected)
                _warpDiagnostics = null;
            if (_warpStatus.State == WarpConnectionState.Error)
                await RecoverWarpAfterFailureAsync(_warpStatus, operationToken);
            else if (!connected)
            {
                await SetWarpBootstrapAsync(false, operationToken, telegramCompatibility: false);
                if (MobileDnsPolicy.ShouldRun(_protectionRequested, _settings.CompatibilityMode, warpOwnsSystemDns: false))
                    await StartProtectedDnsGuardAsync(operationToken);
            }
            await _settingsStore.SaveAsync(_settings, operationToken);
            await LogToUiAsync(_warpStatus.State == WarpConnectionState.Error ? "error" : "ok", _warpStatus.Detail);
        }
        catch(OperationCanceledException) when(operationToken.IsCancellationRequested){throw;}
        catch (Exception ex)
        {
            _warpStatus = _warpStatus with { State = WarpConnectionState.Error, Detail = ex.Message, Error = ex.Message, CheckedAt = DateTimeOffset.UtcNow };
            _settings.Warp.LastError = ex.Message;
            await RecoverWarpAfterFailureAsync(_warpStatus, operationToken);
            await _settingsStore.SaveAsync(_settings, operationToken);
            await LogToUiAsync("error", $"WARP: {ex.Message}");
        }
        finally
        {
            _warpTransitioning = false;
            _nextHealthCheckUtc = DateTimeOffset.UtcNow.AddSeconds(12);
            await PublishWarpStatusAsync();
            UpdateTrayState(_protectionRequested);
        }
    }

    private async Task RecoverWarpAfterFailureAsync(WarpStatus failure, CancellationToken cancellationToken)
    {
        // Disconnect first while the bootstrap route still exists. Removing
        // winws before the daemon leaves Connecting can strand Cloudflare DNS/WFP
        // filters and make the ordinary Internet appear offline.
        var recovery = await _warpService.RecoverOrdinaryRouteAsync(cancellationToken);
        try { await SetWarpBootstrapAsync(false, cancellationToken, telegramCompatibility: false); }
        catch (Exception restoreError)
        {
            await LogToUiAsync("error", $"Не удалось восстановить исходные правила winws: {restoreError.Message}");
            recovery = recovery with { Succeeded = false, Detail = $"{recovery.Detail} Исходные правила winws не восстановлены: {restoreError.Message}" };
        }

        _settings.Warp.RequestedConnected = false;
        DnsOverrideService.InvalidateCache();
        var original = failure.Error ?? failure.Detail;
        _warpStatus = failure with
        {
            State = WarpConnectionState.Error,
            Detail = recovery.Succeeded ? $"{original} Обычный интернет восстановлен." : $"{original} {recovery.Detail}",
            Error = recovery.Succeeded ? $"{original} Обычный интернет восстановлен." : $"{original} {recovery.Detail}",
            ServiceRunning = recovery.Status.ServiceRunning,
            Registered = recovery.Status.Registered || failure.Registered,
            Protocol = recovery.Status.Protocol ?? failure.Protocol,
            CheckedAt = DateTimeOffset.UtcNow
        };
        await LogToUiAsync(recovery.Succeeded ? "warn" : "error", recovery.Detail);
        if (MobileDnsPolicy.ShouldRun(_protectionRequested, _settings.CompatibilityMode, warpOwnsSystemDns: false))
            await StartProtectedDnsGuardAsync(cancellationToken);
    }

    private async Task RegisterWarpAsync()
    {
        if (_warpTransitioning) return;
        var confirmation = System.Windows.MessageBox.Show(
            "Будет создана бесплатная регистрация Consumer WARP. Продолжая, вы принимаете условия использования Cloudflare. VortexPass не сохраняет ID устройства, ключи или лицензию.",
            "Регистрация Consumer WARP", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes) return;
        _warpTransitioning = true;
        var bootstrapAlreadyActive = _warpBootstrap.IsActive;
        _warpStatus = _warpStatus with { State = WarpConnectionState.Connecting, Detail = "Регистрация Consumer WARP", Progress = .08 };
        await PublishWarpStatusAsync();
        try
        {
            // Registration is an HTTPS operation outside the future tunnel.
            // Its lease neither changes hosts nor hands Windows DNS to WARP.
            _warpStatus = await WarpRegistrationBootstrap.RunAsync(bootstrapAlreadyActive,
                token => _warpBootstrap.StartAsync(WinwsProfileComposer.ComposeWarpBootstrap(), _protectionRequested, token),
                token => _warpService.RegisterConsumerAsync(token), async () => { await _warpBootstrap.StopAsync(); }, _lifetime.Token);
            if (!_warpStatus.Registered)
            {
                var error = _warpStatus.Error ?? _warpStatus.Detail;
                if (WarpService.RequiresNativeOnboarding(error)) _warpService.OpenClient();
                throw new InvalidOperationException(error);
            }
            await LogToUiAsync("ok", "Бесплатная регистрация Consumer WARP готова.");
        }
        catch (Exception ex)
        {
            _warpStatus = _warpStatus with { State = WarpConnectionState.Error, Detail = ex.Message, Error = ex.Message, CheckedAt = DateTimeOffset.UtcNow };
            await LogToUiAsync("error", $"Регистрация WARP: {ex.Message}");
        }
        finally
        {
            _warpTransitioning = false;
            await PublishWarpStatusAsync();
        }
    }

    private void CopyWarpDiagnostics()
    {
        try
        {
            System.Windows.Clipboard.SetText(WarpService.BuildSafeDiagnosticReport(_warpStatus, _warpDiagnostics, _warpBootstrap.IsActive));
            _ = LogToUiAsync("ok", "Безопасная диагностика WARP скопирована без идентификаторов, ключей и лицензии.");
        }
        catch (Exception ex) { _ = LogToUiAsync("error", $"Не удалось скопировать диагностику WARP: {ex.Message}"); }
    }

    private async Task SetWarpBootstrapAsync(bool enabled, CancellationToken cancellationToken, bool? telegramCompatibility = null)
    {
        var targetCompatibility = telegramCompatibility ?? _warpTelegramCompatibilityActive;
        if(enabled&&!WarpPreflight.RequiresBootstrap(_warpBootstrapMode))
        {
            await _warpBootstrap.StopAsync();
            _warpTelegramCompatibilityActive=targetCompatibility;
            return;
        }
        if (enabled == _warpBootstrap.IsActive && targetCompatibility == _warpTelegramCompatibilityActive) return;
        var profiles = _activeProfiles.AnyEnabled ? _activeProfiles : new BypassProfileSettings();
        var protectionRunning = _components.GetStatuses().Any(status => status.Id == "dpi" && status.State == ServiceState.Running);

        if (enabled)
        {
            await _warpBootstrap.StartAsync(WinwsProfileComposer.ComposeWarpBootstrap(_warpBootstrapMode), protectionRunning, cancellationToken);
            _warpTelegramCompatibilityActive = targetCompatibility;
            // Remove every VortexPass-owned CDN pin before Cloudflare changes
            // route. The main protection process stays untouched and running.
            await ApplyRuntimeHostsAsync(profiles, cancellationToken);
        }
        else
        {
            _warpTelegramCompatibilityActive = false;
            await _warpBootstrap.StopAsync();
            DnsOverrideService.InvalidateCache();
            if (profiles.AnyEnabled)
                await ApplyRuntimeHostsAsync(profiles, cancellationToken);
            else
                await _components.StopAllAsync(); // no main winws: remove the now-unused owned driver
        }
        await SendServiceSnapshotAsync();
    }

    private async Task RefreshWarpAsync(bool runDiagnostics)
    {
        if (_warpTransitioning || _startupConnectCts is not null || _connectionLaunching) return;
        try
        {
            var refreshed=await _warpService.GetStatusAsync(_lifetime.Token);
            if(_warpTransitioning)return;
            _warpStatus=refreshed;
            if(!runDiagnostics&&_warpStatus.Registered&&_settings.Warp.SuppressGui)await ApplyWarpGuiPolicyAsync();
            // A user-requested diagnostic refresh is read-only: it must not restart
            // winws or alter the active protection. Bootstrap reconciliation is
            // reserved for background/startup status refreshes.
            var telegramCompatibility = ShouldUseWarpForTelegram();
            if (!runDiagnostics && _warpStatus.State == WarpConnectionState.Connected &&
                (!_warpBootstrap.IsActive || _warpTelegramCompatibilityActive != telegramCompatibility))
                await SetWarpBootstrapAsync(true, _lifetime.Token, telegramCompatibility: telegramCompatibility);
            else if (!runDiagnostics && _warpStatus.State == WarpConnectionState.Disconnected && (_warpBootstrap.IsActive || _warpTelegramCompatibilityActive))
                await SetWarpBootstrapAsync(false, _lifetime.Token, telegramCompatibility: false);
            else if (!runDiagnostics && WarpService.NeedsEmergencyRecovery(_warpStatus.State, _settings.Warp.RequestedConnected, _warpBootstrap.IsActive))
                await RecoverWarpAfterFailureAsync(_warpStatus, _lifetime.Token);
            if (!runDiagnostics && _warpStatus.State == WarpConnectionState.Connected)
            {
                await ApplyWarpRoutingAsync(_lifetime.Token);
                await EnsureTelegramSmartRouteAsync(_lifetime.Token);
            }
            else if (!runDiagnostics && _warpStatus.State == WarpConnectionState.Disconnected)
                await StopWarpRoutingAsync(_lifetime.Token);
            if (runDiagnostics && _warpStatus.State == WarpConnectionState.Connected)
                _warpDiagnostics = await _warpService.DiagnoseAsync(
                    _settings.Warp.Telegram,
                    _settings.Warp.Games,
                    _lifetime.Token,
                    telegramProxyRunning: _telegramProxy.IsRunning,
                    telegramProxyHasClientActivity: _telegramProxy.HasTelegramClientActivity,
                    fullTelegramProbe: true,
                    proxyUri: GetWarpProxyUri());
            _settings.Warp.DetectedCliPath = _warpStatus.CliPath ?? string.Empty;
            _settings.Warp.LastCheckedUtc = _warpStatus.CheckedAt;
            _settings.Warp.LastError = _warpStatus.Error ?? string.Empty;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        catch (Exception ex)
        {
            _warpStatus = new(WarpConnectionState.Error, ex.Message, Error: ex.Message, CheckedAt: DateTimeOffset.UtcNow);
        }
        await PublishWarpStatusAsync();
        UpdateTrayState(_protectionRequested);
    }

    private async Task InstallWarpAsync()
    {
        if (_warpTransitioning) return;
        if (_warpService.Detect().CliPath is not null)
        {
            await RefreshWarpAsync(runDiagnostics: false);
            return;
        }
        var confirmation = System.Windows.MessageBox.Show(
            "VortexPass скачает официальный установщик Cloudflare WARP, проверит цифровую подпись Cloudflare и запустит штатную установку Windows. Продолжить?",
            "Установка Cloudflare WARP", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes) return;

        _warpTransitioning = true;
        _warpStatus = new(WarpConnectionState.Connecting, "Скачивание официального установщика WARP");
        await PublishWarpStatusAsync();
        try
        {
            var progress = new Progress<double>(value => _ = PostUiAsync(new { type = "warp.progress", progress = value }));
            var installer = await _warpService.DownloadInstallerAsync(progress, _lifetime.Token);
            _warpStatus = _warpStatus with { Detail = "Ожидание завершения установки WARP" };
            await PublishWarpStatusAsync();
            var exitCode = await _warpService.InstallAsync(installer, _lifetime.Token);
            if (exitCode != 0) throw new InvalidOperationException($"Установщик Cloudflare WARP завершился с кодом {exitCode}.");
            _warpStatus = await _warpService.GetStatusAsync(_lifetime.Token);
            await LogToUiAsync("ok", "Официальный Cloudflare WARP установлен. Подключение остаётся выключенным до команды пользователя.");
        }
        catch (Exception ex)
        {
            _warpStatus = new(WarpConnectionState.Error, ex.Message, Error: ex.Message, CheckedAt: DateTimeOffset.UtcNow);
            await LogToUiAsync("error", $"Установка WARP: {ex.Message}");
        }
        finally
        {
            _warpTransitioning = false;
            await PublishWarpStatusAsync();
            UpdateTrayState(_protectionRequested);
        }
    }

    private async Task SetWarpScenariosAsync(JsonElement root)
    {
        if (root.TryGetProperty("telegram", out var telegram) && telegram.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _settings.Warp.Telegram = telegram.GetBoolean();
        if (root.TryGetProperty("games", out var games) && games.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _settings.Warp.Games = games.GetBoolean();
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        await PublishWarpStatusAsync();
    }

    private async Task<WarpStatus> ApplyWarpClientPreferencesAsync(bool reconnect, CancellationToken cancellationToken)
    {
        var protocol = WarpTransportPolicy.Resolve(_settings.Warp).Protocol;
        var effectiveMode = GetEffectiveWarpRoutingMode();
        var status = await _warpService.ApplyConfigurationAsync(new WarpClientConfiguration(
            effectiveMode, protocol, WarpTransportPolicy.Resolve(_settings.Warp).Masque,
            _settings.Warp.DnsMode, _settings.Warp.ProxyPort), reconnect, cancellationToken);
        _warpAppliedRoutingMode = effectiveMode;
        return status;
    }

    private string GetEffectiveWarpRoutingMode()
    {
        if (_settings.Warp.RoutingMode != "smart") return _settings.Warp.RoutingMode;
        var decisions = _settings.Warp.NetworkOptimizations
            .Where(item => item.NetworkKey == WarpService.GetNetworkFingerprint()).OrderByDescending(item => item.CheckedAt)
            .FirstOrDefault()?.RouteDecisions;
        return _settings.Warp.RouteProfiles.Where(profile => profile.Enabled).Any(profile =>
            profile.Connection == "warpSystem" || (profile.Connection == "auto" && decisions?.GetValueOrDefault(profile.Id) == "warpSystem"))
            ? "full" : "smart";
    }

    private bool ShouldUseWarpForTelegram() => WarpRoutingPlanner.UsesWarpForBuiltIn(
        _settings.Warp, WarpService.GetNetworkFingerprint(), "telegram");

    private async Task EnsureTelegramSmartRouteAsync(CancellationToken cancellationToken)
    {
        if (_warpStatus.State != WarpConnectionState.Connected || GetEffectiveWarpRoutingMode() is not ("smart" or "proxy")) return;
        var profile = _settings.Warp.RouteProfiles.FirstOrDefault(item => item.Enabled &&
            item.BuiltInId.Equals("telegram", StringComparison.OrdinalIgnoreCase) && item.Connection == "auto");
        if (profile is null) return;
        var networkKey = WarpService.GetNetworkFingerprint();
        var existing = _settings.Warp.NetworkOptimizations
            .Where(item => item.NetworkKey == networkKey).OrderByDescending(item => item.CheckedAt).FirstOrDefault();
        if (existing?.RouteDecisions.GetValueOrDefault(profile.Id) == "warpProxy") return;

        var target = new Uri("https://web.telegram.org/");
        var proxyUri = new Uri($"socks5://127.0.0.1:{_settings.Warp.ProxyPort}");
        var directTask = _warpService.ProbeWebRouteAsync(target, null, cancellationToken);
        var proxyTask = _warpService.ProbeWebRouteAsync(target, proxyUri, cancellationToken);
        await Task.WhenAll(directTask, proxyTask);
        var direct = directTask.Result;
        var proxy = proxyTask.Result;
        var selected = WarpRoutingPlanner.SelectMeasuredWebRoute(
            direct.Available, direct.MedianLatencyMs, proxy.Available, proxy.MedianLatencyMs);
        if (selected is null)
        {
            await ApplyRuntimeHostsAsync(_activeProfiles.AnyEnabled ? _activeProfiles : _settings.Profiles, cancellationToken);
            await LogToUiAsync("warn", $"Telegram Web не ответил напрямую ({direct.Detail}) и через WARP proxy ({proxy.Detail}); проверены кандидаты DNS/Flowseal, неподтверждённые IP не закрепляются.");
            return;
        }


        if (existing is null)
        {
            existing = new WarpNetworkOptimization
            {
                NetworkKey = networkKey,
                SelectedProtocol = _warpStatus.Protocol?.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) == true ? "WireGuard" : "MASQUE"
            };
            _settings.Warp.NetworkOptimizations.Add(existing);
        }
        existing.RouteDecisions[profile.Id] = selected;
        existing.CheckedAt = DateTimeOffset.UtcNow;
        existing.Reason = selected == "warpProxy"
            ? $"Telegram Web: прямой маршрут не ответил ({direct.Detail}), выбран WARP proxy ({proxy.MedianLatencyMs} мс)."
            : $"Telegram Web: прямой маршрут подтверждён ({direct.MedianLatencyMs} мс), WARP proxy — {proxy.MedianLatencyMs?.ToString() ?? "нет ответа"} мс.";
        await _settingsStore.SaveAsync(_settings, cancellationToken);
        if (selected == "warpProxy")
        {
            await ApplyWarpRoutingAsync(cancellationToken);
            await LogToUiAsync("ok", $"Telegram Web автоматически направлен через локальный WARP proxy ({proxy.MedianLatencyMs} мс); остальные профили не изменены.");
        }
        else
        {
            await LogToUiAsync("info", existing.Reason);
        }
    }

    private Uri? GetWarpProxyUri() =>
        _warpStatus.State == WarpConnectionState.Connected && GetEffectiveWarpRoutingMode() is "smart" or "proxy"
            ? new Uri($"socks5://127.0.0.1:{_settings.Warp.ProxyPort}") : null;

    private async Task ApplyWarpRoutingAsync(CancellationToken cancellationToken)
    {
        var plan = WarpRoutingPlanner.BuildPlan(_settings.Warp, WarpService.GetNetworkFingerprint());
        if (plan.Conflicts.Count > 0)
            throw new InvalidOperationException($"Конфликт WARP-профилей: {string.Join("; ", plan.Conflicts)}");
        foreach (var warning in plan.Warnings) await LogToUiAsync("warn", warning);
        var effectiveMode = GetEffectiveWarpRoutingMode();
        var telegramCompatibility = ShouldUseWarpForTelegram();
        if (_warpStatus.State == WarpConnectionState.Connected && _warpTelegramCompatibilityActive != telegramCompatibility)
            await SetWarpBootstrapAsync(true, cancellationToken, telegramCompatibility);
        var localProxy = effectiveMode is "smart" or "proxy";
        if (localProxy && plan.ProxyDomains.Count > 0)
        {
            _warpPac.DiagnosticsEnabled=_settings.NetworkDiagnosticDetails;
            var pacUri = await _warpPac.StartAsync(plan.ProxyDomains, _settings.Warp.ProxyPort, _settings.Warp.ProxyPort + 1, cancellationToken);
            _proxyManager.ApplyPac(pacUri);
            await LogToUiAsync("ok", $"Умный WARP: {plan.ProxyDomains.Count} доменных зон направлены через локальный SOCKS5; остальной Web-трафик — DIRECT.");
        }
        else
        {
            await _warpPac.StopAsync();
            RestoreProxyUnlessWarpPacActive();
        }

        var fullTunnel = effectiveMode == "full";
        var owned = await _warpService.SyncSplitTunnelExclusionsAsync(
            fullTunnel ? plan.DirectDomains : [], fullTunnel ? plan.DirectIpRanges : [],
            _settings.Warp.OwnedSplitTunnelHosts, _settings.Warp.OwnedSplitTunnelIps, cancellationToken);
        _settings.Warp.OwnedSplitTunnelHosts = owned.Hosts.ToList();
        _settings.Warp.OwnedSplitTunnelIps = owned.Ips.ToList();
        await _settingsStore.SaveAsync(_settings, cancellationToken);
    }

    private async Task StopWarpRoutingAsync(CancellationToken cancellationToken)
    {
        _warpMediaCts?.Cancel();
        await _warpPac.StopAsync();
        _proxyManager.RestoreIfOwned();
        if (_settings.Warp.OwnedSplitTunnelHosts.Count > 0 || _settings.Warp.OwnedSplitTunnelIps.Count > 0)
        {
            var owned = await _warpService.SyncSplitTunnelExclusionsAsync([], [], _settings.Warp.OwnedSplitTunnelHosts, _settings.Warp.OwnedSplitTunnelIps, cancellationToken);
            _settings.Warp.OwnedSplitTunnelHosts = owned.Hosts.ToList();
            _settings.Warp.OwnedSplitTunnelIps = owned.Ips.ToList();
        }
    }

    private void RestoreProxyUnlessWarpPacActive()
    {
        if (_warpPac.PacUri is null) _proxyManager.RestoreIfOwned();
    }

    private async Task UpsertWarpProfileAsync(JsonElement root)
    {
        if (!root.TryGetProperty("profile", out var value) || value.ValueKind != JsonValueKind.Object) return;
        var id = value.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        var existing = _settings.Warp.RouteProfiles.FirstOrDefault(profile => profile.Id == id);
        existing ??= new WarpRouteProfile { Id = Guid.NewGuid().ToString("N") };
        if (!_settings.Warp.RouteProfiles.Contains(existing)) _settings.Warp.RouteProfiles.Add(existing);
        if (string.IsNullOrEmpty(existing.BuiltInId))
        {
            existing.Name = value.TryGetProperty("name", out var name) ? (name.GetString() ?? "Новый профиль").Trim() : existing.Name;
            existing.Kind = value.TryGetProperty("kind", out var kind) && kind.GetString() == "game" ? "game" : "site";
            existing.ExecutablePath = value.TryGetProperty("executablePath", out var executable) ? executable.GetString() ?? string.Empty : existing.ExecutablePath;
            existing.Domains = ReadStringArray(value, "domains").Select(WarpRoutingPlanner.NormalizeDomain).Where(item => item is not null).Cast<string>().Distinct().ToList();
            existing.IpRanges = ReadStringArray(value, "ipRanges").Where(WarpRoutingPlanner.IsIpOrCidr).Distinct().ToList();
        }
        existing.Connection = WarpRoutingPlanner.NormalizeConnection(value.TryGetProperty("connection", out var connection) ? connection.GetString() : existing.Connection);
        if (existing.Kind == "game" && existing.Connection == "warpProxy") existing.Connection = "direct";
        await SaveAndApplyWarpProfilesAsync();
    }

    private async Task DeleteWarpProfileAsync(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var value) ? value.GetString() : null;
        _settings.Warp.RouteProfiles.RemoveAll(profile => profile.Id == id && string.IsNullOrEmpty(profile.BuiltInId));
        await SaveAndApplyWarpProfilesAsync();
    }

    private async Task ToggleWarpProfileAsync(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var value) ? value.GetString() : null;
        var profile = _settings.Warp.RouteProfiles.FirstOrDefault(item => item.Id == id);
        if (profile is not null) profile.Enabled = root.TryGetProperty("value", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        await SaveAndApplyWarpProfilesAsync();
    }

    private async Task SaveAndApplyWarpProfilesAsync()
    {
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        try
        {
            if (_warpStatus.State == WarpConnectionState.Connected)
            {
                if (_warpAppliedRoutingMode != GetEffectiveWarpRoutingMode())
                    _warpStatus = await ApplyWarpClientPreferencesAsync(reconnect: true, _lifetime.Token);
                await ApplyWarpRoutingAsync(_lifetime.Token);
            }
        }
        catch (Exception ex) { await LogToUiAsync("error", ex.Message); }
        await PublishWarpStatusAsync();
    }

    private static IEnumerable<string> ReadStringArray(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty)
            : [];

    private async Task RefreshWarpPerformanceAsync()
    {
        if (_warpTransitioning || _startupConnectCts is not null || _connectionLaunching) return;
        try
        {
            _warpStatus = await _warpService.GetStatusAsync(_lifetime.Token);
            var connected = _warpStatus.State == WarpConnectionState.Connected;
            _warpPerformance = await _warpService.MeasurePerformanceAsync(connected, _lifetime.Token, proxyUri: GetWarpProxyUri());
            if (!connected)
            {
                _settings.Warp.PerformanceBaseline = new WarpPerformanceBaseline
                {
                    NetworkKey = _warpPerformance.NetworkKey,
                    CheckedAt = _warpPerformance.CheckedAt,
                    MedianLatencyMs = _warpPerformance.Endpoints
                        .Where(item => item.Value.Available && item.Value.MedianLatencyMs.HasValue)
                        .ToDictionary(item => item.Key, item => item.Value.MedianLatencyMs!.Value, StringComparer.OrdinalIgnoreCase),
                    JitterMs = _warpPerformance.Endpoints
                        .Where(item => item.Value.Available && item.Value.JitterMs.HasValue)
                        .ToDictionary(item => item.Key, item => item.Value.JitterMs!.Value, StringComparer.OrdinalIgnoreCase)
                };
                _warpPerformanceComparison = new(WarpPerformanceVerdict.BaselineSaved, "Прямой baseline сохранён для текущей сети.");
                await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            }
            else
            {
                var baseline = _settings.Warp.PerformanceBaseline;
                _warpPerformanceComparison = baseline is not null && baseline.NetworkKey == _warpPerformance.NetworkKey
                    ? WarpService.ComparePerformance(_warpPerformance.Endpoints, baseline.MedianLatencyMs)
                    : new(WarpPerformanceVerdict.BaselineUnavailable, "Для этой сети нет прямого baseline. Отключите WARP и выполните проверку один раз.");
            }
            await LogToUiAsync("info", $"WARP производительность: {_warpPerformanceComparison.Detail}");
        }
        catch (Exception ex)
        {
            await LogToUiAsync("error", $"WARP производительность: {ex.Message}");
        }
        finally
        {
            await PublishWarpStatusAsync();
        }
    }

    private Task PublishWarpStatusAsync() => PostUiAsync(new
    {
        type = "warp.status",
        routeConfirmed = _warpRouteConfirmed,
        activeTransport = _warpStatus.State==WarpConnectionState.Connected?_activeWarpVariant:null,
        observedTransport = _warpStatus.State==WarpConnectionState.Connected?_activeWarpObservation?.Transport:null,
        diagnosticDetails = _settings.NetworkDiagnosticDetails,
        proxyMetrics = _warpPac?.ProxyMetrics,
        mediaComparison = _warpMediaComparison,
        mediaProgress = _warpMediaProgress,
        mediaRunning = _warpMediaCts is not null,
        youtubeComparison = _youtubeComparison,
        youtubeComparing = _youtubeComparing,
        guiSuppressed = _settings.Warp.SuppressGui,
        guiStartupCanRestore = _settings.Warp.GuiStartupBackup.Count>0,
        preparationCompleted = _settings.Warp.PreparationCompletedUtc is not null,
        setupChecked = true,
        state = WarpStateName(_warpStatus.State),
        installed = _warpStatus.CliPath is not null,
        connected = _warpStatus.State == WarpConnectionState.Connected,
        pending = _warpTransitioning||_warpMediaCts is not null,
        detail = _warpStatus.Detail,
        error = _warpStatus.Error,
        version = _warpStatus.Version,
        cliPath = _warpStatus.CliPath,
        checkedAt = _warpStatus.CheckedAt,
        stage = _warpStatus.Stage,
        progress = _warpStatus.Progress,
        registered = _warpStatus.Registered,
        serviceRunning = _warpStatus.ServiceRunning,
        protocol = _warpStatus.Protocol,
        routingMode = _settings.Warp.RoutingMode,
        effectiveRoutingMode = GetEffectiveWarpRoutingMode(),
        transportSelection = WarpTransportPolicy.Selection(_settings.Warp),
                    transportSelectionSource = _settings.Warp.TransportSelectionSource,
                    transportCandidates = CurrentWarpCandidates(),
                    protocolPreference = WarpTransportPolicy.IsAutomatic(_settings.Warp)?"auto":WarpTransportPolicy.Resolve(_settings.Warp).Protocol,
        masquePreference = WarpTransportPolicy.Resolve(_settings.Warp).Masque,
        dnsMode = _settings.Warp.DnsMode,
        proxyPort = _settings.Warp.ProxyPort,
        profiles = _settings.Warp.RouteProfiles.Select(profile => new
        {
            id = profile.Id, name = profile.Name, kind = profile.Kind, builtInId = profile.BuiltInId,
            enabled = profile.Enabled, connection = profile.Connection, domains = profile.Domains,
            ipRanges = profile.IpRanges, executablePath = profile.ExecutablePath
        }),
        optimization = _settings.Warp.NetworkOptimizations
            .Where(item => item.NetworkKey == WarpService.GetNetworkFingerprint())
            .OrderByDescending(item => item.CheckedAt).FirstOrDefault(),
        routingConflicts = WarpRoutingPlanner.BuildPlan(_settings.Warp, WarpService.GetNetworkFingerprint()).Conflicts,
        routingWarnings = WarpRoutingPlanner.BuildPlan(_settings.Warp, WarpService.GetNetworkFingerprint()).Warnings,
        bootstrapActive = _warpBootstrap.IsActive,
        telegramCompatibilityActive = _warpTelegramCompatibilityActive,
        scenarios = new { telegram = _settings.Warp.Telegram, games = _settings.Warp.Games },
        performance = _warpPerformance is null ? null : new
        {
            warpConnected = _warpPerformance.WarpConnected,
            checkedAt = _warpPerformance.CheckedAt,
            summary = _warpPerformance.Summary,
            verdict = _warpPerformanceComparison?.Verdict.ToString(),
            recommendation = _warpPerformanceComparison?.Detail,
            endpoints = _warpPerformance.Endpoints.ToDictionary(endpoint => endpoint.Key, endpoint => new
            {
                label = endpoint.Value.Label,
                available = endpoint.Value.Available,
                medianLatencyMs = endpoint.Value.MedianLatencyMs,
                jitterMs = endpoint.Value.JitterMs,
                detail = endpoint.Value.Detail
            })
        },
        diagnostics = _warpDiagnostics is null ? null : new
        {
            routeActive = _warpDiagnostics.WarpRouteActive,
            dnsAvailable = _warpDiagnostics.DnsAvailable,
            latencyMs = _warpDiagnostics.LatencyMs,
            externalIp = _warpDiagnostics.ExternalIp,
            coloCode = _warpDiagnostics.ColoCode,
            traceLocationCode = _warpDiagnostics.TraceLocationCode,
            pop = _warpDiagnostics.PopLocation is null ? null : new
            {
                code = _warpDiagnostics.PopLocation.Code,
                city = _warpDiagnostics.PopLocation.City,
                country = _warpDiagnostics.PopLocation.Country,
                status = _warpDiagnostics.PopLocation.Status,
                displayName = _warpDiagnostics.PopLocation.DisplayName
            },
            detail = _warpDiagnostics.Detail,
            checkedAt = _warpDiagnostics.CheckedAt,
            scenarios = _warpDiagnostics.Scenarios,
            scenarioDetails = _warpDiagnostics.ScenarioDetails?.ToDictionary(
                scenario => scenario.Key,
                scenario => new
                {
                    state = WarpProbeStateName(scenario.Value.State),
                    summary = scenario.Value.Summary,
                    endpoints = scenario.Value.Endpoints.ToDictionary(
                        endpoint => endpoint.Key,
                        endpoint => new
                        {
                            label = endpoint.Value.Label,
                            state = WarpProbeStateName(endpoint.Value.State),
                            detail = endpoint.Value.Detail,
                            httpStatus = endpoint.Value.HttpStatus,
                            latencyMs = endpoint.Value.LatencyMs
                        })
                })
        }
    });

    private static string WarpProbeStateName(WarpProbeState state) => state switch
    {
        WarpProbeState.Available => "available",
        WarpProbeState.Restricted => "restricted",
        WarpProbeState.Partial => "partial",
        WarpProbeState.ServiceError => "serviceError",
        WarpProbeState.TimedOut => "timedOut",
        WarpProbeState.RouteUnconfirmed => "routeUnconfirmed",
        WarpProbeState.AvailabilityUnknown => "availabilityUnknown",
        WarpProbeState.RegionRestricted => "regionRestricted",
        _ => "unavailable"
    };

    private static string WarpStateName(WarpConnectionState state) => state switch
    {
        WarpConnectionState.NotInstalled => "notInstalled",
        WarpConnectionState.Disconnected => "disconnected",
        WarpConnectionState.Connecting => "connecting",
        WarpConnectionState.Connected => "connected",
        _ => "error"
    };

    private async Task SetProtectionAsync(bool enabled,CancellationToken cancellationToken=default)
    {
        if (!enabled)
        {
            _startupConnectCts?.Cancel();
            Interlocked.Increment(ref _userStopGeneration);
            _advisorStopProtectionRequested = _advisorRunning;
            StopProfileAdvisor();
            CancelPendingProfileWork();
            await StopProtectionAsync();
            return;
        }

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
            await ApplyProfilesAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested();
            await OptimizeRobloxConnectionAsync();
            return;
        }

    }

    private void CancelPendingProfileWork()
    {
        try { _profileApplyCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { _healthCheckCts.Cancel(); }
        catch (ObjectDisposedException) { }
        Interlocked.Increment(ref _healthGeneration);
    }

    private void ResetHealthChecksAfterRouteChange()
    {
        try { _healthCheckCts.Cancel(); }
        catch (ObjectDisposedException) { }
        _healthCheckCts.Dispose();
        _healthCheckCts = new CancellationTokenSource();
        Interlocked.Increment(ref _healthGeneration);
        _healthFailureStreaks.Clear();
    }

    private async Task ConfigureTelegramProxyAsync()
    {
        if (!_telegramProxy.IsRunning)
        {
            await LogToUiAsync("warn", "Сначала отметьте Telegram и запустите защиту, чтобы включить локальный Telegram WS Proxy.");
            return;
        }

        try
        {
            _telegramProxy.OpenTelegramConfiguration();
            await LogToUiAsync("info", "Telegram открыт. Подтвердите подключение к локальному прокси 127.0.0.1:1443.");
        }
        catch (Exception ex)
        {
            await LogToUiAsync("error", $"Не удалось открыть настройку прокси Telegram Desktop: {ex.Message}");
        }
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
            _settings.StartWithWindows = previous; try{_startupService.SetEnabled(previous,Path.Combine(AppContext.BaseDirectory,"VortexPass.exe"));}catch(Exception rollback){await LogToUiAsync("error","Восстановление задачи автозапуска: "+rollback.Message);}
            await LogToUiAsync("error", $"Не удалось изменить автозапуск: {ex.Message}");
        }
        await PublishSettingsStatusAsync();
    }

    private Task RunProfileAdvisorAsync() => RunLocalAdvisorAsync();

    private void StopProfileAdvisor()
    {
        if (!_advisorRunning)
            return;

        try { _advisorCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private sealed record AdvisorEvaluation(int Healthy, int Total, long? LatencyMs, long Score, string Summary);

    private static AdvisorEvaluation EvaluateAdvisorCandidate(
        bool running,
        IReadOnlyCollection<ServiceHealthResult> results,
        int tuning,
        AdvisorHistoryEntry? history)
    {
        if (!running) return new(0, results.Count, null, -10000, "winws не запустился");
        var total = results.Count;
        if (total == 0) return new(0, 0, null, 100, "winws запущен");

        var score = 250L - tuning * 30L;
        var healthy = 0;
        var summaries = new List<string>();
        var latencies = new List<long>();

        foreach (var result in results)
        {
            var latency = result.LatencyMs ?? 900;
            if (result.LatencyMs.HasValue) latencies.Add(result.LatencyMs.Value);
            var serviceScore = result.IsHealthy ? 900L : -700L;
            serviceScore -= Math.Min(latency, 1500) / 3;

            var detail = $"{result.Detail} {result.Stages?.Http} {result.Stages?.Cdn} {result.Stages?.Gateway}";
            if (detail.Contains("slow", StringComparison.OrdinalIgnoreCase) || detail.Contains("медленно", StringComparison.OrdinalIgnoreCase))
                serviceScore -= 550;
            if (detail.Contains("timeout", StringComparison.OrdinalIgnoreCase) || detail.Contains("тайм-аут", StringComparison.OrdinalIgnoreCase))
                serviceScore -= 900;
            if (detail.Contains("thumbnails OK", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("media OK", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("stickers OK", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("voice доступны", StringComparison.OrdinalIgnoreCase))
                serviceScore += 250;

            if (result.IsHealthy) healthy++;
            summaries.Add($"{ServiceDisplayName(result.Id)}: {CompactHealthSummary(result)}");
            score += serviceScore;
        }

        if (history is not null)
            score += Math.Clamp(history.Score / 6, -450L, 450L);

        var latencyValue = latencies.Count == 0 ? null : (long?)Math.Round(latencies.Average());
        if (latencyValue is > 450) score -= 450;
        if (healthy == total) score += 500;
        return new(healthy, total, latencyValue, score, string.Join("; ", summaries));
    }

    private static string CompactHealthSummary(ServiceHealthResult result)
    {
        if (!result.IsHealthy) return result.Detail;
        var detail = result.Detail;
        return result.Id switch
        {
            "youtube" => detail.Contains("thumbnails", StringComparison.OrdinalIgnoreCase) ? detail : $"{detail}; thumbnails проверены",
            "telegram" => detail.Contains("media", StringComparison.OrdinalIgnoreCase) ? detail : $"{detail}; media проверены",
            "discord" => detail,
            _ => detail
        };
    }

    private IEnumerable<string> AdvisorServiceIds()
    {
        if (_settings.Profiles.YouTube || _settings.Profiles.Web) yield return "youtube";
        if (_settings.Profiles.Discord || _settings.Profiles.Web) yield return "discord";
        if (_settings.Profiles.Telegram || _settings.Profiles.TelegramWeb) yield return "telegram";
        if (_settings.Profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("roblox", StringComparison.OrdinalIgnoreCase))) yield return "roblox";
        if (_settings.Profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase))) yield return "phasmophobia";
    }


    private IEnumerable<AdvisorStrategy> OrderAdvisorStrategies(ProviderInfo? provider, IEnumerable<AdvisorStrategy> strategies)
    {
        if (provider is null) return strategies;
        var order = new[] { "medium", "moderate", "complex", "aggressive", "light" };
        var history = _settings.AdvisorHistory
            .Where(item => item.ProviderKey.Equals(provider.Key, StringComparison.OrdinalIgnoreCase))
            .GroupBy(
                item => $"{WinwsProfileComposer.NormalizeLevel(item.Level)}:{WinwsProfileComposer.NormalizeTuning(item.Tuning)}",
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.LastTestUtc).First(),
                StringComparer.OrdinalIgnoreCase);
        return strategies
            .OrderByDescending(strategy => history.TryGetValue($"{strategy.Level}:{strategy.Tuning}", out var entry) ? entry.Score : long.MinValue)
            .ThenBy(strategy => strategy.Tuning)
            .ThenBy(strategy => Array.IndexOf(order, strategy.Level));
    }

    private AdvisorHistoryEntry? FindAdvisorHistory(ProviderInfo? provider, string level, int tuning)
    {
        if (provider is null) return null;
        var normalizedLevel = WinwsProfileComposer.NormalizeLevel(level);
        var normalizedTuning = WinwsProfileComposer.NormalizeTuning(tuning);
        return _settings.AdvisorHistory.FirstOrDefault(item =>
            item.ProviderKey.Equals(provider.Key, StringComparison.OrdinalIgnoreCase) &&
            WinwsProfileComposer.NormalizeLevel(item.Level).Equals(normalizedLevel, StringComparison.OrdinalIgnoreCase) &&
            WinwsProfileComposer.NormalizeTuning(item.Tuning) == normalizedTuning);
    }

    private void UpdateAdvisorHistory(ProviderInfo? provider, IEnumerable<AdvisorCandidate> candidates)
    {
        if (provider is null) return;
        _settings.AdvisorHistory.RemoveAll(item => item.ProviderKey.Equals(provider.Key, StringComparison.OrdinalIgnoreCase));
        _settings.AdvisorHistory.AddRange(candidates.Select(candidate => new AdvisorHistoryEntry
        {
            ProviderKey = provider.Key,
            ProviderName = provider.DisplayName,
            Level = candidate.Level,
            Tuning = candidate.Tuning,
            Score = candidate.Score,
            Healthy = candidate.Healthy,
            Total = candidate.Total,
            LatencyMs = candidate.LatencyMs,
            LastTestUtc = DateTimeOffset.UtcNow
        }));
        _settings.AdvisorHistory = _settings.AdvisorHistory
            .OrderByDescending(item => item.LastTestUtc)
            .Take(120)
            .ToList();
    }

    private static string? NormalizeProviderKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Trim().ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private async Task StopProtectionAsync(CancellationToken cancellationToken=default)
    {
        CancelPendingProfileWork();
        await _protectionGate.WaitAsync(cancellationToken);
        try
        {
            _protectionTransitioning = true;
            _protectionRequested = false;
            await SendServiceSnapshotAsync();
            if (!_advisorRunning || _advisorStopProtectionRequested)
                await DisconnectWarpWithProtectionAsync(cancellationToken);
            await _dnsGuard.StopAsync(cancellationToken);
            await _components.StopAllAsync(cleanupDriver: !_warpBootstrap.IsActive);
            await _telegramProxy.StopAsync();
            await RestoreHostsCoreAsync();
            RestoreProxyUnlessWarpPacActive();
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
        await RestoreConnectionWarpAsync(cancellationToken);
    }

    private async Task SetProfileAsync(string id, bool enabled)
    {
        switch (id.ToLowerInvariant())
        {
            case "youtube": _settings.Profiles.YouTube = enabled; break;
            case "discord": _settings.Profiles.Discord = enabled; break;
            case "telegram": _settings.Profiles.Telegram = enabled; break;
            case "telegramweb": _settings.Profiles.TelegramWeb = enabled; break;
            case "web": _settings.Profiles.Web = enabled; break;
            case "gamefilter": _settings.Profiles.GameFilter = enabled; break;
            default: throw new ArgumentException($"Неизвестный профиль: {id}");
        }
        await _settingsStore.SaveAsync(_settings);
        await LogToUiAsync("info", $"Профиль {id} {(enabled ? "выбран" : "выключен")}. Нажмите главную кнопку, чтобы применить изменения.");
        await SendServiceSnapshotAsync();
    }

    private async Task SetBypassLevelAsync(string? level, int tuning = 0)
    {
        _settings.BypassLevel = WinwsProfileComposer.NormalizeLevel(level);
        _settings.BypassTuning = WinwsProfileComposer.NormalizeTuning(tuning);
        _settings.LocalAdvisor.AppliedStrategy = null;
        await _settingsStore.SaveAsync(_settings);
        await LogToUiAsync("info", $"Выбран профиль: {ProfileDisplayName(_settings.BypassLevel, _settings.BypassTuning, _settings.CompatibilityMode)}.");
        await PublishProfileStatusAsync();
    }

    private async Task SetFavoriteProfileAsync(string? level, int tuning, string? mode, bool favorite)
    {
        var normalizedLevel = WinwsProfileComposer.NormalizeLevel(level);
        var normalizedTuning = WinwsProfileComposer.NormalizeTuning(tuning);
        var normalizedMode = WinwsProfileComposer.NormalizeCompatibilityMode(mode ?? _settings.CompatibilityMode);
        var existing = _settings.FavoriteProfiles.FirstOrDefault(profile =>
            profile.Level.Equals(normalizedLevel, StringComparison.OrdinalIgnoreCase) &&
            profile.Tuning == normalizedTuning &&
            WinwsProfileComposer.NormalizeCompatibilityMode(profile.Mode) == normalizedMode);

        if (favorite && existing is null)
        {
            if (_settings.FavoriteProfiles.Count >= 5)
            {
                await LogToUiAsync("warn", "В избранное можно добавить не больше пяти профилей.");
                await PublishProfileStatusAsync();
                return;
            }

            _settings.FavoriteProfiles.Add(new FavoriteProfileSettings { Level = normalizedLevel, Tuning = normalizedTuning, Mode = normalizedMode });
            await LogToUiAsync("ok", $"Профиль «{ProfileDisplayName(normalizedLevel, normalizedTuning, normalizedMode)}» добавлен на дашборд.");
        }
        else if (!favorite && existing is not null)
        {
            _settings.FavoriteProfiles.Remove(existing);
            await LogToUiAsync("info", $"Профиль «{ProfileDisplayName(normalizedLevel, normalizedTuning, normalizedMode)}» удалён с дашборда.");
        }
        else
        {
            await PublishProfileStatusAsync();
            return;
        }

        _settings.FavoriteProfiles = NormalizeFavorites(_settings.FavoriteProfiles);
        await _settingsStore.SaveAsync(_settings);
        await PublishProfileStatusAsync();
    }

    private async Task LaunchProfileAsync(string? level, int tuning, string? mode = null)
    {
        if (_advisorRunning || _protectionTransitioning)
        {
            await LogToUiAsync("warn", "Дождитесь завершения текущей операции с защитой.");
            return;
        }

        await SetProfileModeOnlyAsync(mode);
        await SetBypassLevelAsync(level, tuning);
        await LogToUiAsync("info", $"Быстрый запуск профиля «{ProfileDisplayName(_settings.BypassLevel, _settings.BypassTuning, _settings.CompatibilityMode)}».");
        if (_protectionRequested) await ApplyProfilesAsync();
        else await SetProtectionAsync(true);
    }

    private async Task SetProfileModeOnlyAsync(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        var normalized = "standard";
        _settings.CompatibilityMode = normalized;
        var entry = EnsureCurrentNetworkCompatibility();
        entry.Mode = normalized;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        await _settingsStore.SaveAsync(_settings);
    }

    private bool IsMobileCompatibilityMode() =>
        WinwsProfileComposer.NormalizeCompatibilityMode(_settings.CompatibilityMode) == "mobile";

    private NetworkCompatibilitySettings? CurrentNetworkCompatibility() =>
        _settings.NetworkCompatibilityProfiles.FirstOrDefault(profile =>
            profile.NetworkKey.Equals(CurrentNetworkKey(), StringComparison.Ordinal));

    private NetworkCompatibilitySettings EnsureCurrentNetworkCompatibility()
    {
        var current = CurrentNetworkCompatibility();
        if (current is not null) return current;
        current = new NetworkCompatibilitySettings
        {
            NetworkKey = CurrentNetworkKey(),
            Mode = _settings.CompatibilityMode,
            DiscordVoiceResolvedMode = "minimal",
            TelegramStrategy = "strong"
        };
        _settings.NetworkCompatibilityProfiles.Add(current);
        return current;
    }

    private static MobileServiceRouteState EnsureServiceRoute(NetworkCompatibilitySettings entry, string serviceId)
    {
        entry.ServiceRoutes ??= new(StringComparer.OrdinalIgnoreCase);
        if (entry.ServiceRoutes.TryGetValue(serviceId, out var existing)) return existing;
        var route = new MobileServiceRouteState { ServiceId = serviceId };
        entry.ServiceRoutes[serviceId] = route;
        return route;
    }

    private DnsGuardStatus CurrentDnsStatus => DnsModePolicy.UsesGuard(_settings.DnsMode)
        ? _dnsGuard.Status : new("system", "Используется исходный системный DNS", "Системный");

    private async Task RestartSelectedDnsAsync()
    {
        _currentNetworkKey = CurrentNetworkKey();
        _draftRecommendations.Clear();
        _settings.LocalAdvisor.AppliedStrategy = null;
        foreach (var profile in _settings.SavedConnections.Where(p => p.Strategy is not null)) profile.RouteNeedsCheck = true;
        if (_activeConnection is not null) _activeConnection.RouteNeedsCheck = true;
        await _settingsStore.SaveAsync(_settings);
        await _dnsGuard.StopAsync(CancellationToken.None);
        if (!MobileDnsPolicy.WarpOwnsSystemDns(_warpStatus.State == WarpConnectionState.Connected, GetEffectiveWarpRoutingMode()))
            await StartProtectedDnsGuardAsync(_lifetime.Token);
        await PublishConnectionsAsync();
        await PublishLocalAdvisorStatusAsync();
    }

    private async Task StartProtectedDnsGuardAsync(CancellationToken cancellationToken)
    {
        if (!DnsModePolicy.UsesGuard(_settings.DnsMode))
        {
            await _dnsGuard.StopAsync(cancellationToken);
            await HandleDnsGuardStatusAsync(new("system", "Используется исходный системный DNS; VortexPass не меняет DNS адаптера"));
            return;
        }
        if (!_protectionRequested && !_advisorRunning) return;
        var services = new List<string>();
        if (_settings.Profiles.YouTube) services.Add("youtube");
        if (_settings.Profiles.Discord) services.Add("discord");
        if (_settings.Profiles.TelegramWeb) services.Add("telegram");
        var hosts = RouteProbePolicy.DnsHostsForServices(services).Concat(_settings.CustomBypassDomains).ToArray();
        _dnsGuard.DetailedDiagnostics=_settings.NetworkDiagnosticDetails;
        var status = await _dnsGuard.StartAsync(cancellationToken, _settings.DnsMode, hosts, _settings.DnsMode=="google-udp"?_settings.GoogleDnsAddress:_settings.SelectedDnsProvider);
        await HandleDnsGuardStatusAsync(status);
        await LogToUiAsync(status.Ready ? "ok" : "warn", $"Защищённый DNS: {status.Detail}{(string.IsNullOrWhiteSpace(status.Resolver) ? string.Empty : $" · {status.Resolver}")}");
        if (status.Ready && _webReady && _lastUpdate?.State == UpdateState.Unavailable)
            _ = CheckForUpdatesAsync();
    }

    private async Task HandleDnsGuardStatusAsync(DnsGuardStatus status)
    {
        if (_settingsLoaded && _settings.NetworkCompatibilityProfiles is not null)
        {
            var current = EnsureCurrentNetworkCompatibility();
            current.MobileDns ??= new MobileDnsState();
            current.MobileDns.NetworkKey = current.NetworkKey;
            current.MobileDns.State = status.State;
            current.MobileDns.Detail = status.Detail;
            current.MobileDns.Resolver = status.Resolver ?? string.Empty;
            current.MobileDns.LastCheckedUtc = status.CheckedAt ?? DateTimeOffset.UtcNow;
            try { await _settingsStore.SaveAsync(_settings, CancellationToken.None); } catch { }
        }
        await PostUiAsync(new
        {
            type = "compatibility.dns.status",
            dnsState = status.State,
            dnsDetail = status.Detail,
            dnsResolver = status.Resolver,
            dnsAdapter = status.Adapter
        });
    }

    private object CurrentCompatibilityStatus()
    {
        var current = CurrentNetworkCompatibility();
        return new
        {
            mode = WinwsProfileComposer.NormalizeCompatibilityMode(_settings.CompatibilityMode),
            networkKey = CurrentNetworkKey(),
            remembered = current is not null,
            youtubeTransport = WinwsProfileComposer.NormalizeYoutubeTransport(current?.YouTubeTransport),
            discordVoiceMode = WinwsProfileComposer.NormalizeDiscordVoiceMode(current?.DiscordVoiceMode),
            discordVoiceApplied = CurrentDiscordVoiceMode(),
            providerName = current?.ProviderName,
            telegramRouteCount = _mobileTelegramRouteCount,
            winwsDomainCount = CountActiveWinwsDomains(),
            pinnedIpCount = _hostsStatus.Applied ? _hostsStatus.EntryCount : 0,
            pinnedIpDetail = _hostsStatus.Applied ? $"Закреплённые IP: {_hostsStatus.EntryCount}" : "Закреплённые IP: нет — безопасный DNS-маршрут",
            routes = current?.ServiceRoutes?.ToDictionary(item => item.Key, item => new
            {
                strategy = item.Value.Strategy,
                route = item.Value.AppliedRoute,
                state = item.Value.State,
                detail = item.Value.Detail,
                checkedAt = item.Value.LastCheckedUtc
            }),
            optimizeState = current?.OptimizationState ?? (IsMobileCompatibilityMode() ? "checking" : "idle"),
            optimizeDetail = current?.OptimizationDetail ?? string.Empty,
            dnsState = CurrentDnsStatus.State,
            dnsDetail = CurrentDnsStatus.Detail,
            dnsResolver = CurrentDnsStatus.Resolver,
            dnsAdapter = CurrentDnsStatus.Adapter
        };
    }

    private int CountActiveWinwsDomains()
    {
        static int Count(string path)
        {
            try { return File.ReadLines(path).Count(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#')); }
            catch { return 0; }
        }
        var files = Path.Combine(AppContext.BaseDirectory, "bin", "files");
        var count = 0;
        if (_activeProfiles.YouTube || _activeProfiles.Web) count += Count(Path.Combine(files, "list-youtube.txt"));
        if (_activeProfiles.Discord || _activeProfiles.Web) count += Count(Path.Combine(files, "list-discord.txt"));
        if (_activeProfiles.TelegramWeb || _activeProfiles.Telegram) count += Count(Path.Combine(files, "list-telegram.txt"));
        return count;
    }

    private string CurrentDiscordVoiceMode()
    {
        if (CurrentAdvisorStrategy() is { DiscordVoice: not "inherit" } strategy) return strategy.DiscordVoice;
        if (!IsMobileCompatibilityMode()) return "minimal";
        var current = CurrentNetworkCompatibility();
        var requested = WinwsProfileComposer.NormalizeDiscordVoiceMode(current?.DiscordVoiceMode);
        return requested == "auto"
            ? "minimal"
            : requested;
    }

    private async Task SetDiscordVoiceModeAsync(string? mode)
    {
        var current = CurrentNetworkCompatibility();
        if (current is null)
        {
            current = new NetworkCompatibilitySettings { NetworkKey = CurrentNetworkKey(), Mode = _settings.CompatibilityMode };
            _settings.NetworkCompatibilityProfiles.Add(current);
        }
        current.DiscordVoiceMode = WinwsProfileComposer.NormalizeDiscordVoiceMode(mode);
        current.DiscordVoiceResolvedMode = current.DiscordVoiceMode == "minimal" ? "minimal" : "direct";
        current.UpdatedAt = DateTimeOffset.UtcNow;
        await _settingsStore.SaveAsync(_settings);
        await LogToUiAsync("info", $"Discord voice для мобильной сети: {DiscordVoiceModeDisplay(CurrentDiscordVoiceMode())}.");
        await PublishProfileStatusAsync();
        if (_protectionRequested && IsMobileCompatibilityMode()) await ApplyProfilesAsync();
    }

    private static string DiscordVoiceModeDisplay(string mode) => mode switch
    {
        "minimal" => "минимальный desync",
        "off" => "выключен",
        _ => "прямой UDP"
    };

    private async Task SetCompatibilityModeAsync(string? mode, bool launch)
    {
        if (_advisorRunning || _protectionTransitioning)
        {
            await LogToUiAsync("warn", "Дождитесь завершения текущей операции с защитой.");
            return;
        }
        var normalized = "standard";
        var previous = WinwsProfileComposer.NormalizeCompatibilityMode(_settings.CompatibilityMode);
        var wasRunning = _components.GetStatuses().Any(status => status.Id == "dpi" && status.State == ServiceState.Running);
        var networkKey = CurrentNetworkKey();
        var entry = CurrentNetworkCompatibility();
        if (entry is null)
        {
            entry = new NetworkCompatibilitySettings
            {
                NetworkKey = networkKey,
                DiscordVoiceResolvedMode = "minimal",
                TelegramStrategy = "strong"
            };
            _settings.NetworkCompatibilityProfiles.Add(entry);
        }
        entry.Mode = normalized;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        _settings.CompatibilityMode = normalized;
        DnsOverrideService.InvalidateCache();
        ResetHealthChecksAfterRouteChange();
        await _settingsStore.SaveAsync(_settings);
        await LogToUiAsync("info", normalized == "mobile"
            ? "Для текущей сети выбран профиль «Мобильная совместимость». Настройка запомнена только для этой сети."
            : "Для текущей сети выбран обычный профиль. Мобильные ограничения отключены.");
        await PublishProfileStatusAsync();

        if (!launch && !wasRunning) return;
        if (!_settings.Profiles.AnyEnabled)
        {
            await LogToUiAsync("warn", "Профиль сети сохранён. Выберите хотя бы один сервис перед запуском защиты.");
            return;
        }

        await ApplyProfilesAsync();
        var running = _components.GetStatuses().Any(status => status.Id == "dpi" && status.State == ServiceState.Running);
        if (running || !wasRunning) return;

        // A mode switch must not strand a previously working connection. Restore
        // exactly the prior network mode and re-apply the old protection.
        _settings.CompatibilityMode = previous;
        entry.Mode = previous;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        await LogToUiAsync("error", "Мобильный профиль не запустился; восстановлен предыдущий рабочий профиль сети.");
        await ApplyProfilesAsync();
        await PublishProfileStatusAsync();
    }

    private async Task RunMobileCompatibilityAdvisorAsync()
    {
        _advisorRunning = true;
        _advisorCts?.Dispose();
        _advisorCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _advisorCts.Token;
        try
        {
            var entry = EnsureCurrentNetworkCompatibility();
            entry.DiscordVoiceMode = "auto";
            entry.DiscordVoiceResolvedMode = "minimal";
            var ids = AdvisorServiceIds()
                .Where(id => id is "youtube" or "discord" or "telegram")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var recentConfirmed = entry.LastSuccessfulOptimizationUtc > DateTimeOffset.UtcNow.AddHours(-6) &&
                ids.All(id => entry.ServiceRoutes.TryGetValue(id, out var route) &&
                    MobileRoutePolicy.HasFreshSuccessfulStrategy(route, DateTimeOffset.UtcNow, TimeSpan.FromHours(6)));
            if (recentConfirmed)
            {
                foreach (var id in ids)
                {
                    var route = EnsureServiceRoute(entry, id);
                    route.Strategy = MobileRoutePolicy.PreferredStrategy(route);
                }
                entry.TelegramStrategy = EnsureServiceRoute(entry, "telegram").Strategy == "fallback" ? "balanced" : "strong";
                entry.OptimizationState = "cached";
                entry.OptimizationDetail = "Сразу применён последний подтверждённый набор этой сети; повторная проверка идёт в фоне без перезапуска";
                await PublishCompatibilityRouteStatusAsync(entry);
                await LogToUiAsync("ok", "Мобильный автоподбор: использован сохранённый рабочий набор без повторного запуска winws.");
                return;
            }
            foreach (var id in ids)
            {
                var route = EnsureServiceRoute(entry, id);
                route.Strategy = MobileRoutePolicy.PreferredStrategy(route);
                route.State = "checking";
                route.Detail = "Быстрая проверка сохранённого маршрута";
            }
            entry.TelegramStrategy = EnsureServiceRoute(entry, "telegram").Strategy == "fallback" ? "balanced" : "strong";
            await PublishCompatibilityRouteStatusAsync(entry);

            if (!_components.GetStatuses().Any(status => status.Id == "dpi" && status.State == ServiceState.Running))
                await ApplyProfilesAsync();
            await Task.Delay(300, token);
            var primary = await CheckMobileServicesAsync(ids, token);
            await RememberMobileTransportChoiceAsync(primary);
            UpdateMobileRouteStates(entry, primary);

            var failed = primary.Where(result => !MobileRoutePolicy.AcceptsDirectRoute(result))
                .Select(result => result.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (failed.Count > 0)
            {
                // One synthetic timeout must not tear down working browser
                // sessions. Confirm it against the same running command before
                // using the single permitted restart.
                await Task.Delay(600, token);
                var confirmation = await CheckMobileServicesAsync(failed, token);
                await RememberMobileTransportChoiceAsync(confirmation);
                UpdateMobileRouteStates(entry, confirmation);
                failed = confirmation
                    .Where(result => !MobileRoutePolicy.AcceptsDirectRoute(result))
                    .Select(result => result.Id)
                    .Where(id => MobileRoutePolicy.ShouldTryAlternate(EnsureServiceRoute(entry, id).ConsecutiveFailures))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            if (failed.Count > 0)
            {
                foreach (var id in failed)
                {
                    var route = EnsureServiceRoute(entry, id);
                    // The first probe already used the saved strategy. Test the
                    // other bounded strategy instead of restarting the exact
                    // same command (the old behavior for cached fallback).
                    route.Strategy = MobileRoutePolicy.AlternateStrategy(route.Strategy);
                    route.State = "checking";
                    route.Detail = $"Проверяется второй узкий вариант: {route.Strategy}";
                }
                entry.TelegramStrategy = EnsureServiceRoute(entry, "telegram").Strategy == "fallback" ? "balanced" : "strong";
                entry.OptimizationState = "fallback";
                entry.OptimizationDetail = $"Один резервный запуск: {string.Join(", ", failed)}";
                entry.UpdatedAt = DateTimeOffset.UtcNow;
                await _settingsStore.SaveAsync(_settings, CancellationToken.None);
                await PublishCompatibilityRouteStatusAsync(entry);

                // Exactly one atomic restart: working services retain their own
                // cached strategy while failed services switch to fallback.
                await ApplyProfilesAsync();
                await Task.Delay(300, token);
                var fallbackResults = await CheckMobileServicesAsync(failed, token);
                await RememberMobileTransportChoiceAsync(fallbackResults);
                UpdateMobileRouteStates(entry, fallbackResults);
                failed = fallbackResults.Where(result => !MobileRoutePolicy.AcceptsDirectRoute(result))
                    .Select(result => result.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            if (failed.Count > 0)
                await ApplyMobileWarpFallbackAsync(entry, failed, token);

            var confirmed = ids.Count(id => EnsureServiceRoute(entry, id).State == "confirmed");
            entry.OptimizationState = confirmed == ids.Length ? "confirmed" : "partial";
            entry.OptimizationDetail = confirmed == ids.Length
                ? "Маршруты сервисов подтверждены"
                : $"Подтверждено {confirmed}/{ids.Length}; подробности показаны отдельно";
            if (confirmed > 0) entry.LastSuccessfulOptimizationUtc = DateTimeOffset.UtcNow;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            await PublishCompatibilityRouteStatusAsync(entry);
            await PostUiAsync(new
            {
                type = "advisor.status",
                state = "complete",
                progress = 1,
                total = 2,
                provider = entry.ProviderName,
                best = "Раздельные мобильные маршруты",
                message = entry.OptimizationDetail,
                candidates = Array.Empty<object>()
            });
            await LogToUiAsync(confirmed == ids.Length ? "ok" : "warn", $"Мобильный автоподбор: {entry.OptimizationDetail}.");
        }
        catch (OperationCanceledException)
        {
            await PostUiAsync(new { type = "advisor.status", state = "idle", message = "Мобильная проверка остановлена." });
        }
        finally
        {
            _advisorRunning = false;
            _advisorCts?.Dispose();
            _advisorCts = null;
            await SendServiceSnapshotAsync();
        }
    }

    private async Task DisconnectWarpWithProtectionAsync(CancellationToken cancellationToken)
    {
        // The main protection switch is the master switch. Remove PAC first so
        // browsers immediately return to their ordinary route, then disconnect
        // the official client and its bootstrap without rebuilding winws.
        await _warpPac.StopAsync();
        _proxyManager.RestoreIfOwned();
        await StopWarpRoutingAsync(cancellationToken);
        foreach (var profile in _settings.NetworkCompatibilityProfiles)
            profile.MobileWarpFallbackOwned = false;
        _mobileWarpFallbackServices.Clear();
        _mobileWarpFallbackActive = false;
        _mobileWarpWasConnected = false;

        if (_warpStatus.State is WarpConnectionState.Connected or WarpConnectionState.Connecting ||
            _settings.Warp.RequestedConnected)
        {
            _warpStatus = _warpStatus with
            {
                State = WarpConnectionState.Connecting,
                Detail = "Защита выключается: отключение WARP",
                Progress = null
            };
            await PublishWarpStatusAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                _warpStatus = await _warpService.SetConnectedAsync(false, timeout.Token);
                if (_warpStatus.State != WarpConnectionState.Disconnected)
                {
                    var recovery = await _warpService.RecoverOrdinaryRouteAsync(cancellationToken);
                    _warpStatus = recovery.Status;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var recovery = await _warpService.RecoverOrdinaryRouteAsync(cancellationToken);
                _warpStatus = recovery.Status;
            }
        }

        _settings.Warp.RequestedConnected = false;
        _warpDiagnostics = null;
        _warpTelegramCompatibilityActive = false;
        await _warpBootstrap.StopAsync();
        try { await _settingsStore.SaveAsync(_settings, CancellationToken.None); } catch { }
        await PublishWarpStatusAsync();
        await LogToUiAsync("ok", "WARP выключен вместе с защитой.");
    }

    private async Task<IReadOnlyList<ServiceHealthResult>> CheckMobileServicesAsync(IEnumerable<string> serviceIds, CancellationToken cancellationToken)
    {
        var ids = serviceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(11));
        try
        {
            return await Task.WhenAll(ids.Select(id => _healthChecks.CheckAsync(
                id, true, true, timeout.Token,
                appSelected: id == "telegram" && _settings.Profiles.Telegram,
                webSelected: id == "telegram" && _settings.Profiles.TelegramWeb,
                telegramProxyRunning: id == "telegram" && _telegramProxy.IsRunning,
                telegramProxyHasClientActivity: id == "telegram" && _telegramProxy.HasTelegramClientActivity,
                discordVoiceMode: id == "discord" ? CurrentDiscordVoiceMode() : "off")));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ids.Select(id => new ServiceHealthResult(id, ServiceHealthState.HttpUnavailable, "Проверка маршрута не завершилась за 11 секунд")).ToArray();
        }
    }

    private static void UpdateMobileRouteStates(NetworkCompatibilitySettings entry, IEnumerable<ServiceHealthResult> results)
    {
        foreach (var result in results)
        {
            var route = EnsureServiceRoute(entry, result.Id);
            route.LastCheckedUtc = DateTimeOffset.UtcNow;
            route.Detail = result.Detail;
            if (MobileRoutePolicy.AcceptsDirectRoute(result))
            {
                route.AppliedRoute = result.Id == "youtube" && entry.YouTubeTransport == "http2" ? "http2+winws" : "winws";
                route.State = "confirmed";
                route.ConsecutiveFailures = 0;
                route.LastConfirmedUtc = DateTimeOffset.UtcNow;
                route.LastSuccessfulStrategy = WinwsProfileComposer.NormalizeMobileServiceStrategy(route.Strategy);
            }
            else
            {
                route.State = "failed";
                route.ConsecutiveFailures++;
            }
        }
    }

    private async Task ApplyMobileWarpFallbackAsync(NetworkCompatibilitySettings entry, IReadOnlySet<string> failedServices, CancellationToken cancellationToken)
    {
        var webServices = failedServices.Where(id => id=="discord"||id=="telegram"&&_settings.TelegramWebRoute!="warpProxy").ToArray();
        foreach (var id in failedServices.Except(webServices, StringComparer.OrdinalIgnoreCase))
        {
            var route = EnsureServiceRoute(entry, id);
            route.State = "manual";
            route.Detail = "Два прямых маршрута не подтвердили загрузку контента; WARP-прокси для YouTube автоматически не включается";
        }
        if (webServices.Length == 0) return;

        _warpStatus = await _warpService.GetStatusAsync(cancellationToken);
        if (_warpStatus.State == WarpConnectionState.NotInstalled || string.IsNullOrWhiteSpace(_warpStatus.CliPath))
        {
            foreach (var id in webServices)
            {
                var route = EnsureServiceRoute(entry, id);
                route.State = "warpUnavailable";
                route.Detail = "Официальный WARP не установлен — доступна кнопка установки во вкладке WARP";
            }
            await LogToUiAsync("warn", "Мобильный резерв: официальный WARP не установлен. Установка без подтверждения не выполняется.");
            return;
        }
        if (!_warpStatus.Registered)
        {
            foreach (var id in webServices)
            {
                var route = EnsureServiceRoute(entry, id);
                route.State = "warpUnavailable";
                route.Detail = "Нужно один раз завершить бесплатную регистрацию Consumer WARP";
            }
            await LogToUiAsync("warn", "Мобильный резерв найден, но Consumer WARP ещё не зарегистрирован.");
            return;
        }

        _mobileWarpWasConnected = _warpStatus.State == WarpConnectionState.Connected;
        entry.MobileWarpWasConnected = _mobileWarpWasConnected;
        var useSystemWarp = webServices.Contains("discord", StringComparer.OrdinalIgnoreCase);
        var warpMode = useSystemWarp ? "full" : "proxy";
        try
        {
            if (!_mobileWarpWasConnected)
            {
                await _dnsGuard.StopAsync(CancellationToken.None);
                await SetWarpBootstrapAsync(true, cancellationToken, telegramCompatibility: useSystemWarp);
                var protocol = WarpTransportPolicy.Resolve(_settings.Warp).Protocol;
                _warpStatus = await _warpService.ApplyConfigurationAsync(new WarpClientConfiguration(
                    warpMode, protocol, WarpTransportPolicy.Resolve(_settings.Warp).Masque, _settings.Warp.DnsMode, _settings.Warp.ProxyPort),
                    reconnect: false, cancellationToken);
                _warpStatus = await _warpService.SetConnectedAsync(true, cancellationToken);
            }
            else
            {
                var protocol = WarpTransportPolicy.Resolve(_settings.Warp).Protocol;
                _warpStatus = await _warpService.ApplyConfigurationAsync(new WarpClientConfiguration(
                    warpMode, protocol, WarpTransportPolicy.Resolve(_settings.Warp).Masque, _settings.Warp.DnsMode, _settings.Warp.ProxyPort),
                    reconnect: true, cancellationToken);
            }
            if (_warpStatus.State != WarpConnectionState.Connected)
                throw new InvalidOperationException(_warpStatus.Error ?? "WARP не подключился");

            if (useSystemWarp)
            {
                await _warpPac.StopAsync();
                _proxyManager.RestoreIfOwned();
                var owned = await _warpService.SyncSplitTunnelExclusionsAsync(
                    MobileRoutePolicy.SystemWarpDirectDomains,
                    [],
                    _settings.Warp.OwnedSplitTunnelHosts,
                    _settings.Warp.OwnedSplitTunnelIps,
                    cancellationToken);
                _settings.Warp.OwnedSplitTunnelHosts = owned.Hosts.ToList();
                _settings.Warp.OwnedSplitTunnelIps = owned.Ips.ToList();
            }
            else
            {
                var domains = MobileRoutePolicy.DomainsForFallback(webServices);
                _warpPac.DiagnosticsEnabled=_settings.NetworkDiagnosticDetails;
            var pacUri = await _warpPac.StartAsync(domains, _settings.Warp.ProxyPort, _settings.Warp.ProxyPort + 1, cancellationToken);
                _proxyManager.ApplyPac(pacUri);
                if (!_proxyManager.IsOwnedPacApplied()) throw new InvalidOperationException("Windows не применил PAC VortexPass");
            }

            var detectedFirefoxCompatibility = FirefoxUsesSystemProxy(out var firefoxDetail);
            var firefoxCompatible = useSystemWarp || detectedFirefoxCompatibility;
            var confirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var proxyUri = useSystemWarp ? null : new Uri($"socks5://127.0.0.1:{_settings.Warp.ProxyPort}");
            foreach (var id in webServices)
            {
                var target = MobileRoutePolicy.ProbeUri(id);
                if (target is null) continue;
                var probe = await _warpService.ProbeWebRouteAsync(target, proxyUri, cancellationToken);
                var route = EnsureServiceRoute(entry, id);
                route.AppliedRoute = useSystemWarp ? "warpSystem" : "warpProxy";
                route.LastCheckedUtc = DateTimeOffset.UtcNow;
                if (probe.Available && firefoxCompatible)
                {
                    route.State = "confirmed";
                    route.Detail = $"{(useSystemWarp ? "Системный WARP" : "WARP-прокси")} подтверждён: {probe.Detail}";
                    route.LastConfirmedUtc = DateTimeOffset.UtcNow;
                    route.ConsecutiveFailures = 0;
                    confirmed.Add(id);
                }
                else
                {
                    route.State = firefoxCompatible ? "failed" : "manual";
                    route.Detail = firefoxCompatible
                        ? $"WARP-прокси не подтвердился: {probe.Detail}"
                        : $"WARP-прокси готов, но Firefox не использует системный PAC: {firefoxDetail}. Выберите «Системные настройки прокси» в Firefox.";
                }
            }

            if (confirmed.Count == 0 && firefoxCompatible)
                throw new InvalidOperationException("Ни один выборочный WARP-маршрут не подтвердился");

            _mobileWarpFallbackActive = true;
            _mobileWarpFallbackServices.Clear();
            foreach (var id in webServices) _mobileWarpFallbackServices.Add(id);
            entry.MobileWarpFallbackOwned = true;
            _settings.Warp.RequestedConnected = true;
            await LogToUiAsync(confirmed.Count > 0 ? "ok" : "warn",
                useSystemWarp
                    ? $"Мобильный WARP-fallback: Discord Desktop и Telegram направлены через системный WARP; YouTube и Roblox добавлены в прямые исключения."
                    : $"Мобильный WARP-fallback: через proxy направлен только {string.Join(", ", webServices)}; остальные сервисы остаются прямыми.");
            await PublishWarpStatusAsync();
        }
        catch (Exception ex)
        {
            foreach (var id in webServices)
            {
                var route = EnsureServiceRoute(entry, id);
                route.State = "failed";
                route.Detail = $"WARP-fallback не применён: {ex.Message}";
            }
            await StopMobileWarpFallbackAsync(CancellationToken.None);
            await LogToUiAsync("warn", $"Мобильный WARP-fallback: {ex.Message}");
        }
    }

    private async Task StopMobileWarpFallbackAsync(CancellationToken cancellationToken)
    {
        if (!_mobileWarpFallbackActive && !_settings.NetworkCompatibilityProfiles.Any(item => item.MobileWarpFallbackOwned)) return;
        await _warpPac.StopAsync();
        _proxyManager.RestoreIfOwned();
        if (_settings.Warp.OwnedSplitTunnelHosts.Count > 0 || _settings.Warp.OwnedSplitTunnelIps.Count > 0)
        {
            var owned = await _warpService.SyncSplitTunnelExclusionsAsync(
                [], [], _settings.Warp.OwnedSplitTunnelHosts, _settings.Warp.OwnedSplitTunnelIps, cancellationToken);
            _settings.Warp.OwnedSplitTunnelHosts = owned.Hosts.ToList();
            _settings.Warp.OwnedSplitTunnelIps = owned.Ips.ToList();
        }
        if (!_mobileWarpWasConnected && _warpStatus.State == WarpConnectionState.Connected)
        {
            _warpStatus = await _warpService.SetConnectedAsync(false, cancellationToken);
            _settings.Warp.RequestedConnected = false;
            await SetWarpBootstrapAsync(false, cancellationToken, telegramCompatibility: false);
        }
        else if (_mobileWarpWasConnected && _warpStatus.State == WarpConnectionState.Connected)
        {
            await ApplyWarpClientPreferencesAsync(reconnect: true, cancellationToken);
            await ApplyWarpRoutingAsync(cancellationToken);
        }
        foreach (var profile in _settings.NetworkCompatibilityProfiles) profile.MobileWarpFallbackOwned = false;
        _mobileWarpFallbackServices.Clear();
        _mobileWarpFallbackActive = false;
        _mobileWarpWasConnected = false;
        try { await _settingsStore.SaveAsync(_settings, CancellationToken.None); } catch { }
    }

    private static bool FirefoxUsesSystemProxy(out string detail)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(root)) { detail = "профиль Firefox не найден"; return true; }
        var prefs = Directory.EnumerateFiles(root, "prefs.js", SearchOption.AllDirectories).ToArray();
        if (prefs.Length == 0) { detail = "настройка Firefox не определена"; return true; }
        foreach (var file in prefs)
        {
            try
            {
                var line = File.ReadLines(file).LastOrDefault(value => value.Contains("network.proxy.type", StringComparison.Ordinal));
                if (line is null || line.Contains(", 5)", StringComparison.Ordinal))
                {
                    detail = "Firefox использует системный прокси";
                    return true;
                }
            }
            catch { }
        }
        detail = "в профиле Firefox выбран собственный режим прокси";
        return false;
    }

    private Task PublishCompatibilityRouteStatusAsync(NetworkCompatibilitySettings entry) => PostUiAsync(new
    {
        type = "compatibility.route.status",
        networkKey = entry.NetworkKey,
        routes = entry.ServiceRoutes.ToDictionary(item => item.Key, item => new
        {
            strategy = item.Value.Strategy,
            route = item.Value.AppliedRoute,
            state = item.Value.State,
            detail = item.Value.Detail,
            checkedAt = item.Value.LastCheckedUtc
        })
    });

    private async Task RememberMobileTransportChoiceAsync(IEnumerable<ServiceHealthResult> results)
    {
        if (!IsMobileCompatibilityMode()) return;
        var youtube = results.FirstOrDefault(result => result.Id.Equals("youtube", StringComparison.OrdinalIgnoreCase));
        var http = youtube?.Stages?.Http;
        if (string.IsNullOrWhiteSpace(http)) return;

        var entry = CurrentNetworkCompatibility();
        if (entry is null)
        {
            entry = new NetworkCompatibilitySettings
            {
                NetworkKey = CurrentNetworkKey(),
                Mode = "mobile"
            };
            _settings.NetworkCompatibilityProfiles.Add(entry);
        }
        var http3Succeeded = http.Contains("QUIC=[HTTP/3 ", StringComparison.OrdinalIgnoreCase);
        var http3Failed = http.Contains("HTTP/3 недоступен", StringComparison.OrdinalIgnoreCase);
        if (!http3Succeeded && !http3Failed) return;
        if (http3Succeeded)
        {
            entry.YouTubeHttp3Successes++;
            entry.YouTubeHttp3Failures = 0;
        }
        else
        {
            entry.YouTubeHttp3Failures++;
            entry.YouTubeHttp3Successes = 0;
        }
        var choice = entry.YouTubeHttp3Successes >= 2
            ? "quic"
            : entry.YouTubeHttp3Failures >= 2
                ? "http2"
                : null;
        if (choice is null)
        {
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            return;
        }
        if (entry.YouTubeTransport.Equals(choice, StringComparison.OrdinalIgnoreCase)) return;

        entry.YouTubeTransport = choice;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        await LogToUiAsync("info", choice == "quic"
            ? "Мобильная сеть: HTTP/3 подтверждён; при следующем запуске YouTube сохранит узкий QUIC-маршрут."
            : "Мобильная сеть: HTTP/3 не подтверждён; при следующем запуске YouTube будет использовать TCP/HTTP2 без перехвата QUIC.");
        await PublishProfileStatusAsync();
    }

    private Task PublishProfileStatusAsync() => PostUiAsync(new
    {
        type = "profile.status",
        level = _settings.BypassLevel,
        tuning = _settings.BypassTuning,
        compatibility = CurrentCompatibilityStatus(),
        favoriteProfiles = _settings.FavoriteProfiles.Select(profile => new
        {
            level = WinwsProfileComposer.NormalizeLevel(profile.Level),
            tuning = WinwsProfileComposer.NormalizeTuning(profile.Tuning),
            mode = WinwsProfileComposer.NormalizeCompatibilityMode(profile.Mode)
        })
    });

    private static List<FavoriteProfileSettings> NormalizeFavorites(IEnumerable<FavoriteProfileSettings> favorites)
    {
        return favorites
            .Where(profile => profile is not null)
            .Select(profile => new FavoriteProfileSettings
            {
                Level = WinwsProfileComposer.NormalizeLevel(profile.Level),
                Tuning = WinwsProfileComposer.NormalizeTuning(profile.Tuning),
                Mode = WinwsProfileComposer.NormalizeCompatibilityMode(profile.Mode)
            })
            .DistinctBy(profile => $"{profile.Mode}:{profile.Level}:{profile.Tuning}", StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private async Task SetGameFilterAsync(JsonElement root)
    {
        var games = root.TryGetProperty("selectedGames", out var rawGames) && rawGames.ValueKind == JsonValueKind.Array
            ? rawGames.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "")
                .Where(x => x.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase) ||
                            x.Equals("roblox", StringComparison.OrdinalIgnoreCase) ||
                            x.Equals("deadbydaylight", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var protocol = root.TryGetProperty("protocol", out var rawProtocol) ? rawProtocol.GetString() : null;
        _settings.GameFilter.SelectedGames = games;
        _settings.GameFilter.Protocol = WinwsProfileComposer.NormalizeGameProtocol(protocol);
        await _settingsStore.SaveAsync(_settings);
        var gameNames = games.Count == 0
            ? "игры не выбраны"
            : string.Join(", ", games.Select(x => x.Equals("roblox", StringComparison.OrdinalIgnoreCase)
                ? "Roblox"
                : x.Equals("deadbydaylight", StringComparison.OrdinalIgnoreCase) ? "Dead by Daylight" : "Phasmophobia"));
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

    private Task ApplyProfilesAsync()=>ApplyProfilesAsync(CancellationToken.None);
    private async Task ApplyProfilesAsync(CancellationToken cancellationToken)
    {
        if (_settings.Profiles.AnyEnabled)
        {
            try { _components.EnsureNoForeignProcesses(); }
            catch (InvalidOperationException ex)
            {
                _protectionRequested = _components.GetStatuses().Any(s=>s.State==ServiceState.Running);
                await LogToUiAsync("error", ex.Message);
                await PostUiAsync(new { type = "error", message = ex.Message });
                await SendServiceSnapshotAsync();
                return;
            }
        }
        if (!await _protectionGate.WaitAsync(0))
        {
            await SendServiceSnapshotAsync();
            return;
        }

        try
        {
            CancelMobileTelegramOptimization();
            try { _healthCheckCts.Cancel(); }
            catch (ObjectDisposedException) { }
            _healthCheckCts = new CancellationTokenSource();
            _profileApplyCts?.Dispose();
            _profileApplyCts = _advisorRunning && _advisorCts is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _advisorCts.Token,cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,cancellationToken);
            var operationToken = _profileApplyCts.Token;
            if(IsProxyOnlyProfile())
            {
                if(GetTelegramWebProxyUri() is null)throw new InvalidOperationException("Для Telegram Web сначала подключите выборочный WARP");
                await _dnsGuard.StopAsync(operationToken);
                await _components.StopAllAsync(cleanupDriver:!_warpBootstrap.IsActive);
                _protectionRequested=true;_protectionStarted=DateTimeOffset.UtcNow;_activeProfiles=CloneProfiles(_settings.Profiles);
                var selected=_settings.SavedConnections.FirstOrDefault(p=>p.Id==_settings.SelectedConnectionId);
                _activeConnection=selected is null?null:SavedConnectionProfilePolicy.Clone(selected);
                await RestoreHostsCoreAsync();await SendServiceSnapshotAsync();
                await LogToUiAsync("ok","Telegram Web подключён только через выборочный WARP; основной winws не запускается");
                return;
            }
            _protectionTransitioning = true;
            _protectionRequested = _settings.Profiles.AnyEnabled;
            await SendServiceSnapshotAsync();

            if (_settings.Profiles.AnyEnabled)
            {
                if (IsMobileCompatibilityMode() && !_advisorRunning)
                {
                    var mobile = EnsureCurrentNetworkCompatibility();
                    var restored = new List<string>();
                    foreach (var id in new[] { "youtube", "discord", "telegram" })
                    {
                        var route = EnsureServiceRoute(mobile, id);
                        if (string.IsNullOrWhiteSpace(route.LastSuccessfulStrategy)) continue;
                        route.Strategy = MobileRoutePolicy.PreferredStrategy(route);
                        restored.Add($"{id}={route.Strategy}");
                    }
                    mobile.TelegramStrategy = EnsureServiceRoute(mobile, "telegram").Strategy == "fallback" ? "balanced" : "strong";
                    if (restored.Count > 0)
                        await LogToUiAsync("ok", $"Сохранённая мобильная стратегия применена сразу: {string.Join(", ", restored)}.");
                }
                _serviceStatuses.Clear();
                Interlocked.Increment(ref _healthGeneration);
                var counters = ReadNetworkCounters();
                _baselineBytes = counters.Bytes;
                _baselinePackets = counters.Packets;
                _protectionStarted = DateTimeOffset.UtcNow;
                var runtimeLists = await _domainLists.WriteAsync(_settings.CustomBypassDomains, _settings.ExcludedDomains, operationToken);
                var arguments = WinwsProfileComposer.Compose(
                    ProfilesForDpi(),
                    _settings.BypassLevel,
                    _settings.GameFilter,
                    runtimeLists.BypassPath,
                    runtimeLists.ExclusionPath,
                    _settings.BypassTuning,
                    includeWarpBootstrap: false,
                    warpCompatibilityMode: false,
                    mobileCompatibilityMode: IsMobileCompatibilityMode(),
                    youtubeTransport: CurrentNetworkCompatibility()?.YouTubeTransport ?? "auto",
                    discordVoiceMode: CurrentDiscordVoiceMode(),
                    telegramStrategy: CurrentNetworkCompatibility()?.TelegramStrategy ?? "strong",
                    youtubeMobileStrategy: CurrentNetworkCompatibility()?.ServiceRoutes?.GetValueOrDefault("youtube")?.Strategy ?? "primary",
                    discordMobileStrategy: CurrentNetworkCompatibility()?.ServiceRoutes?.GetValueOrDefault("discord")?.Strategy ?? "primary",
                    advisorStrategy: CurrentAdvisorStrategy(),siteRules:_settings.CurrentSiteRules);
                await LogToUiAsync("info", $"Применение уровня {LevelDisplayName(_settings.BypassLevel)} · {TuningDisplayName(_settings.BypassTuning)}. Сервисы: {string.Join(", ", ActiveProfileNames())}.");
                if (IsMobileCompatibilityMode())
                    await LogToUiAsync("info", $"Мобильная совместимость активна: широкие игровые TCP/UDP-правила отключены; Discord voice — {DiscordVoiceModeDisplay(CurrentDiscordVoiceMode())}; YouTube — {WinwsProfileComposer.NormalizeYoutubeTransport(CurrentNetworkCompatibility()?.YouTubeTransport).ToUpperInvariant()}.");
                await LogToUiAsync("info", $"Пользовательские списки: обход — {runtimeLists.BypassCount}, исключения — {runtimeLists.ExclusionCount}.");
                await _dnsGuard.StopAsync(CancellationToken.None);
                await _components.StopAllAsync(cleanupDriver: !_warpBootstrap.IsActive);
                await _telegramProxy.StopAsync();
                _components.SetArguments("dpi", arguments);
                await PostUiAsync(new { type = "log", level = "info", message = "Запуск компонентов обхода…" });

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await _components.StartAllAsync(timeout.Token, reuseExistingOwnedDriver: _warpBootstrap.IsActive);
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
                    await _components.StopAllAsync(cleanupDriver: !_warpBootstrap.IsActive);
                    await RestoreHostsCoreAsync();
                    RestoreProxyUnlessWarpPacActive();
                    _activeProfiles = new BypassProfileSettings();
                    _protectionRequested = false;
                    _protectionStarted = null;
                    var reason = failed?.Detail ?? "winws не перешёл в состояние «Работает».";
                    await PostUiAsync(new { type = "log", level = "error", message = $"Обход не запущен: {reason}" });
                }
                else
                {
                    _activeProfiles = CloneProfiles(_settings.Profiles);
                    var selectedConnection = _settings.SavedConnections.FirstOrDefault(p => p.Id == _settings.SelectedConnectionId);
                    _activeConnection = selectedConnection is null ? null : SavedConnectionProfilePolicy.Clone(selectedConnection);
                    if (_activeConnection is not null) _activeConnection.Mode = _settings.CompatibilityMode;
                    // Publish the running state before DNS/hosts preparation. This keeps
                    // the Stop button available while optional network probes are pending.
                    await SendServiceSnapshotAsync();
                    if (MobileDnsPolicy.ShouldRun(
                            _protectionRequested,
                            _settings.CompatibilityMode,
                            MobileDnsPolicy.WarpOwnsSystemDns(
                                _warpStatus.State == WarpConnectionState.Connected,
                                GetEffectiveWarpRoutingMode())))
                        await StartProtectedDnsGuardAsync(operationToken);
                    else
                        await _dnsGuard.StopAsync(CancellationToken.None);
                    if (_activeProfiles.Telegram)
                    {
                        if (IsMobileCompatibilityMode())
                        {
                            _ = StartTelegramProxyGuardedAsync(operationToken);
                        }
                        else
                        {
                            await StartTelegramProxyGuardedAsync(operationToken);
                        }
                    }
                    else
                    {
                        await _telegramProxy.StopAsync();
                    }
                    await PostUiAsync(new { type = "log", level = "ok", message = $"Профили активны: {string.Join(", ", ActiveProfileNames())}." });
                    // winws and the optional Desktop proxy are already usable here.
                    // Do not make the UI look stuck on "Telegram WS Proxy" while
                    // temporary Telegram DNS entries are being prepared.
                    await SendServiceSnapshotAsync();
                    if (IsMobileCompatibilityMode() && _activeProfiles.TelegramWeb && !_warpTelegramCompatibilityActive)
                    {
                        await ApplyCachedMobileTelegramRoutesAsync(_activeProfiles, operationToken);
                        if (!_advisorRunning && CurrentAdvisorStrategy() is null)
                        {
                            StartMobileTelegramOptimization(_activeProfiles);
                            _ = RunMobileCompatibilityAdvisorAsync();
                        }
                    }
                    else
                    {
                        CancelMobileTelegramOptimization();
                        if (_advisorRunning) await RestoreHostsCoreAsync();
                        else await ApplyRuntimeHostsAsync(_activeProfiles, operationToken);
                    }
                    _nextHealthCheckUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                    if (_activeProfiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase)))
                        await LogToUiAsync("info", "Phasmophobia: применён отдельный мягкий UDP-профиль голоса Vivox; широкая игровая стратегия не получает приоритет над голосом.");
                    if (_activeProfiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("roblox", StringComparison.OrdinalIgnoreCase)))
                        await LogToUiAsync("info", "Roblox: CDN работает через защищённый DNS и узкие доменные правила; закрепление CDN-IP и общий перехват игровых портов отключены.");
                    if (_activeProfiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("deadbydaylight", StringComparison.OrdinalIgnoreCase)))
                        await LogToUiAsync("info", "Dead by Daylight: применены узкие доменные правила авторизации и матчмейкинга; общий UDP Steam/Epic не перехватывается.");

                    if (!_advisorRunning) _ = RunHealthChecksGuardedAsync(CancellationToken.None);
                }
            }
            else
            {
                await _dnsGuard.StopAsync(CancellationToken.None);
                await _components.StopAllAsync(cleanupDriver: !_warpBootstrap.IsActive);
                CancelMobileTelegramOptimization();
                await _telegramProxy.StopAsync();
                await RestoreHostsCoreAsync();
                RestoreProxyUnlessWarpPacActive();
                _activeProfiles = new BypassProfileSettings();
                _protectionStarted = null;
                await PostUiAsync(new { type = "log", level = "warn", message = "Все профили выключены. Сетевые настройки восстановлены." });
            }
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            await LogToUiAsync("info", "Текущая операция запуска отменена.");
        }
        finally
        {
            _protectionTransitioning = false;
            await SendServiceSnapshotAsync();
            _protectionGate.Release();
        }
    }

    private async Task StartTelegramProxyGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _telegramProxy.StartAsync(cancellationToken);
            await LogToUiAsync("ok", $"Локальный Telegram Desktop WS Proxy v{TelegramDesktopProxyService.Version} запущен на 127.0.0.1:{TelegramDesktopProxyService.Port}. Ожидается подключение Telegram Desktop.");
            if (!_advisorRunning && _telegramProxy.ShouldOfferConfiguration)
            {
                _telegramProxy.OpenTelegramConfiguration();
                await LogToUiAsync("info", "Telegram открыт впервые. Нажмите «Подключить», чтобы Desktop использовал локальный WebSocket-маршрут VortexPass.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await LogToUiAsync("error", $"Telegram Desktop proxy не запущен: {ex.Message}");
        }
    }

    private IEnumerable<string> ActiveProfileNames()
    {
        if (_settings.Profiles.YouTube) yield return "YouTube";
        if (_settings.Profiles.Discord) yield return "Discord";
        if (_settings.Profiles.Telegram) yield return "Telegram";
        if (_settings.Profiles.TelegramWeb) yield return "Telegram Web";
        if (_settings.Profiles.Web) yield return "Web";
        if (_settings.Profiles.GameFilter) yield return "Game Filter (точечный)";
        if (IsMobileCompatibilityMode()) yield return "Мобильная совместимость";
        if (_settings.Profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase)))
            yield return "Phasmophobia stability-first";
        if (_settings.Profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("deadbydaylight", StringComparison.OrdinalIgnoreCase)))
            yield return "Dead by Daylight";
    }

    private async Task<IReadOnlyList<HostsOverride>> CreateHostsOverridesAsync(BypassProfileSettings profiles, CancellationToken cancellationToken)
    {
        var overrides = new List<HostsOverride>();
        // Never pin YouTube CDN hosts. Google rotates both media and image
        // edges frequently; synchronous validation used to delay protection by
        // more than 30 seconds and a stale pin could make Firefox even slower.

        if (profiles.TelegramWeb && _settings.TelegramWebRoute!="warpProxy" && !_warpTelegramCompatibilityActive)
            overrides.AddRange(await CreateTelegramWebHostsOverridesAsync(
                mobileCompatibilityMode: IsMobileCompatibilityMode(),
                cancellationToken));

        // Roblox CDN edges rotate independently. Mapping every rbxcdn hostname
        // to one address resolved for tr.rbxcdn.com breaks assets and may prevent
        // the desktop client from joining a game. DNS Guard now provides the
        // protected resolver, so Roblox must retain normal per-host DNS answers.

        return overrides;
    }

    private async Task ApplyRuntimeHostsAsync(BypassProfileSettings profiles, CancellationToken cancellationToken)
    {
        // Remove only our old block before collecting DNS candidates: otherwise
        // Windows simply returns the previous, possibly unreachable pinned IP.
        await RestoreHostsCoreAsync();
        DnsOverrideService.InvalidateCache();
        var hostsOverrides = await CreateHostsOverridesAsync(profiles, cancellationToken);
        // A blocked Telegram network often prevents every DNS/TLS candidate
        // from being verified, which is exactly when the signed Flowseal route
        // is needed. Apply only its Telegram subset; never restore the old
        // hundreds of region-specific Discord media pins.
        // Signed entries are candidates, not proof of reachability on this network.
        var useStaticFallbacks = false;
        if (hostsOverrides.Count == 0 && !useStaticFallbacks)
        {
            await RestoreHostsCoreAsync();
            if (profiles.TelegramWeb)
                await LogToUiAsync("info", _warpTelegramCompatibilityActive ? "Telegram: WARP-маршрут активен, IP не закреплялись."
                    : "Telegram: подтверждённых закреплений нет; оставлены защищённый DNS и hostname-правила.");
            await PublishProfileStatusAsync();
            return;
        }

        _telegramFallbackHostCount = 0;
        _telegramDnsHostCount = 0;
        _robloxHostCount = 0;
        try
        {
            // Every runtime entry below has already passed an SNI-aware TLS/HTTP
            // probe for its exact hostname. Rechecking just the bare IP with a
            // raw TCP connection loses SNI and used to discard valid Telegram
            // routes on filtered providers.
            _hostsStatus = await _hostsManager.ApplyAsync(
                hostsOverrides,
                useStaticFallbacks,
                validateOverrides: false,
                cancellationToken);
            if (!IsMobileCompatibilityMode())
            {
                var rechecks = await Task.WhenAll(hostsOverrides.Where(e => HostsFileManager.IsTelegramWebFallbackHost(e.Host))
                    .Select(e => new TelegramRouteProbeService().ProbeAsync(e.Host, e.Address, cancellationToken)));
                var failed = rechecks.Where(r => !r.Confirmed).Select(r => r.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (failed.Count > 0)
                {
                    DnsOverrideService.InvalidateCache();
                    hostsOverrides = hostsOverrides.Where(e => !failed.Contains(e.Host)).ToArray();
                    _hostsStatus = hostsOverrides.Count == 0 ? await _hostsManager.RestoreAsync(cancellationToken)
                        : await _hostsManager.ApplyAsync(hostsOverrides, false, false, cancellationToken);
                    await LogToUiAsync("info", $"Telegram: не подтвердились {failed.Count} закреплений; возвращён DNS-маршрут.");
                }
            }
            _telegramDnsHostCount = hostsOverrides
                .Count(entry => HostsFileManager.IsTelegramWebFallbackHost(entry.Host));
            _robloxHostCount = hostsOverrides.Count - _telegramDnsHostCount;
            _telegramFallbackHostCount = useStaticFallbacks
                ? Math.Max(0, _hostsStatus.EntryCount - hostsOverrides
                    .Select(entry => entry.Host)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count())
                : 0;
            var hostsDetail = string.IsNullOrWhiteSpace(_hostsStatus.Detail) ? string.Empty : $"; {_hostsStatus.Detail}";
            await LogToUiAsync("ok", $"Системный hosts применён: {_hostsStatus.EntryCount} записей, источник кандидатов Flowseal {HostsFileManager.HostsSourceCommit[..7]}; временных DNS-записей: {hostsOverrides.Count}{hostsDetail}.");
            await LogToUiAsync("info", $"Маршруты hosts: Telegram fallback — {_telegramFallbackHostCount}; Telegram DNS-кандидаты — {_telegramDnsHostCount}; Roblox CDN — {_robloxHostCount}.");
            if (profiles.TelegramWeb)
            {
                var telegramEntries = hostsOverrides.Count(entry => HostsFileManager.IsTelegramWebFallbackHost(entry.Host));
                await LogToUiAsync(telegramEntries > 0 ? "ok" : "info",
                    telegramEntries > 0
                        ? $"Telegram Web: применено подтверждённых маршрутов — {telegramEntries}."
                        : "Telegram Web: IP не закреплялись; используются защищённый DNS и правила winws.");
            }
        }
        catch (Exception ex)
        {
            if (_warpTelegramCompatibilityActive)
            {
                try
                {
                    _hostsStatus = await _hostsManager.RestoreAsync(cancellationToken);
                    await LogToUiAsync("warn", $"WARP-режим Telegram: обновить hosts не удалось, поэтому блок VortexPass полностью удалён во избежание CDN-закрепления. {ex.Message}");
                    await PublishHostsStatusAsync();
                    await PublishProfileStatusAsync();
                    return;
                }
                catch (Exception restoreError)
                {
                    _hostsStatus = _hostsManager.GetStatus();
                    await LogToUiAsync("error", $"WARP-режим Telegram: не удалось удалить старые hosts-записи: {restoreError.Message}");
                    await PublishHostsStatusAsync();
                    await PublishProfileStatusAsync();
                    return;
                }
            }
            _hostsStatus = _hostsManager.GetStatus();
            await LogToUiAsync("error", $"winws запущен, но hosts не применён: {ex.Message}");
        }
        await PublishHostsStatusAsync();
        await PublishProfileStatusAsync();
    }

    private CancellationTokenSource? _startupConnectCts;
    private Task? _startupTask;
    private string? _startupConnectionError;
    private bool _seasonalIcon=true;
    private async Task StartProtectionOnWindowsStartupAsync()
    {
        if(_startupConnectCts is not null)return;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(165));_startupConnectCts=deadline;
        try
        {
            var id=_settings.StartupConnectionId;
            if(!_settings.SavedConnections.Any(p=>p.Id==id))throw new InvalidOperationException("Выберите профиль автозапуска в настройках");
            var until=DateTimeOffset.UtcNow.AddSeconds(30);
            while(!NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel && n.GetIPProperties().GatewayAddresses.Count>0)&&DateTimeOffset.UtcNow<until)await Task.Delay(1000,deadline.Token);
            for(var attempt=1;attempt<=2;attempt++)
            {
                await StartupConnectionPolicy.WaitReadyAsync(()=>!_connectionLaunching&&!_advisorRunning&&!_protectionTransitioning&&!_warpTransitioning,TimeSpan.FromSeconds(15),deadline.Token);
                using var round=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);round.CancelAfter(TimeSpan.FromSeconds(60));
                using var cancel=round.Token.Register(CancelPendingProfileWork);
                try
                {
                    round.Token.ThrowIfCancellationRequested();
                    await LogToUiAsync("info",$"Автоподключение: попытка {attempt}/2; подключаем сохранённый профиль.");
                    await LaunchConnectionAsync(id,round.Token);
                    round.Token.ThrowIfCancellationRequested();
                    if(!_protectionRequested)throw new IOException("Профиль не подключён"); _startupConnectionError=null;
                    await LogToUiAsync("ok","Автоподключение: сохранённый профиль подключён.");
                    return;
                }
                catch(Exception ex) when(!deadline.IsCancellationRequested)
                {
                    await StopProtectionAsync(deadline.Token);
                    await LogToUiAsync("warn",$"Автоподключение {attempt}/2: {ex.Message}");
                    if(attempt==2)throw;
                    await Task.Delay(10000,deadline.Token);
                }
            }
        }
        catch(Exception ex)
        {
            using var restore=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try{await StopProtectionAsync(restore.Token);}catch(Exception error){await LogToUiAsync("error","Восстановление: "+error.Message);}
            _startupConnectionError="Не удалось автоматически подключить профиль. Повторите подключение вручную. "+ex.Message;
            _trayIcon.ShowBalloonTip(6000,"VortexPass",_startupConnectionError,Forms.ToolTipIcon.Warning);
            await PublishSettingsStatusAsync(); await AppendBackgroundErrorAsync("Автоподключение профиля",ex);
        }
        finally{_startupConnectCts=null;await SendServiceSnapshotAsync();}
    }
    private async Task<bool> IsCurrentStartupProfileHealthyAsync(CancellationToken cancellationToken)
    {
        if (!_components.GetStatuses().Any(status => status.Id == "dpi" && status.State == ServiceState.Running)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var profiles = _activeProfiles;
        var checks = AdvisorServiceIds().Select(id => _healthChecks.CheckAsync(
            id, true, true, timeout.Token,
            appSelected: id == "telegram" && profiles.Telegram,
            webSelected: id == "telegram" && profiles.TelegramWeb,
            telegramProxyRunning: id == "telegram" && _telegramProxy.IsRunning,
            telegramProxyHasClientActivity: id == "telegram" && _telegramProxy.HasTelegramClientActivity));
        try
        {
            var results = await Task.WhenAll(checks);
            return results.Length == 0 || results.All(result => result.IsHealthy);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RememberSuccessfulStrategyAsync()
    {
        var level = WinwsProfileComposer.NormalizeLevel(_settings.BypassLevel);
        var tuning = WinwsProfileComposer.NormalizeTuning(_settings.BypassTuning);
        var networkKey = CurrentNetworkKey();
        if (_settings.LastSuccessfulStrategy.Equals(level, StringComparison.OrdinalIgnoreCase) &&
            _settings.LastSuccessfulTuning == tuning &&
            _settings.LastSuccessfulNetworkKey.Equals(networkKey, StringComparison.Ordinal) &&
            _settings.LastSuccessfulStrategyUtc > DateTimeOffset.UtcNow.AddHours(-6))
        {
            _startupFallbackApplied = false;
            UpdateTrayState(_protectionRequested);
            return;
        }
        _settings.LastSuccessfulStrategy = level;
        _settings.LastSuccessfulTuning = tuning;
        _settings.LastSuccessfulStrategyUtc = DateTimeOffset.UtcNow;
        _settings.LastSuccessfulNetworkKey = networkKey;
        _startupFallbackApplied = false;
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        UpdateTrayState(_protectionRequested);
    }

    private string CurrentNetworkKey()
    {
        var identity = string.Join('|', NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel &&
                              !adapter.Name.Contains("WARP",StringComparison.OrdinalIgnoreCase) && !adapter.Description.Contains("Cloudflare",StringComparison.OrdinalIgnoreCase) &&
                              adapter.GetIPProperties().GatewayAddresses.Any(g=>!g.Address.Equals(IPAddress.Any)&&!g.Address.Equals(IPAddress.IPv6Any)))
            .OrderBy(adapter => adapter.Id, StringComparer.Ordinal)
            .Select(adapter => $"{adapter.Id}:{adapter.GetIPProperties().DnsSuffix}:{string.Join(',', adapter.GetIPProperties().GatewayAddresses.Select(gateway => gateway.Address))}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "|warp-client:"+WarpClientVersionKey()+"|warp-preflight-v15:"+WarpTransportPolicy.Selection(_settings.Warp)+":"+_settings.Warp.ProxyPort+"|dns:" + DnsModePolicy.Normalize(_settings.DnsMode) + (_settings.DnsMode == "doh" ? ":" + _settings.SelectedDnsProvider : _settings.DnsMode=="google-udp"?":"+_settings.GoogleDnsAddress:""))));
    }

    private void NetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_shutdownStarted || _lifetime.IsCancellationRequested) return;
        var generation = Interlocked.Increment(ref _networkChangeGeneration);
        _ = Dispatcher.InvokeAsync(() => _ = HandleNetworkChangeAsync(generation));
    }

    private async Task HandleNetworkChangeAsync(int generation)
    {
        try
        {
            // Windows emits several notifications while a hotspot obtains its
            // gateway and DNS. Debounce them before deriving the fingerprint.
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            if (generation != Volatile.Read(ref _networkChangeGeneration)) return;
            var networkKey = CurrentNetworkKey();
            if (networkKey.Equals(_currentNetworkKey, StringComparison.Ordinal)) return;
            if (_advisorSessionNetwork is not null && networkKey != _advisorSessionNetwork)
            {
                StopProfileAdvisor();
                while (_advisorSessionNetwork is not null && !_shutdownStarted)
                    await Task.Delay(100, _lifetime.Token);
            }
            await StopMobileWarpFallbackAsync(CancellationToken.None);
            await _dnsGuard.StopAsync(CancellationToken.None);
            _currentNetworkKey = networkKey;
            DnsOverrideService.InvalidateCache();

            var remembered = _settings.NetworkCompatibilityProfiles.FirstOrDefault(profile =>
                profile.NetworkKey.Equals(networkKey, StringComparison.Ordinal));
            var mode = remembered?.Mode ?? (IsMobileCompatibilityMode() ? "mobile" : "standard");
            await LogToUiAsync("info", remembered is null
                ? mode == "mobile"
                    ? "Обнаружена новая сеть. Мобильная совместимость перенесена на неё до ручного переключения."
                    : "Обнаружена новая сеть. Используется обычный профиль."
                : $"Сеть изменилась. Восстанавливается сохранённый профиль «{(WinwsProfileComposer.NormalizeCompatibilityMode(mode) == "mobile" ? "Мобильная совместимость" : "Обычная сеть")}». ");
            await SetCompatibilityModeAsync(mode, launch: false);
            if (MobileDnsPolicy.ShouldRun(
                    _protectionRequested,
                    mode,
                    MobileDnsPolicy.WarpOwnsSystemDns(
                        _warpStatus.State == WarpConnectionState.Connected,
                        GetEffectiveWarpRoutingMode())))
                await StartProtectedDnsGuardAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await AppendBackgroundErrorAsync("Смена сети", ex);
        }
    }

    private async Task<IReadOnlyList<HostsOverride>> CreateTelegramWebHostsOverridesAsync(
        bool mobileCompatibilityMode,
        CancellationToken cancellationToken)
    {
        var endpoints = TelegramEndpointRegistry.Pages.Select(uri => (Host: uri.Host, HttpProbe: (string?)uri.AbsoluteUri))
            .Concat(TelegramEndpointRegistry.TransportHosts.Select(host => (Host: host, HttpProbe: (string?)null)))
            .DistinctBy(endpoint => endpoint.Host).ToList();
        var knownTelegramEdges = new[]
        {
            IPAddress.Parse("149.154.167.233"),
            IPAddress.Parse("149.154.167.99"),
            IPAddress.Parse("149.154.167.220")
        };
        var tasks = endpoints.Select(async endpoint =>
        {
            // DNS is the primary route. A pinned candidate is an emergency repair,
            // checked twice with the original SNI and a real MTProto response for DC hosts.
            if (!mobileCompatibilityMode)
            {
                try
                {
                    var systemAddresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
                    var systemProbe = new TelegramRouteProbeService();
                    var systemChecks = await Task.WhenAll(systemAddresses.Take(2).Select(address => systemProbe.ProbeAsync(endpoint.Host, address, cancellationToken)));
                    if (systemChecks.Any(route => route.Confirmed)) return null;
                }
                catch (System.Net.Sockets.SocketException) { }
                var resolved = await _dnsOverrides.ResolveCandidatesAsync(endpoint.Host, verifyTls: false,
                    timeout: TimeSpan.FromSeconds(3), cancellationToken: cancellationToken);
                var signed = await _hostsManager.ReadTelegramFallbackCandidatesAsync(cancellationToken);
                var addresses = resolved.Where(r => r.Address is not null).Select(r => r.Address!)
                    .Concat(signed.Where(r => r.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)).Select(r => r.Address))
                    .Distinct().Take(4).ToArray();
                var probe = new TelegramRouteProbeService();
                var checkedRoutes = await Task.WhenAll(addresses.Select(address => probe.ProbeAsync(endpoint.Host, address, cancellationToken)));
                await LogToUiAsync("info", $"Telegram {endpoint.Host}: " + string.Join("; ", checkedRoutes.Select(route => $"{route.Group}: {route.Detail}")));
                var best = TelegramRouteProbeService.SelectConfirmed(checkedRoutes).FirstOrDefault();
                return best is null ? null : new HostsOverride(best.Address, best.Host);
            }

            var dnsCandidatesTask = _dnsOverrides.ResolveCandidatesAsync(
                endpoint.Host,
                verifyTls: true,
                httpProbeUrl: endpoint.HttpProbe,
                timeout: TimeSpan.FromSeconds(3),
                cancellationToken: cancellationToken);
            var knownCandidatesTask = Task.WhenAll(knownTelegramEdges.Select(address =>
                _dnsOverrides.ValidateCandidateAsync(
                    endpoint.Host,
                    address,
                    "Telegram fallback",
                    endpoint.HttpProbe,
                    TimeSpan.FromSeconds(3),
                    cancellationToken)));
            await Task.WhenAll(dnsCandidatesTask, knownCandidatesTask);
            // Prefer a route that has already proved stable for Telegram Web.
            // Some resolvers return .220 first even though that edge accepts a
            // short TLS probe but stalls the long-lived WebSocket on filtered
            // networks. Keep it as the last-resort candidate only.
            var resolution = dnsCandidatesTask.Result
                .Concat(knownCandidatesTask.Result)
                .Where(candidate => candidate.Succeeded && candidate.Address is not null)
                .GroupBy(candidate => candidate.Address!)
                .Select(group => group.OrderBy(candidate => candidate.LatencyMs ?? long.MaxValue).First())
                .OrderBy(candidate => TelegramEdgePreference(candidate.Address!))
                .ThenBy(candidate => candidate.LatencyMs ?? long.MaxValue)
                .FirstOrDefault();

            return resolution?.Address is not null
                ? new HostsOverride(resolution.Address, endpoint.Host)
                : null;
        });
        var overrides = (await Task.WhenAll(tasks)).Where(item => item is not null).Cast<HostsOverride>().ToList();
        await LogToUiAsync(overrides.Count > 0 ? "ok" : "info",
            overrides.Count > 0
                ? mobileCompatibilityMode
                    ? $"Telegram Web: применено отдельных защищённых DNS-маршрутов — {overrides.Count}; по одному IP на hostname."
                    : $"Telegram emergency-host: {overrides.Count} из {endpoints.Count}; каждый маршрут проверен дважды."
                : "Telegram: используется защищённый системный DNS; дополнительных подтверждённых emergency-host нет.");
        return overrides;
    }

    private static int TelegramEdgePreference(IPAddress address) => address.ToString() switch
    {
        "149.154.167.233" => 0,
        "149.154.167.99" => 1,
        "149.154.167.220" => 3,
        _ => 2
    };

    private async Task ApplyCachedMobileTelegramRoutesAsync(BypassProfileSettings profiles, CancellationToken cancellationToken)
    {
        var current = EnsureCurrentNetworkCompatibility();
        current.TelegramRoutes ??= new(StringComparer.OrdinalIgnoreCase);
        var cachedTelegram = current.TelegramRoutes
            .Where(pair => IPAddress.TryParse(pair.Value, out _) && HostsFileManager.IsTelegramWebFallbackHost(pair.Key))
            .Select(pair => new HostsOverride(IPAddress.Parse(pair.Value), pair.Key))
            .ToArray();
        var cached = cachedTelegram;
        if (cached.Length == 0)
        {
            _mobileTelegramRouteCount = 0;
            current.OptimizationState = "checking";
            current.OptimizationDetail = "Telegram: подбор маршрута";
            await PublishCompatibilityOptimizationStatusAsync();
            return;
        }

        try
        {
            _hostsStatus = await _hostsManager.ApplyAsync(cached, includeStaticFallbacks: false, validateOverrides: false, cancellationToken);
            _telegramFallbackHostCount = 0;
            _telegramDnsHostCount = cachedTelegram.Length;
            _robloxHostCount = 0;
            _mobileTelegramRouteCount = cachedTelegram.Length;
            current.OptimizationState = "cached";
            current.OptimizationDetail = "Используется сохранённый маршрут; быстрая проверка идёт в фоне";
            await LogToUiAsync("ok", $"Telegram: применён сохранённый полностью проверенный маршрут ({cachedTelegram.Length}).");
            await PublishHostsStatusAsync();
            await PublishCompatibilityOptimizationStatusAsync();
        }
        catch (Exception ex)
        {
            current.TelegramRoutes.Clear();
            _mobileTelegramRouteCount = 0;
            current.OptimizationState = "checking";
            current.OptimizationDetail = "Сохранённый маршрут отброшен; выполняется новая проверка";
            await LogToUiAsync("info", $"Telegram: сохранённый маршрут больше не применяется ({ex.Message}).");
        }
    }

    private void StartMobileTelegramOptimization(BypassProfileSettings profiles)
    {
        CancelMobileTelegramOptimization();
        _mobileOptimizationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation = Interlocked.Increment(ref _mobileOptimizationGeneration);
        var snapshot = CloneProfiles(profiles);
        var token = _mobileOptimizationCts.Token;
        _ = RunMobileTelegramOptimizationGuardedAsync(snapshot, generation, token);
    }

    private async Task RunMobileTelegramOptimizationGuardedAsync(BypassProfileSettings profiles, int generation, CancellationToken cancellationToken)
    {
        try { await OptimizeMobileTelegramRoutesAsync(profiles, generation, cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await AppendBackgroundErrorAsync("Подбор Telegram-маршрута", ex); }
    }

    private void CancelMobileTelegramOptimization()
    {
        try { _mobileOptimizationCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _mobileOptimizationCts?.Dispose();
        _mobileOptimizationCts = null;
    }

    private async Task OptimizeMobileTelegramRoutesAsync(BypassProfileSettings profiles, int generation, CancellationToken cancellationToken)
    {
        var current = EnsureCurrentNetworkCompatibility();
        current.OptimizationState = "checking";
        current.OptimizationDetail = "Быстрая проверка System DNS, Cloudflare, Google, Quad9 и Flowseal";
        await PublishCompatibilityOptimizationStatusAsync();

        var providerTask = DetectProviderAsync(cancellationToken);
        var fallbackEntries = await _hostsManager.ReadTelegramFallbackCandidatesAsync(cancellationToken);
        var fallbackByHost = fallbackEntries
            .GroupBy(entry => entry.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Address).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        if (fallbackByHost.TryGetValue("web.telegram.org", out var webFallback))
        {
            fallbackByHost.TryAdd("webk.telegram.org", webFallback);
            fallbackByHost.TryAdd("webz.telegram.org", webFallback);
        }

        var probes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["web.telegram.org"] = "https://web.telegram.org/",
            ["webk.telegram.org"] = "https://webk.telegram.org/",
            ["webz.telegram.org"] = "https://webz.telegram.org/",
            ["kws2.web.telegram.org"] = "https://kws2.web.telegram.org/apiws",
            ["kws4.web.telegram.org"] = "https://kws4.web.telegram.org/apiws",
            ["cdn.telesco.pe"] = "https://cdn.telesco.pe/"
        };
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var limiter = new SemaphoreSlim(4, 4);
        var routeTasks = probes.Select(async probe =>
        {
            await limiter.WaitAsync(totalTimeout.Token);
            try
            {
                var candidates = (await _dnsOverrides.ResolveCandidatesAsync(
                    probe.Key, verifyTls: true, httpProbeUrl: probe.Value,
                    timeout: TimeSpan.FromMilliseconds(1800), cancellationToken: totalTimeout.Token)).ToList();
                if (fallbackByHost.TryGetValue(probe.Key, out var staticAddresses))
                {
                    foreach (var address in staticAddresses)
                    {
                        var checkedRoute = await _dnsOverrides.ValidateCandidateAsync(
                            probe.Key, address, "Flowseal", probe.Value,
                            TimeSpan.FromMilliseconds(1800), totalTimeout.Token);
                        if (checkedRoute.Succeeded) candidates.Add(checkedRoute);
                    }
                }
                var best = candidates
                    .Where(candidate => candidate.Succeeded && candidate.Address is not null)
                    .OrderBy(candidate => candidate.LatencyMs ?? long.MaxValue)
                    .FirstOrDefault();
                if (best?.Address is null) return (Host: probe.Key, Result: (DnsOverrideResult?)null);
                var confirmation = await _dnsOverrides.ValidateCandidateAsync(
                    probe.Key, best.Address, best.Source, probe.Value,
                    TimeSpan.FromMilliseconds(1800), totalTimeout.Token);
                return (Host: probe.Key, Result: confirmation.Succeeded ? confirmation : null);
            }
            finally { limiter.Release(); }
        }).ToArray();

        (string Host, DnsOverrideResult? Result)[] measured;
        try { measured = await Task.WhenAll(routeTasks); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            measured = routeTasks.Where(task => task.IsCompletedSuccessfully).Select(task => task.Result).ToArray();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _mobileOptimizationGeneration)) return;

        var confirmed = measured
            .Where(item => item.Result?.Address is not null && MobileDnsPolicy.CanPinEmergencyHost(item.Host))
            .ToDictionary(item => item.Host, item => item.Result!.Address!.ToString(), StringComparer.OrdinalIgnoreCase);
        var webOk = confirmed.Keys.Any(host => host is "web.telegram.org" or "webk.telegram.org" or "webz.telegram.org");
        var wsOk = confirmed.Keys.Any(host => host.StartsWith("kws", StringComparison.OrdinalIgnoreCase));
        var mediaOk = confirmed.ContainsKey("cdn.telesco.pe");

        var selected = new Dictionary<string, string>(confirmed, StringComparer.OrdinalIgnoreCase);

        var provider = await providerTask;
        if (provider is not null)
        {
            current.ProviderKey = provider.Key;
            current.ProviderName = provider.DisplayName;
        }
        current.TelegramRoutes = selected;
        current.UpdatedAt = DateTimeOffset.UtcNow;

        if (selected.Count > 0)
        {
            var overrides = selected.Select(pair => new HostsOverride(IPAddress.Parse(pair.Value), pair.Key)).ToArray();
            _hostsStatus = await _hostsManager.ApplyAsync(overrides, includeStaticFallbacks: false, validateOverrides: false, cancellationToken);
            _telegramFallbackHostCount = 0;
            _telegramDnsHostCount = overrides.Count();
            _robloxHostCount = 0;
            _mobileTelegramRouteCount = selected.Count;
            await PublishHostsStatusAsync();
            await LogToUiAsync("ok", $"Telegram emergency hosts: дважды подтверждено {selected.Count} маршрутов; YouTube/Discord не закрепляются.");
        }
        else
        {
            _mobileTelegramRouteCount = 0;
            _hostsStatus = await _hostsManager.RestoreAsync(cancellationToken);
            await PublishHostsStatusAsync();
        }

        if (webOk && wsOk)
        {
            current.OptimizationState = "confirmed";
            current.OptimizationDetail = mediaOk
                ? $"Telegram-маршрут подтверждён: {selected.Count} узлов"
                : "Web и сообщения подтверждены; media/CDN оставлен через DNS";
            current.LastSuccessfulOptimizationUtc = DateTimeOffset.UtcNow;
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            await LogToUiAsync("ok", $"Telegram: подтверждён мобильный маршрут ({selected.Count} узлов, Web + WebSocket{(mediaOk ? " + media" : string.Empty)}).");
            await PublishCompatibilityOptimizationStatusAsync();
            return;
        }

        current.OptimizationState = selected.Count > 0 ? "partial" : "manual";
        current.OptimizationDetail = selected.Count > 0
            ? $"Telegram emergency route: Web={webOk}, сообщения={wsOk}, media={mediaOk}"
            : "Telegram IP не прошли две TLS/HTTP-проверки; hosts очищен, используется защищённый DNS + winws";
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        await LogToUiAsync("info", current.OptimizationDetail);
        await PublishCompatibilityOptimizationStatusAsync();
    }

    private Task PublishCompatibilityOptimizationStatusAsync()
    {
        var current = CurrentNetworkCompatibility();
        return PostUiAsync(new
        {
            type = "compatibility.optimize.status",
            providerName = current?.ProviderName,
            optimizeState = current?.OptimizationState ?? "checking",
            optimizeDetail = current?.OptimizationDetail ?? "Telegram: подбор маршрута",
            telegramRouteCount = _mobileTelegramRouteCount,
            discordVoiceApplied = CurrentDiscordVoiceMode(),
            youtubeTransport = WinwsProfileComposer.NormalizeYoutubeTransport(current?.YouTubeTransport)
        });
    }

    private async Task RestoreHostsAsync()
    {
        try
        {
            _hostsStatus = await _hostsManager.RestoreAsync(_lifetime.Token);
            _telegramFallbackHostCount = 0;
            _telegramDnsHostCount = 0;
            _robloxHostCount = 0;
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
        _telegramFallbackHostCount = 0;
        _telegramDnsHostCount = 0;
        _robloxHostCount = 0;
        await PublishHostsStatusAsync();
    }

    private Task PublishHostsStatusAsync() => PostUiAsync(new
    {
        type = "hosts.status",
        applied = _hostsStatus.Applied,
        entryCount = _hostsStatus.EntryCount,
        updatedAt = _hostsStatus.UpdatedAt,
        detail = _hostsStatus.Detail,
        telegramFallbackCount = _telegramFallbackHostCount,
        telegramDnsCount = _telegramDnsHostCount,
        robloxCdnCount = _robloxHostCount,
        sourceCommit = HostsFileManager.HostsSourceCommit
    });

    private async void ComponentStatusChanged(object? sender, ServiceStatus status)
    {
        try { await Dispatcher.InvokeAsync(()=>HandleComponentStatusAsync(status)).Task.Unwrap(); }
        catch(Exception ex){await AppendBackgroundErrorAsync("ComponentStatusChanged",ex);}
    }
    private async Task HandleComponentStatusAsync(ServiceStatus status)
    {
        try
        {
            if(!_components.IsCurrent(status))return;
            _serviceStatuses[status.Id] = status;
            await PostUiAsync(new
            {
                type = "service.status",
                id = status.Id,
                state = status.State.ToString().ToLowerInvariant(),
                detail = status.Detail, generation=status.Generation, revision=status.Revision, planned=status.Planned
            });
            var level = status.State == ServiceState.Error ? "error" : status.State == ServiceState.Running ? "ok" : "info";
            var detail = string.IsNullOrWhiteSpace(status.Detail) ? string.Empty : $" {status.Detail}";
            await LogToUiAsync(level, $"[{status.DisplayName}] {status.State}; поколение {status.Generation}, событие {status.Revision}, {(status.Planned?"плановый переход":"состояние процесса")}.{detail}");
            if(!_components.IsCurrent(status))return;
            if (status.State is ServiceState.Running or ServiceState.Error or ServiceState.Stopped)
                await SendServiceSnapshotAsync();
            if (status.Id.Equals("dpi", StringComparison.OrdinalIgnoreCase) &&
                status.State == ServiceState.Stopped && !status.Planned && _components.IsCurrent(status) && _protectionRequested && !_protectionTransitioning && _advisorTrialSpec is null)
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


    private readonly System.Collections.Concurrent.ConcurrentQueue<object> _connectionNotices = new();
    private async Task LogToUiAsync(string level, string message)
    {
        if(level is "warn" or "error")
        {
            _connectionNotices?.Enqueue(new {time=DateTimeOffset.Now,level,message,stage=_advisorRunning?"advisor":_connectionLaunching||_protectionTransitioning?"connecting":"running",generation=_components?.GetStatuses().FirstOrDefault(s=>s.Id=="dpi")?.Generation??0});
            if(_connectionNotices is not null)while(_connectionNotices.Count>40)_connectionNotices.TryDequeue(out _);
        }
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
        var proxyWeb=_activeProfiles.TelegramWeb&&(_activeConnection?.TelegramWebRoute??_settings.TelegramWebRoute)=="warpProxy"&&GetTelegramWebProxyUri() is not null;
        var enabled = _activeProfiles.AnyEnabled && (winwsRunning||proxyWeb||_protectionRequested&&(_protectionTransitioning||_advisorRunning));
        await PostUiAsync(new
        {
            type = "protection.status",
            enabled,
            pending = _protectionTransitioning || _advisorRunning || _connectionLaunching,
            connectionNotices = _connectionNotices?.ToArray()??[],
            youtube = winwsRunning && _activeProfiles.YouTube,
            discord = winwsRunning && _activeProfiles.Discord,
            telegram = winwsRunning && _activeProfiles.Telegram,
            telegramWeb = (winwsRunning||proxyWeb) && _activeProfiles.TelegramWeb,
            web = winwsRunning && _activeProfiles.Web,
            gameFilter = winwsRunning && _activeProfiles.GameFilter,
            requestedYoutube = _settings.Profiles.YouTube,
            requestedDiscord = _settings.Profiles.Discord,
            requestedTelegram = _settings.Profiles.Telegram,
            requestedTelegramWeb = _settings.Profiles.TelegramWeb,
            requestedWeb = _settings.Profiles.Web,
            requestedGameFilter = _settings.Profiles.GameFilter
        });
        UpdateTrayState(enabled);
        await PublishSettingsStatusAsync();
        await PublishConfigurationAsync();
        await PublishConnectionsAsync();
        await PublishHostsStatusAsync();
        await PublishHealthSnapshotAsync(winwsRunning);
        await PublishWarpStatusAsync();
        await PublishYoutubeReadinessAsync();
    }

    private Task PublishSettingsStatusAsync() => PostUiAsync(new
    {
        type = "settings.status",
        theme = _settings.Theme,
        reduceMotion = _settings.ReduceMotion,
        autostart = _settings.StartWithWindows,
        tray = _settings.MinimizeToTray,
        selectedDnsProvider = _settings.SelectedDnsProvider,
            dnsMode = DnsModePolicy.Normalize(_settings.DnsMode),
        startProtectionOnStartup = _settings.StartProtectionOnStartup,
        startupStrategy = _settings.StartupStrategy, startupConnectionError = _startupConnectionError, startupConnectionId = _settings.StartupConnectionId, startupProfileChoiceRequired = _settings.StartupProfileChoiceRequired,
        gameFilterIpsetMode = _settings.GameFilter.IpsetMode
    });

    private void UpdateTrayState(bool protectionEnabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => UpdateTrayState(protectionEnabled));
            return;
        }
        if (_shutdownStarted) return;

        var hasError = HasProtectionError();
        var warpError = _warpStatus.State == WarpConnectionState.Error;
        _trayIcon.Text = warpError ? "VortexPass — ошибка WARP" :
            _warpTransitioning ? "VortexPass — WARP подключается" :
            _warpStatus.State == WarpConnectionState.Connected ? "VortexPass — WARP подключён" :
            hasError ? "VortexPass — ошибка защиты" :
            _startupFallbackApplied ? "VortexPass — нужна проверка маршрута" :
            protectionEnabled ? "VortexPass — защита включена" : "VortexPass — защита выключена";
        _trayMenu.UpdateState(protectionEnabled, _protectionTransitioning || _advisorRunning,
            _startupFallbackApplied, hasError, _warpStatus.State == WarpConnectionState.Connected, _warpTransitioning);
    }

    private bool HasProtectionError() => _serviceStatuses.Values.Any(status => status.State == ServiceState.Error);

    private async Task RunMetricsLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var counters = ReadNetworkCounters();
            var active = _protectionStarted.HasValue;
            if (active && !_advisorRunning && DateTimeOffset.UtcNow >= _nextHealthCheckUtc)
            {
                // Diagnostics are deliberately sparse: several simultaneous
                // CDN probes can compete with an actively playing video on a
                // throttled route and do not need to run every 30 seconds.
                _nextHealthCheckUtc = DateTimeOffset.UtcNow.AddSeconds(90);
                _ = RunHealthChecksGuardedAsync(cancellationToken);
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

    private async Task RunHealthChecksGuardedAsync(CancellationToken cancellationToken)
    {
        if (_advisorRunning) return;
        if (Interlocked.Exchange(ref _healthCheckRunning, 1) != 0) return;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _healthCheckCts.Token);
            await RunHealthChecksAsync(linked.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await AppendBackgroundErrorAsync("Сетевая проверка", ex);
        }
        finally
        {
            Volatile.Write(ref _healthCheckRunning, 0);
        }
    }

    private async Task RunHealthChecksAsync(CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _healthGeneration);
        var winwsRunning = _components.GetStatuses().Any(x => x.Id == "dpi" && x.State == ServiceState.Running);
        var profiles = _activeProfiles.AnyEnabled ? _activeProfiles : _settings.Profiles;
        var checks = new List<Task<ServiceHealthResult>>
        {
            _healthChecks.CheckAsync("youtube", profiles.YouTube || profiles.Web, winwsRunning, cancellationToken),
            _healthChecks.CheckAsync("discord", profiles.Discord || profiles.Web, winwsRunning, cancellationToken,
                discordVoiceMode: profiles.Discord ? CurrentDiscordVoiceMode() : "off"),
            _settings.TelegramWebRoute=="warpProxy"&&profiles.TelegramWeb ? CheckTelegramWarpHealthAsync(cancellationToken) :
            _healthChecks.CheckAsync("telegram", profiles.Telegram || profiles.TelegramWeb, winwsRunning, cancellationToken, profiles.Telegram, profiles.TelegramWeb, _telegramProxy.IsRunning, _telegramProxy.HasTelegramClientActivity)
        };
        if (profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("roblox", StringComparison.OrdinalIgnoreCase)))
            checks.Add(_healthChecks.CheckAsync("roblox", true, winwsRunning, cancellationToken));
        if (profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase)))
            checks.Add(_healthChecks.CheckAsync("phasmophobia", true, winwsRunning, cancellationToken));
        if(profiles.Custom)
            checks.AddRange((_activeConnection?.SiteRules??_settings.CurrentSiteRules).Select(rule=>ServiceHealthCheckService.CheckSiteRuleAsync(rule,
                rule.Connection=="warpProxy"?GetWarpProxyUri():null,cancellationToken,_warpStatus.State==WarpConnectionState.Connected&&GetEffectiveWarpRoutingMode()=="full")));
        var results = await Task.WhenAll(checks);
        results = await ApplyWarpFallbackHealthAsync(results, cancellationToken);
        if (generation != Volatile.Read(ref _healthGeneration)) return;
        await RememberMobileTransportChoiceAsync(results);
        foreach (var result in results)
        {
            if (result.IsHealthy)
            {
                _healthFailureStreaks.Remove(result.Id);
            }
            else if (result.Id == "telegram" &&
                     result.State is not ServiceHealthState.NotSelected and not ServiceHealthState.WinwsStopped)
            {
                // Telegram's synthetic HttpClient probes can lose the first TLS
                // race immediately after hosts/DNS refresh even while Firefox is
                // already working. Require two consecutive failed rounds before
                // replacing the UI status or writing a red diagnostic entry.
                var failures = _healthFailureStreaks.GetValueOrDefault(result.Id) + 1;
                _healthFailureStreaks[result.Id] = failures;
                if (failures < 2)
                {
                    _healthStatuses[result.Id] = new ServiceHealthResult(
                        result.Id,
                        ServiceHealthState.Checking,
                        "Основной маршрут ещё проверяется; выполняется повторная диагностика",
                        result.LatencyMs,
                        result.Stages);
                    continue;
                }
            }
            else if (_healthStatuses.TryGetValue(result.Id, out var lastHealthy) && lastHealthy.IsHealthy &&
                     result.State is not ServiceHealthState.NotSelected and not ServiceHealthState.WinwsStopped)
            {
                var failures = _healthFailureStreaks.GetValueOrDefault(result.Id) + 1;
                _healthFailureStreaks[result.Id] = failures;
                if (failures < 2)
                    continue;
            }
            _healthStatuses[result.Id] = result;
            if (result.State is not ServiceHealthState.NotSelected and not ServiceHealthState.WinwsStopped)
            {
                var level = result.State == ServiceHealthState.Partial ? "info" : result.IsHealthy ? "ok" : "error";
                var latencyLabel = result.MeasurementType switch
                {
                    ServiceMeasurementType.VoiceProbe => "проверка voice-маршрута",
                    ServiceMeasurementType.GameSession => "игровая сессия",
                    _ => "проверка маршрута"
                };
                var latency = result.LatencyMs is null ? string.Empty : $" ({latencyLabel}: {result.LatencyMs} ms)";
                var stages = result.Stages is null ? string.Empty :
                    $" [DNS: {result.Stages.Dns}] [TCP: {result.Stages.Tcp}] [TLS: {result.Stages.Tls}] [HTTP: {result.Stages.Http}]" +
                    (result.Stages.Voice is null ? string.Empty : $" [Voice/Media: {result.Stages.Voice}]") +
                    (result.Stages.Gateway is null ? string.Empty : $" [Gateway: {result.Stages.Gateway}]") +
                    (result.Stages.Cdn is null ? string.Empty : $" [CDN: {result.Stages.Cdn}]");

                var diagHeader = result.State == ServiceHealthState.Partial ? "[DIAG/PARTIAL]" : result.IsHealthy ? "[DIAG/OK]" : "[DIAG/WARN]";
                await LogToUiAsync(level, $"{diagHeader} {ServiceDisplayName(result.Id)}: {result.Detail}{latency}.{stages}");

            }

        }
        if (_protectionStarted.HasValue && !_advisorRunning && results.Length > 0 && results.All(result => result.IsHealthy))
            await RememberSuccessfulStrategyAsync();
        await PublishHealthSnapshotAsync(winwsRunning);
    }

    private async Task<ServiceHealthResult[]> ApplyWarpFallbackHealthAsync(
        ServiceHealthResult[] results,
        CancellationToken cancellationToken)
    {
        if (!_mobileWarpFallbackActive || _warpStatus.State != WarpConnectionState.Connected)
            return results;

        var entry = CurrentNetworkCompatibility();
        var updated = results.ToArray();
        for (var index = 0; index < updated.Length; index++)
        {
            var current = updated[index];
            if (!_mobileWarpFallbackServices.Contains(current.Id)) continue;
            var target = MobileRoutePolicy.ProbeUri(current.Id);
            if (target is null) continue;
            var appliedRoute = entry?.ServiceRoutes?.GetValueOrDefault(current.Id)?.AppliedRoute;
            var proxy = appliedRoute == "warpProxy"
                ? new Uri($"socks5://127.0.0.1:{_settings.Warp.ProxyPort}")
                : null;
            var probe = await _warpService.ProbeWebRouteAsync(target, proxy, cancellationToken);
            if (!probe.Available) continue;

            var state = current.Id.Equals("discord", StringComparison.OrdinalIgnoreCase)
                ? ServiceHealthState.Partial
                : ServiceHealthState.Healthy;
            var routeName = appliedRoute == "warpSystem" ? "системный WARP" : "WARP-прокси";
            var detail = current.Id.Equals("discord", StringComparison.OrdinalIgnoreCase)
                ? $"Discord Web/Gateway доступен через {routeName}; голос проверяется только во время реального звонка"
                : $"Telegram Web подтверждён через {routeName}";
            updated[index] = new ServiceHealthResult(
                current.Id,
                state,
                detail,
                probe.MedianLatencyMs,
                new ServiceProbeStages(
                    "маршрут WARP",
                    "туннель подтверждён",
                    "через WARP",
                    probe.Detail,
                    current.Id.Equals("discord", StringComparison.OrdinalIgnoreCase) ? "не подтверждён без активного звонка" : null,
                    current.Id.Equals("discord", StringComparison.OrdinalIgnoreCase) ? "доступен через WARP" : "WebSocket проверяется браузером",
                    null));
        }
        return updated;
    }

    private async Task PublishHealthSnapshotAsync(bool winwsRunning)
    {
        foreach (var id in new[] { "youtube", "discord", "telegram", "phasmophobia", "roblox" })
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
                measurementType = x.Value.MeasurementType.ToString().ToLowerInvariant(),
                probeVersion = RouteProbePolicy.Version,
                systemDnsReady = x.Value.SystemDnsReady,
                groups = x.Value.Groups?.Select(g => new { id = g.Id, state = g.State, detail = g.Detail }),
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
            "telegram" => profiles.Telegram || profiles.TelegramWeb,
            "phasmophobia" => profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase)),
            "roblox" => profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("roblox", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static string ServiceDisplayName(string id) => id switch { "youtube" => "YouTube", "discord" => "Discord", "telegram" => "Telegram", "roblox" => "Roblox", "phasmophobia" => "Phasmophobia", _ => id };

    private static BypassProfileSettings CloneProfiles(BypassProfileSettings profiles) => new()
    {
        Custom = profiles.Custom,
        YouTube = profiles.YouTube,
        Discord = profiles.Discord,
        Telegram = profiles.Telegram,
        TelegramWeb = profiles.TelegramWeb,
        Web = profiles.Web,
        GameFilter = profiles.GameFilter
    };

    private static string LevelDisplayName(string level) => level switch
    {
        "light" => "Лёгкий",
        "moderate" => "Умеренный",
        "medium" => "Средний",
        "complex" => "Сложный",
        "aggressive" => "Агрессивный",
        _ => "Средний"
    };

    private static string TuningDisplayName(int tuning) => WinwsProfileComposer.NormalizeTuning(tuning) switch
    {
        0 => "Стандарт",
        1 => "Усиленный +2",
        2 => "Усиленный +4",
        3 => "Максимум +6",
        _ => "Стандарт"
    };

    private static string ProfileDisplayName(string level, int tuning, string mode = "standard")
    {
        var normalizedLevel = WinwsProfileComposer.NormalizeLevel(level);
        return $"{LevelDisplayName(normalizedLevel)} · {TuningDisplayName(tuning)}";
    }

    private static (long Bytes, long Packets) ReadNetworkCounters()
    {
        long bytes = 0, packets = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            try
            {
                var stats = nic.GetIPStatistics();
                bytes += stats.BytesReceived + stats.BytesSent;
                packets += stats.UnicastPacketsReceived + stats.UnicastPacketsSent + stats.NonUnicastPacketsReceived + stats.NonUnicastPacketsSent;
            }
            catch (NetworkInformationException) { }
        }
        return (bytes, packets);
    }


    private async Task<bool> TryInstallPendingUpdateOnStartupAsync()
    {
        try
        {
            var pending = await _pendingUpdates.LoadAsync(_lifetime.Token);
            if (pending is null) return false;
            if (pending.Status == "failed")
            {
                _startupUpdateError = $"Предыдущее обновление не установилось: {pending.LastError}";
                return false;
            }
            if (pending.Status != "install-on-next-start") return false;

            if (!File.Exists(pending.ZipPath) || !await HashVerifier.MatchesAsync(pending.ZipPath, pending.Sha256, _lifetime.Token))
            {
                _startupUpdateError = "Отложенное обновление повреждено. Файл состояния сброшен, запуск продолжается без обновления.";
                await _pendingUpdates.ClearAsync(_lifetime.Token);
                await AppendApplicationLogLineAsync($"[WARN] {_startupUpdateError}");
                return false;
            }

            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "updater", "VortexPass.Updater.exe")))
            {
                _startupUpdateError = "Отложенное обновление не запущено: в portable-комплекте нет updater/VortexPass.Updater.exe.";
                await AppendApplicationLogLineAsync($"[ERROR] {_startupUpdateError}");
                return false;
            }

            pending.Status = "install-now";
            pending.LastError = null;
            await _pendingUpdates.SaveAsync(pending, _lifetime.Token);
            StartUpdaterProcess(pending);
            _allowClose = true;
            System.Windows.Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            _startupUpdateError = $"Отложенное обновление не запущено: {ex.Message}";
            await AppendApplicationLogLineAsync($"[ERROR] {_startupUpdateError}");
            return false;
        }
    }

    private async Task LogUpdateCommandLineAsync()
    {
        var args = Environment.GetCommandLineArgs();
        string? Get(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var from = Get("--updated-from");
        var to = Get("--updated-to");
        if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
            await AppendApplicationLogLineAsync($"[INFO] Обновление установлено: {from} -> {to}");
    }

    private async Task CheckForUpdatesAsync()
    {
        if (Interlocked.Exchange(ref _updateCheckRunning, 1) != 0) return;
        try
        {
        await PostUiAsync(new { type = "update.status", state = "checking", currentVersion = CurrentVersion().ToString(3) });
        _lastUpdate = await _updates.CheckAsync(CurrentVersion(), _lifetime.Token);
        if (_lastUpdate.State is UpdateState.Current or UpdateState.Available)
        {
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            await _settingsStore.SaveAsync(_settings);
        }
        var state = _lastUpdate.State switch
        {
            UpdateState.Current => "current",
            UpdateState.Available => "availablePrompt",
            UpdateState.Unavailable => "unavailable",
            _ => "failed"
        };
        if (_lastUpdate.State == UpdateState.Available && _updatePromptDismissedThisSession)
            state = "available";
        var pending = await _pendingUpdates.LoadAsync(_lifetime.Token);
        if (_lastUpdate.State == UpdateState.Available && pending?.Status == "install-on-next-start" && pending.Version == _lastUpdate.Release?.Version.ToString(3))
            state = "deferred";
        await PostUiAsync(new
        {
            type = "update.status",
            state,
            currentVersion = CurrentVersion().ToString(3),
            availableVersion = _lastUpdate.Release?.Version.ToString(3),
            checkedAt = _settings.LastUpdateCheckUtc,
            error = _lastUpdate.Error
        });
        }
        finally
        {
            Volatile.Write(ref _updateCheckRunning, 0);
        }
    }

    private async Task InstallUpdateAsync()
    {
        await InstallUpdateNowAsync();
    }

    private async Task InstallUpdateNowAsync()
    {
        if (_lastUpdate?.State != UpdateState.Available || _lastUpdate.Release is null)
        {
            await CheckForUpdatesAsync();
            return;
        }
        await PostUiAsync(new { type = "update.status", state = "preparing", currentVersion = CurrentVersion().ToString(3), availableVersion = _lastUpdate.Release.Version.ToString(3) });
        try
        {
            var pending = await PrepareUpdateStateAsync(_lastUpdate.Release, "install-now");
            await PostUiAsync(new { type = "update.status", state = "installing", currentVersion = CurrentVersion().ToString(3), availableVersion = pending.Version });
            StartUpdaterProcess(pending);
            await ShutdownAsync();
        }
        catch (Exception ex)
        {
            await PostUiAsync(new { type = "update.status", state = "failed", currentVersion = CurrentVersion().ToString(3), availableVersion = _lastUpdate.Release.Version.ToString(3), error = ex.Message });
        }
    }

    private async Task DeferUpdateAsync()
    {
        if (_lastUpdate?.State != UpdateState.Available || _lastUpdate.Release is null)
        {
            await CheckForUpdatesAsync();
            return;
        }
        await PostUiAsync(new { type = "update.status", state = "preparing", currentVersion = CurrentVersion().ToString(3), availableVersion = _lastUpdate.Release.Version.ToString(3) });
        try
        {
            var pending = await PrepareUpdateStateAsync(_lastUpdate.Release, "install-on-next-start");
            _updatePromptDismissedThisSession = true;
            await PostUiAsync(new { type = "update.status", state = "deferred", currentVersion = CurrentVersion().ToString(3), availableVersion = pending.Version, checkedAt = _settings.LastUpdateCheckUtc });
        }
        catch (Exception ex)
        {
            await PostUiAsync(new { type = "update.status", state = "failed", currentVersion = CurrentVersion().ToString(3), availableVersion = _lastUpdate.Release.Version.ToString(3), error = ex.Message });
        }
    }

    private async Task<PendingUpdateState> PrepareUpdateStateAsync(ReleaseInfo release, string status)
    {
        EnsureUpdaterAvailable();
        var downloaded = await _updates.DownloadAsync(release, cancellationToken: _lifetime.Token);
        var state = new PendingUpdateState
        {
            Status = status,
            Version = release.Version.ToString(3),
            Sha256 = downloaded.Sha256,
            ZipPath = downloaded.ZipPath,
            TargetPath = AppContext.BaseDirectory,
            RestartPath = Path.Combine(AppContext.BaseDirectory, "VortexPass.exe"),
            LastError = null
        };
        await _pendingUpdates.SaveAsync(state, _lifetime.Token);
        return state;
    }

    private void EnsureUpdaterAvailable()
    {
        var updaterExe = Path.Combine(AppContext.BaseDirectory, "updater", "VortexPass.Updater.exe");
        if (!File.Exists(updaterExe))
            throw new FileNotFoundException("Не найден updater/VortexPass.Updater.exe. Соберите полный portable-комплект через dist.bat.", updaterExe);
    }

    private void StartUpdaterProcess(PendingUpdateState state)
    {
        EnsureUpdaterAvailable();
        var updaterSource = Path.Combine(AppContext.BaseDirectory, "updater");
        var updaterTemp = Path.Combine(Path.GetTempPath(), "VortexPass-updater", Guid.NewGuid().ToString("N"));
        CopyDirectory(updaterSource, updaterTemp);
        var updaterExe = Path.Combine(updaterTemp, "VortexPass.Updater.exe");
        var start = new ProcessStartInfo(updaterExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updaterTemp
        };
        if(_settings.Theme=="light")start.ArgumentList.Add("--light");
        if(!_seasonalIcon)start.ArgumentList.Add("--standard");
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("--zip");
        start.ArgumentList.Add(state.ZipPath);
        start.ArgumentList.Add("--sha256");
        start.ArgumentList.Add(state.Sha256);
        start.ArgumentList.Add("--target");
        start.ArgumentList.Add(state.TargetPath);
        start.ArgumentList.Add("--restart");
        start.ArgumentList.Add(state.RestartPath);
        start.ArgumentList.Add("--state");
        start.ArgumentList.Add(_pendingUpdates.StatePath);
        start.ArgumentList.Add("--from");
        start.ArgumentList.Add(CurrentVersion().ToString(3));
        start.ArgumentList.Add("--to");
        start.ArgumentList.Add(state.Version);
        Process.Start(start);
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
            await AppendApplicationLogLineAsync($"[ERROR] {source}: {exception}");
        }
        catch { }
        await PostUiAsync(new { type = "log", level = "error", message = $"{source}: {exception.Message}" });
    }

    private async Task AppendApplicationLogLineAsync(string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_applicationLogPath)!);
        await File.AppendAllTextAsync(_applicationLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }

    private async Task ResetApplicationLogAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_applicationLogPath)!);
            await File.WriteAllTextAsync(_applicationLogPath, $"{DateTimeOffset.Now:O} [INFO] VortexPass started. Сборка public-v22-20261028.{Environment.NewLine}", _lifetime.Token);
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
            NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
            _trayMenu.Hide();
            _trayMenu.Close();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _lifetime.Cancel();
            CancelMobileTelegramOptimization();
            return;
        }
        e.Cancel = true;
        if (_settings.MinimizeToTray) Hide();
        else await ShutdownAsync();
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async Task ShutdownAsync()
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _warpPreparationCts?.Cancel();
        if(_warpPreparationFinished?.Task is {IsCompleted:false} preparation)
        {try{await preparation.WaitAsync(TimeSpan.FromSeconds(10));}catch(TimeoutException){await AppendApplicationLogLineAsync("[WARN] WARP preparation cancellation still pending during shutdown");}}
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        CancelMobileTelegramOptimization();
        try
        {
            try { await StopMobileWarpFallbackAsync(_lifetime.Token); }
            catch (Exception ex) { await AppendApplicationLogLineAsync($"[WARN] Mobile WARP fallback cleanup: {ex.Message}"); }
            try { await StopWarpRoutingAsync(_lifetime.Token); }
            catch (Exception ex) { await AppendApplicationLogLineAsync($"[WARN] WARP routing cleanup: {ex.Message}"); }
            if (WarpService.NeedsEmergencyRecovery(_warpStatus.State, _settings.Warp.RequestedConnected, _warpBootstrap.IsActive))
            {
                using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
                var recovery = await _warpService.RecoverOrdinaryRouteAsync(recoveryTimeout.Token);
                await _warpBootstrap.StopAsync();
                await AppendApplicationLogLineAsync(recovery.Succeeded
                    ? "[INFO] WARP аварийно отключён перед выходом; обычный маршрут восстановлен."
                    : $"[ERROR] WARP recovery on shutdown: {recovery.Detail}");
            }
            // Stop both independent winws processes, then remove the owned
            // WinDivert driver. This is application shutdown, not a WARP toggle.
            await _warpBootstrap.StopAsync();
            await _dnsGuard.StopAsync(CancellationToken.None);
            await _components.StopAllAsync();
            await _telegramProxy.StopAsync();
            await RestoreHostsCoreAsync();
            _proxyManager.RestoreIfOwned();
        }
        finally
        {
            _components.Dispose();
            _telegramProxy.Dispose();
            _warpPac.Dispose();
            _warpService.Dispose();
            _warpBootstrap.Dispose();
            _dnsGuard.Dispose();
            _allowClose = true;
            Close();
        }
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}








