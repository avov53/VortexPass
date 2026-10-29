using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public interface ILocalStrategyModel
{
    Task<IReadOnlyList<AdvisorStrategySpec>> ProposeAsync(AdvisorRequest request, bool experimental,
        IReadOnlyList<AdvisorRecommendation> evidence, CancellationToken cancellationToken);
}

public interface IAdvisorTrialSession : IAsyncDisposable
{
    AdvisorStrategySpec? CurrentStrategy => null;
    string ConfigurationKey(AdvisorStrategySpec strategy) => strategy.Key;
    Task<IReadOnlyList<AdvisorServiceEvidence>> CheckCurrentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AdvisorServiceEvidence>>([]);
    Task<IReadOnlyList<AdvisorServiceEvidence>> TestAsync(AdvisorStrategySpec strategy, CancellationToken cancellationToken);
    Task<IReadOnlyList<AdvisorServiceEvidence>> TestSamplesAsync(AdvisorStrategySpec strategy,int samples,CancellationToken token)=>TestAsync(strategy,token);
    Task<IReadOnlyList<AdvisorServiceEvidence>> CheckCurrentSamplesAsync(int samples,CancellationToken token)=>CheckCurrentAsync(token);
    void BeginYoutubePhase(AdvisorStrategySpec baseline) { }
}

public sealed class LocalStrategyAdvisor(ILocalStrategyModel model,TimeSpan? searchBudget=null,TimeSpan? modelBudget=null)
{
    public const int RequiredSamples = 3;
    public async Task<IReadOnlyList<AdvisorRecommendation>> RunAsync(AdvisorRequest request,
        Func<CancellationToken, Task<IAdvisorTrialSession>> openSession, Action<AdvisorProgress> progress,
        IReadOnlyList<AdvisorRecommendation> history, CancellationToken cancellationToken)
    {
        AdvisorStrategyPolicy.NormalizeProvider(request.Provider);
        if(request.Scope!="all")throw new ArgumentException("Для отдельного YouTube используется изолированный YoutubeOnlySearch");
        if(request.Services.Count==0)throw new ArgumentException("Выберите хотя бы один сервис.");
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(searchBudget??TimeSpan.FromSeconds(request.SearchMode=="extended"?345:165));
        var token=budget.Token;var results=new List<AdvisorRecommendation>();IAdvisorTrialSession? session=null;
        var watch=System.Diagnostics.Stopwatch.StartNew();var totalBudget=searchBudget??TimeSpan.FromSeconds(request.SearchMode=="extended"?345:165);
        AdvisorRecommendation Measured(AdvisorStrategySpec spec,IReadOnlyList<AdvisorServiceEvidence> evidence)=>Rate(spec,request.Provider,
            request.Services.Select(id=>evidence.FirstOrDefault(s=>s.Service==id)??new AdvisorServiceEvidence(id,"incomplete",false,"Измерение не завершено",null,null,0)).ToArray());
        try
        {
            session=await openSession(token);
            using var modelDeadline=CancellationTokenSource.CreateLinkedTokenSource(token);
            IReadOnlyList<AdvisorRecommendation> modelEvidence=history;
            async Task<IReadOnlyList<AdvisorStrategySpec>> Propose()
            {
                try{return await model.ProposeAsync(request,false,modelEvidence,modelDeadline.Token).WaitAsync(modelDeadline.Token);}
                catch(Exception ex)when(ex is OperationCanceledException or TimeoutException or IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
                {progress(new("testing",ex is OperationCanceledException or TimeoutException?
                    "Время ответа модели истекло; продолжаем подбор встроенными вариантами — штатный резерв":
                    "Локальная модель не вернула допустимый результат; продолжаем подбор встроенными вариантами"));return [];}
            }
            var proxyOnly=request.TelegramWebRoute=="warpProxy"&&request.Services.All(s=>s is "telegram" or "telegram-desktop");
            var control=session.CurrentStrategy??new(){Level=request.Level,Tuning=request.PreferredTuning};
            progress(new("testing","Исходная диагностика сети для локальной модели",0,4,1));
            var baseline=await session.CheckCurrentSamplesAsync(1,token);
            if(baseline.Count>0)results.Add(Measured(control,baseline));
            modelEvidence=new[]{Measured(control,baseline)}.Concat(history.Take(2)).ToArray();
            modelDeadline.CancelAfter(modelBudget??TimeSpan.FromSeconds(request.SearchMode=="extended"?40:20));
            if(!proxyOnly)progress(new("testing","Локальный ИИ анализирует свежую диагностику сети",Phase:2));
            var proposal=proxyOnly?Task.FromResult<IReadOnlyList<AdvisorStrategySpec>>([]):Propose();
            var proposed=await proposal;
            var youtubeSelected=request.Services.Contains("youtube");
            if(youtubeSelected)
            {
                results=await RunYoutubeStagesAsync(request,session,control,baseline,proposed,progress,token,()=>totalBudget-watch.Elapsed);
            }
            else
            {
            var preferredYoutube=YoutubePagePassed(baseline.FirstOrDefault(s=>s.Service=="youtube"))?control.YouTube:
                AdvisorStrategyPolicy.YoutubeBlocks(request.Level).Contains("youtube-tls")?"youtube-tls":"youtube-segment";
            var blocks=AdvisorStrategyPolicy.Blocks(request.Level).Where(b=>b.StartsWith("flowseal-",StringComparison.Ordinal)).Take(2).ToArray();
            if(blocks.Length==0)blocks=["minimal","split"];
            var seeds=blocks.Select(b=>control with {YouTube=youtubeSelected?preferredYoutube:control.YouTube,
                Discord=request.Services.Contains("discord")?b:control.Discord,
                Telegram=request.TelegramWebRoute=="warpProxy"?"baseline":request.Services.Contains("telegram")?b:control.Telegram});
            var candidates=proposed.Concat(seeds).Select(s=>s with {Tuning=request.PreferredTuning,
                YouTube=youtubeSelected?(request.SearchMode=="extended"?s.YouTube:preferredYoutube):control.YouTube,
                YouTubeParameters=control.YouTubeParameters,
                Telegram=request.TelegramWebRoute=="warpProxy"?"baseline":s.Telegram});
            if(request.Services.Contains("roblox")&&request.RobloxAssetsMode!="standard"&&
                (request.RobloxAssetsMode=="enhanced"||request.SearchMode=="extended"&&RobloxSelectionPolicy.NeedsEnhancement(baseline.FirstOrDefault(s=>s.Service=="roblox")?.Groups)))
                candidates=new[]{control with {Roblox="minimal",YouTube=youtubeSelected?preferredYoutube:control.YouTube},control with {Roblox="split",YouTube=youtubeSelected?preferredYoutube:control.YouTube},control with {Roblox="disorder",YouTube=youtubeSelected?preferredYoutube:control.YouTube}}
                    .Where(s=>AdvisorStrategyPolicy.Blocks(request.Level).Contains(s.Roblox)).Concat(candidates);
            if(youtubeSelected)candidates=new[]{control with{YouTube=preferredYoutube,Telegram=request.TelegramWebRoute=="warpProxy"?"baseline":control.Telegram}}.Concat(candidates);
            var screened=new HashSet<string>{session.ConfigurationKey(control)};
            foreach(var spec in candidates.DistinctBy(session.ConfigurationKey).Where(s=>!screened.Contains(session.ConfigurationKey(s))).Take(request.SearchMode=="extended"?6:3))
            {
                token.ThrowIfCancellationRequested();AdvisorStrategyPolicy.Validate(spec,request.Level);
                if(!screened.Add(session.ConfigurationKey(spec)))continue;
                progress(new("testing","Короткий отбор конфигураций",screened.Count-1,request.SearchMode=="extended"?7:4));
                var evidence=await session.TestSamplesAsync(spec,1,token);results.Add(Measured(spec,evidence));
            }
            var finalists=Rank(results.Where(r=>RobloxSelectionPolicy.Preserves(baseline.FirstOrDefault(s=>s.Service=="roblox")?.Groups,r.Services.FirstOrDefault(s=>s.Service=="roblox")?.Groups))).Take(2).ToArray();
            foreach(var finalist in finalists)
            {
                token.ThrowIfCancellationRequested();progress(new("testing","Три подтверждающих замера лучшего варианта",0,2));
                var evidence=await session.TestSamplesAsync(finalist.Strategy,RequiredSamples,token);
                results.RemoveAll(r=>session.ConfigurationKey(r.Strategy)==session.ConfigurationKey(finalist.Strategy));
                results.Add(Measured(finalist.Strategy,evidence));
            }
            }
        }
        catch(OperationCanceledException)when(!cancellationToken.IsCancellationRequested)
        {progress(new("restoring","Время поиска истекло; сохраняем завершённые измерения",Phase:4));}
        finally
        {
            budget.Cancel();progress(new("restoring","Восстанавливаем исходное соединение",Phase:4));
            if(session is not null)await session.DisposeAsync();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if(results.Count==0)throw new InvalidOperationException("За отведённое время не завершено ни одного измерения. Проверьте DNS и маршрут.");
        return Rank(results).Take(3).ToArray();
    }
    public static IReadOnlyList<AdvisorRecommendation> Rank(IEnumerable<AdvisorRecommendation> results) => results
        .OrderByDescending(r => r.FullyConfirmed).ThenByDescending(r => r.Confirmed)
        .ThenByDescending(r=>YoutubePagePassed(r.Services.FirstOrDefault(s=>s.Service=="youtube")))
        .ThenByDescending(r => r.Services.Count(s => s.State is "healthy" or "partial"))
        .ThenByDescending(r=>RobloxSelectionPolicy.Score(r.Services.FirstOrDefault(s=>s.Service=="roblox")?.Groups))
        .ThenBy(r => r.JitterMs ?? long.MaxValue).ThenBy(r=>r.Services.FirstOrDefault(s=>s.Service=="youtube")?.Groups?.FirstOrDefault(g=>g.Id=="page")?.DurationMs??long.MaxValue).ThenBy(r => r.MedianLatencyMs ?? long.MaxValue)
        .ThenBy(r => r.Strategy.Experimental).ToArray();

    private static async Task<List<AdvisorRecommendation>> RunYoutubeStagesAsync(AdvisorRequest request,IAdvisorTrialSession session,AdvisorStrategySpec control,
        IReadOnlyList<AdvisorServiceEvidence> baseline,IReadOnlyList<AdvisorStrategySpec> proposed,Action<AdvisorProgress> progress,CancellationToken token,Func<TimeSpan> remaining)
    {
        var baseSpec=control;var baseEvidence=baseline;
        var others=baseline.Where(s=>s.Service!="youtube"&&s.State!="sessionRequired").ToArray();
        var protectedIds=others.Where(s=>s.Confirmed||s.State=="healthy").Select(s=>s.Service).ToHashSet();
        int OtherScore(IReadOnlyList<AdvisorServiceEvidence> e)=>e.Count(s=>s.Service!="youtube"&&(s.Confirmed||s.State=="healthy"));
        if(!request.PreserveNonYoutube&&others.Any(s=>!s.Confirmed&&s.State!="healthy"))
        {
            var seeds=AdvisorStrategyPolicy.Blocks(request.Level).Where(b=>b.StartsWith("flowseal-",StringComparison.Ordinal)).Take(2).Select(b=>control with{
                Discord=request.Services.Contains("discord")?b:control.Discord,
                Telegram=request.Services.Contains("telegram")&&request.TelegramWebRoute!="warpProxy"?b:control.Telegram});
            var seen=new HashSet<string>{session.ConfigurationKey(control)};var attempts=0;
            foreach(var suggestion in proposed.Concat(seeds))
            {
                if(attempts>=(request.SearchMode=="extended"?4:2)||remaining()<TimeSpan.FromSeconds(72))break;
                var candidate=suggestion with{Level=control.Level,Tuning=control.Tuning,YouTube=control.YouTube,YouTubeParameters=control.YouTubeParameters,YouTubeTransport=control.YouTubeTransport};
                if(protectedIds.Count>0)candidate=candidate with{RouteTemplate=control.RouteTemplate,TcpRepeats=control.TcpRepeats,QuicRepeats=control.QuicRepeats,Overlap=control.Overlap,DiscordVoice=control.DiscordVoice};
                if(protectedIds.Contains("discord")||!request.Services.Contains("discord"))candidate=candidate with{Discord=control.Discord};
                if(protectedIds.Contains("telegram")||request.TelegramWebRoute=="warpProxy"||!request.Services.Contains("telegram"))candidate=candidate with{Telegram=control.Telegram};
                if(!request.Services.Contains("roblox"))candidate=candidate with{Roblox=control.Roblox};
                try{AdvisorStrategyPolicy.Validate(candidate,request.Level);}catch(ArgumentException){continue;}
                if(!seen.Add(session.ConfigurationKey(candidate)))continue;
                attempts++;progress(new("testing","Выбираем основу остальных сервисов перед изолированным YouTube",attempts,2));
                var evidence=await session.TestSamplesAsync(candidate,1,token);
                if(protectedIds.Any(id=>!evidence.Any(s=>s.Service==id&&(s.Confirmed||s.State=="healthy"))))continue;
                if(OtherScore(evidence)>OtherScore(baseEvidence)){baseSpec=candidate;baseEvidence=evidence;}
            }
        }
        session.BeginYoutubePhase(baseSpec);
        var allEvidence=new Dictionary<ServiceHealthResult,IReadOnlyList<AdvisorServiceEvidence>>(ReferenceEqualityComparer.Instance);
        var baseYoutube=baseEvidence.FirstOrDefault(s=>s.Service=="youtube");
        var baseProbe=baseYoutube is null?null:new ServiceHealthResult("youtube",baseYoutube.Confirmed?ServiceHealthState.Healthy:ServiceHealthState.Partial,baseYoutube.Detail,baseYoutube.LatencyMs,ContentConfirmed:baseYoutube.Confirmed,Groups:baseYoutube.Groups,SystemDnsReady:baseYoutube.SystemDnsReady);
        if(baseProbe is not null)allEvidence[baseProbe]=baseEvidence;
        var candidates=await YoutubePersonalizedSearch.RunAsync(baseSpec,async(spec,ct)=>
        {
            YoutubeOnlyPolicy.Validate(baseSpec,spec);
            var measured=await session.TestSamplesAsync(spec,1,ct);
            var youtube=measured.FirstOrDefault(e=>e.Service=="youtube")??new("youtube","incomplete",false,"Измерение не завершено",null,null,0);
            var result=new ServiceHealthResult("youtube",youtube.Confirmed?ServiceHealthState.Healthy:ServiceHealthState.Partial,youtube.Detail,youtube.LatencyMs,ContentConfirmed:youtube.Confirmed,Groups:youtube.Groups,SystemDnsReady:youtube.SystemDnsReady);
            allEvidence[result]=measured;return result;
        },(_,_)=>Task.FromResult(proposed),(message,count)=>progress(new("testing",message,count,request.SearchMode=="extended"?7:4)),token,
            TimeSpan.FromMilliseconds(Math.Max(1,remaining().TotalMilliseconds)),session.ConfigurationKey,request.SearchMode,provider:request.Provider,baselineMeasurement:baseProbe);
        var baselineCandidate=candidates.FirstOrDefault(c=>c.Strategy==baseSpec);
        return candidates.Where(c=>baselineCandidate is null||!baselineCandidate.PageConfirmed||c.Strategy==baseSpec||YoutubePersonalizedSearch.Improves(baselineCandidate,c)).Select(candidate=>
        {
            var rows=candidate.Samples.Select(s=>allEvidence[s]).ToArray();
            var evidence=request.Services.Select(id=>AggregateEvidence(id,rows.Select(row=>row.FirstOrDefault(s=>s.Service==id)).ToArray())).ToArray();
            return Rate(candidate.Strategy,request.Provider,evidence);
        }).Where(r=>r.Strategy==baseSpec||protectedIds.All(id=>r.Services.Any(s=>s.Service==id&&s.Confirmed))).ToList();
    }

    public static AdvisorServiceEvidence AggregateEvidence(string id,IReadOnlyList<AdvisorServiceEvidence?> samples)
    {
        var actual=samples.Where(s=>s is not null).Select(s=>s!).ToArray();
        if(actual.Length==0)return new(id,"incomplete",false,"Измерение не завершено",null,null,0);
        var last=actual[^1];var confirmed=actual.Length==samples.Count&&actual.All(s=>s.Confirmed);
        var count=actual.All(s=>s.Samples>=RequiredSamples)?RequiredSamples:actual.Length;
        var groups=actual.SelectMany(s=>s.Groups??[]).Select(g=>g.Id).Distinct().Select(group=>
        {
            var entries=samples.Select(s=>s?.Groups?.FirstOrDefault(g=>g.Id==group)).ToArray();var end=entries.LastOrDefault(g=>g is not null)!;
            return end with{State=entries.All(g=>g?.State=="confirmed")?"confirmed":entries.FirstOrDefault(g=>g?.State!="confirmed")?.State??"incomplete",DurationMs=Median(entries.Where(g=>g?.DurationMs is not null).Select(g=>g!.DurationMs!.Value))};
        }).ToArray();
        var times=actual.Where(s=>s.LatencyMs.HasValue).Select(s=>s.LatencyMs!.Value).ToArray();
        return last with{Confirmed=confirmed,Samples=count,Groups=groups,LatencyMs=Median(times),JitterMs=times.Length>1?times.Max()-times.Min():last.JitterMs};
    }

    public static bool YoutubePagePassed(AdvisorServiceEvidence? evidence)=>evidence?.Groups?.Any(g=>g.Id=="page"&&g.State=="confirmed")==true;

    public static AdvisorRecommendation Rate(AdvisorStrategySpec spec, string provider, IReadOnlyList<AdvisorServiceEvidence> services)
    {
        services = services.Select(s => s with { Confirmed = s.Confirmed && s.Samples >= RequiredSamples }).ToArray();
        var measurable = services.Where(s => s.State != "sessionRequired").ToArray();
        var confirmed = measurable.Count(s => s.Confirmed && s.Samples >= RequiredSamples);
        var full = measurable.Length > 0 && confirmed == measurable.Length;
        var latency = Median(services.Where(s => s.LatencyMs.HasValue).Select(s => s.LatencyMs!.Value));
        var jitter = Median(services.Where(s => s.JitterMs.HasValue).Select(s => s.JitterMs!.Value));
        var reason = full ? "Все выбранные проверки подтверждены повторными замерами."
            : $"Подтверждено {confirmed}/{measurable.Length}; не подтверждены: {string.Join(", ", measurable.Where(s => !s.Confirmed || s.Samples < RequiredSamples).Select(s => s.Service))}.";
        if (services.Any(s => s.State == "sessionRequired")) reason += " Voice/Desktop проверяются отдельно в активной сессии.";
        if(YoutubePagePassed(services.FirstOrDefault(s=>s.Service=="youtube")))reason+=" Страница YouTube отвечает; подтверждение видео учитывается отдельно.";
        reason += latency.HasValue ? $" Медиана транспортного ответа {latency} мс." : " Задержка не измерена.";
        return new(Guid.NewGuid().ToString("N"), spec, provider, confirmed, measurable.Length, full, latency, jitter, reason, services, RouteProbePolicy.Version,
            YoutubeProbeVersion:services.Any(s=>s.Service=="youtube"&&s.Groups?.Any(g=>g.Id=="page")==true)?YoutubeOnlyPolicy.ProbeVersion:0,YoutubeSearchVersion:spec.YouTubeParameters is null?0:YoutubeParametersPolicy.SearchVersion);
    }

    public static long? Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
