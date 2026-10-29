using System.Collections.Concurrent;
using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;
internal static class ExternalLifecycleTests
{
    public static async Task Run()
    {
        var executable=Path.Combine(AppContext.BaseDirectory,"VortexPass.Tests.exe");
        var folder=Path.GetFullPath(Path.Combine("artifacts","component-lifecycle-tests",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(Path.Combine(folder,"assets"));
        var manifest=new ExternalComponentManifest{Components=[new(){Id="fixture",DisplayName="Local test process",Executable=executable,Arguments="--component-lifecycle-fixture hold",RequiredFiles=[new(){Path=executable,Sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(executable)))}]}]};
        await File.WriteAllTextAsync(Path.Combine(folder,"assets","external-components.json"),JsonSerializer.Serialize(manifest));
        using var service=new ExternalComponentService(folder);var events=new ConcurrentQueue<ServiceStatus>();service.StatusChanged+=(_,s)=>events.Enqueue(s);
        await service.LoadManifestAsync();await service.StartAsync("fixture");var first=service.GetStatuses().Single();
        if(first.State!=ServiceState.Running)throw new Exception("Test fixture did not start");
        for(var i=0;i<3;i++)await service.RestartOwnedAsync("fixture","--component-lifecycle-fixture hold",default);
        var latest=service.GetStatuses().Single();
        if(latest.State!=ServiceState.Running||latest.Generation!=first.Generation+3||service.IsCurrent(first))throw new Exception("Owned restart reused generation or accepted stale running event");
        if(events.Any(e=>e.State==ServiceState.Stopping&&!e.Planned))throw new Exception("Planned stop could disable live profile");
        if(events.Where(e=>e.Generation<latest.Generation).Any(service.IsCurrent))throw new Exception("Old process status remains actionable");
        await service.RestartOwnedAsync("fixture","--component-lifecycle-fixture exit",default);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(service.GetStatuses().Single().State!=ServiceState.Stopped)await Task.Delay(20,deadline.Token);
        var stopped=service.GetStatuses().Single();if(stopped.Planned||!service.IsCurrent(stopped))throw new Exception("Real current exit ignored");
        await service.StopAllAsync(cleanupDriver:false);
    }
}
