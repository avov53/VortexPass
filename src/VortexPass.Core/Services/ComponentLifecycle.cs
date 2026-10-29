using VortexPass.Core.Models;
namespace VortexPass.Core.Services;

// Events are immutable. A delayed exit must not replace a newer process or a newer state.
public sealed class ComponentLifecycle
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ServiceStatus> _current = new(StringComparer.OrdinalIgnoreCase);
    public ServiceStatus Begin(string id, string name)
    {
        lock (_gate)
        {
            var old = _current.GetValueOrDefault(id);
            return _current[id] = new(id, name, ServiceState.Starting, Generation: (old?.Generation ?? 0) + 1, Revision: (old?.Revision ?? 0) + 1, Planned: true);
        }
    }
    public ServiceStatus? Set(string id, string name, long generation, ServiceState state, string? detail = null, bool planned = false)
    {
        lock (_gate)
        {
            var old = _current.GetValueOrDefault(id);
            if (old is not null && old.Generation != generation) return null;
            return _current[id] = new(id, name, state, detail, generation, (old?.Revision ?? 0) + 1, planned);
        }
    }
    public ServiceStatus? Get(string id) { lock (_gate) return _current.GetValueOrDefault(id); }
    public bool IsCurrent(ServiceStatus status) { lock (_gate) return _current.GetValueOrDefault(status.Id) == status; }
}
