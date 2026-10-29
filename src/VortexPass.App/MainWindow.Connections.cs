using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

namespace VortexPass.App;
public partial class MainWindow
{
    private SavedConnectionProfile? _activeConnection;
    private WarpSettings? _connectionOriginalWarp;
    private bool _connectionWarpWasConnected;
    private WarpNativeConfiguration? _connectionNativeConfiguration;
    private string _connectionOriginalBootstrapMode="default";
    private bool _connectionLaunching;
    private Uri? GetTelegramWebProxyUri()=>_warpStatus.State==WarpConnectionState.Connected?_warpPac.HttpProxyUri:null;
    private async Task<ServiceHealthResult> CheckTelegramWarpHealthAsync(CancellationToken token)
    {
        var proxy=GetTelegramWebProxyUri();if(proxy is null)return new("telegram",ServiceHealthState.Partial,"Выборочный WARP недоступен; прямой резерв не включается",ContentConfirmed:false);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try{return await TelegramWarpProbe.CheckAsync(proxy,deadline.Token);}
        catch(OperationCanceledException)when(!token.IsCancellationRequested){return new("telegram",ServiceHealthState.Partial,"Проверка WARP не завершилась за 8 секунд",ContentConfirmed:false);}
    }
    private bool IsProxyOnlyProfile()
    {
        var p=ProfilesForDpi();return _settings.TelegramWebRoute=="warpProxy"&&_settings.Profiles.TelegramWeb&&
            !p.YouTube&&!p.Discord&&!p.Telegram&&!p.Web&&!p.GameFilter&&_settings.CustomBypassDomains.Count==0&&_settings.CurrentSiteRules.All(r=>r.Connection=="warpProxy");
    }
    private bool ProtectionRouteRunning()=>_components.GetStatuses().Any(s=>s.Id=="dpi"&&s.State==ServiceState.Running)||
        _protectionRequested&&_warpStatus.State==WarpConnectionState.Connected&&_warpPac.PacUri is not null;
    private BypassProfileSettings ProfilesForDpi()
    {
        var profiles=CloneProfiles(_settings.Profiles);
        if(_settings.TelegramWebRoute=="warpProxy")profiles.TelegramWeb=false;
        return profiles;
    }
    private async Task PrepareConnectionWarpAsync(CancellationToken token)
    {
        var rules=SiteRulePolicy.Validate(TelegramWebPolicy.Rules(_settings.CurrentSiteRules,_settings.TelegramWebRoute,_settings.Profiles.TelegramWeb)).Where(r=>r.Connection is "warpProxy" or "warpSystem").ToArray();
        if(rules.Length==0){await RestoreConnectionWarpAsync(token);return;}
        var status=await _warpService.GetStatusAsync(token);
        _warpStatus=status;
        await _warpService.ValidateConsumerAsync(token);
        _proxyManager.EnsureCanApply();
        if(status.State==WarpConnectionState.Connected&&!rules.Any(r=>r.Connection=="warpSystem")&&GetEffectiveWarpRoutingMode()=="full")
            throw new InvalidOperationException("Активен полный туннель WARP. Выборочный профиль не переключает его автоматически; сначала отключите полное подключение.");
        if(status.State==WarpConnectionState.Connected&&_connectionOriginalWarp is null&&!_settings.Warp.RequestedConnected)
            throw new InvalidOperationException("Уже активно другое подключение WARP. Отключите его перед подключением профиля VortexPass.");
        if(status.CliPath is null||!status.Registered)throw new InvalidOperationException("Для выбранных правил установите и зарегистрируйте официальный WARP во вкладке WARP.");
        if(_connectionOriginalWarp is null)
        {
            _connectionOriginalWarp=JsonSerializer.Deserialize<WarpSettings>(JsonSerializer.Serialize(_settings.Warp))!;
            _connectionWarpWasConnected=status.State==WarpConnectionState.Connected;
            _connectionNativeConfiguration=await _warpService.CaptureNativeConfigurationAsync(token);
            _connectionOriginalBootstrapMode=_warpBootstrapMode;
            _settings.PendingConnectionWarpRestore=new(_connectionOriginalWarp,_connectionWarpWasConnected,_connectionNativeConfiguration,_connectionOriginalBootstrapMode);
        }
        await _settingsStore.SaveAsync(_settings,token);
        if(status.State==WarpConnectionState.Connected)await SetWarpConnectedAsync(false,token,runDiagnostics:false);
        _settings.Warp.RoutingMode=rules.Any(r=>r.Connection=="warpSystem")?"full":"smart";
        _settings.Warp.Telegram=false;
        _settings.Warp.RouteProfiles.RemoveAll(route=>route.Id.StartsWith("connection-rule-",StringComparison.Ordinal));
        foreach(var route in _settings.Warp.RouteProfiles)route.Enabled=false;
        foreach(var rule in rules)_settings.Warp.RouteProfiles.Add(new WarpRouteProfile
            {Id="connection-rule-"+rule.Id,Name=rule.Name,Kind="site",Enabled=true,Connection=rule.Connection,Domains=SiteRulePolicy.Domains(rule).ToList()});
        await SetWarpConnectedAsync(true,token,runDiagnostics:false);
        if(_warpStatus.State!=WarpConnectionState.Connected)throw new InvalidOperationException("WARP для выбранных правил не подключился: "+_warpStatus.Detail);
        if(_settings.Warp.RoutingMode=="full")await LogToUiAsync("info","Выбран системный WARP: туннель действует на весь компьютер; прямые правила не являются исключениями из него.");
    }
    private async Task RestoreConnectionWarpAsync(CancellationToken token=default)
    {
        if(_connectionOriginalWarp is null && _settings.PendingConnectionWarpRestore is { } pending)
        {_connectionOriginalWarp=pending.Settings;_connectionWarpWasConnected=pending.WasConnected;_connectionNativeConfiguration=pending.Configuration;_connectionOriginalBootstrapMode=pending.BootstrapMode;}
        if(_connectionOriginalWarp is not { } original)return;
        var connected=_connectionWarpWasConnected;
        await SetWarpConnectedAsync(false,token,runDiagnostics:false);
        original.VerifiedVariants=_settings.Warp.VerifiedVariants;
        _settings.Warp=original;
        if(_connectionNativeConfiguration is { } native)
        {
            _warpBootstrapMode=_connectionOriginalBootstrapMode;
            if(connected&&WarpPreflight.RequiresBootstrap(_warpBootstrapMode))await _warpBootstrap.StartAsync(WinwsProfileComposer.ComposeWarpBootstrap(_warpBootstrapMode),_protectionRequested,token);
            await _warpService.RestoreNativeConfigurationAsync(native,connected,token);
            _warpStatus=await _warpService.GetStatusAsync(token);
            if(connected)await ApplyWarpRoutingAsync(token);
        }
        else if(connected)await SetWarpConnectedAsync(true,token,runDiagnostics:false);
        if(connected&&_warpStatus.State!=WarpConnectionState.Connected)throw new InvalidOperationException("Не удалось восстановить исходное подключение WARP; снимок сохранён для повторной попытки.");
        _settings.PendingConnectionWarpRestore=null;
        _connectionOriginalWarp=null;_connectionNativeConfiguration=null;
        await _settingsStore.SaveAsync(_settings);
    }
    private void MigrateSavedConnections()
    {
        _settings.SavedConnections ??= [];
        if (_settings.ConnectionsMigrationVersion >= 2) return;
        // Only existing user bookmarks are migrated. A fresh installation has no seeded profiles.
        if (_settings.ConnectionsMigrationVersion < 1 && _settings.SavedConnections.Count == 0)
            foreach (var favorite in _settings.FavoriteProfiles)
                _settings.SavedConnections.Add(new()
                {
                    Id = Guid.NewGuid().ToString("D"), Name = $"{ProfileDisplayName(favorite.Level, favorite.Tuning, favorite.Mode)}",
                    Services = SavedConnectionProfilePolicy.SelectedServices(_settings.Profiles),
                    Games = [.. _settings.GameFilter.SelectedGames], Domains = [.. _settings.CustomBypassDomains],
                    Level = favorite.Level, Tuning = favorite.Tuning, Mode = favorite.Mode, Favorite = true, FollowNetworkMode = false
                });
        UnifiedProfileMigration.Apply(_settings);
        _settings.ConnectionsMigrationVersion = 2;
    }
    private static object ConnectionDto(SavedConnectionProfile p) => new
    {
        id = p.Id, name = p.Name, provider = p.Provider, services = p.Services, games = p.Games, domains = p.Domains, siteRules=p.SiteRules,
        telegramWebRoute = p.TelegramWebRoute, robloxAssetsMode = p.RobloxAssetsMode, level = p.Level, tuning = p.Tuning, mode = p.Mode, favorite = p.Favorite, updatedAt = p.UpdatedAt,
        hasStrategy = p.Strategy is not null && p.ProbeVersion == RouteProbePolicy.Version,
        youtubeVerification = p.YoutubeVerification,
        routeNeedsCheck = p.RouteNeedsCheck || p.Strategy is not null && p.ProbeVersion != RouteProbePolicy.Version
    };
    private Task PublishConnectionsAsync(string? change = null, string? changedId = null) => PostUiAsync(new
    {
        type = "connections.status", profiles = _settings.SavedConnections.Select(ConnectionDto), selectedId = _settings.SelectedConnectionId,
        change, changedId,
        active = _protectionRequested && _activeConnection is not null ? ConnectionDto(_activeConnection) : null
    });
    private SavedConnectionProfile GetConnection(string? id) => _settings.SavedConnections.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentException("Сохранённый профиль не найден.");
    private async Task SaveConnectionAsync(JsonElement root)
    {
        var input = root.GetProperty("profile");
        var profile = SavedConnectionProfilePolicy.Validate(SaveConnectionRequest.Read(input));
        var old = _settings.SavedConnections.FirstOrDefault(p => p.Id == profile.Id);
        if (string.IsNullOrWhiteSpace(profile.Provider)) profile.Provider = old?.Provider ?? "";
        if (!string.IsNullOrWhiteSpace(profile.Provider)) profile.Provider = AdvisorStrategyPolicy.NormalizeProvider(profile.Provider);
        if (old is not null) { profile.FollowNetworkMode = old.FollowNetworkMode; profile.Mode = old.Mode; }
        if (old is not null && old.TelegramWebRoute == profile.TelegramWebRoute && old.RobloxAssetsMode == profile.RobloxAssetsMode && old.Level == profile.Level && old.Tuning == profile.Tuning && old.Mode == profile.Mode &&
            old.Services.SequenceEqual(profile.Services) && old.Games.SequenceEqual(profile.Games) && old.Domains.SequenceEqual(profile.Domains) && JsonSerializer.Serialize(old.SiteRules)==JsonSerializer.Serialize(profile.SiteRules))
        { profile.YoutubeVerification=old.YoutubeVerification; profile.Strategy = old.Strategy; profile.ProbeVersion = old.ProbeVersion; profile.RouteNeedsCheck = old.RouteNeedsCheck; profile.StrategyNetworkKey = old.StrategyNetworkKey; profile.WarpProofKey=old.WarpProofKey; profile.NetworkRoutes = new(old.NetworkRoutes ?? []); }
        else { profile.Strategy = null; profile.ProbeVersion = 0; profile.RouteNeedsCheck = old?.Strategy is not null || old?.RouteNeedsCheck == true; }
        if (root.TryGetProperty("recommendationId", out var recommendationId))
        {
            var id = recommendationId.GetString() ?? "";
            if (_advisorRunning || !_draftRecommendations.TryGetValue(id, out var tested))
                throw new ArgumentException("Профиль или сеть изменились. Повторите подбор маршрута.");
            DraftRecommendationPolicy.Validate(profile, tested, CurrentNetworkKey(), DateTimeOffset.UtcNow);
            var chosen = tested.Recommendation;
            AdvisorStrategyPolicy.Validate(chosen.Strategy, profile.Level);
            WinwsProfileComposer.Compose(SavedConnectionProfilePolicy.ServiceFlags(profile), profile.Level,
                new GameFilterSettings { SelectedGames = [.. profile.Games] }, null, null, profile.Tuning, advisorStrategy: chosen.Strategy,siteRules:profile.SiteRules);
            profile.Provider = tested.Provider;
            profile.Strategy = chosen.Strategy; profile.ProbeVersion = chosen.ProbeVersion;
            profile.RouteNeedsCheck = false;
            profile.StrategyNetworkKey = tested.Network;
            profile.WarpProofKey=chosen.WarpProofKey;
            if(chosen.YoutubeRuleSignature is {} signature)
            {
                var yt=chosen.Services.FirstOrDefault(s=>s.Service=="youtube");
                profile.YoutubeVerification=new(YoutubeOnlyPolicy.ProbeVersion,tested.Network,chosen.Strategy.Key,signature,DateTimeOffset.UtcNow,
                    yt is {Samples:>=3}&&yt.Groups?.Any(g=>g.Id=="page"&&g.State=="confirmed")==true,yt is {Samples:>=3}&&yt.Groups?.Any(g=>g.Id=="video"&&g.State=="confirmed")==true,SearchVersion:chosen.YoutubeSearchVersion);
            }
            profile.NetworkRoutes[tested.Network] = new(chosen.Strategy, tested.Provider, chosen.ProbeVersion, DateTimeOffset.UtcNow,chosen.WarpProofKey,profile.YoutubeVerification);
        }
        profile = SavedConnectionProfilePolicy.Validate(profile);
        if (root.TryGetProperty("imported", out var imported) && imported.ValueKind == JsonValueKind.True) profile.RouteNeedsCheck = true;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        if (old is null && _settings.SavedConnections.Count >= 100) throw new ArgumentException("Допускается до 100 профилей.");
        var index = _settings.SavedConnections.FindIndex(p => p.Id == profile.Id);
        var previousSelected = _settings.SelectedConnectionId;
        if (index >= 0) _settings.SavedConnections[index] = profile; else _settings.SavedConnections.Add(profile);
        _settings.SelectedConnectionId = profile.Id;
        try { await _settingsStore.SaveAsync(_settings); }
        catch
        {
            if (index >= 0) _settings.SavedConnections[index] = old!; else _settings.SavedConnections.Remove(profile);
            _settings.SelectedConnectionId = previousSelected;
            throw;
        }
        await PublishConnectionsAsync("saved", profile.Id);
        _draftRecommendations.Clear();
        await LogToUiAsync("info", $"Профиль «{profile.Name}» сохранён. Изменения применятся при следующем подключении.");
    }
    private void LoadConnectionConfiguration(SavedConnectionProfile p)
    {
        p = SavedConnectionProfilePolicy.Validate(p);
        if (!string.IsNullOrWhiteSpace(p.Provider)) _settings.LocalAdvisor.Provider = p.Provider;
        if (p.FollowNetworkMode)
        {
            if (p.Mode != _settings.CompatibilityMode) p.Strategy = null;
            p.Mode = _settings.CompatibilityMode;
        }
        _settings.Profiles = SavedConnectionProfilePolicy.ServiceFlags(p);
        _settings.BypassLevel = p.Level; _settings.BypassTuning = p.Tuning; _settings.CompatibilityMode = p.Mode;
        _settings.TelegramWebRoute = p.TelegramWebRoute; _settings.GameFilter.RobloxAssetsMode = p.RobloxAssetsMode; _settings.GameFilter.SelectedGames = [.. p.Games]; _settings.CustomBypassDomains = [.. p.Domains];
        _settings.CurrentSiteRules=SiteRulePolicy.Validate(p.SiteRules);
        _settings.SelectedConnectionId = p.Id;
        if (p.NetworkRoutes?.GetValueOrDefault(CurrentNetworkKey()) is { } cached &&
            cached.Provider == _settings.LocalAdvisor.Provider &&
            AdvisorStrategyPolicy.CanReuseStored(cached.Strategy,p.Level,CurrentNetworkKey(),CurrentNetworkKey(),cached.ProbeVersion))
        {
            AdvisorStrategyPolicy.Validate(cached.Strategy, p.Level);
            p.Strategy = cached.Strategy; p.ProbeVersion = cached.ProbeVersion; p.StrategyNetworkKey = CurrentNetworkKey();
            p.WarpProofKey=cached.WarpProofKey;p.YoutubeVerification=cached.YoutubeVerification;
        }
        _settings.LocalAdvisor.AppliedStrategy = AdvisorStrategyPolicy.CanReuseStored(p.Strategy,p.Level,p.StrategyNetworkKey,CurrentNetworkKey(),p.ProbeVersion) ? p.Strategy : null;
        _settings.LocalAdvisor.AppliedProbeVersion = p.ProbeVersion;
        _settings.LocalAdvisor.AppliedMode = p.Mode; _settings.LocalAdvisor.AppliedNetworkKey = p.StrategyNetworkKey;
    }
    private async Task LaunchConnectionAsync(string? id,CancellationToken token=default)
    {
        if(!token.CanBeCanceled && _startupConnectCts is not null)
        {
            _startupConnectCts.Cancel();
            if(_startupTask is not null)await _startupTask;
        }
        if(_connectionLaunching||_advisorRunning||_protectionTransitioning||_warpTransitioning)return;
        _connectionLaunching=true;
        try{await SendServiceSnapshotAsync();await LaunchConnectionCoreAsync(id,token);}
        finally{_connectionLaunching=false;await SendServiceSnapshotAsync();}
    }
    private async Task LaunchConnectionCoreAsync(string? id,CancellationToken token=default)
    {
        if (_advisorRunning || _protectionTransitioning) return;
        _components.EnsureNoForeignProcesses();
        var original=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        var wasRunning=_protectionRequested;
        var stopGeneration=Volatile.Read(ref _userStopGeneration);
        var p = GetConnection(id); LoadConnectionConfiguration(p);
        try
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,token); await PrepareConnectionWarpAsync(linked.Token); token.ThrowIfCancellationRequested();
            await _settingsStore.SaveAsync(_settings);
            await SetProtectionAsync(true,token); token.ThrowIfCancellationRequested();
            if(stopGeneration!=Volatile.Read(ref _userStopGeneration))return;
            if(!_protectionRequested)throw new InvalidOperationException("Не удалось запустить профиль. Предыдущее соединение восстанавливается.");
        }
        catch
        {
            if(stopGeneration!=Volatile.Read(ref _userStopGeneration))return;
            using var rollback=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RestoreConnectionWarpAsync(rollback.Token);
            _settings=original;
            await _settingsStore.SaveAsync(_settings,rollback.Token);
            if(wasRunning)await ApplyProfilesAsync(rollback.Token);
            throw;
        }
        _startupConnectionError=null; await PublishSettingsStatusAsync(); _youtubeRollbackError=null;
        await PublishConnectionsAsync();
    }
    private async Task SelectConnectionAsync(string? id)
    {
        _settings.SelectedConnectionId = GetConnection(id).Id;
        await _settingsStore.SaveAsync(_settings); await PublishConnectionsAsync("selected", _settings.SelectedConnectionId);
    }
    private async Task DeleteConnectionAsync(string? id)
    {
        var p = GetConnection(id);
        if (_activeConnection?.Id == p.Id && _protectionRequested) await SetProtectionAsync(false);
        if(_settings.StartupConnectionId==p.Id){_settings.StartupConnectionId="";_settings.StartProtectionOnStartup=false;} _settings.SavedConnections.Remove(p);
        if (_settings.SelectedConnectionId == p.Id) _settings.SelectedConnectionId = string.Empty;
        await _settingsStore.SaveAsync(_settings); await PublishConnectionsAsync("deleted", p.Id);
    }
}



