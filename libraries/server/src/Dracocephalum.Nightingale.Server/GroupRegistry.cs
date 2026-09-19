namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The groups this instance is serving right now, one consumer each, and the id the instance
/// leases them under. A group is registered when its consumer connects and removed when the
/// consumer leaves, so the limit of one consumer per group holds within the instance; the lease
/// holds it across instances.
/// </summary>
public sealed class GroupRegistry
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);

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
    public bool TryClaim(string stream, string group)
    {
        lock (_gate)
        {
            return _live.Add(LeaseName(stream, group));
        }
    }

    /// <summary>Releases a group a consumer held here.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    public void Release(string stream, string group)
    {
        lock (_gate)
        {
            _live.Remove(LeaseName(stream, group));
        }
    }
}
