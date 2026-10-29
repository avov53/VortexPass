namespace VortexPass.Core.Models;

public enum ServiceState { Stopped, Starting, Running, Error, Stopping }

public sealed record ServiceStatus(string Id, string DisplayName, ServiceState State, string? Detail = null,
    long Generation = 0, long Revision = 0, bool Planned = false);
