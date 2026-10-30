using System.IO;
using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

namespace VortexPass.App;
public partial class MainWindow
{
    private sealed record YoutubeRun(string RunId,string ProfileId,string Network,string Frozen,string Command,AdvisorStrategySpec Baseline,DateTimeOffset Expires,long StopGeneration,long ComponentGeneration,IReadOnlyList<YoutubeCandidate> Candidates);
    private YoutubeRun? _youtubeRun;
    private bool _youtubeApplying;
    private string? _youtubeRunId;
    private string? _youtubeRollbackError;
    private long _youtubeRevision;
    private long _youtubeStartedAt;
    private static string RequestYoutubeRunId(JsonElement root)=>root.TryGetProperty("runId",out var id)&&Guid.TryParse(id.GetString(),out var value)?value.ToString():Guid.NewGuid().ToString();
    private Task YoutubeStatusAsync(object payload)=>PostUiAsync(payload);
    private string? YoutubeBlockedReason(YoutubeRun run)
    {
        if(_youtubeRollbackError is not null)return "Восстановление не завершено: "+_youtubeRollbackError;
        if(_advisorRunning||_protectionTransitioning||_connectionLaunching||_warpTransitioning)return "Другая операция ещё выполняется";
        if(!_protectionRequested||_activeConnection?.Id!=run.ProfileId)return "Профиль отключён. Подключите его и повторите проверку YouTube";
        if(run.StopGeneration!=Volatile.Read(ref _userStopGeneration)||run.ComponentGeneration!=_components.GetStatuses().FirstOrDefault(s=>s.Id=="dpi")?.Generation)return "Соединение перезапускалось. Повторите проверку YouTube";
        if(run.Expires<DateTimeOffset.UtcNow)return "Результат устарел. Повторите проверку YouTube";
        if(run.Network!=CurrentNetworkKey()||run.Frozen!=YoutubeFrozen(GetConnection(run.ProfileId))||_components.GetArguments("dpi")!=run.Command)return "Профиль или сеть изменились. Повторите проверку YouTube";
        if(!_components.GetStatuses().Any(s=>s.Id=="dpi"&&s.State==ServiceState.Running))return "Основной winws не работает. Подключите профиль и повторите проверку";
        return null;
    }
    private Task PublishYoutubeReadinessAsync()
    {
        if(_youtubeRun is not {} run||_advisorRunning)return Task.CompletedTask;
        var reason=YoutubeBlockedReason(run);
        return PostUiAsync(new{type="youtube.advisor.status",state="readiness",runId=run.RunId,profileId=run.ProfileId,revision=Interlocked.Increment(ref _youtubeRevision),readiness=reason is null?"ready":"blocked",blockedReason=reason});
    }
    private Task PublishYoutubeErrorAsync(JsonElement root,Exception error)
    {
        var detail=error is AggregateException aggregate?"Ошибка применения: "+YoutubeRouteProbe.Error(aggregate.InnerExceptions[0])+". Ошибка восстановления: "+string.Join("; ",aggregate.InnerExceptions.Skip(1).Select(YoutubeRouteProbe.Error)):
            error is OperationCanceledException?"Применение отменено; исходный блок восстановлен":YoutubeRouteProbe.Error(error);
        var reason=_youtubeRun is null?"Нужна новая проверка":YoutubeBlockedReason(_youtubeRun);
        return YoutubeStatusAsync(new{type="youtube.advisor.status",state="applyFailed",runId=root.TryGetProperty("runId",out var id)?id.GetString():_youtubeRunId,revision=Interlocked.Increment(ref _youtubeRevision),profileId=_youtubeRun?.ProfileId,message=detail,error=detail,readiness=reason is null?"ready":"blocked",blockedReason=reason});
    }
    private string YoutubeFrozen(SavedConnectionProfile p)=>JsonSerializer.Serialize(new{p.Id,p.Services,p.Games,p.Domains,p.SiteRules,p.Level,p.Tuning,p.Mode,p.Provider,p.TelegramWebRoute,p.RobloxAssetsMode,p.Strategy,
        _settings.Warp,_settings.DnsMode,_settings.SelectedDnsProvider,_settings.GoogleDnsAddress,_settings.GameFilter,_settings.CustomBypassDomains,_settings.ExcludedDomains,_settings.Profiles,_settings.BypassLevel,_settings.BypassTuning,_settings.CompatibilityMode,
        warpBootstrapPid=_warpBootstrap.ProcessId,pac=_warpPac.PacUri?.AbsoluteUri});
    private void RequireYoutubeProfile(SavedConnectionProfile profile)
    {
        if(!_protectionRequested||_activeConnection?.Id!=profile.Id||!_components.GetStatuses().Any(s=>s.Id=="dpi"&&s.State==ServiceState.Running))
            throw new InvalidOperationException("Сначала подключите этот сохранённый профиль. Подбор YouTube не включает и не переподключает WARP.");
        if(!profile.Services.Any(s=>s is "youtube" or "web"))throw new ArgumentException("В профиле не выбран YouTube");
        if(_warpStatus.State==WarpConnectionState.Connected&&GetEffectiveWarpRoutingMode()=="full")throw new InvalidOperationException("Включён полный WARP. Для отдельной проверки DPI нужен прямой маршрут YouTube; туннель автоматически не переключается.");
        var plan=WarpRoutingPlanner.BuildPlan(_settings.Warp,WarpService.GetNetworkFingerprint());
        if(plan.ProxyDomains.Any(d=>WarpHttpProxy.Allows("www.youtube.com",[d])||WarpHttpProxy.Allows("googlevideo.com",[d])||d.EndsWith(".youtube.com",StringComparison.OrdinalIgnoreCase)||d.EndsWith(".googlevideo.com",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("YouTube включён в правила WARP. Отдельный DPI-подбор не меняет эти правила автоматически.");
    }
    private async Task RestartYoutubeWinwsAsync(string command,CancellationToken token)
    {
        await _protectionGate.WaitAsync(token);
        try
        {
            if(!_protectionRequested||_advisorStopProtectionRequested)throw new OperationCanceledException("Защита остановлена пользователем",token);
            _protectionTransitioning=true;await _components.RestartOwnedAsync("dpi",command,token);
        }
        finally{_protectionTransitioning=false;_protectionGate.Release();}
    }
    private async Task RunYoutubeOnlyAsync(JsonElement root)
    {
        var runId=RequestYoutubeRunId(root);
        if(_advisorRunning||_protectionTransitioning||_warpTransitioning||_connectionLaunching||_warpMediaCts is not null)
            throw new InvalidOperationException("Подключение или другая проверка ещё выполняется. Дождитесь завершения и повторите проверку YouTube.");
        var profile=GetConnection(root.GetProperty("profileId").GetString());RequireYoutubeProfile(profile);
        var baseline=CurrentAdvisorStrategy()??profile.Strategy??new AdvisorStrategySpec{Level=profile.Level,Tuning=profile.Tuning};
        var searchMode=root.TryGetProperty("searchMode",out var mode)&&mode.GetString()=="extended"?"extended":"normal";
        var workMs=searchMode=="extended"?345000:165000;
        var command=_components.GetArguments("dpi");if(string.IsNullOrWhiteSpace(command))throw new IOException("Исходная конфигурация winws недоступна");
        var frozen=YoutubeFrozen(profile);var network=CurrentNetworkKey();var watch=System.Diagnostics.Stopwatch.StartNew();
        if(_youtubeRollbackError is not null)throw new InvalidOperationException("Сначала штатно переподключите профиль после ошибки восстановления: "+_youtubeRollbackError);
        _youtubeRunId=runId;_youtubeStartedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();_youtubeRun=null;_advisorRunning=true;_advisorStopProtectionRequested=false;_advisorTrialSpec=baseline;
        var stopGeneration=Volatile.Read(ref _userStopGeneration);
        _advisorCts=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);ResetHealthChecksAfterRouteChange();
        var backup=Path.Combine(_settingsStore.DirectoryPath,"backups","youtube",DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);
        IReadOnlyList<YoutubeCandidate> candidates=[];string? failure=null;
        async Task Status(string state,string message)=>await PostUiAsync(new{type="youtube.advisor.status",state,runId,startedAt=_youtubeStartedAt,revision=Interlocked.Increment(ref _youtubeRevision),readiness="busy",profileId=profile.Id,message,elapsedMs=watch.ElapsedMilliseconds,remainingMs=Math.Max(0,workMs+15000-watch.ElapsedMilliseconds),backupDirectory=backup});
        try
        {
            await Status("testing","Сохраняем исходный профиль. Меняются только правила YouTube; WARP и PAC остаются подключёнными.");await SendServiceSnapshotAsync();
            Directory.CreateDirectory(backup);
            await File.WriteAllTextAsync(Path.Combine(backup,"settings.json"),JsonSerializer.Serialize(_settings,new JsonSerializerOptions{WriteIndented=true}),_advisorCts.Token);
            await File.WriteAllTextAsync(Path.Combine(backup,"profile.json"),JsonSerializer.Serialize(profile,new JsonSerializerOptions{WriteIndented=true}),_advisorCts.Token);
            await File.WriteAllTextAsync(Path.Combine(backup,"winws.txt"),command,_advisorCts.Token);
            await File.WriteAllTextAsync(Path.Combine(backup,"warp-confirmations.json"),JsonSerializer.Serialize(_settings.Warp.VerifiedVariants),_advisorCts.Token);
            await File.WriteAllTextAsync(Path.Combine(backup,"README.txt"),"Личная резервная копия. Закройте VortexPass штатно перед восстановлением settings.json. Не распространяйте эту папку вместе с portable. Файлы регистрации WARP, cookies и авторизация не читались.",_advisorCts.Token);
            await LogToUiAsync("info","YouTube-only: резервная копия сохранена; общий подбор, DNS и WARP не запускаются.");
            candidates=await YoutubePersonalizedSearch.RunAsync(baseline,async(spec,token)=>
            {
                if(network!=CurrentNetworkKey()||frozen!=YoutubeFrozen(GetConnection(profile.Id)))throw new InvalidOperationException("Контекст профиля или сеть изменились");
                var trial=WinwsProfileComposer.ComposeYoutubeOnly(command,baseline,spec);YoutubeOnlyPolicy.AssertIsolated(command,trial);
                using var round=CancellationTokenSource.CreateLinkedTokenSource(token);round.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    if(_components.GetArguments("dpi")!=trial||!_components.GetStatuses().Any(s=>s.Id=="dpi"&&s.State==ServiceState.Running))await RestartYoutubeWinwsAsync(trial,round.Token);
                    // Leave time for returning already measured page/TLS evidence when media discovery times out.
                    var result=await YoutubeRouteProbe.CheckAsync(round.Token,TimeSpan.FromSeconds(6));
                    await LogToUiAsync("info",$"YouTube-only {spec.YouTube}: {result.Detail}; {result.Stages?.Tcp}; {result.Stages?.Tls}");return result;
                }
                catch(OperationCanceledException)when(!token.IsCancellationRequested){return new("youtube",ServiceHealthState.Partial,"Раунд YouTube не завершился за 8 секунд",ContentConfirmed:false);}
                catch(IOException ex){return new("youtube",ServiceHealthState.Partial,"Конфигурация не запустилась: "+YoutubeRouteProbe.Error(ex),ContentConfirmed:false);}
            },async(fresh,token)=>
            {
                var request=new AdvisorRequest(baseline.Level,string.IsNullOrWhiteSpace(profile.Provider)?"local":profile.Provider,network,profile.Mode,["youtube"],baseline.Tuning,SearchMode:searchMode,Scope:"youtube",PreserveNonYoutube:true);
                var history=_settings.LocalAdvisor.YoutubeHistory.Concat(_settings.LocalAdvisor.History).Where(h=>h.NetworkKey==network&&h.Level==baseline.Level&&h.Mode==profile.Mode&&h.Provider==request.Provider&&h.CheckedAt>DateTimeOffset.UtcNow.AddDays(-7)).OrderByDescending(h=>h.CheckedAt).SelectMany(h=>h.Recommendations).Where(r=>r.Strategy.YouTubeParameters is null||r.YoutubeSearchVersion==YoutubeParametersPolicy.SearchVersion).Take(2);
                return await _localStrategyModel.ProposeAsync(request,false,new[]{fresh}.Concat(history).ToArray(),token);
            },(message,count)=>{_ = Status("testing",message+$"; измерено конфигураций: {count}");},_advisorCts.Token,TimeSpan.FromMilliseconds(Math.Max(1,workMs-watch.ElapsedMilliseconds)),s=>WinwsProfileComposer.ComposeYoutubeOnly(command,baseline,s),searchMode);
        }
        catch(OperationCanceledException){failure="Подбор YouTube отменён";}
        catch(Exception ex){failure=YoutubeRouteProbe.Error(ex);}
        finally
        {
            await Status("restoring","Восстанавливаем исходный блок YouTube");
            using var restore=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                if(_protectionRequested&&!_advisorStopProtectionRequested&&(_components.GetArguments("dpi")!=command||!_components.GetStatuses().Any(s=>s.Id=="dpi"&&s.State==ServiceState.Running)))await RestartYoutubeWinwsAsync(command,restore.Token);
                else _components.SetArguments("dpi",command);
                if(!_protectionRequested||_advisorStopProtectionRequested||stopGeneration!=Volatile.Read(ref _userStopGeneration))throw new OperationCanceledException("Защита остановлена пользователем; результаты не готовы к применению");
                if(_activeConnection?.Id!=profile.Id||_components.GetArguments("dpi")!=command||!_components.GetStatuses().Any(s=>s.Id=="dpi"&&s.State==ServiceState.Running))throw new IOException("Исходный профиль или процесс winws не восстановлен");
            }
            catch(Exception ex){failure=ex is OperationCanceledException?"Защита остановлена пользователем; результаты не применяются":"Не удалось восстановить исходный winws: "+YoutubeRouteProbe.Error(ex);if(ex is not OperationCanceledException)_youtubeRollbackError=YoutubeRouteProbe.Error(ex);}
            _advisorTrialSpec=null;_advisorRunning=false;_advisorStopProtectionRequested=false;_advisorCts.Dispose();_advisorCts=null;
            await SendServiceSnapshotAsync();ResetHealthChecksAfterRouteChange();
        }
        if(network!=CurrentNetworkKey()||frozen!=YoutubeFrozen(GetConnection(profile.Id)))failure??="Сеть или контекст профиля изменились; результаты не предлагаются к применению";
        if(failure is not null||candidates.Count==0){await Status("failed",failure??"За бюджет не завершено измерений YouTube; исходная стратегия сохранена");return;}
        _youtubeRun=new(runId,profile.Id,network,frozen,command,baseline,DateTimeOffset.UtcNow.AddMinutes(30),stopGeneration,_components.GetStatuses().Single(s=>s.Id=="dpi").Generation,candidates);
        var control=candidates.FirstOrDefault(c=>c.Strategy==baseline);
        await PostUiAsync(new{type="youtube.advisor.status",state="complete",runId,startedAt=_youtubeStartedAt,revision=Interlocked.Increment(ref _youtubeRevision),readiness="ready",blockedReason=(string?)null,profileId=profile.Id,backupDirectory=backup,elapsedMs=watch.ElapsedMilliseconds,
            message=$"Измерено вариантов YouTube: {candidates.Count}. Исходный профиль восстановлен. Остальные сервисы не переподбирались.",firefox=FirefoxNetworkDiagnostics.Read(),
            candidates=candidates.Select(c=>new{id=c.Id,label=c.Strategy.YouTube,source=c.Strategy==baseline?"current":c.Strategy.YouTubeParameters is null?"reserve":"ai",parameters=c.Strategy.YouTubeParameters,searchVersion=YoutubeParametersPolicy.SearchVersion,pageConfirmed=c.PageConfirmed,mediaConfirmed=c.MediaConfirmed,fullyConfirmed=c.FullyConfirmed,samples=c.Samples.Count,latencyMs=c.MedianMs,pageMs=c.PageMedianMs,
                canApply=control is not null&&YoutubePersonalizedSearch.Improves(control,c),
                groups=c.Samples.SelectMany((s,i)=>(s.Groups??[]).Select(g=>new{id=g.Id,state=g.State,detail=$"Замер {i+1}: {g.Detail}"}))})});
    }
    private async Task ApplyYoutubeOnlyAsync(JsonElement root)
    {
        if(_youtubeApplying&&root.TryGetProperty("runId",out var duplicate)&&duplicate.GetString()==_youtubeRunId)return;
        if(_advisorRunning||_protectionTransitioning||_warpTransitioning||_connectionLaunching||_warpMediaCts is not null)throw new InvalidOperationException("Операция ещё выполняется. Дождитесь её завершения");
        var run=_youtubeRun??throw new InvalidOperationException("Сначала выполните отдельный подбор YouTube");
        if(!root.TryGetProperty("runId",out var requestId)||requestId.GetString()!=run.RunId)throw new InvalidOperationException("Это результат другого запуска. Повторите проверку YouTube");
        if(YoutubeBlockedReason(run) is {} blocked)throw new InvalidOperationException(blocked);
        var profile=GetConnection(run.ProfileId);RequireYoutubeProfile(profile);
        var candidate=run.Candidates.SingleOrDefault(c=>c.Id==root.GetProperty("id").GetString())??throw new ArgumentException("Измеренный вариант не найден");
        var control=run.Candidates.First(c=>c.Strategy==run.Baseline);
        if(run.Expires<DateTimeOffset.UtcNow||run.Network!=CurrentNetworkKey()||run.Frozen!=YoutubeFrozen(profile)||_components.GetArguments("dpi")!=run.Command)throw new InvalidOperationException("Профиль или сеть изменились. Повторите подбор только YouTube.");
        if(!YoutubePersonalizedSearch.Improves(control,candidate))throw new InvalidOperationException("Выигрыш не подтверждён; исходная стратегия сохраняется");
        if(!candidate.FullyConfirmed&&!(root.TryGetProperty("allowPartial",out var partial)&&partial.ValueKind==JsonValueKind.True))throw new ArgumentException("Подтвердите применение с непроверенными компонентами YouTube");
        var command=WinwsProfileComposer.ComposeYoutubeOnly(run.Command,run.Baseline,candidate.Strategy);YoutubeOnlyPolicy.AssertIsolated(run.Command,command);
        var previous=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;var oldActive=_activeConnection;
        _youtubeApplying=true;_advisorRunning=true;_advisorStopProtectionRequested=false;_advisorTrialSpec=run.Baseline;
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);operation.CancelAfter(TimeSpan.FromSeconds(20));
        _advisorCts=operation;
        try
        {
            await PostUiAsync(new{type="youtube.advisor.status",state="applying",runId=run.RunId,revision=Interlocked.Increment(ref _youtubeRevision),readiness="busy",profileId=profile.Id,message="Применяем только блок YouTube и проверяем TLS, страницу и картинку"});await SendServiceSnapshotAsync();
            await YoutubeApplyTransaction.RunAsync(token=>RestartYoutubeWinwsAsync(command,token),
                token=>YoutubeRouteProbe.CheckAsync(token,TimeSpan.FromSeconds(7)),async(evidence,token)=>
            {
                token.ThrowIfCancellationRequested();
                if(candidate.Samples.All(s=>YoutubeOnlyPolicy.Group(s,"avatars"))&&!YoutubeOnlyPolicy.Group(evidence,"avatars"))
                    throw new IOException("Аватарки каналов не прошли свежую проверку; исходный блок YouTube восстанавливается.");
                if(!_protectionRequested||_advisorStopProtectionRequested||run.StopGeneration!=Volatile.Read(ref _userStopGeneration))throw new OperationCanceledException("Защита остановлена пользователем");
                if(run.Network!=CurrentNetworkKey()||run.Frozen!=YoutubeFrozen(profile))throw new InvalidOperationException("Контекст изменился во время применения YouTube");
                YoutubeOnlyPolicy.AssertIsolated(run.Command,_components.GetArguments("dpi"));
                YoutubeRoutePersistence.Update(_settings,profile,run.Baseline,candidate.Strategy,run.Network,YoutubeOnlyPolicy.Signature(command),candidate.MediaConfirmed&&YoutubeOnlyPolicy.Group(evidence,"video"));
                var cacheProvider=string.IsNullOrWhiteSpace(profile.Provider)?"local":profile.Provider;
                var measured=run.Candidates.Take(3).Select(c=>LocalStrategyAdvisor.Rate(c.Strategy,cacheProvider,[LocalStrategyAdvisor.AggregateEvidence("youtube",c.Samples.Select(s=>(AdvisorServiceEvidence?)new AdvisorServiceEvidence("youtube",s.State.ToString(),s.ContentConfirmed==true,s.Detail,s.LatencyMs,null,1,s.Groups)).ToArray())]) with{YoutubeRuleSignature=YoutubeOnlyPolicy.Signature(WinwsProfileComposer.ComposeYoutubeOnly(run.Command,run.Baseline,c.Strategy))}).ToArray();
                _settings.LocalAdvisor.YoutubeHistory.Insert(0,new(run.Network,cacheProvider,profile.Mode,profile.Level,DateTimeOffset.UtcNow,measured,profile.TelegramWebRoute));
                _settings.LocalAdvisor.YoutubeHistory=_settings.LocalAdvisor.YoutubeHistory.Take(20).ToList();
                await _settingsStore.SaveAsync(_settings,token);token.ThrowIfCancellationRequested();_activeConnection=SavedConnectionProfilePolicy.Clone(profile);
            },operation.Token);
            _youtubeRun=null;
            await PostUiAsync(new{type="youtube.advisor.status",state="applied",runId=run.RunId,revision=Interlocked.Increment(ref _youtubeRevision),readiness="applied",profileId=profile.Id,message="Применён и сохранён только YouTube; свежая проверка TLS, страницы и картинки прошла. Откройте Firefox и проверьте видео и перемотку; затем Telegram и Discord."});
            await PublishConnectionsAsync();await LogToUiAsync("ok","Применён только YouTube; DNS, WARP, Discord, Telegram и игровые правила сохранены.");
        }
        catch(Exception failure)
        {
            _settings=previous;_activeConnection=oldActive;
            using var restore=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try{await YoutubeOnlyRollback.RestoreAsync(async token=>{if(_protectionRequested&&!_advisorStopProtectionRequested)await RestartYoutubeWinwsAsync(run.Command,token);},
                token=>_settingsStore.SaveAsync(_settings,token),failure,restore.Token);}
            catch(AggregateException rollback){_youtubeRollbackError=string.Join("; ",rollback.InnerExceptions.Skip(1).Select(YoutubeRouteProbe.Error));throw;}
            if(_youtubeRun is not null)_youtubeRun=run with{ComponentGeneration=_components.GetStatuses().FirstOrDefault(s=>s.Id=="dpi")?.Generation??-1};
            throw;
        }
        finally{_youtubeApplying=false;_advisorRunning=false;_advisorStopProtectionRequested=false;_advisorTrialSpec=null;_advisorCts=null;await SendServiceSnapshotAsync();}
    }
    private async Task ConfirmYoutubeBrowserAsync(JsonElement root)
    {
        if(_advisorRunning||_protectionTransitioning)return;
        var profile=GetConnection(root.GetProperty("profileId").GetString());RequireYoutubeProfile(profile);
        var proof=profile.YoutubeVerification??throw new InvalidOperationException("Нет актуального подтверждения YouTube");
        if(proof.Version!=YoutubeOnlyPolicy.ProbeVersion||proof.NetworkKey!=CurrentNetworkKey()||proof.StrategyKey!=profile.Strategy?.Key||proof.RuleSignature!=YoutubeOnlyPolicy.Signature(_components.GetArguments("dpi")))throw new InvalidOperationException("Контекст YouTube изменился; браузерное подтверждение не сохранено");
        var previous=SavedConnectionProfilePolicy.Clone(profile);
        profile.YoutubeVerification=proof with{BrowserConfirmed=true};
        if(profile.NetworkRoutes.TryGetValue(proof.NetworkKey,out var route))profile.NetworkRoutes[proof.NetworkKey]=route with{YoutubeVerification=profile.YoutubeVerification};
        try{await _settingsStore.SaveAsync(_settings,_lifetime.Token);}
        catch{var i=_settings.SavedConnections.FindIndex(p=>p.Id==profile.Id);_settings.SavedConnections[i]=previous;throw;}
        _activeConnection=SavedConnectionProfilePolicy.Clone(profile);await PublishConnectionsAsync();
        await PostUiAsync(new{type="youtube.advisor.status",state="browserConfirmed",runId=_youtubeRunId,revision=Interlocked.Increment(ref _youtubeRevision),profileId=profile.Id,message="Отмечено пользователем: Firefox открывает YouTube, видео и перемотка работают. Измеренные результаты страницы и медиа сохраняются отдельно."});
    }
}
