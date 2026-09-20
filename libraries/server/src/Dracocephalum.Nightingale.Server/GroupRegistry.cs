using System.Collections.Concurrent;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The groups this instance is serving right now, one consumer each, and the id the instance
/// leases them under. A group is claimed when its consumer connects and released when the
/// consumer leaves, so the limit of one consumer per group holds within the instance; the lease
/// holds it across instances. Once the group runs, it attaches a wake-up, so a replay asked for
/// while its consumer is connected reaches the consumer at once instead of at its next
/// connection.
/// </summary>
public sealed class GroupRegistry
{
    private readonly ConcurrentDictionary<string, Action?> _live = new(StringComparer.Ordinal);

    /// <summary>Gets the id this instance leases groups under, unique per process.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The lease name of a group.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <returns>The name.</returns>
    public static string LeaseName(string stream, string group) => "group:" + stream + ":" + group;

    /// <summary>Claims a group for a consumer in this instance.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <returns>True when no consumer holds it here; false when one does.</returns>
    public bool TryClaim(string stream, string group) => _live.TryAdd(LeaseName(stream, group), null);

    /// <summary>Attaches the wake-up of a running group, once it runs.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="wake">What to call so the group looks at its outbox.</param>
    public void Attach(string stream, string group, Action wake)
    {
        ArgumentNullException.ThrowIfNull(wake);
        var name = LeaseName(stream, group);
        if (_live.ContainsKey(name))
        {
            _live[name] = wake;
        }
    }

    /// <summary>Wakes a group running here, so it looks at its outbox.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <returns>True when a running group was woken; false when none runs here.</returns>
    public bool Wake(string stream, string group)
    {
        if (!_live.TryGetValue(LeaseName(stream, group), out var wake) || wake is null)
        {
            return false;
        }

        wake();
        return true;
    }

    /// <summary>Releases a group a consumer held here.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    public void Release(string stream, string group) => _live.TryRemove(LeaseName(stream, group), out _);
}
