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
    private sealed record AdvisorCandidate(string Level, int Tuning, string Label, int Healthy, int Total, long? LatencyMs, string Summary, long Score, string Source);
    private sealed record AdvisorStrategy(string Level, int Tuning);
    private sealed record ProviderInfo(string DisplayName, string Key);

    private readonly AppSettingsStore _settingsStore;
    private readonly WindowsStartupService _startupService = new();
    private readonly ExternalComponentService _components;
    private readonly TelegramDesktopProxyService _telegramProxy;
    private readonly ServiceHealthCheckService _healthChecks = new();
    private readonly SystemProxyManager _proxyManager;
    private readonly HostsFileManager _hostsManager;
    private readonly RobloxThumbnailResolver _robloxThumbnailResolver = new();
    private readonly DnsOverrideService _dnsOverrides = new();
    private readonly UserDomainListService _domainLists;
    private readonly GitHubUpdateService _updates = new();
    private readonly PendingUpdateStore _pendingUpdates;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly TrayMenuWindow _trayMenu;
    private readonly HttpClient _advisorHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _applicationLogPath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _protectionGate = new(1, 1);
    private readonly Dictionary<string, ServiceStatus> _serviceStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServiceHealthResult> _healthStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _healthFailureStreaks = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings = new();
    private BypassProfileSettings _activeProfiles = new();
    private HostsFileStatus _hostsStatus = new(false, 0, null, "hosts не применён");
    private UpdateCheckResult? _lastUpdate;
    private string? _startupUpdateError;
    private bool _updatePromptDismissedThisSession;
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
    private int _healthCheckRunning;
    private bool _advisorRunning;
    private CancellationTokenSource? _advisorCts;
    private CancellationTokenSource? _profileApplyCts;
    private CancellationTokenSource _healthCheckCts = new();
    private bool _advisorStopProtectionRequested;
    private int _startupAddedDefaultExclusions;

    public MainWindow()
    {
        InitializeComponent();
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VortexPass");
        _settingsStore = new AppSettingsStore(appData);
        _pendingUpdates = new PendingUpdateStore(appData);
        _applicationLogPath = Path.Combine(appData, "app.log");
        _components = new ExternalComponentService(AppContext.BaseDirectory);
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
                await SetProtectionAsync(!_protectionRequested);
            },
            ShutdownAsync);
        _trayMenu.UpdateState(false, false);
        _trayIcon.MouseUp += TrayIconMouseUp;
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();
        _advisorHttp.DefaultRequestHeaders.UserAgent.ParseAdd("VortexPass/0.1 (+https://github.com/avov53/VortexPass)");
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
        _trayMenu.UpdateState(_protectionRequested, _protectionTransitioning || _advisorRunning);
        _trayMenu.ShowAt(cursorPoint, workArea);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ResetApplicationLogAsync();
        await LogUpdateCommandLineAsync();
        if (await TryInstallPendingUpdateOnStartupAsync()) return;
        _settings = await _settingsStore.LoadAsync(_lifetime.Token);
        _settings.Profiles ??= new BypassProfileSettings();
        _settings.GameFilter ??= new GameFilterSettings();
        _settings.GameFilter.SelectedGames ??= [];
        _settings.GameFilter.Protocol = WinwsProfileComposer.NormalizeGameProtocol(_settings.GameFilter.Protocol);
        _settings.CustomBypassDomains ??= [];
        _settings.ExcludedDomains ??= DefaultDomainLists.CreateExclusions();
        _startupAddedDefaultExclusions = DefaultDomainLists.MergeDefaultExclusions(_settings.ExcludedDomains);
        _settings.FavoriteProfiles ??= [];
        _settings.FavoriteProfiles = NormalizeFavorites(_settings.FavoriteProfiles);
        _settings.AdvisorHistory ??= [];
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
        if (_startupAddedDefaultExclusions > 0)
        {
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
                bypassTuning = WinwsProfileComposer.NormalizeTuning(_settings.BypassTuning),
                favoriteProfiles = _settings.FavoriteProfiles.Select(profile => new
                {
                    level = WinwsProfileComposer.NormalizeLevel(profile.Level),
                    tuning = WinwsProfileComposer.NormalizeTuning(profile.Tuning)
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
        await SendServiceSnapshotAsync();
        if (!string.IsNullOrWhiteSpace(_startupUpdateError))
            await PostUiAsync(new { type = "update.status", state = "failed", currentVersion = CurrentVersion().ToString(3), error = _startupUpdateError });
        if (_startupAddedDefaultExclusions > 0)
            await LogToUiAsync("info", $"Исключения обновлены: добавлено {_startupAddedDefaultExclusions} доменов для CurseForge/Battle.net.");
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
                    var bypassTuning = root.TryGetProperty("tuning", out var rawTuning) && rawTuning.TryGetInt32(out var tuning) ? tuning : 0;
                    await SetBypassLevelAsync(bypassLevel, bypassTuning);
                    break;
                case "profile.favorite.set":
                    var favoriteLevel = root.TryGetProperty("level", out var rawFavoriteLevel) ? rawFavoriteLevel.GetString() : null;
                    var favoriteTuning = root.TryGetProperty("tuning", out var rawFavoriteTuning) && rawFavoriteTuning.TryGetInt32(out var favoriteTuningValue) ? favoriteTuningValue : 0;
                    var favorite = root.TryGetProperty("favorite", out var rawFavorite) && rawFavorite.ValueKind is JsonValueKind.True;
                    await SetFavoriteProfileAsync(favoriteLevel, favoriteTuning, favorite);
                    break;
                case "profile.launch":
                    var launchLevel = root.TryGetProperty("level", out var rawLaunchLevel) ? rawLaunchLevel.GetString() : null;
                    var launchTuning = root.TryGetProperty("tuning", out var rawLaunchTuning) && rawLaunchTuning.TryGetInt32(out var launchTuningValue) ? launchTuningValue : 0;
                    await LaunchProfileAsync(launchLevel, launchTuning);
                    break;
                case "advisor.run":
                    await RunProfileAdvisorAsync();
                    break;
                case "advisor.stop":
                    StopProfileAdvisor();
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

    private async Task SetProtectionAsync(bool enabled)
    {
        if (!enabled)
        {
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
            await ApplyProfilesAsync();
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
        _advisorCts?.Dispose();
        _advisorCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var advisorToken = _advisorCts.Token;
        var savedLevel = WinwsProfileComposer.NormalizeLevel(_settings.BypassLevel);
        var savedTuning = WinwsProfileComposer.NormalizeTuning(_settings.BypassTuning);
        var wasRunning = _activeProfiles.AnyEnabled && _components.GetStatuses().Any(x => x.Id == "dpi" && x.State == ServiceState.Running);
        var candidates = new List<AdvisorCandidate>();
        ProviderInfo? provider = null;
        try
        {
            await PostUiAsync(new { type = "advisor.status", state = "detecting", progress = 0, message = "Определяем провайдера…" });
            provider = await DetectProviderAsync(advisorToken);
            await LogToUiAsync("info", provider is null
                ? "Провайдер не определён. Подбор продолжится по сетевым проверкам."
                : $"Автоподбор: определён провайдер {provider.DisplayName}.");

            if (wasRunning) await StopProtectionAsync();

            var strategies = new[] { "light", "moderate", "medium", "complex", "aggressive" }
                .SelectMany(level => Enumerable.Range(0, 4).Select(tuning => new AdvisorStrategy(level, tuning)))
                .ToArray();
            strategies = OrderAdvisorStrategies(provider, strategies).ToArray();
            for (var index = 0; index < strategies.Length; index++)
            {
                advisorToken.ThrowIfCancellationRequested();
                var (level, tuning) = strategies[index];
                _settings.BypassLevel = level;
                _settings.BypassTuning = tuning;
                var strategyLabel = $"{LevelDisplayName(level)} · {TuningDisplayName(tuning)}";
                var history = FindAdvisorHistory(provider, level, tuning);
                await PostUiAsync(new
                {
                    type = "advisor.status",
                    state = "testing",
                    provider = provider?.DisplayName,
                    level,
                    tuning,
                    progress = index,
                    total = strategies.Length,
                    message = $"Проверяем стратегию «{strategyLabel}»…",
                    best = candidates.OrderByDescending(item => item.Score).FirstOrDefault()?.Label
                });

                await ApplyProfilesAsync();
                advisorToken.ThrowIfCancellationRequested();
                var running = _components.GetStatuses().Any(x => x.Id == "dpi" && x.State == ServiceState.Running);
                var results = new List<ServiceHealthResult>();
                if (running)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(advisorToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    try
                    {
                        var checks = AdvisorServiceIds()
                            .Select(id => _healthChecks.CheckAsync(
                                id,
                                true,
                                true,
                                timeout.Token,
                                appSelected: id == "telegram" && _settings.Profiles.Telegram,
                                webSelected: id == "telegram" && _settings.Profiles.Web,
                                telegramProxyRunning: id == "telegram" && _telegramProxy.IsRunning));
                        results.AddRange(await Task.WhenAll(checks));
                    }
                    catch (OperationCanceledException) when (!advisorToken.IsCancellationRequested) { }
                }

                var evaluation = EvaluateAdvisorCandidate(running, results, tuning, history);
                var summary = !running
                    ? "winws не запустился"
                    : evaluation.Total == 0
                        ? "winws запускается; для выбранного Game Filter нет HTTP-проверки"
                        : evaluation.Summary;
                var source = history is null ? "full-test" : "provider-history";
                if (history is not null)
                    summary = $"{summary}; история провайдера {history.Healthy}/{history.Total}";
                candidates.Add(new AdvisorCandidate(level, tuning, strategyLabel, evaluation.Healthy, evaluation.Total, evaluation.LatencyMs, summary, evaluation.Score, source));

                if (running || _protectionRequested) await StopProtectionAsync();
                var stableCandidates = candidates.Count(item => item.Total > 0 && item.Healthy == item.Total && item.Score >= 2500);
                if (stableCandidates >= 3 && index >= 5)
                {
                    await LogToUiAsync("info", "Автоподбор: найдено 3 стабильных кандидата, оставшиеся стратегии пропущены для ускорения.");
                    break;
                }
            }

            var best = candidates.OrderByDescending(item => item.Score).Take(3).Select(item => new
            {
                level = item.Level,
                tuning = item.Tuning,
                label = item.Label,
                healthy = item.Healthy,
                total = item.Total,
                latencyMs = item.LatencyMs,
                summary = item.Summary,
                score = item.Score,
                source = item.Source
            }).ToArray();
            UpdateAdvisorHistory(provider, candidates);
            await PostUiAsync(new { type = "advisor.status", state = "complete", provider = provider?.DisplayName, candidates = best });
            await LogToUiAsync("ok", $"Автоматический подбор завершён{(provider is null ? "" : $" для {provider.DisplayName}")}. Выберите один из предложенных уровней вручную.");
        }
        catch (OperationCanceledException) when (advisorToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            await PostUiAsync(new { type = "advisor.status", state = "failed", provider = provider?.DisplayName, error = "Автоподбор остановлен пользователем." });
            await LogToUiAsync("warn", "Автоматический подбор остановлен. Предыдущий профиль будет восстановлен.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await PostUiAsync(new { type = "advisor.status", state = "failed", provider = provider?.DisplayName, error = ex.Message });
            await LogToUiAsync("error", $"Автоматический подбор не завершён: {ex.Message}");
        }
        finally
        {
            _settings.BypassLevel = savedLevel;
            _settings.BypassTuning = savedTuning;
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            if (!_shutdownStarted && wasRunning && !_advisorStopProtectionRequested)
            {
                try { await ApplyProfilesAsync(); }
                catch (Exception ex) { await LogToUiAsync("error", $"Не удалось восстановить прежний профиль после диагностики: {ex.Message}"); }
            }
            else if (!_shutdownStarted && !_advisorStopProtectionRequested)
            {
                try { await StopProtectionAsync(); }
                catch (Exception ex) { await LogToUiAsync("error", $"Не удалось остановить временный профиль после диагностики: {ex.Message}"); }
            }
            _advisorRunning = false;
            _advisorStopProtectionRequested = false;
            _advisorCts?.Dispose();
            _advisorCts = null;
            await PublishProfileStatusAsync();
            await SendServiceSnapshotAsync();
        }
    }

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
        if (_settings.Profiles.Telegram || _settings.Profiles.Web) yield return "telegram";
        if (_settings.Profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("roblox", StringComparison.OrdinalIgnoreCase))) yield return "roblox";
        if (_settings.Profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase))) yield return "phasmophobia";
    }

    private async Task<ProviderInfo?> DetectProviderAsync(CancellationToken cancellationToken)
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
            if (string.IsNullOrWhiteSpace(result)) return null;
            var key = NormalizeProviderKey(asn) ?? NormalizeProviderKey(organization) ?? result.ToLowerInvariant();
            return new ProviderInfo(result, key);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
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

    private async Task StopProtectionAsync()
    {
        CancelPendingProfileWork();
        await _protectionGate.WaitAsync();
        try
        {
            _protectionTransitioning = true;
            _protectionRequested = false;
            await SendServiceSnapshotAsync();
            await _components.StopAllAsync();
            await _telegramProxy.StopAsync();
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

    private async Task SetBypassLevelAsync(string? level, int tuning = 0)
    {
        _settings.BypassLevel = WinwsProfileComposer.NormalizeLevel(level);
        _settings.BypassTuning = WinwsProfileComposer.NormalizeTuning(tuning);
        await _settingsStore.SaveAsync(_settings);
        await LogToUiAsync("info", $"Выбран общий уровень обхода: {LevelDisplayName(_settings.BypassLevel)} · {TuningDisplayName(_settings.BypassTuning)}.");
        await PublishProfileStatusAsync();
    }

    private async Task SetFavoriteProfileAsync(string? level, int tuning, bool favorite)
    {
        var normalizedLevel = WinwsProfileComposer.NormalizeLevel(level);
        var normalizedTuning = WinwsProfileComposer.NormalizeTuning(tuning);
        var existing = _settings.FavoriteProfiles.FirstOrDefault(profile =>
            profile.Level.Equals(normalizedLevel, StringComparison.OrdinalIgnoreCase) &&
            profile.Tuning == normalizedTuning);

        if (favorite && existing is null)
        {
            if (_settings.FavoriteProfiles.Count >= 5)
            {
                await LogToUiAsync("warn", "В избранное можно добавить не больше пяти профилей.");
                await PublishProfileStatusAsync();
                return;
            }

            _settings.FavoriteProfiles.Add(new FavoriteProfileSettings { Level = normalizedLevel, Tuning = normalizedTuning });
            await LogToUiAsync("ok", $"Профиль «{ProfileDisplayName(normalizedLevel, normalizedTuning)}» добавлен на дашборд.");
        }
        else if (!favorite && existing is not null)
        {
            _settings.FavoriteProfiles.Remove(existing);
            await LogToUiAsync("info", $"Профиль «{ProfileDisplayName(normalizedLevel, normalizedTuning)}» удалён с дашборда.");
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

    private async Task LaunchProfileAsync(string? level, int tuning)
    {
        if (_advisorRunning || _protectionTransitioning)
        {
            await LogToUiAsync("warn", "Дождитесь завершения текущей операции с защитой.");
            return;
        }

        await SetBypassLevelAsync(level, tuning);
        await LogToUiAsync("info", $"Быстрый запуск профиля «{ProfileDisplayName(_settings.BypassLevel, _settings.BypassTuning)}».");
        await SetProtectionAsync(true);
    }

    private Task PublishProfileStatusAsync() => PostUiAsync(new
    {
        type = "profile.status",
        level = _settings.BypassLevel,
        tuning = _settings.BypassTuning,
        favoriteProfiles = _settings.FavoriteProfiles.Select(profile => new
        {
            level = WinwsProfileComposer.NormalizeLevel(profile.Level),
            tuning = WinwsProfileComposer.NormalizeTuning(profile.Tuning)
        })
    });

    private static List<FavoriteProfileSettings> NormalizeFavorites(IEnumerable<FavoriteProfileSettings> favorites)
    {
        return favorites
            .Where(profile => profile is not null)
            .Select(profile => new FavoriteProfileSettings
            {
                Level = WinwsProfileComposer.NormalizeLevel(profile.Level),
                Tuning = WinwsProfileComposer.NormalizeTuning(profile.Tuning)
            })
            .DistinctBy(profile => $"{profile.Level}:{profile.Tuning}", StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
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
            try { _healthCheckCts.Cancel(); }
            catch (ObjectDisposedException) { }
            _healthCheckCts = new CancellationTokenSource();
            _profileApplyCts?.Dispose();
            _profileApplyCts = _advisorRunning && _advisorCts is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _advisorCts.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var operationToken = _profileApplyCts.Token;
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
                var runtimeLists = await _domainLists.WriteAsync(_settings.CustomBypassDomains, _settings.ExcludedDomains, operationToken);
                var arguments = WinwsProfileComposer.Compose(
                    _settings.Profiles,
                    _settings.BypassLevel,
                    _settings.GameFilter,
                    runtimeLists.BypassPath,
                    runtimeLists.ExclusionPath,
                    _settings.BypassTuning);
                await LogToUiAsync("info", $"Применение уровня {LevelDisplayName(_settings.BypassLevel)} · {TuningDisplayName(_settings.BypassTuning)}. Сервисы: {string.Join(", ", ActiveProfileNames())}.");
                await LogToUiAsync("info", $"Пользовательские списки: обход — {runtimeLists.BypassCount}, исключения — {runtimeLists.ExclusionCount}.");
                await _components.StopAllAsync();
                await _telegramProxy.StopAsync();
                _components.SetArguments("dpi", arguments);
                await PostUiAsync(new { type = "log", level = "info", message = "Запуск компонентов обхода…" });

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
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
                    // Publish the running state before DNS/hosts preparation. This keeps
                    // the Stop button available while optional network probes are pending.
                    await SendServiceSnapshotAsync();
                    if (_activeProfiles.Telegram)
                    {
                        try
                        {
                            await _telegramProxy.StartAsync(operationToken);
                            await LogToUiAsync("ok", $"Локальный Telegram Desktop WS Proxy v{TelegramDesktopProxyService.Version} запущен на 127.0.0.1:{TelegramDesktopProxyService.Port}. Ожидается подключение Telegram Desktop.");
                            if (_telegramProxy.ShouldOfferConfiguration)
                            {
                                _telegramProxy.OpenTelegramConfiguration();
                                await LogToUiAsync("info", "Telegram открыт впервые. Нажмите «Подключить», чтобы Desktop использовал локальный WebSocket-маршрут VortexPass.");
                            }
                        }
                        catch (Exception ex)
                        {
                            await LogToUiAsync("error", $"Telegram Desktop proxy не запущен: {ex.Message}");
                        }
                    }
                    else
                    {
                        await _telegramProxy.StopAsync();
                    }
                    var hostsOverrides = await CreateHostsOverridesAsync(_activeProfiles, operationToken);
                    if (RequiresHosts(_activeProfiles) || hostsOverrides.Count != 0)
                    {
                        try
                        {
                            _hostsStatus = await _hostsManager.ApplyAsync(hostsOverrides, operationToken);
                            var overrideDetail = hostsOverrides.Count == 0 ? string.Empty : $"; временных DNS-записей: {hostsOverrides.Count}";
                            var hostsDetail = string.IsNullOrWhiteSpace(_hostsStatus.Detail) ? string.Empty : $"; {_hostsStatus.Detail}";
                            await LogToUiAsync("ok", $"Системный hosts применён: {_hostsStatus.EntryCount} записей, источник Flowseal {WinwsProfileComposer.FlowsealCommit[..7]}{overrideDetail}{hostsDetail}.");
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
                    if (_activeProfiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase)))
                        await LogToUiAsync("info", "Phasmophobia: игровой UDP оставлен прямым для стабильности сессии; VortexPass обрабатывает только домены Photon/Unity/Vivox/Steam.");
                }
            }
            else
            {
                await _components.StopAllAsync();
                await _telegramProxy.StopAsync();
                await RestoreHostsCoreAsync();
                _proxyManager.RestoreIfOwned();
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

    private IEnumerable<string> ActiveProfileNames()
    {
        if (_settings.Profiles.YouTube) yield return "YouTube";
        if (_settings.Profiles.Discord) yield return "Discord";
        if (_settings.Profiles.Telegram) yield return "Telegram";
        if (_settings.Profiles.Web) yield return "Web";
        if (_settings.Profiles.GameFilter) yield return $"Game Filter ({_settings.GameFilter.Protocol.ToUpperInvariant()})";
        if (_settings.Profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase)))
            yield return "Phasmophobia stability-first";
    }

    private async Task<IReadOnlyList<HostsOverride>> CreateHostsOverridesAsync(BypassProfileSettings profiles, CancellationToken cancellationToken)
    {
        var overrides = new List<HostsOverride>();
        if (profiles.YouTube || profiles.Web)
            overrides.AddRange(await CreateYoutubeHostsOverridesAsync(cancellationToken));

        var robloxSelected = profiles.GameFilter && _settings.GameFilter.SelectedGames
            .Any(game => game.Equals("roblox", StringComparison.OrdinalIgnoreCase));
        if (robloxSelected)
        {
            var resolution = await _robloxThumbnailResolver.ResolveAsync(cancellationToken);
            if (!resolution.Succeeded)
            {
                await LogToUiAsync("warn", $"Roblox CDN: не удалось получить временный DNS для {RobloxThumbnailResolver.ThumbnailHost}: {resolution.Detail}. Правила winws всё равно применены.");
            }
            else
            {
                await LogToUiAsync("ok", $"Roblox CDN: временный DNS для {RobloxThumbnailResolver.ThumbnailHost} подтверждён ({resolution.Detail}).");
                overrides.Add(new HostsOverride(resolution.Address!, RobloxThumbnailResolver.ThumbnailHost));
            }
        }

        return overrides;
    }

    private async Task<IReadOnlyList<HostsOverride>> CreateYoutubeHostsOverridesAsync(CancellationToken cancellationToken)
    {
        var hosts = new[]
        {
            "www.youtube.com",
            "suggestqueries.google.com",
            "suggestqueries-clients6.youtube.com",
            "youtubei.googleapis.com",
            "i.ytimg.com"
        };
        var tasks = hosts.Select(async host =>
        {
            var probeUrl = host switch
            {
                "www.youtube.com" => "https://www.youtube.com/generate_204",
                "i.ytimg.com" => "https://i.ytimg.com/generate_204",
                "s.ytimg.com" => "https://s.ytimg.com/",
                "yt3.ggpht.com" => "https://yt3.ggpht.com/",
                _ => null
            };
            var resolution = await _dnsOverrides.ResolveBestAsync(
                host,
                verifyTls: true,
                httpProbeUrl: probeUrl,
                timeout: TimeSpan.FromSeconds(2),
                cancellationToken: cancellationToken);
            if (!resolution.Succeeded)
            {
                await LogToUiAsync("warn", $"YouTube DNS: не удалось закрепить {host}: {resolution.Detail}.");
                return null;
            }

            await LogToUiAsync("info", $"YouTube DNS: {host} → {resolution.Address} через {resolution.Source}.");
            return new HostsOverride(resolution.Address!, host);
        }).ToArray();
        var overrides = (await Task.WhenAll(tasks)).Where(item => item is not null).Cast<HostsOverride>().ToList();

        if (overrides.Count != 0)
            await LogToUiAsync("ok", $"YouTube DNS: закреплено {overrides.Count} доменов для системного DNS Windows.");
        else
            await LogToUiAsync("warn", "YouTube DNS: не удалось подготовить временные hosts-записи; winws всё равно запущен.");

        return overrides;
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
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => UpdateTrayState(protectionEnabled));
            return;
        }
        if (_shutdownStarted) return;

        _trayIcon.Text = protectionEnabled ? "VortexPass — защита включена" : "VortexPass — защита выключена";
        _trayMenu.UpdateState(protectionEnabled, _protectionTransitioning || _advisorRunning);
    }

    private async Task RunMetricsLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var counters = ReadNetworkCounters();
            var active = _protectionStarted.HasValue;
            if (active && !_advisorRunning && DateTimeOffset.UtcNow >= _nextHealthCheckUtc)
            {
                _nextHealthCheckUtc = DateTimeOffset.UtcNow.AddSeconds(30);
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
        if (Interlocked.Exchange(ref _healthCheckRunning, 1) != 0) return;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _healthCheckCts.Token);
            await RunHealthChecksAsync(linked.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
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
            _healthChecks.CheckAsync("discord", profiles.Discord || profiles.Web, winwsRunning, cancellationToken),
            _healthChecks.CheckAsync("telegram", profiles.Telegram || profiles.Web, winwsRunning, cancellationToken, profiles.Telegram, profiles.Web, _telegramProxy.IsRunning, _telegramProxy.HasTelegramClientActivity)
        };
        if (profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("roblox", StringComparison.OrdinalIgnoreCase)))
            checks.Add(_healthChecks.CheckAsync("roblox", true, winwsRunning, cancellationToken));
        if (profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase)))
            checks.Add(_healthChecks.CheckAsync("phasmophobia", true, winwsRunning, cancellationToken));
        var results = await Task.WhenAll(checks);
        if (generation != Volatile.Read(ref _healthGeneration)) return;
        foreach (var result in results)
        {
            if (result.IsHealthy)
            {
                _healthFailureStreaks.Remove(result.Id);
            }
            else if (_healthStatuses.TryGetValue(result.Id, out var lastHealthy) && lastHealthy.IsHealthy &&
                     result.State is not ServiceHealthState.NotSelected and not ServiceHealthState.WinwsStopped)
            {
                var failures = _healthFailureStreaks.GetValueOrDefault(result.Id) + 1;
                _healthFailureStreaks[result.Id] = failures;
                if (failures < 2)
                    continue;
            }
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
        foreach (var id in new[] { "youtube", "discord", "telegram", "phasmophobia" })
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
            "phasmophobia" => profiles.GameFilter && _settings.GameFilter.SelectedGames.Any(game => game.Equals("phasmophobia", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static string ServiceDisplayName(string id) => id switch { "youtube" => "YouTube", "discord" => "Discord", "telegram" => "Telegram", "roblox" => "Roblox", "phasmophobia" => "Phasmophobia", _ => id };

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

    private static string TuningDisplayName(int tuning) => WinwsProfileComposer.NormalizeTuning(tuning) switch
    {
        0 => "Стандарт",
        1 => "Усиленный +2",
        2 => "Усиленный +4",
        3 => "Максимум +6",
        _ => "Стандарт"
    };

    private static string ProfileDisplayName(string level, int tuning) =>
        $"{LevelDisplayName(WinwsProfileComposer.NormalizeLevel(level))} · {TuningDisplayName(tuning)}";

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
        await PostUiAsync(new { type = "update.status", state = "checking", currentVersion = CurrentVersion().ToString(3) });
        _lastUpdate = await _updates.CheckAsync(CurrentVersion(), _lifetime.Token);
        _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        await _settingsStore.SaveAsync(_settings);
        var state = _lastUpdate.State switch { UpdateState.Current => "current", UpdateState.Available => "availablePrompt", _ => "failed" };
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
            _trayMenu.Hide();
            _trayMenu.Close();
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
            await _telegramProxy.StopAsync();
            await RestoreHostsCoreAsync();
            _proxyManager.RestoreIfOwned();
        }
        finally
        {
            _components.Dispose();
            _telegramProxy.Dispose();
            _advisorHttp.Dispose();
            _allowClose = true;
            Close();
        }
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
