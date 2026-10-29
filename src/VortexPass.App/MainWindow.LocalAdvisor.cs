using System.IO;
using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

namespace VortexPass.App;

public partial class MainWindow
{
    private readonly LlamaLocalStrategyModel _localStrategyModel = new(Path.Combine(AppContext.BaseDirectory, "local-ai"));
    private AdvisorStrategySpec? _advisorTrialSpec;
    private string _advisorSearchMode="normal";
    private CancellationTokenSource? _advisorRestoreCts;
    private CancellationToken AdvisorRestoreToken
    {
        get
        {
            if(_advisorRestoreCts is null)
            {var created=new CancellationTokenSource(TimeSpan.FromSeconds(15));if(Interlocked.CompareExchange(ref _advisorRestoreCts,created,null) is not null)created.Dispose();}
            return _advisorRestoreCts.Token;
        }
    }
    private string? _advisorSessionNetwork;
    private SavedConnectionProfile? _advisorDraft;
    private bool _advisorPreserveNonYoutube;
    private object? _pendingDraftAdvisorStatus;
    private readonly Dictionary<string, CheckedDraftRecommendation> _draftRecommendations = [];

    private async Task RunConnectionAdvisorAsync(JsonElement root)
    {
        var scope=root.TryGetProperty("scope",out var scopeValue)?scopeValue.GetString():"all";
        if(scope=="youtube")
        {
            try{await RunYoutubeOnlyAsync(root);}
            catch(Exception ex){await PostUiAsync(new{type="youtube.advisor.status",state="failed",runId=RequestYoutubeRunId(root),revision=Interlocked.Increment(ref _youtubeRevision),readiness="blocked",blockedReason=YoutubeRouteProbe.Error(ex),profileId=root.TryGetProperty("profileId",out var pid)?pid.GetString():null,message=YoutubeRouteProbe.Error(ex)});throw;}
            return;
        }
        if(scope!="all")throw new ArgumentException("Неизвестная область подбора");
        if (_advisorRunning || _protectionTransitioning || _warpMediaCts is not null) return;
        if(_warpPreparationCts is not null){await PostUiAsync(new {type="advisor.local.status",state="failed",error="Дождитесь завершения подготовки WARP"});return;}
        _dnsDiagnosticCts?.Cancel();++_dnsDiagnosticGeneration;_dnsChoices.Clear();
        await PostUiAsync(new {type="advisor.dns.status",state="idle"});
        _advisorRestoreCts?.Dispose();_advisorRestoreCts=null;
        _advisorDnsNeedsDiagnosis=false;
        _advisorSearchMode=root.TryGetProperty("searchMode",out var searchMode)&&searchMode.GetString()=="extended"?"extended":"normal";
        await LogToUiAsync("info",$"Подбор advisor-personal-youtube-v1-20261025: режим DNS {_settings.DnsMode}, провайдер {(_settings.DnsMode=="google-udp"?_settings.GoogleDnsAddress:_settings.SelectedDnsProvider)}, черновик {root.TryGetProperty("draft",out _)}.");
        _advisorAllowPartialDns=root.TryGetProperty("allowPartialDns",out var allowPartial)&&allowPartial.ValueKind==JsonValueKind.True;
        var requestedDraft=root.TryGetProperty("draft",out var input)?SavedConnectionProfilePolicy.Validate(SaveConnectionRequest.Read(input)):null;
        var requestedRules=requestedDraft?.SiteRules??(root.TryGetProperty("profileId",out var requestedId)?GetConnection(requestedId.GetString()).SiteRules:[]);
        if (_warpStatus.State == WarpConnectionState.Connected && !(requestedDraft?.TelegramWebRoute=="warpProxy"||requestedRules.Any(r=>r.Connection is "warpProxy" or "warpSystem")))
            throw new InvalidOperationException("Для проверки прямого маршрута выключите WARP и повторите подбор.");
        var original = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        var originalCommand = _components.GetArguments("dpi");
        var originalProfiles = CloneProfiles(_activeProfiles);
        var originalWarpOwnership=_connectionOriginalWarp;var originalWarpConnected=_connectionWarpWasConnected;
        var originalNativeWarp=_connectionNativeConfiguration;
        var originalBootstrapMode=_connectionOriginalBootstrapMode;
        var running = _protectionRequested && ProtectionRouteRunning();
        var targetId=requestedDraft?.Id??(root.TryGetProperty("profileId",out var target)?target.GetString():_activeConnection?.Id);
        var preserve=running&&_activeConnection is not null&&_activeConnection.Id==targetId&&(_activeConnection.Services.Any(s=>s is "youtube" or "web"))&&
            (requestedDraft is null||DraftRecommendationPolicy.Composition(requestedDraft)==DraftRecommendationPolicy.Composition(_activeConnection));
        var originalDpiRunning=_components.GetStatuses().Any(s=>s.Id=="dpi"&&s.State==ServiceState.Running);
        var originalHosts=running?await _hostsManager.ReadOwnedOverridesAsync(_lifetime.Token):[];
        var provider = AdvisorStrategyPolicy.NormalizeProvider(root.GetProperty("provider").GetString());
        try
        {
            _draftRecommendations.Clear();
            _pendingDraftAdvisorStatus = null;
            if(!preserve){_connectionOriginalWarp=null;_connectionNativeConfiguration=null;_settings.PendingConnectionWarpRestore=null;}
            _advisorPreserveNonYoutube=preserve;
            _advisorDraft = preserve&&requestedDraft is not null?SavedConnectionProfilePolicy.Clone(_activeConnection!):requestedDraft;
            if (_advisorDraft is not null) LoadConnectionConfiguration(_advisorDraft);
            else if (root.TryGetProperty("profileId", out var id)) LoadConnectionConfiguration(GetConnection(id.GetString()));
            _settings.LocalAdvisor.Provider = provider;
            await RunProfileAdvisorAsync();
        }
        finally
        {
            var reloadLists = !original.CustomBypassDomains.SequenceEqual(_settings.CustomBypassDomains) ||
                !original.ExcludedDomains.SequenceEqual(_settings.ExcludedDomains);
            var history = _settings.LocalAdvisor.History;
            var theme = _settings.Theme;
            var reduceMotion = _settings.ReduceMotion;
            original.PendingConnectionWarpRestore=_settings.PendingConnectionWarpRestore??original.PendingConnectionWarpRestore;
            original.Warp.VerifiedVariants=_settings.Warp.VerifiedVariants;
            _settings = original;
            _connectionOriginalWarp=originalWarpOwnership;_connectionWarpWasConnected=originalWarpConnected;
            _connectionNativeConfiguration=originalNativeWarp;
            _connectionOriginalBootstrapMode=originalBootstrapMode;
            _settings.Theme = theme;
            _settings.ReduceMotion = reduceMotion;
            _settings.LocalAdvisor.Provider = provider;
            _settings.LocalAdvisor.History = history;
            _advisorDraft = null;
            if(!preserve)await _domainLists.WriteAsync(original.CustomBypassDomains, original.ExcludedDomains, CancellationToken.None);
            if (running && _protectionRequested && !AdvisorRestoreToken.IsCancellationRequested)
            {
                if (originalDpiRunning && (reloadLists || _components.GetArguments("dpi") != originalCommand))
                    await RestartAdvisorWinwsAsync(originalCommand, AdvisorRestoreToken);
                _activeProfiles = originalProfiles;
                if (!preserve&&originalDpiRunning && MobileDnsPolicy.ShouldRun(true, original.CompatibilityMode,
                    MobileDnsPolicy.WarpOwnsSystemDns(_warpStatus.State == WarpConnectionState.Connected, GetEffectiveWarpRoutingMode())))
                    await StartProtectedDnsGuardAsync(AdvisorRestoreToken);
                // Restore the previous owned entries without new DNS/TLS measurements.
                if(!preserve){if(originalHosts.Count>0)_hostsStatus=await _hostsManager.ApplyAsync(originalHosts,false,false,AdvisorRestoreToken);else await RestoreHostsCoreAsync();}
            }
            await _settingsStore.SaveAsync(_settings);
            await PublishProfileStatusAsync();
            await PublishLocalAdvisorStatusAsync();
            await PublishConnectionsAsync();
            if (_pendingDraftAdvisorStatus is { } status) await PostUiAsync(status);
            _pendingDraftAdvisorStatus = null;
            _advisorPreserveNonYoutube=false;
        }
        // Diagnose after the trial has restored the original connection and settings.
        // Applying an alternative remains an explicit action in the wizard.
        if(_advisorDnsNeedsDiagnosis&&requestedDraft is not null&&!_lifetime.IsCancellationRequested&&!_shutdownStarted)
            await DiagnoseAdvisorDnsAsync(root);
    }

    private Task<ProviderInfo?> DetectProviderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var provider = _settings.LocalAdvisor.Provider?.Trim();
        return Task.FromResult(string.IsNullOrEmpty(provider) ? null : new ProviderInfo(provider, NormalizeProviderKey(provider) ?? provider));
    }

    private AdvisorStrategySpec? CurrentAdvisorStrategy()
    {
        if (_advisorTrialSpec is not null) return _advisorTrialSpec;
        var local = _settings.LocalAdvisor;
        return local.AppliedStrategy is { } spec && spec.Level == _settings.BypassLevel &&
            AdvisorStrategyPolicy.CanReuseStored(spec, _settings.BypassLevel, local.AppliedNetworkKey, CurrentNetworkKey(), local.AppliedProbeVersion) &&
            local.AppliedNetworkKey == CurrentNetworkKey() && local.AppliedMode == _settings.CompatibilityMode
            ? spec : null;
    }

    private Task PublishLocalAdvisorStatusAsync()
    {
        var status = _localStrategyModel.GetStatus();
        return PostUiAsync(new
        {
            type = "advisor.model.status", provider = _settings.LocalAdvisor.Provider,
            model = new { ready = status.Ready, name = status.Name, detail = status.Detail }, applied = CurrentAdvisorStrategy(),
            providers = new[] { "Т2 / Tele2", "МТС", "МегаФон", "Билайн", "Ростелеком", "Дом.ру" }
        });
    }

    private async Task SetAdvisorProviderAsync(string? provider)
    {
        if (_advisorRunning) return;
        _settings.LocalAdvisor.Provider = AdvisorStrategyPolicy.NormalizeProvider(provider);
        await _settingsStore.SaveAsync(_settings);
        await PublishLocalAdvisorStatusAsync();
    }

    private string[] LocalAdvisorServiceIds()
    {
        var ids = AdvisorServiceIds().ToList();
        if (_settings.Profiles.Discord) ids.Add("discord-voice");
        if (_settings.Profiles.Telegram && _settings.Profiles.TelegramWeb) ids.Add("telegram-desktop");
        if (_settings.Profiles.GameFilter && _settings.GameFilter.SelectedGames.Contains("deadbydaylight")) ids.Add("deadbydaylight");
        ids.AddRange(_settings.CustomBypassDomains.Select(domain => "domain:" + domain));
        ids.AddRange(_settings.CurrentSiteRules.Select(rule=>"site-rule:"+rule.Id));
        return ids.Distinct().ToArray();
    }

    private async Task RunLocalAdvisorAsync()
    {
        if (_advisorRunning || _protectionTransitioning || _warpMediaCts is not null) return;
        var runWatch = System.Diagnostics.Stopwatch.StartNew();
        AdvisorRequest request;
        try
        {
            request = new(WinwsProfileComposer.NormalizeLevel(_settings.BypassLevel),
                AdvisorStrategyPolicy.NormalizeProvider(_settings.LocalAdvisor.Provider), CurrentNetworkKey(),
                _settings.CompatibilityMode, LocalAdvisorServiceIds(), _settings.BypassTuning, _settings.GameFilter.RobloxAssetsMode, _settings.TelegramWebRoute, _advisorSearchMode,PreserveNonYoutube:_advisorPreserveNonYoutube);
            if (request.Services.Count == 0) throw new ArgumentException("Сначала выберите сервисы на дашборде.");

        }
        catch (Exception ex)
        {
            await PostUiAsync(new { type = "advisor.local.status", state = "failed", error = ex.Message });
            return;
        }

        _advisorRunning = true;
        _advisorStopProtectionRequested = false;
        _advisorSessionNetwork = request.NetworkKey;
        _advisorWarpProofKey=null;
        _advisorCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        await PostUiAsync(new { type = "advisor.local.status", state = "testing", provider = request.Provider,phase=1,elapsedMs=0,
            message = request.PreserveNonYoutube?"Сохраняем рабочие сервисы и измеряем YouTube без смены DNS/WARP":"Подготавливаем маршрут профиля. Исходное подключение будет восстановлено." });
        try
        {
            var history = _settings.LocalAdvisor.History.Concat(_settings.LocalAdvisor.YoutubeHistory).Where(h => h.NetworkKey == request.NetworkKey &&
                h.TelegramWebRoute==request.TelegramWebRoute &&
                h.Provider == request.Provider && h.Mode == request.Mode && h.Level == request.Level &&
                h.CheckedAt > DateTimeOffset.UtcNow.AddDays(-7)).OrderByDescending(h => h.CheckedAt)
                .SelectMany(h => h.Recommendations).Where(r => r.ProbeVersion == RouteProbePolicy.Version&&(r.Strategy.YouTubeParameters is null||r.YoutubeSearchVersion==YoutubeParametersPolicy.SearchVersion)&&(!r.Services.Any(s=>s.Service=="youtube")||r.YoutubeProbeVersion==YoutubeOnlyPolicy.ProbeVersion)).Take(3).ToArray();
            await LogToUiAsync("info", $"Локальный ИИ: {request.Provider}; уровень {LevelDisplayName(request.Level)}. Провайдер указан вручную.");
            var advisor = new LocalStrategyAdvisor(_localStrategyModel);
            var recommendations = await advisor.RunAsync(request, async token =>
            {
                var session = new AdvisorTrialSession(this, request);
                try { await session.PrepareAsync(token); return session; }
                catch { await session.DisposeAsync(); throw; }
            }, progress =>
            {
                _ = PostUiAsync(new { type = "advisor.local.status", state = progress.State, provider = request.Provider,
                    level = request.Level, progress = progress.Completed, total = progress.Total, message = progress.Message,phase=progress.Phase,elapsedMs=runWatch.ElapsedMilliseconds,remainingMs=Math.Max(0,(_advisorSearchMode=="extended"?360000:180000)-runWatch.ElapsedMilliseconds) });
                _ = LogToUiAsync("info", $"ИИ этап {progress.State} ({runWatch.Elapsed.TotalSeconds:F1} с): {progress.Message}");
            }, history, _advisorCts.Token);

            recommendations=recommendations.Select(r=>r with{WarpProofKey=_advisorWarpProofKey,YoutubeRuleSignature=r.Services.Any(s=>s.Service=="youtube")?YoutubeOnlyPolicy.Signature(request.PreserveNonYoutube?
                WinwsProfileComposer.ComposeYoutubeOnly(_components.GetArguments("dpi"),CurrentAdvisorStrategy()??new(){Level=request.Level,Tuning=request.PreferredTuning},r.Strategy):ComposeAdvisorCommand(r.Strategy)):null}).ToArray();
            _settings.LocalAdvisor.History.Insert(0, new(request.NetworkKey, request.Provider, request.Mode,
                request.Level, DateTimeOffset.UtcNow, recommendations,request.TelegramWebRoute));
            if (_advisorDraft is not null)
                foreach (var recommendation in recommendations)
                    _draftRecommendations[recommendation.Id] = new(DraftRecommendationPolicy.Composition(_advisorDraft), request.Provider,
                        request.NetworkKey, DateTimeOffset.UtcNow.AddMinutes(30), recommendation);
            _settings.LocalAdvisor.History = _settings.LocalAdvisor.History.Take(20).ToList();
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            var completedStatus = new
            {
                type = "advisor.local.status", state = "complete", provider = request.Provider,phase=4,elapsedMs=runWatch.ElapsedMilliseconds,
                message = "Исходное соединение восстановлено. Выберите рекомендацию для применения.",
                candidates = recommendations.Select((r, index) => new
                {
                    id = r.Id, level = r.Strategy.Level, tuning = r.Strategy.Tuning, mode = request.Mode,
                    label = r.Strategy.YouTubeParameters is not null?$"{LevelDisplayName(r.Strategy.Level)} · индивидуальный YouTube":r.Services.Any(s=>s.Service=="youtube")&&r.Strategy.YouTube=="youtube-tls"?$"{LevelDisplayName(r.Strategy.Level)} · резерв YouTube":$"{LevelDisplayName(r.Strategy.Level)} · измеренный вариант {index + 1}",
                    healthy = r.Confirmed, total = r.Total, fullyConfirmed = r.FullyConfirmed,
                    latencyMs = r.MedianLatencyMs, jitterMs = r.JitterMs, summary = r.Reason,
                    source = r.Strategy.YouTubeParameters is not null?"network-personalized":r.Strategy.YouTube=="youtube-tls"?"built-in-reserve":r.Strategy.Experimental ? "bounded-parameters" : "verified-blocks",
                    services = r.Services.Select(s => new { service = s.Service, state = s.State, confirmed = s.Confirmed,
                        detail = s.Detail, latencyMs = s.LatencyMs, jitterMs = s.JitterMs, samples = s.Samples,
                        groups = s.Groups?.Select(g => new { id = g.Id, state = g.State, detail = g.Detail }), systemDnsReady = s.SystemDnsReady }), strategy = r.Strategy,
                    probeVersion = r.ProbeVersion, engineVersion = WinwsProfileComposer.FlowsealVersion,
                    blocks = new { youtube = r.Strategy.YouTube, discord = r.Strategy.Discord, telegram = r.Strategy.Telegram }
                })
            };
            if (_advisorDraft is null) await PostUiAsync(completedStatus);
            else _pendingDraftAdvisorStatus = completedStatus;
            await LogToUiAsync("ok", $"Локальный подбор завершён за {runWatch.Elapsed.TotalSeconds:F1} с; измерено вариантов: {recommendations.Count}. Полностью подтверждено: {recommendations.Count(r=>r.FullyConfirmed)}. Выбор — вручную.");
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            var reason = _advisorRestoreCts?.IsCancellationRequested==true ? "Восстановление превысило 15 секунд; проверьте состояние подключения, снимок WARP сохранён" : request.NetworkKey != CurrentNetworkKey() ? "Сеть изменилась" : "Подбор отменён пользователем";
            await LogToUiAsync("info", $"{reason} через {runWatch.Elapsed.TotalSeconds:F1} с; исходное соединение восстановлено.");
            await PostUiAsync(new { type = "advisor.local.status", state = "failed", error = reason + "; исходное состояние восстановлено." });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await PostUiAsync(new { type = "advisor.local.status", state = "failed", error = ex.Message });
            await LogToUiAsync("error", $"Локальный подбор: {ex.Message}");
        }
        finally
        {
            _advisorTrialSpec = null;
            _advisorSessionNetwork = null;
            _advisorRunning = false;
            _advisorStopProtectionRequested = false;
            _advisorCts?.Dispose();
            _advisorCts = null;
            ResetHealthChecksAfterRouteChange();
            await PublishProfileStatusAsync();
            await PublishLocalAdvisorStatusAsync();
            await SendServiceSnapshotAsync();
        }
    }

    private async Task ApplyAdvisorRecommendationAsync(string? id)
    {
        if (_advisorRunning || _protectionTransitioning || _warpMediaCts is not null) return;
        var run = _settings.LocalAdvisor.History.FirstOrDefault(h => h.Recommendations.Any(r => r.Id == id));
        var chosen = run?.Recommendations.FirstOrDefault(r => r.Id == id);
        if (chosen is null || chosen.ProbeVersion != RouteProbePolicy.Version || run is null || run.NetworkKey != CurrentNetworkKey() || run.Level != _settings.BypassLevel ||
            run.Mode != _settings.CompatibilityMode || run.Provider != _settings.LocalAdvisor.Provider ||
            run.CheckedAt < DateTimeOffset.UtcNow.AddHours(-24) ||
            !chosen.Services.Select(s => s.Service).Order().SequenceEqual(LocalAdvisorServiceIds().Order()))
            throw new InvalidOperationException("Сеть, уровень или выбранные сервисы изменились. Повторите подбор.");
        AdvisorStrategyPolicy.Validate(chosen.Strategy, run.Level);
        if(chosen.Services.Any(s=>s.Service=="youtube")&&chosen.YoutubeProbeVersion!=YoutubeOnlyPolicy.ProbeVersion)throw new InvalidOperationException("Проверка YouTube устарела; перепроверьте его маршрут");
        // Recompose from trusted values before changing or persisting state.
        WinwsProfileComposer.Compose(_settings.Profiles, run.Level, _settings.GameFilter,
            null, null, mobileCompatibilityMode: run.Mode == "mobile", advisorStrategy: chosen.Strategy);
        var savedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        var wasRunning = _components.GetStatuses().Any(s => s.Id == "dpi" && s.State == ServiceState.Running);
        try
        {
            _settings.LocalAdvisor.AppliedStrategy = chosen.Strategy;
            _settings.LocalAdvisor.AppliedNetworkKey = run.NetworkKey;
            _settings.LocalAdvisor.AppliedMode = run.Mode;
            _settings.LocalAdvisor.AppliedProbeVersion = RouteProbePolicy.Version;
            await ApplyProfilesAsync();
            if (!_components.GetStatuses().Any(s => s.Id == "dpi" && s.State == ServiceState.Running))
                throw new InvalidOperationException("Не удалось применить рекомендацию.");
            await _settingsStore.SaveAsync(_settings);
            var selectedConnection = _settings.SavedConnections.FirstOrDefault(p => p.Id == _settings.SelectedConnectionId);
            if (selectedConnection is not null)
            {
                selectedConnection.Strategy = chosen.Strategy; selectedConnection.ProbeVersion = chosen.ProbeVersion;
                selectedConnection.StrategyNetworkKey = run.NetworkKey;
                selectedConnection.WarpProofKey=chosen.WarpProofKey;
                selectedConnection.NetworkRoutes ??= [];
                selectedConnection.NetworkRoutes[run.NetworkKey] = new(chosen.Strategy, run.Provider, chosen.ProbeVersion, DateTimeOffset.UtcNow,chosen.WarpProofKey);
                if (selectedConnection.NetworkRoutes.Count > 20)
                    selectedConnection.NetworkRoutes.Remove(selectedConnection.NetworkRoutes.MinBy(pair => pair.Value.ConfirmedAt).Key);
                selectedConnection.Mode = run.Mode;
                _activeConnection = SavedConnectionProfilePolicy.Clone(selectedConnection);
                await _settingsStore.SaveAsync(_settings);
                await PublishConnectionsAsync();
            }
        }
        catch
        {
            _settings = savedSettings;
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            if (wasRunning) await ApplyProfilesAsync();
            throw;
        }
        await PublishProfileStatusAsync();
        await PublishLocalAdvisorStatusAsync();
        await LogToUiAsync("info", chosen.FullyConfirmed ? "Применён проверенный ИИ-вариант." : $"Применён частично подтверждённый ИИ-вариант: {chosen.Reason}");
    }

    private string ComposeAdvisorCommand(AdvisorStrategySpec strategy) => WinwsProfileComposer.Compose(
        ProfilesForDpi(), _settings.BypassLevel, _settings.GameFilter,
        _advisorRuntimeBypassPath, _advisorRuntimeExclusionPath, _settings.BypassTuning,
        mobileCompatibilityMode: IsMobileCompatibilityMode(),
        youtubeTransport: CurrentNetworkCompatibility()?.YouTubeTransport ?? "auto",
        discordVoiceMode: CurrentDiscordVoiceMode(),
        telegramStrategy: CurrentNetworkCompatibility()?.TelegramStrategy ?? "strong",
        youtubeMobileStrategy: CurrentNetworkCompatibility()?.ServiceRoutes?.GetValueOrDefault("youtube")?.Strategy ?? "primary",
        discordMobileStrategy: CurrentNetworkCompatibility()?.ServiceRoutes?.GetValueOrDefault("discord")?.Strategy ?? "primary",
        advisorStrategy: strategy,siteRules:_settings.CurrentSiteRules);

    private string? _advisorRuntimeBypassPath;
    private string? _advisorRuntimeExclusionPath;

    private async Task RestartAdvisorWinwsAsync(string command, CancellationToken token)
    {
        await _protectionGate.WaitAsync(token);
        try
        {
            _protectionTransitioning = true;
            await _components.RestartOwnedAsync("dpi",command,token);
            if (!_components.GetStatuses().Any(s => s.Id == "dpi" && s.State == ServiceState.Running))
                throw new InvalidOperationException("Проверяемая стратегия не запустилась.");
            _protectionRequested = true;
        }
        finally { _protectionTransitioning = false; _protectionGate.Release(); }
    }

    private sealed class AdvisorTrialSession : IAdvisorTrialSession
    {
        private readonly MainWindow _owner;
        private readonly AdvisorRequest _request;
        private readonly bool _wasRunning;
        private readonly bool _originalDpiRunning;
        private readonly AdvisorStrategySpec _controlStrategy;
        public AdvisorStrategySpec? CurrentStrategy => _controlStrategy;
        private readonly string _originalArguments;
        private readonly BypassProfileSettings _originalProfiles;
        private readonly AppSettings _originalSettings;
        private readonly SavedConnectionProfile? _originalConnection;
        private readonly bool _dnsWasRunning;
        private bool _baselineOnly;
        private int _samples=1;
        private AdvisorServiceEvidence? _warpEvidence;
        private CancellationTokenRegistration _budgetCancellation;
        private bool _touched;
        private bool _disposed;
        private AdvisorStrategySpec? _youtubeBase;
        private string? _youtubeCommand;
        private string? _controlCommand;
        public void BeginYoutubePhase(AdvisorStrategySpec baseline)
        {
            _youtubeBase=baseline;
            _youtubeCommand=baseline==_controlStrategy?(_controlCommand??_originalArguments):_owner.ComposeAdvisorCommand(baseline);
        }
        private string TrialCommand(AdvisorStrategySpec spec)=>_youtubeBase is null?_owner.ComposeAdvisorCommand(spec):WinwsProfileComposer.ComposeYoutubeOnly(_youtubeCommand!,_youtubeBase,spec);

        public AdvisorTrialSession(MainWindow owner, AdvisorRequest request)
        {
            _owner = owner; _request = request;
            _wasRunning = owner._protectionRequested && owner.ProtectionRouteRunning();
            _originalDpiRunning=owner._components.GetStatuses().Any(s=>s.Id=="dpi"&&s.State==ServiceState.Running);
            _controlStrategy = owner.CurrentAdvisorStrategy() ?? new() { Level=request.Level,Tuning=request.PreferredTuning };
            _originalArguments = owner._components.GetArguments("dpi");
            _originalProfiles = CloneProfiles(owner._activeProfiles);
            _originalConnection = owner._activeConnection is null ? null : SavedConnectionProfilePolicy.Clone(owner._activeConnection);
            _dnsWasRunning = owner._dnsGuard.IsRunning;
            _originalSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(owner._settings))
                ?? throw new InvalidOperationException("Не удалось сохранить исходные настройки.");
        }

        public async Task PrepareAsync(CancellationToken token)
        {
            _budgetCancellation=token.Register(()=>{_ = _owner.AdvisorRestoreToken;});
            _owner._components.EnsureNoForeignProcesses();
            if(_request.PreserveNonYoutube)
            {
                if(!_wasRunning||!_originalDpiRunning)throw new InvalidOperationException("Для сохранения остальных сервисов требуется активный профиль");
                _controlCommand=_originalArguments;_touched=true;await CacheWarpEvidenceAsync(token);
                await _owner.ConfirmAdvisorDnsAsync(_request,token);
                return;
            }
            await _owner.PrepareConnectionWarpAsync(token);
            _owner.CancelMobileTelegramOptimization();
            _owner.CancelPendingProfileWork();
            var lists = await _owner._domainLists.WriteAsync(_owner._settings.CustomBypassDomains, _owner._settings.ExcludedDomains, token);
            _owner._advisorRuntimeBypassPath = lists.BypassPath;
            _owner._advisorRuntimeExclusionPath = lists.ExclusionPath;
            if (!_wasRunning||_owner.IsProxyOnlyProfile())
            {
                _touched = true;
                await _owner.ApplyProfilesAsync(token);
                token.ThrowIfCancellationRequested();
                if (!_owner.ProtectionRouteRunning())
                    throw new InvalidOperationException("Не удалось подготовить защиту к проверке.");
            }
            else
            {
                _touched = true;
                // The selected draft may differ from the active connection.
                // Establish its control route before measuring the baseline.
                await _owner.RestartAdvisorWinwsAsync(_owner.ComposeAdvisorCommand(_controlStrategy), token);
                if (MobileDnsPolicy.ShouldRun(true, _request.Mode, MobileDnsPolicy.WarpOwnsSystemDns(
                    _owner._warpStatus.State == WarpConnectionState.Connected, _owner.GetEffectiveWarpRoutingMode())))
                    await _owner.StartProtectedDnsGuardAsync(token);
                await _owner.RestoreHostsCoreAsync();
            }
            await _owner.ConfirmAdvisorDnsAsync(_request,token);
            await _owner.LogToUiAsync("info",$"ИИ DNS подготовлен: {_owner._settings.DnsMode}; {_owner.CurrentDnsStatus.Detail}; {_owner.CurrentDnsStatus.Resolver}");
            await CacheWarpEvidenceAsync(token);
            _controlCommand=_owner._components.GetArguments("dpi");
        }

        private async Task CacheWarpEvidenceAsync(CancellationToken token)
        {
            if(_request.TelegramWebRoute=="warpProxy"&&_request.Services.Contains("telegram"))
            {
                var checks=new List<ServiceHealthResult>();
                for(var i=0;i<3;i++)
                {
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(8));
                    var proxy=_owner.GetTelegramWebProxyUri()??throw new InvalidOperationException("Telegram Web: WARP-прокси не готов");
                    try{checks.Add(await TelegramWarpProbe.CheckAsync(proxy,deadline.Token));}
                    catch(OperationCanceledException)when(!token.IsCancellationRequested){checks.Add(new("telegram",ServiceHealthState.Partial,"WARP: проверка не завершилась за 8 секунд",ContentConfirmed:false));}
                }
                var stable=checks.All(c=>c.ContentConfirmed==true);
                var times=checks.Where(c=>c.LatencyMs.HasValue).Select(c=>c.LatencyMs!.Value).ToArray();
                _warpEvidence=new("telegram",stable?"healthy":"partial",stable,checks[^1].Detail,LocalStrategyAdvisor.Median(times),times.Length>1?times.Max()-times.Min():null,3,checks[^1].Groups,true);
                await _owner.LogToUiAsync("info","Telegram Web: отдельные три замера WARP завершены; повторов при смене DPI не будет");
            }
        }

        public async Task<IReadOnlyList<AdvisorServiceEvidence>> TestSamplesAsync(AdvisorStrategySpec strategy,int samples,CancellationToken token)
        {_samples=samples;return await TestAsync(strategy,token);}
        public async Task<IReadOnlyList<AdvisorServiceEvidence>> CheckCurrentSamplesAsync(int samples,CancellationToken token)
        {_samples=samples;return await CheckCurrentAsync(token);}
        public async Task<IReadOnlyList<AdvisorServiceEvidence>> CheckCurrentAsync(CancellationToken token)
        {
            _baselineOnly = true;
            try { return await TestAsync(_controlStrategy, token); }
            finally { _baselineOnly = false; }
        }
        private string[] TrialDnsHosts()=>RouteProbePolicy.DnsHostsForServices(_request.Services)
            .Concat(_owner._settings.CurrentSiteRules.Where(r=>r.Connection is "auto" or "direct").SelectMany(SiteRulePolicy.Domains)).Distinct().ToArray();

        public string ConfigurationKey(AdvisorStrategySpec strategy) => _owner.IsProxyOnlyProfile()?"proxy-only":Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(TrialCommand(strategy))));

        public async Task<IReadOnlyList<AdvisorServiceEvidence>> TestAsync(AdvisorStrategySpec strategy, CancellationToken token)
        {
            _owner._components.EnsureNoForeignProcesses();
            if (_request.NetworkKey != _owner.CurrentNetworkKey()) throw new OperationCanceledException("Сеть изменилась", token);
            AdvisorStrategyPolicy.Validate(strategy, _request.Level);
            if (!_baselineOnly) _owner._advisorTrialSpec = strategy;
            _touched = true;
            try { if (!_baselineOnly&&!_owner.IsProxyOnlyProfile()) await _owner.RestartAdvisorWinwsAsync(TrialCommand(strategy), token); }
            catch (InvalidOperationException)
            {
                return _request.Services.Select(id => new AdvisorServiceEvidence(id, "unconfirmed", false,
                    "winws не принял проверяемую конфигурацию", null, null, 0)).ToArray();
            }
            if(DnsModePolicy.UsesGuard(_owner._settings.DnsMode)&&!_owner._dnsGuard.IsRunning)
            {
                if(_youtubeBase is not null||_request.PreserveNonYoutube)throw new IOException("DNS Guard остановился во время изолированного YouTube; настройки не меняются");
                await _owner.LogToUiAsync("warn","ИИ: DNS Guard потерял готовность; восстанавливаем выбранный DNS перед следующим испытанием.");
                await _owner.StartProtectedDnsGuardAsync(token);
            }
            var missing=await _owner.ConfirmAdvisorDnsAsync(_request,token,requireReady:false);
            var ids = _request.Services.Where(id => id is not "discord-voice" and not "telegram-desktop" and not "deadbydaylight" &&
                !(id == "telegram" && !_owner._settings.Profiles.TelegramWeb)).ToArray();
            async Task<ServiceHealthResult> Probe(string id)
            {
                if(id=="telegram"&&_request.TelegramWebRoute=="warpProxy")
                {
                    if(_warpEvidence is not null)return new(id,_warpEvidence.Confirmed?ServiceHealthState.Healthy:ServiceHealthState.Partial,_warpEvidence.Detail,_warpEvidence.LatencyMs,ContentConfirmed:_warpEvidence.Confirmed,Groups:_warpEvidence.Groups,SystemDnsReady:true);
                    var proxy=_owner.GetTelegramWebProxyUri();
                    if(proxy is null)return new(id,ServiceHealthState.Partial,"WARP-прокси не готов",ContentConfirmed:false);
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(8));
                    try{return await TelegramWarpProbe.CheckAsync(proxy,deadline.Token);}catch(OperationCanceledException)when(!token.IsCancellationRequested){return new(id,ServiceHealthState.Partial,"WARP Telegram: бюджет проверки 8 секунд",ContentConfirmed:false);}
                }
                var unavailable=_owner.AdvisorServiceDnsHosts(id).Where(h=>missing.Contains(h,StringComparer.OrdinalIgnoreCase)).ToArray();
                if(unavailable.Length>0)return new(id,ServiceHealthState.DnsUnavailable,"DNS не подтверждён: "+string.Join(", ",unavailable),ContentConfirmed:false,SystemDnsReady:false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var result = id.StartsWith("site-rule:",StringComparison.Ordinal)
                        ? await ServiceHealthCheckService.CheckSiteRuleAsync(_owner._settings.CurrentSiteRules.Single(r=>"site-rule:"+r.Id==id),
                            _owner._settings.CurrentSiteRules.Single(r=>"site-rule:"+r.Id==id).Connection=="warpProxy"?_owner.GetWarpProxyUri():null,timeout.Token,
                            _owner._warpStatus.State==WarpConnectionState.Connected&&_owner.GetEffectiveWarpRoutingMode()=="full")
                        : id.StartsWith("domain:", StringComparison.Ordinal)
                        ? await ServiceHealthCheckService.CheckCustomDomainAsync(id[7..], timeout.Token)
                        : await _owner._healthChecks.CheckAsync(id, true,
                        _owner._components.GetStatuses().Any(s => s.Id == "dpi" && s.State == ServiceState.Running), timeout.Token,
                        appSelected: id == "telegram" && _owner._settings.Profiles.Telegram,
                        webSelected: id == "telegram" && _owner._settings.Profiles.TelegramWeb,
                        telegramProxyRunning: _owner._telegramProxy.IsRunning,
                        telegramProxyHasClientActivity: _owner._telegramProxy.HasTelegramClientActivity,
                        discordVoiceMode: "off", probeOptionalTransport: false, deepTelegramProbe: true);
                    await _owner.LogToUiAsync("info", $"ИИ проверка {id}: {result.Detail}; {watch.ElapsedMilliseconds} мс. " +
                        string.Join("; ", result.Groups?.Select(g => $"{g.Id}: {g.Detail}") ?? []));
                    return result; // Preserve transport RTT; diagnostic duration is logged separately.
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { return new(id, ServiceHealthState.Checking, "Проверка не завершилась за 8 секунд"); }
            }
            async Task<ServiceHealthResult[][]> Collect()
            {
                var rounds = new List<ServiceHealthResult[]>();
                for (var sample = 0; sample < _samples; sample++)
                {
                    token.ThrowIfCancellationRequested();
                    if (sample > 0) await Task.Delay(400, token);
                    rounds.Add(await Task.WhenAll(ids.Select(Probe)));
                }
                return rounds.ToArray();
            }
            var rounds = await Collect();
            var after=await _owner.ConfirmAdvisorDnsAsync(_request,token,requireReady:false);
            var changed=after.Except(missing,StringComparer.OrdinalIgnoreCase).ToArray();
            if(changed.Length>0)
            {
                foreach(var round in rounds)
                for(var i=0;i<round.Length;i++)
                    if(_owner.AdvisorServiceDnsHosts(round[i].Id).Any(h=>after.Contains(h,StringComparer.OrdinalIgnoreCase)))
                        round[i]=round[i] with {State=ServiceHealthState.DnsUnavailable,ContentConfirmed=false,SystemDnsReady=false,Detail="DNS отказал во время замеров; результат DPI не подтверждён"};
            }
            var stillRunning = _owner.ProtectionRouteRunning();
            var results = ids.Select(id =>
            {
                if(id=="telegram"&&_request.TelegramWebRoute=="warpProxy"&&_warpEvidence is not null)return _warpEvidence;
                var samples = rounds.Select(round => round.Single(r => r.Id == id)).ToArray();
                var last = samples[^1];
                var passed = samples.Count(r => r.ContentConfirmed ?? r.State == ServiceHealthState.Healthy);
                var confirmed = stillRunning && passed == samples.Length && id != "phasmophobia";
                if (id == "telegram" && !_owner._settings.Profiles.TelegramWeb) confirmed = false;
                var timings = samples.Where(r => r.LatencyMs.HasValue).Select(r => r.LatencyMs!.Value).ToArray();
                var median = LocalStrategyAdvisor.Median(timings);
                var spread = timings.Length > 1 ? timings.Max() - timings.Min() : (long?)null;
                var stableGroups=samples.SelectMany(r=>r.Groups??[]).Select(g=>g.Id).Distinct().Select(groupId=>
                {
                    var entries=samples.Select(r=>r.Groups?.FirstOrDefault(g=>g.Id==groupId)).ToArray();
                    var detail=entries.LastOrDefault(g=>g is not null)?.Detail??"Проверка не завершена";
                    return new ServiceRouteGroup(groupId,entries.All(g=>g?.State=="confirmed")?"confirmed":entries.FirstOrDefault(g=>g?.State!="confirmed")?.State??"incomplete",detail,LocalStrategyAdvisor.Median(entries.Where(g=>g?.DurationMs is not null).Select(g=>g!.DurationMs!.Value)));
                }).ToArray();
                return new AdvisorServiceEvidence(id, confirmed ? "healthy" : passed > 0 || last.IsHealthy ? "partial" : last.State == ServiceHealthState.DnsUnavailable ? "dnsUnavailable" : last.State == ServiceHealthState.Checking ? "incomplete" : "routeUnavailable", confirmed,
                    stillRunning ? $"{passed}/{samples.Length} замеров: {last.Detail}" : "Проверяемый winws завершился",
                    median, spread, samples.Length, stableGroups, samples.All(r => r.SystemDnsReady != false));
            }).ToList();
            foreach (var id in _request.Services.Except(ids))
                results.Add(new(id, "sessionRequired", false, id is "telegram-desktop" or "telegram"
                    ? "Desktop-прокси запущен; для подтверждения нужна реальная сессия клиента"
                    : "Голос/игровая сессия не подтверждаются HTTPS-проверкой", null, null, 0));
            if(_request.TelegramWebRoute=="warpProxy"&&_samples>=3)_warpEvidence=results.FirstOrDefault(r=>r.Service=="telegram");
            return results;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _owner._advisorTrialSpec = null;
            try
            {
                if (!_touched || _owner._shutdownStarted) return;
                if (_owner._advisorStopProtectionRequested || !_wasRunning)
                    await _owner.StopProtectionAsync(_owner.AdvisorRestoreToken);
                else
                {
                    if(_originalDpiRunning)await _owner.RestartAdvisorWinwsAsync(_originalArguments, _owner.AdvisorRestoreToken);
                    else await _owner._components.StopAllAsync(cleanupDriver:!_owner._warpBootstrap.IsActive);
                    _owner._activeProfiles = _originalProfiles;
                    if (!_request.PreserveNonYoutube&&!_dnsWasRunning) await _owner._dnsGuard.StopAsync(_owner.AdvisorRestoreToken);
                    await _owner.LogToUiAsync("info", "Исходная конфигурация защиты восстановлена после подбора.");
                }
                _owner._components.SetArguments("dpi", _originalArguments);
            }
            finally
            {
                if(!_request.PreserveNonYoutube)await _owner.RestoreConnectionWarpAsync(_owner.AdvisorRestoreToken);
                // Trial setup may create temporary mobile diagnostic/cache state.
                // Restore the complete settings snapshot as well as wire rules.
                if (_owner._advisorStopProtectionRequested)
                    _originalSettings.Warp = _owner._settings.Warp;
                _originalSettings.Theme = _owner._settings.Theme;
                _originalSettings.ReduceMotion = _owner._settings.ReduceMotion;
                _originalSettings.Warp.VerifiedVariants=_owner._settings.Warp.VerifiedVariants;
                _owner._settings = _originalSettings;
                _owner._activeConnection = _originalConnection;
                await _owner._settingsStore.SaveAsync(_owner._settings, CancellationToken.None);
                _budgetCancellation.Dispose();
            }
        }
    }
}
