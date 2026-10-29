using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

namespace VortexPass.App;
public partial class MainWindow
{
    private bool _warpSettingsTransactionActive;
    private async Task<bool> UpdateWarpSettingsAsync(JsonElement root)
    {
        if(_warpTransitioning||_advisorRunning||_warpPreparationCts is not null||_warpMediaCts is not null)return false;
        var previous=JsonSerializer.Deserialize<WarpSettings>(JsonSerializer.Serialize(_settings.Warp))!;
        WarpTransportPolicy.Migrate(previous);
        var candidate=JsonSerializer.Deserialize<WarpSettings>(JsonSerializer.Serialize(previous))!;
        WarpTransportPolicy.ApplyMessage(candidate,root);
        if(root.TryGetProperty("routingMode",out var routing))candidate.RoutingMode=WarpRoutingPlanner.NormalizeRoutingMode(routing.GetString());
        if(root.TryGetProperty("dnsMode",out var dns))candidate.DnsMode=WarpRoutingPlanner.NormalizeDnsMode(dns.GetString());
        if(root.TryGetProperty("proxyPort",out var port)&&port.TryGetInt32(out var number))candidate.ProxyPort=Math.Clamp(number,1024,65535);
        var wasConnected=_warpStatus.State==WarpConnectionState.Connected;
        var oldVariant=_activeWarpVariant;var oldConfirmed=_warpRouteConfirmed;var oldBootstrap=_warpBootstrapMode;
        var previousPending=_settings.PendingConnectionWarpRestore;
        WarpNativeConfiguration? native=null;
        _warpTransitioning=true;
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);operation.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await PublishWarpStatusAsync();
            if(wasConnected)native=await _warpService.CaptureNativeConfigurationAsync(operation.Token);
            _warpSettingsTransactionActive=true;
            await WarpSettingsTransaction.ExecuteAsync(async token=>
            {
                _settings.Warp=candidate;_warpRouteConfirmed=false;
                if(!wasConnected)return;
                await StopWarpRoutingAsync(token);
                await _warpService.SetConnectedAsync(false,token);
                if(GetEffectiveWarpRoutingMode() is "smart" or "proxy")_warpStatus=await ConnectSelectiveWarpAsync(token);
                else
                {
                    _warpStatus=await ApplyWarpClientPreferencesAsync(false,token);
                    _warpStatus=await _warpService.SetConnectedAsync(true,token);
                }
                if(_warpStatus.State!=WarpConnectionState.Connected)throw new InvalidOperationException(_warpStatus.Detail);
                await ApplyWarpRoutingAsync(token);
            },token=>_settingsStore.SaveAsync(_settings,token),async token=>
            {
                _settings.Warp=previous;
                _settings.PendingConnectionWarpRestore=previousPending;
                _activeWarpVariant=oldVariant;_warpRouteConfirmed=oldConfirmed;_warpBootstrapMode=oldBootstrap;
                try
                {
                    if(native is not null)
                    {
                        await StopWarpRoutingAsync(token);await _warpBootstrap.StopAsync();
                        if(wasConnected&&WarpPreflight.RequiresBootstrap(oldBootstrap))await _warpBootstrap.StartAsync(WinwsProfileComposer.ComposeWarpBootstrap(oldBootstrap),_protectionRequested,token);
                        await _warpService.RestoreNativeConfigurationAsync(native,wasConnected,token);
                        _warpStatus=await _warpService.GetStatusAsync(token);
                        if(wasConnected)await ApplyWarpRoutingAsync(token);
                    }
                }
                catch{_warpRouteConfirmed=false;if(native is not null)_settings.PendingConnectionWarpRestore=new(previous,wasConnected,native,oldBootstrap);throw;}
                finally{await _settingsStore.SaveAsync(_settings,token);}
            },operation.Token);
            await LogToUiAsync("info","Выбор транспорта WARP сохранён: "+WarpTransportPolicy.Label(_settings.Warp));
            return true;
        }
        catch(Exception ex)
        {
            _settings.Warp=previous;
            await LogToUiAsync("error","Настройка WARP не применена: "+WarpService.DescribePreparationError(ex));
            return false;
        }
        finally{_warpSettingsTransactionActive=false;_warpTransitioning=false;await PublishWarpStatusAsync();}
    }

    private async Task UseAutoWarpAndRetryAdvisorAsync(JsonElement root)
    {
        if(_advisorRunning||_warpTransitioning)return;
        AdvisorStrategyPolicy.NormalizeProvider(root.GetProperty("provider").GetString());
        if(root.TryGetProperty("draft",out var draft))SavedConnectionProfilePolicy.Validate(SaveConnectionRequest.Read(draft));
        using var update=JsonDocument.Parse("{\"transportSelection\":\"auto\"}");
        var succeeded=await WarpSettingsTransaction.SaveAndRetryAsync(()=>UpdateWarpSettingsAsync(update.RootElement),async()=>
        {_draftRecommendations.Clear();await RunConnectionAdvisorAsync(root);});
        if(!succeeded)await PostUiAsync(new{type="advisor.local.status",state="failed",error="Не удалось сохранить Авто для WARP. Подбор не запущен; черновик сохранён."});
    }

    private async Task OptimizeWarpAsync()
    {
        if(_warpTransitioning||_advisorRunning)return;
        var wasConnected=_warpStatus.State==WarpConnectionState.Connected;
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);operation.CancelAfter(TimeSpan.FromSeconds(90));
        var native=await _warpService.CaptureNativeConfigurationAsync(operation.Token);
        var originalBootstrap=_warpBootstrapMode;
        var originalVariant=_activeWarpVariant;var originalConfirmed=_warpRouteConfirmed;var succeeded=false;
        try
        {
            await SetWarpConnectedAsync(true,operation.Token,runDiagnostics:false);
            if(_warpStatus.State!=WarpConnectionState.Connected)throw new InvalidOperationException(_warpStatus.Detail);
            _warpTransitioning=true;
            _warpPerformance=await _warpService.MeasurePerformanceAsync(true,operation.Token,proxyUri:GetWarpProxyUri());
            await _settingsStore.SaveAsync(_settings,operation.Token);
            await LogToUiAsync("ok","WARP: проверен выбранный режим "+WarpTransportPolicy.Label(_settings.Warp)+"; измерения выполнены через подтверждённый маршрут");
            succeeded=true;
        }
        finally
        {
            _warpTransitioning=false;
            if(!wasConnected||!succeeded)
            {
                using var restore=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await StopWarpRoutingAsync(restore.Token);await _warpBootstrap.StopAsync();
                _warpBootstrapMode=originalBootstrap;
                if(wasConnected&&WarpPreflight.RequiresBootstrap(originalBootstrap))await _warpBootstrap.StartAsync(WinwsProfileComposer.ComposeWarpBootstrap(originalBootstrap),_protectionRequested,restore.Token);
                await _warpService.RestoreNativeConfigurationAsync(native,wasConnected,restore.Token);
                _warpStatus=await _warpService.GetStatusAsync(restore.Token);
                _settings.Warp.RequestedConnected=wasConnected;_activeWarpVariant=originalVariant;_warpRouteConfirmed=originalConfirmed&&wasConnected;
                if(wasConnected)await ApplyWarpRoutingAsync(restore.Token);
                await _settingsStore.SaveAsync(_settings,restore.Token);
            }
            await PublishWarpStatusAsync();
        }
    }
}
