using VortexPass.Core.Models;

namespace VortexPass.Core.Services;
public static class WarpRegistrationBootstrap
{
    public static async Task<WarpStatus> RunAsync(bool alreadyActive, Func<CancellationToken, Task> start,
        Func<CancellationToken, Task<WarpStatus>> register, Func<Task> release, CancellationToken token)
    {
        await start(token);
        try { return await register(token); }
        finally { if (!alreadyActive) await release(); }
    }
}
