using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using VortexPass.App.Infrastructure;
using VortexPass.App.Services;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

namespace VortexPass.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string _applicationDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VortexPass");
    private readonly AppSettingsStore _settingsStore;
    private readonly ExternalComponentService _components;
    private readonly GitHubUpdateService _updates = new();
    private readonly WindowsStartupService _startup = new();
    private readonly SystemProxyManager _proxy;
    private readonly TemporaryUpdaterLauncher _updater;
    private AppSettings _settings = new();
    private string _currentPage = "dashboard";
    private bool _isProtectionEnabled;
    private bool _isTelegramWebEnabled;
    private bool _startWithWindows;
    private bool _minimizeToTray = true;
    private UpdateState _updateState;
    private ReleaseInfo? _latestRelease;
    private string? _updateError;
    private ServiceStatus _youtube = new("dpi", "YouTube", ServiceState.Stopped, "Ожидание запуска");
    private ServiceStatus _discord = new("dpi", "Discord", ServiceState.Stopped, "Ожидание запуска");
    private ServiceStatus _telegram = new("telegram", "Telegram", ServiceState.Stopped, "Ожидание запуска");

    public MainViewModel()
    {
        _settingsStore = new AppSettingsStore(_dataDirectory);
        _components = new ExternalComponentService(_applicationDirectory);
        _components.StatusChanged += OnComponentStatusChanged;
        _proxy = new SystemProxyManager(_dataDirectory);
        _updater = new TemporaryUpdaterLauncher(_applicationDirectory);
        ToggleProtectionCommand = new AsyncRelayCommand(ToggleProtectionAsync);
        CheckOrInstallUpdateCommand = new AsyncRelayCommand(CheckOrInstallUpdateAsync);
        ApplyTelegramProxyCommand = new RelayCommand(ApplyTelegramProxy);
        SelectDashboardCommand = new RelayCommand(() => CurrentPage = "dashboard");
        SelectYouTubeCommand = new RelayCommand(() => CurrentPage = "youtube");
        SelectTelegramCommand = new RelayCommand(() => CurrentPage = "telegram");
        SelectSettingsCommand = new RelayCommand(() => CurrentPage = "settings");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? UpdateReadyToInstall;
    public ObservableCollection<ActivityLogEntry> Logs { get; } = [];

    public AsyncRelayCommand ToggleProtectionCommand { get; }
    public AsyncRelayCommand CheckOrInstallUpdateCommand { get; }
    public RelayCommand ApplyTelegramProxyCommand { get; }
    public RelayCommand SelectDashboardCommand { get; }
    public RelayCommand SelectYouTubeCommand { get; }
    public RelayCommand SelectTelegramCommand { get; }
    public RelayCommand SelectSettingsCommand { get; }

    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!SetField(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(IsDashboard));
            OnPropertyChanged(nameof(IsYouTube));
            OnPropertyChanged(nameof(IsTelegram));
            OnPropertyChanged(nameof(IsSettings));
        }
    }

    public bool IsDashboard => CurrentPage == "dashboard";
    public bool IsYouTube => CurrentPage == "youtube";
    public bool IsTelegram => CurrentPage == "telegram";
    public bool IsSettings => CurrentPage == "settings";

    public bool IsProtectionEnabled
    {
        get => _isProtectionEnabled;
        set => SetField(ref _isProtectionEnabled, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetField(ref _startWithWindows, value)) return;
            _startup.SetEnabled(value, Path.Combine(_applicationDirectory, "VortexPass.exe"));
            _ = SaveSettingsAsync();
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!SetField(ref _minimizeToTray, value)) return;
            _ = SaveSettingsAsync();
        }
    }

    public bool IsTelegramWebEnabled
    {
        get => _isTelegramWebEnabled;
        set
        {
            if (!SetField(ref _isTelegramWebEnabled, value)) return;
            try
            {
                if (value)
                {
                    var pacPath = Path.Combine(_dataDirectory, "telegram.pac");
                    File.WriteAllText(pacPath, "function FindProxyForURL(url, host) { if (shExpMatch(host, '*.telegram.org') || host == 'telegram.org') return 'SOCKS5 127.0.0.1:1080; DIRECT'; return 'DIRECT'; }");
                    _proxy.ApplyPac(new Uri(pacPath));
                    AddLog("OK", "PAC-маршрутизация Telegram Web включена.");
                }
                else
                {
                    _proxy.RestoreIfOwned();
                    AddLog("INFO", "PAC-маршрутизация Telegram Web отключена.");
                }
            }
            catch (Exception ex)
            {
                _isTelegramWebEnabled = false;
                OnPropertyChanged();
                AddLog("ERR", $"Не удалось изменить системный прокси: {ex.Message}");
            }
        }
    }

    public ServiceStatus Youtube { get => _youtube; private set { if (SetField(ref _youtube, value)) RefreshProtectionStatus(); } }
    public ServiceStatus Discord { get => _discord; private set { if (SetField(ref _discord, value)) RefreshProtectionStatus(); } }
    public ServiceStatus Telegram { get => _telegram; private set { if (SetField(ref _telegram, value)) RefreshProtectionStatus(); } }
    public bool YoutubeRunning => Youtube.State == ServiceState.Running;
    public bool DiscordRunning => Discord.State == ServiceState.Running;
    public bool TelegramRunning => Telegram.State == ServiceState.Running;
    public bool ProtectionRunning => YoutubeRunning && TelegramRunning;
    public string ProtectionTitle => ProtectionRunning ? "Защита активна" : IsProtectionEnabled ? "Требуется настройка" : "Запустить защиту";
    public string ProtectionDetail => ProtectionRunning ? "DPI-Bypass · SOCKS5 · PAC — работают" : IsProtectionEnabled ? "Проверьте журнал и проверенные файлы компонентов" : "Нажмите, чтобы включить службы";

    public string VersionText => $"v{CurrentVersion}";
    public string RepositoryText => "github.com/avov53/VortexPass";
    public string LatestVersionText => _latestRelease is null ? "Проверяем GitHub Releases" : $"Последний релиз: {_latestRelease.Tag}";
    public string LastUpdateCheckText => _settings.LastUpdateCheckUtc is null ? "Ещё не проверялось" : $"Проверено: {_settings.LastUpdateCheckUtc.Value.LocalDateTime:g}";
    public string UpdateButtonText => _updateState switch
    {
        UpdateState.Checking => "Проверка обновлений GitHub…",
        UpdateState.Downloading => "Скачивание обновления…",
        UpdateState.Available when _latestRelease is not null => $"Скачать {_latestRelease.Tag} с GitHub",
        UpdateState.Current => "У вас актуальная версия",
        _ => "Проверить обновления GitHub"
    };
    public string UpdateStatusText => _updateState switch
    {
        UpdateState.Current => "Установлена последняя опубликованная версия",
        UpdateState.Available when _latestRelease is not null => $"Доступна новая версия {_latestRelease.Tag}",
        UpdateState.Downloading => "Пакет скачивается и проверяется",
        UpdateState.Failed => _updateError ?? "Не удалось проверить обновления",
        _ => "Проверка выполняется автоматически после запуска"
    };
    public bool UpdateButtonEnabled => _updateState is not UpdateState.Checking and not UpdateState.Downloading and not UpdateState.Current;

    public async Task InitializeAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        _startWithWindows = _settings.StartWithWindows;
        _minimizeToTray = _settings.MinimizeToTray;
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(MinimizeToTray));
        _proxy.RestoreIfOwned();
        await _components.LoadManifestAsync();
        AddLog("INFO", "VortexPass готов. Ожидание запуска компонентов.");
        foreach (var status in _components.GetStatuses()) OnComponentStatusChanged(this, status);
        await CheckForUpdatesAsync();
    }

    public async Task ShutdownAsync()
    {
        if (IsTelegramWebEnabled) _proxy.RestoreIfOwned();
        await _components.StopAllAsync();
        await SaveSettingsAsync();
    }

    private async Task ToggleProtectionAsync()
    {
        if (IsProtectionEnabled)
        {
            IsProtectionEnabled = false;
            await _components.StopAllAsync();
            if (IsTelegramWebEnabled)
                IsTelegramWebEnabled = false;
            else
                _proxy.RestoreIfOwned();
            AddLog("INFO", "Службы остановлены пользователем.");
        }
        else
        {
            IsProtectionEnabled = true;
            AddLog("INFO", "Проверяем компоненты и запускаем службы…");
            await _components.StartAllAsync();
        }
        RefreshProtectionStatus();
    }

    private async Task CheckOrInstallUpdateAsync()
    {
        if (_updateState == UpdateState.Available && _latestRelease is not null)
        {
            try
            {
                _updateState = UpdateState.Downloading;
                RefreshUpdateProperties();
                var release = await _updates.DownloadAsync(_latestRelease);
                AddLog("OK", $"Обновление {_latestRelease.Tag} проверено. Запускаем installer.");
                _updater.Launch(release);
                UpdateReadyToInstall?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _updateState = UpdateState.Failed;
                _updateError = ex.Message;
                AddLog("ERR", ex.Message);
                RefreshUpdateProperties();
            }
            return;
        }
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        _updateState = UpdateState.Checking;
        _updateError = null;
        RefreshUpdateProperties();
        var result = await _updates.CheckAsync(CurrentVersion);
        _updateState = result.State;
        _latestRelease = result.Release;
        _updateError = result.Error;
        _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        await SaveSettingsAsync();
        AddLog(result.State == UpdateState.Failed ? "ERR" : "INFO", result.State == UpdateState.Failed ? result.Error! : $"Проверен GitHub Releases: {result.Release?.Tag ?? "без релиза"}.");
        RefreshUpdateProperties();
    }

    private void ApplyTelegramProxy()
    {
        try
        {
            Process.Start(new ProcessStartInfo("tg://proxy?server=127.0.0.1&port=1080") { UseShellExecute = true });
            AddLog("INFO", "Открыта настройка прокси в Telegram Desktop.");
        }
        catch (Exception ex) { AddLog("ERR", $"Не удалось открыть Telegram: {ex.Message}"); }
    }

    private void OnComponentStatusChanged(object? sender, ServiceStatus status)
    {
        void Update()
        {
            if (status.Id.Equals("dpi", StringComparison.OrdinalIgnoreCase))
            {
                Youtube = status with { DisplayName = "YouTube" };
                Discord = status with { DisplayName = "Discord" };
            }
            else if (status.Id.Equals("telegram", StringComparison.OrdinalIgnoreCase)) Telegram = status;
            AddLog(status.State == ServiceState.Error ? "ERR" : status.State == ServiceState.Running ? "OK" : "INFO", $"{status.DisplayName}: {status.Detail ?? TranslateStatus(status.State)}");
        }
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true) Update();
        else System.Windows.Application.Current?.Dispatcher.Invoke(Update);
    }

    private void RefreshProtectionStatus()
    {
        OnPropertyChanged(nameof(YoutubeRunning));
        OnPropertyChanged(nameof(DiscordRunning));
        OnPropertyChanged(nameof(TelegramRunning));
        OnPropertyChanged(nameof(ProtectionRunning));
        OnPropertyChanged(nameof(ProtectionTitle));
        OnPropertyChanged(nameof(ProtectionDetail));
    }

    private void RefreshUpdateProperties()
    {
        OnPropertyChanged(nameof(LatestVersionText));
        OnPropertyChanged(nameof(LastUpdateCheckText));
        OnPropertyChanged(nameof(UpdateButtonText));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(UpdateButtonEnabled));
        CheckOrInstallUpdateCommand.RaiseCanExecuteChanged();
    }

    private async Task SaveSettingsAsync()
    {
        _settings.StartWithWindows = StartWithWindows;
        _settings.MinimizeToTray = MinimizeToTray;
        await _settingsStore.SaveAsync(_settings);
        OnPropertyChanged(nameof(LastUpdateCheckText));
    }

    private void AddLog(string level, string message)
    {
        Logs.Add(new ActivityLogEntry(DateTime.Now.ToString("HH:mm:ss"), level, message));
        while (Logs.Count > 80) Logs.RemoveAt(0);
    }

    private static string TranslateStatus(ServiceState state) => state switch
    {
        ServiceState.Starting => "запускается",
        ServiceState.Running => "работает",
        ServiceState.Stopping => "останавливается",
        _ => "остановлен"
    };

    private static Version CurrentVersion
    {
        get
        {
            var value = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
            return Version.TryParse(value, out var version) ? version : new Version(0, 1, 0);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    public void Dispose() => _components.Dispose();
}

public sealed record ActivityLogEntry(string Time, string Level, string Message);
