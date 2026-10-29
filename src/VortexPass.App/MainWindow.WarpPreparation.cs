using System.IO;
using System.Text.Json;
using VortexPass.Core.Services;

namespace VortexPass.App;
public partial class MainWindow
{
    private CancellationTokenSource? _warpPreparationCts;
    private TaskCompletionSource? _warpPreparationFinished;
    private async Task PrepareWarpAsync(JsonElement root)
    {
        if(_warpPreparationCts is not null||_advisorRunning||_warpTransitioning)return;
        if(!root.TryGetProperty("acceptTerms",out var terms)||terms.ValueKind!=JsonValueKind.True)
            throw new InvalidOperationException("Для подготовки WARP подтвердите условия Cloudflare.");
        _warpPreparationCts=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);var token=_warpPreparationCts.Token;
        _warpPreparationFinished=new(TaskCreationOptions.RunContinuationsAsynchronously);
        _warpTransitioning=true;var bootstrapWasActive=_warpBootstrap.IsActive;
        var stage="Проверка клиента";
        async Task Status(string state,string detail){stage=detail;await PostUiAsync(new {type="warp.preparation",state,detail});}
        async Task Bootstrap(string mode="default")
        {
            await Status("bootstrap","Включаем временный обход Cloudflare для регистрации устройства");
            await _warpBootstrap.StartAsync(WinwsProfileComposer.ComposeWarpBootstrap(mode),_protectionRequested,token);
        }
        try
        {
            _proxyManager.EnsureCanApply();
            var found=await _warpService.GetStatusAsync(token);
            if(found.State==WarpConnectionState.Connected)throw new InvalidOperationException("Сначала отключите текущее подключение WARP; оно не будет заменено автоматически.");
            await _warpService.ValidateConsumerAsync(token);
            if(found.AppPath is not null)await ApplyWarpGuiPolicyAsync(token);
            if(found.CliPath is null)
            {
                await Status("verifyingInstaller","Проверяем встроенный официальный установщик WARP: SHA-256 и подпись Cloudflare. Интернет для загрузки не требуется");
                var installer=await WarpInstallerBundle.FindAsync(AppContext.BaseDirectory,token);
                if(installer is null)
                {
                    await Bootstrap();
                    await Status("downloading","В этом комплекте нет установщика WARP. Загружаем с официального сайта Cloudflare");
                    installer=await _warpService.DownloadInstallerAsync(null,token);
                }
                await Status("installing","Устанавливаем службу WARP. Windows может запросить права администратора");
                var code=await _warpService.InstallAsync(installer,token,silent:true);
                if(code is 3010 or 1641)throw new InvalidOperationException("Установка завершена; требуется перезагрузка Windows. PAC не включён. После перезагрузки повторите подготовку.");
                if(code!=0)throw new IOException($"Установка WARP завершилась с кодом {code}; PAC не включён");
                await ApplyWarpGuiPolicyAsync(token);
            }
            await Status("service","Ожидаем готовность службы WARP");
            using(var service=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sc.exe","start CloudflareWARP"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}))
            {if(service is not null){var stdout=service.StandardOutput.ReadToEndAsync();var stderr=service.StandardError.ReadToEndAsync();await service.WaitForExitAsync(token);await Task.WhenAll(stdout,stderr);}}
            using(var serviceWait=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                serviceWait.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    while(!WarpService.IsServiceProcessRunning())
                        await Task.Delay(500,serviceWait.Token);
                }
                catch(OperationCanceledException) when(!token.IsCancellationRequested)
                {throw new IOException("Служба WARP не стала доступна за 15 секунд. Проверьте завершение установки или перезагрузите Windows.");}
            }
            var modes=new[]{"default","split","disorder"};
            for(var attempt=0;attempt<modes.Length;attempt++)
            {
                await Bootstrap(modes[attempt]);
                await Status("registering",$"Регистрация устройства WARP без аккаунта: попытка {attempt+1} из {modes.Length} ({modes[attempt]})");
                try{await _warpService.EnsureConsumerAsync(token);break;}
                catch(WarpRegistrationException ex) when(!bootstrapWasActive&&attempt<modes.Length-1)
                {
                    await LogToUiAsync("warn",$"Регистрация WARP ({modes[attempt]}): {WarpService.DescribePreparationError(ex)}. Проверяем следующий вариант HTTPS.");
                    await _warpBootstrap.StopAsync();
                }
            }
            await Status("verifying","Проверяем режим локального прокси; системный туннель не включается");
            _warpStatus=await _warpService.GetStatusAsync(token);_settings.Warp.RoutingMode="smart";_settings.Warp.ProxyPort=41870;
            if(!_warpStatus.Registered||!_warpStatus.ServiceRunning)throw new InvalidOperationException("Служба или регистрация WARP ещё не готова; повторите подготовку после запуска службы");
            _settings.Warp.PreparationCompletedUtc=DateTimeOffset.UtcNow;
            await _settingsStore.SaveAsync(_settings,token);await PublishWarpStatusAsync();
            FirefoxUsesSystemProxy(out var firefoxHint);
            await Status("ready","WARP подготовлен. При подключении профиля будет включён только выбранный Web-маршрут. Сохранённые настройки: "+firefoxHint+"; активный профиль Firefox проверьте в браузере");
        }
        catch(OperationCanceledException){await Status("failed","Подготовка отменена; PAC не применён. Если установка Windows уже началась, дождитесь её завершения перед повтором");}
        catch(Exception ex){var detail=stage+": "+WarpService.DescribePreparationError(ex);await Status("failed",detail);await LogToUiAsync("warn","Подготовка WARP: "+detail);}
        finally
        {
            try{if(!bootstrapWasActive&&!_settings.Warp.RequestedConnected)await _warpBootstrap.StopAsync();}
            finally
            {
                _warpPreparationCts.Dispose();_warpPreparationCts=null;_warpTransitioning=false;
                _warpPreparationFinished.TrySetResult();
            }
            await PublishWarpStatusAsync();
        }
    }
}
