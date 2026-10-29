using System.IO;
using VortexPass.Core.Services;

namespace VortexPass.App;
public partial class MainWindow
{
    private bool _warpRouteConfirmed;
    private WarpTransportVariant? _activeWarpVariant;
    private WarpAttemptObservation? _activeWarpObservation;
    private string _warpBootstrapMode="default";
    private string? _advisorWarpProofKey;
    private async Task ApplyWarpGuiPolicyAsync(CancellationToken token=default)
    {
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(token,_lifetime.Token);token=operation.Token;
        if(!_settings.Warp.SuppressGui)return;
        var found=await _warpService.GetStatusAsync(token);
        if(found.AppPath is null)return;
        await _warpService.ValidateConsumerAsync(token);
        var entries=WarpGuiPolicy.Read(found.AppPath);
        foreach(var entry in entries)
            if(!_settings.Warp.GuiStartupBackup.Any(e=>e.Hive==entry.Hive&&e.View==entry.View))_settings.Warp.GuiStartupBackup.Add(entry);
        await _settingsStore.SaveAsync(_settings,token);
        WarpGuiPolicy.Disable(entries);
        WarpGuiPolicy.CloseWindows(found.AppPath);
    }
    private async Task RestoreWarpGuiStartupAsync()
    {
        if(_warpTransitioning||_advisorRunning)return;
        await _warpService.ValidateConsumerAsync(_lifetime.Token);
        var executable=_warpService.Detect().AppPath??throw new IOException("Интерфейс Cloudflare не установлен");
        WarpGuiPolicy.Restore(_settings.Warp.GuiStartupBackup,executable);
        _settings.Warp.SuppressGui=false;
        await _settingsStore.SaveAsync(_settings,_lifetime.Token);
        await PublishWarpStatusAsync();
    }
    private string WarpProofNetworkKey(string? version)=>WarpPreflight.CacheKey(_settings,version);
    private IReadOnlyList<WarpTransportVariant> CurrentWarpCandidates()
    {
        return WarpPreflight.Candidates(_settings,_warpStatus.Version??WarpClientVersionKey());
    }
    private string WarpClientVersionKey()
    {
        var cli=_warpService.Detect().CliPath;
        try{return cli is null?"absent":System.Diagnostics.FileVersionInfo.GetVersionInfo(cli).FileVersion??"unknown";}
        catch(Exception ex)when(ex is IOException or System.ComponentModel.Win32Exception){return "unknown";}
    }
    private async Task<WarpStatus> ConnectSelectiveWarpAsync(CancellationToken token)
    {
        var originalBootstrapMode=_warpBootstrapMode;var bootstrapWasActive=_warpBootstrap.IsActive;
        _warpRouteConfirmed=false;_activeWarpObservation=null;
        using var total=CancellationTokenSource.CreateLinkedTokenSource(token);total.CancelAfter(TimeSpan.FromSeconds(60));
        var before=await _warpService.GetStatusAsync(total.Token);
        if(!before.Registered)throw new IOException("API регистрации WARP: устройство не зарегистрировано. Откройте диагностику WARP; повторная установка не требуется.");
        var native=await _warpService.CaptureNativeConfigurationAsync(total.Token);
        var wasConnected=before.State==WarpConnectionState.Connected;
        var ownsSnapshot=_settings.PendingConnectionWarpRestore is null;
        if(ownsSnapshot)
        {
            var copy=System.Text.Json.JsonSerializer.Deserialize<VortexPass.Core.Models.WarpSettings>(System.Text.Json.JsonSerializer.Serialize(_settings.Warp))!;
            _settings.PendingConnectionWarpRestore=new(copy,wasConnected,native,originalBootstrapMode);
            if(!_warpSettingsTransactionActive)await _settingsStore.SaveAsync(_settings,total.Token);
        }
        var cacheKey=WarpProofNetworkKey(before.Version);
        var variants=WarpPreflight.Candidates(_settings,before.Version);
        await LogToUiAsync("info",$"WARP: выбор={WarpTransportPolicy.Selection(_settings.Warp)}; источник={_settings.Warp.TransportSelectionSource}; вариантов={variants.Count}; "+string.Join(", ",variants.Select(v=>$"{v.Masque}/{v.Bootstrap}")));
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var variant=await WarpPreflight.RunAsync(variants,async(candidate,attempt,deadline)=>
            {
                var message=$"WARP: {candidate.Masque}, HTTPS {candidate.Bootstrap}; попытка {attempt}/{variants.Count}";
                await LogToUiAsync("info",message);
                var attemptStarted=DateTimeOffset.UtcNow;
                try
                {
                await _warpBootstrap.StopAsync();_warpBootstrapMode=candidate.Bootstrap;
                if(WarpPreflight.RequiresBootstrap(candidate.Bootstrap))await _warpBootstrap.StartAsync(WinwsProfileComposer.ComposeWarpBootstrap(candidate.Bootstrap),_protectionRequested,deadline);
                await LogToUiAsync("info",$"WARP winws: {candidate.Bootstrap}; активен={_warpBootstrap.IsActive}; PID={_warpBootstrap.ProcessId?.ToString()??"нет"}");
                async Task Report(string stage)=>await PostUiAsync(new{type="warp.preflight",stage,message=message+" · "+stage+$" · осталось {Math.Max(0,60-(int)watch.Elapsed.TotalSeconds)} сек.",attempt,total=variants.Count,remainingSeconds=Math.Max(0,60-(int)watch.Elapsed.TotalSeconds)});
                await Report("Настройка локального прокси");
                await _warpService.SetConnectedAsync(false,deadline);
                await _warpService.ApplyConfigurationAsync(new("proxy",candidate.Protocol,candidate.Masque,"system",_settings.Warp.ProxyPort),false,deadline,rollbackOnError:false);
                await Report("Создание туннеля");
                _warpStatus=await _warpService.SetConnectedAsync(true,deadline);
                if(_warpStatus.State!=WarpConnectionState.Connected)throw new IOException(_warpStatus.Detail);
                await Report("Проверка трафика через прокси");
                using var proof=CancellationTokenSource.CreateLinkedTokenSource(deadline);proof.CancelAfter(TimeSpan.FromSeconds(4));
                if(!await _warpService.ConfirmProxyRouteAsync(_settings.Warp.ProxyPort,proof.Token))throw new IOException("Прокси: ответ warp=on не подтверждён");
                _activeWarpObservation=await WarpAttemptObservation.ReadAsync(attemptStarted);
                await LogToUiAsync("info","WARP: запрошено "+candidate.Masque+"; "+_activeWarpObservation.Summary);
                await LogToUiAsync("ok",message+"; warp=on подтверждён через локальный прокси");
                return true;
                }
                catch(Exception ex)
                {
                    var detail=ex is OperationCanceledException?"Истекло время попытки или поступила отмена":WarpService.DescribePreparationError(ex);
                    await LogToUiAsync("warn",message+"; "+detail);
                    var observation=await WarpAttemptObservation.ReadAsync(attemptStarted);
                    await LogToUiAsync("warn","WARP: запрошено "+candidate.Masque+"; "+observation.Summary);
                    throw;
                }
            },total.Token);
            _activeWarpVariant=variant;_warpRouteConfirmed=true;_warpAppliedRoutingMode="proxy";
            _settings.Warp.VerifiedVariants.RemoveAll(v=>v.NetworkDnsClientKey==cacheKey);
            var verified=new WarpVerifiedVariant(cacheKey,variant,DateTimeOffset.UtcNow,_activeWarpObservation?.Transport??"unknown");
            _settings.Warp.VerifiedVariants.Add(verified);
            if(_advisorRunning)_advisorWarpProofKey=verified.ProofKey;
            if(_settings.Warp.VerifiedVariants.Count>20)_settings.Warp.VerifiedVariants.RemoveAt(0);
            if(ownsSnapshot)_settings.PendingConnectionWarpRestore=null;
            await PostUiAsync(new{type="warp.preflight",stage="ready",message="WARP: реальный прокси-маршрут подтверждён",remainingSeconds=0});
            return _warpStatus;
        }
        catch(Exception failure)
        {
            using var restore=CancellationTokenSource.CreateLinkedTokenSource(token.IsCancellationRequested&&_advisorRunning?AdvisorRestoreToken:_lifetime.Token);restore.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await _warpBootstrap.StopAsync();_warpBootstrapMode=originalBootstrapMode;
                if(bootstrapWasActive&&WarpPreflight.RequiresBootstrap(originalBootstrapMode))await _warpBootstrap.StartAsync(WinwsProfileComposer.ComposeWarpBootstrap(originalBootstrapMode),_protectionRequested,restore.Token);
                await _warpService.RestoreNativeConfigurationAsync(native,wasConnected,restore.Token);
                if(ownsSnapshot)_settings.PendingConnectionWarpRestore=null;
            }
            catch(Exception ex){await LogToUiAsync("error","Исходные настройки WARP требуют восстановления: "+ex.Message);}
            _activeWarpVariant=null;_activeWarpObservation=null;
            if(failure is OperationCanceledException&&!token.IsCancellationRequested)throw new IOException("Предварительная проверка WARP не завершилась за 60 секунд. Подбор остановлен до запуска модели.",failure);
            throw;
        }
    }
}
