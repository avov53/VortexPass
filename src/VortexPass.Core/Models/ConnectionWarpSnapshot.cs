namespace VortexPass.Core.Models;
public sealed record ConnectionWarpSnapshot(WarpSettings Settings,bool WasConnected,VortexPass.Core.Services.WarpNativeConfiguration? Configuration=null,string BootstrapMode="default");
