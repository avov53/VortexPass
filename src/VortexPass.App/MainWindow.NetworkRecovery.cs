using System.IO;
using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

namespace VortexPass.App;

public partial class MainWindow
{
    private bool _advisorAllowPartialDns;
    private bool _advisorDnsNeedsDiagnosis;
    private Func<CancellationToken,IEnumerable<string>,Task<IReadOnlyList<string>>> _advisorDnsLookup =
        (token,hosts)=>RouteProbePolicy.MissingWindowsDnsAsync(token,hosts);
    private int _userStopGeneration;
    private CancellationTokenSource? _dnsDiagnosticCts;
    private int _dnsDiagnosticGeneration;
    private readonly Dictionary<string,(DnsResolverChoice Choice,string Network,string Composition,DateTimeOffset Expires)> _dnsChoices=[];

    private static string[] ProfileDnsHosts(SavedConnectionProfile profile)
    {
        var flags=SavedConnectionProfilePolicy.ServiceFlags(profile);var services=new List<string>();
        if(flags.YouTube||flags.Web)services.Add("youtube");if(flags.Discord||flags.Web)services.Add("discord");if(flags.TelegramWeb&&profile.TelegramWebRoute!="warpProxy")services.Add("telegram");if(profile.Games.Contains("roblox"))services.Add("roblox");
        return RouteProbePolicy.DnsHostsForServices(services).Concat(profile.Domains).Concat(profile.SiteRules.Where(r=>r.Connection is "auto" or "direct").SelectMany(SiteRulePolicy.Domains)).Distinct().ToArray();
    }
    private async Task DiagnoseAdvisorDnsAsync(JsonElement root)
    {
        if(_advisorRunning||_protectionTransitioning)return;
        var draft=SavedConnectionProfilePolicy.Validate(SaveConnectionRequest.Read(root.GetProperty("draft")));
        _dnsDiagnosticCts?.Cancel();_dnsDiagnosticCts?.Dispose();_dnsDiagnosticCts=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation=++_dnsDiagnosticGeneration;
        var token=_dnsDiagnosticCts.Token;var network=CurrentNetworkKey();var composition=DraftRecommendationPolicy.Composition(draft);
        _dnsChoices.Clear();await PostUiAsync(new {type="advisor.dns.status",state="checking",composition,draftId=draft.Id});
        try
        {
            var hosts=ProfileDnsHosts(draft);var results=await DnsDiagnosticsService.CheckAsync(hosts,token);
            token.ThrowIfCancellationRequested();
            if(generation!=_dnsDiagnosticGeneration)return;
            if(network!=CurrentNetworkKey())throw new OperationCanceledException("Сеть изменилась",token);
            var choices=DnsDiagnosticsService.Choices(results,hosts);
            foreach(var choice in choices.Where(c=>c.Working))_dnsChoices[choice.Id]=(choice,network,composition,DateTimeOffset.UtcNow.AddMinutes(10));
            foreach(var result in results)await LogToUiAsync("info",$"DNS {result.Provider}/{result.Mode}: {result.Host} {result.RecordType}: {result.State}, {result.DurationMs} мс; {result.Detail}");
            await PostUiAsync(new {type="advisor.dns.status",state="complete",composition,draftId=draft.Id,
                choices=choices.Select(c=>new {id=c.Id,mode=c.Mode,provider=c.Provider,working=c.Working,missingHosts=c.MissingHosts}),
                results=results.Select(r=>new {mode=r.Mode,provider=r.Provider,host=r.Host,recordType=r.RecordType,state=r.State,durationMs=r.DurationMs,detail=r.Detail,addresses=r.Addresses})});
        }
        catch(OperationCanceledException){if(generation==_dnsDiagnosticGeneration)await PostUiAsync(new {type="advisor.dns.status",state="failed",error="Проверка DNS отменена или сеть изменилась"});}
        catch(Exception ex){if(generation==_dnsDiagnosticGeneration)await PostUiAsync(new {type="advisor.dns.status",state="failed",error=ex.Message});}
    }
    private async Task ApplyAdvisorDnsAsync(JsonElement root)
    {
        if(_advisorRunning||_protectionTransitioning)return;
        var id=root.GetProperty("choiceId").GetString()??"";
        var draft=SavedConnectionProfilePolicy.Validate(SaveConnectionRequest.Read(root.GetProperty("draft")));
        if(!_dnsChoices.TryGetValue(id,out var tested)||tested.Network!=CurrentNetworkKey()||tested.Expires<DateTimeOffset.UtcNow||tested.Composition!=DraftRecommendationPolicy.Composition(draft))
            throw new ArgumentException("Профиль или сеть изменились. Повторите проверку DNS.");
        _settings.DnsMode=tested.Choice.Mode;
        if(tested.Choice.Mode=="google-udp")_settings.GoogleDnsAddress=tested.Choice.Provider;
        if(tested.Choice.Mode=="doh")_settings.SelectedDnsProvider=tested.Choice.Provider;
        await RestartSelectedDnsAsync();await PublishSettingsStatusAsync();_dnsChoices.Clear();
        await LogToUiAsync("info",$"По выбору пользователя применён DNS: {tested.Choice.Mode}/{tested.Choice.Provider}. Подбор запускается заново.");
        await RunConnectionAdvisorAsync(root);
    }
    private string[] AdvisorServiceDnsHosts(string service)=>service.StartsWith("site-rule:",StringComparison.Ordinal)
        ? _settings.CurrentSiteRules.Where(r=>"site-rule:"+r.Id==service&&r.Connection is "auto" or "direct").SelectMany(SiteRulePolicy.Domains).ToArray()
        : service=="telegram"&&_settings.TelegramWebRoute=="warpProxy"?[]
        : RouteProbePolicy.DnsHostsForServices([service]);
    private async Task<IReadOnlyList<string>> ConfirmAdvisorDnsAsync(AdvisorRequest request,CancellationToken token,bool requireReady=true)
    {
        var hosts=request.Services.SelectMany(AdvisorServiceDnsHosts).Distinct().ToArray();
        var missing=await _advisorDnsLookup(token,hosts);
        if(missing.Count>0)
        {
            await Task.Delay(300,token);
            missing=await _advisorDnsLookup(token,missing);
        }
        if(missing.Count==0)return missing;
        var measurable=request.Services.Where(s=>s is not "discord-voice" and not "telegram-desktop" and not "deadbydaylight").ToArray();
        var blocked=measurable.Where(s=>AdvisorServiceDnsHosts(s).Any(h=>missing.Contains(h,StringComparer.OrdinalIgnoreCase))).ToArray();
        foreach(var host in missing)await LogToUiAsync("warn",$"DNS домен не разрешён: {host}; сервисы: {string.Join(", ",request.Services.Where(s=>AdvisorServiceDnsHosts(s).Contains(host)))}");
        // Only initial preparation can require an explicit partial-run choice.
        // A failed trial must become evidence, not erase all previous trials.
        if(!requireReady)return missing;
        await PostUiAsync(new {type="advisor.dns.problem",draftId=_advisorDraft?.Id??_settings.SelectedConnectionId,missing,blockedServices=blocked,canContinue=blocked.Length<measurable.Length});
        if(!_advisorAllowPartialDns||blocked.Length==measurable.Length)
        {
            _advisorDnsNeedsDiagnosis=true;
            throw new IOException($"DNS не разрешает: {string.Join(", ",missing)}. Сервисы: {string.Join(", ",blocked)}. Проверьте DNS в мастере"+(blocked.Length<measurable.Length?" или продолжите с ограничениями.":"; нет доступных сервисов для подбора."));
        }
        return missing;
    }

    private async Task DiagnoseRobloxPlayerAsync()
    {
        if(_advisorRunning||_protectionTransitioning)return;
        await PostUiAsync(new {type="roblox.diagnostic",state="checking",detail="Читаем свежий журнал Roblox Player и проверяем публичные ресурсы…"});
        try
        {
            var result=await RobloxContentProbeService.DiagnosePlayerAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Roblox","logs"),_lifetime.Token);
            await PostUiAsync(new {type="roblox.diagnostic",state=result.State,detail=result.Detail,hosts=result.Hosts,resources=result.Resources.Select(r=>new {host=r.Host,kind=r.Kind,state=r.State,detail=r.Detail})});
            foreach(var check in result.Resources)await LogToUiAsync("info",$"Roblox Player {check.Host}: {check.State}; {check.Detail}");
        }
        catch(Exception ex){await PostUiAsync(new {type="roblox.diagnostic",state="failed",detail=ex is IOException?"Журнал Player сейчас недоступен":ex.GetType().Name});}
    }
    private async Task OptimizeRobloxConnectionAsync(bool forceCheck=false)
    {
        if(_advisorRunning||_protectionTransitioning||!_protectionRequested||!_settings.GameFilter.SelectedGames.Contains("roblox")||_settings.GameFilter.RobloxAssetsMode=="standard"&&!forceCheck)return;
        _advisorRunning=true;_advisorStopProtectionRequested=false;_advisorCts=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _advisorCts.CancelAfter(TimeSpan.FromSeconds(120));
        var token=_advisorCts.Token;var original=_components.GetArguments("dpi");var winner=original;var network=CurrentNetworkKey();string? winnerBlock=null;
        try
        {
            await SendServiceSnapshotAsync();
            async Task<(int Score,long? Spread,long? Median,bool Needs,ServiceRouteGroup[] Stable)> Measure()
            {
                var checks=new List<(bool Confirmed,ServiceRouteGroup[] Groups,long? Latency)>();
                for(var i=0;i<3;i++){token.ThrowIfCancellationRequested();checks.Add(await RobloxContentProbeService.CheckAsync(token));}
                var times=checks.Where(c=>c.Latency.HasValue).Select(c=>c.Latency!.Value).ToArray();
                var stable=checks[0].Groups.Select(g=>g with {State=checks.All(c=>c.Groups.Any(v=>v.Id==g.Id&&v.State=="confirmed"))?"confirmed":"unconfirmed"}).ToArray();
                return(RobloxSelectionPolicy.Score(stable),times.Length>1?times.Max()-times.Min():null,LocalStrategyAdvisor.Median(times),checks.Count(c=>RobloxSelectionPolicy.NeedsEnhancement(c.Groups))>=2,stable);
            }
            await LogToUiAsync("info","Roblox: проверяем текущий маршрут контента; игровые UDP-правила сохраняются.");
            var baseline=await Measure();var best=baseline;
            if(!baseline.Stable.Any(g=>g.Id=="dns"&&g.State=="confirmed"))
            {await LogToUiAsync("warn","Roblox: Windows DNS не подтвердил адреса картинок. Перебор TCP остановлен; исходные правила сохранены.");return;}
            // Unknown models alone are not evidence of a broken network.
            if(_settings.GameFilter.RobloxAssetsMode!="standard"&&(_settings.GameFilter.RobloxAssetsMode=="enhanced"||baseline.Needs))
            foreach(var block in new[]{"minimal","split","disorder"}.Where(b=>AdvisorStrategyPolicy.Blocks(_settings.BypassLevel).Contains(b)))
            {
                if(network!=CurrentNetworkKey())throw new OperationCanceledException("Сеть изменилась",token);
                var command=WinwsProfileComposer.ApplyRobloxTcpBlock(original,block,_settings.BypassLevel);
                await LogToUiAsync("info",$"Roblox: испытываем {block}, три замера.");
                await RestartAdvisorWinwsAsync(command,token);var measured=await Measure();
                if(!RobloxSelectionPolicy.Preserves(baseline.Stable,measured.Stable))continue;
                if(!RobloxSelectionPolicy.HasConfirmedContent(measured.Stable))continue;
                if(measured.Score>best.Score||measured.Score==best.Score&&measured.Score>baseline.Score&&(measured.Spread??long.MaxValue)<(best.Spread??long.MaxValue))
                {best=measured;winner=command;winnerBlock=block;}
            }
            if(winner!=original&&winnerBlock is not null)
            {
                await RestartAdvisorWinwsAsync(winner,token);var fresh=await Measure();
                if(network!=CurrentNetworkKey())throw new OperationCanceledException("Сеть изменилась",token);
                if(!RobloxSelectionPolicy.HasConfirmedContent(fresh.Stable)||!RobloxSelectionPolicy.Preserves(best.Stable,fresh.Stable))throw new IOException("Roblox: свежая проверка контента не подтвердила результат");
                var saved=_settings.SavedConnections.FirstOrDefault(p=>p.Id==_activeConnection?.Id);
                if(saved?.Strategy is {} strategy)
                {
                    var previous=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
                    try
                    {
                        saved.Strategy=strategy with {Roblox=winnerBlock};
                        if(saved.NetworkRoutes.TryGetValue(network,out var route))saved.NetworkRoutes[network]=route with {Strategy=saved.Strategy};
                        if(_settings.LocalAdvisor.AppliedStrategy==strategy)_settings.LocalAdvisor.AppliedStrategy=saved.Strategy;
                        await _settingsStore.SaveAsync(_settings,token);_activeConnection=SavedConnectionProfilePolicy.Clone(saved);
                    }
                    catch{_settings=previous;winner=original;throw;}
                }
                else throw new IOException("Roblox: нет сохранённой исходной стратегии для безопасной записи; исходный маршрут восстанавливается");
            }
            await LogToUiAsync(winner==original?"info":"ok",winner==original?"Roblox: улучшение не подтверждено; исходный маршрут сохранён. Модели проверяйте в игре.":"Roblox: выбран улучшившийся маршрут контента. Проверки не подтверждают всю игровую сессию.");
        }
        catch(OperationCanceledException){winner=original;await LogToUiAsync("info","Проверка Roblox отменена; исходный маршрут восстанавливается.");}
        catch(Exception ex){winner=original;await LogToUiAsync("warn",$"Усиление Roblox не применено: {ex.GetType().Name}");}
        finally
        {
            try
            {
                if(_shutdownStarted||_lifetime.IsCancellationRequested) { /* Shutdown owns component cleanup. */ }
                else if(_advisorStopProtectionRequested)await StopProtectionAsync();
                else if(_protectionRequested&&_components.GetArguments("dpi")!=winner)await RestartAdvisorWinwsAsync(winner,CancellationToken.None);
            }
            finally{_advisorRunning=false;_advisorCts?.Dispose();_advisorCts=null;_advisorStopProtectionRequested=false;ResetHealthChecksAfterRouteChange();await SendServiceSnapshotAsync();}
        }
    }
}
