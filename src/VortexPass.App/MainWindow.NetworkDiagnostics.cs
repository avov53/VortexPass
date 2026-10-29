using System.IO;
using System.Text.Json;
using VortexPass.Core.Services;

namespace VortexPass.App;
public partial class MainWindow
{
    private CancellationTokenSource? _warpMediaCts;
    private WarpMediaComparison? _warpMediaComparison;
    private string _warpMediaProgress="";
    private string? _youtubeComparison;
    private bool _youtubeComparing;
    private async Task SetNetworkDiagnosticDetailsAsync(JsonElement root)
    {
        if(_advisorRunning)return;
        var value=root.GetProperty("value").GetBoolean();var old=_settings.NetworkDiagnosticDetails;
        _settings.NetworkDiagnosticDetails=value;
        try{await _settingsStore.SaveAsync(_settings,_lifetime.Token);}
        catch{_settings.NetworkDiagnosticDetails=old;throw;}
        _warpPac.DiagnosticsEnabled=value;_dnsGuard.DetailedDiagnostics=value;
        await PublishWarpStatusAsync();
    }
    private async Task CompareWarpMediaAsync()
    {
        if(_warpMediaCts is not null||_warpTransitioning||_advisorRunning||_protectionTransitioning)return;
        if(_warpStatus.State!=WarpConnectionState.Connected||!_warpRouteConfirmed)
        {_warpMediaProgress="Сначала подключите подтверждённый выборочный WARP. Тест сам туннель не включает.";await PublishWarpStatusAsync();return;}
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_warpMediaCts=operation;
        _warpMediaComparison=null;
        try
        {
            _warpMediaProgress="Сравниваем SOCKS5 и отдельный HTTP-мост; до 48 МиБ, до 90 секунд";await PublishWarpStatusAsync();
            _warpMediaComparison=await WarpMediaBenchmark.RunAsync(_settings.Warp.ProxyPort,i=>
            {_warpMediaProgress=$"Загрузка {i}/6; подключение WARP не меняется";_ = Dispatcher.InvokeAsync(async()=>await PublishWarpStatusAsync());},operation.Token);
            _warpMediaProgress=_warpMediaComparison.Summary;
        }
        catch(OperationCanceledException){_warpMediaProgress="Сравнение остановлено. Маршрут не изменён.";}
        catch(Exception ex)when(ex is IOException or System.Net.Sockets.SocketException){_warpMediaProgress="Диагностический мост недоступен; рабочий маршрут не изменён.";}
        finally{_warpMediaCts=null;await PublishWarpStatusAsync();}
    }
    private async Task CompareYoutubeAsync(string? browser)
    {
        if(_advisorRunning||_protectionTransitioning||_warpMediaCts is not null||_youtubeComparing)return;
        _youtubeComparing=true;
        await PublishWarpStatusAsync();
        try
        {
        var network=CurrentNetworkKey();
        var running=_components.GetStatuses().Any(s=>s.Id=="dpi"&&s.State==VortexPass.Core.Models.ServiceState.Running);
        var result=await _healthChecks.CheckAsync("youtube",true,running,_lifetime.Token,probeOptionalTransport:false);
        var observation=browser switch{"works"=>"Видео работает","slow"=>"Видео загружается медленно","unavailable"=>"Видео не работает",_=>"Не проверено в браузере"};
        if(network!=CurrentNetworkKey()){_youtubeComparison="Сеть или настройки изменились во время сверки. Повторите проверку на одном профиле.";return;}
        _youtubeComparison=$"Профиль: {_activeConnection?.Name??"Текущие настройки"}. Браузер: {observation}. Проверка приложения: {result.Detail}. DNS: {result.Stages?.Dns}; TCP: {result.Stages?.Tcp}; TLS: {result.Stages?.Tls}; HTTP: {result.Stages?.Http}. Правила не менялись.";
        await LogToUiAsync("info","Сверка YouTube: "+_youtubeComparison);await PublishWarpStatusAsync();
        }
        finally{_youtubeComparing=false;await PublishWarpStatusAsync();}
    }
}
