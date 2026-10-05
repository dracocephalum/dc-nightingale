using System.Collections.Concurrent;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The groups this instance is serving right now, one consumer each, and the id the instance
/// leases them under. A group is claimed when its consumer connects and released when the
/// consumer leaves, so the limit of one consumer per group holds within the instance; the lease
/// holds it across instances. Once the group runs, it attaches a wake-up, so a replay asked for
/// while its consumer is connected reaches the consumer at once instead of at its next
/// connection, and a way to ask how it stands, for whoever asks this instance about the group.
/// </summary>
public sealed class SubscriptionGroupRegistry
{
    private readonly ConcurrentDictionary<string, Running?> _live = new(StringComparer.Ordinal);

    /// <summary>Gets the id this instance leases groups under, unique per process.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The lease name of a group.</summary>
    /// <param name="groupId">The group's id.</param>
    /// <returns>The name.</returns>
    public static string LeaseName(Guid groupId) => "group:" + groupId.ToString("N");

    /// <summary>Claims a group for a consumer in this instance.</summary>
    /// <param name="groupId">The group's id.</param>
    /// <returns>True when no consumer holds it here; false when one does.</returns>
    public bool TryClaim(Guid groupId) => _live.TryAdd(LeaseName(groupId), null);

    /// <summary>Attaches a running group, once it runs.</summary>
    /// <param name="groupId">The group's id.</param>
    /// <param name="wake">What to call so the group looks at its outbox.</param>
    /// <param name="describe">What to call to ask the group how it stands; none when it cannot say.</param>
    public void Attach(Guid groupId, Action wake, Func<SubscriptionGroupLive>? describe = null)
    {
        ArgumentNullException.ThrowIfNull(wake);
        var name = LeaseName(groupId);
        if (_live.ContainsKey(name))
        {
            _live[name] = new Running(wake, describe);
        }
    }

    /// <summary>Wakes a group running here, so it looks at its outbox.</summary>
    /// <param name="groupId">The group's id.</param>
    /// <returns>True when a running group was woken; false when none runs here.</returns>
    public bool Wake(Guid groupId)
    {
        if (!_live.TryGetValue(LeaseName(groupId), out var running) || running is null)
        {
            return false;
        }

        running.Wake();
        return true;
    }

    /// <summary>Asks a group running here how it stands.</summary>
    /// <param name="groupId">The group's id.</param>
    /// <returns>What it says, or <see langword="null"/> when none runs here or it cannot say.</returns>
    public SubscriptionGroupLive? Describe(Guid groupId) =>
        _live.TryGetValue(LeaseName(groupId), out var running) ? running?.Describe?.Invoke() : null;

    /// <summary>Releases a group a consumer held here.</summary>
    /// <param name="groupId">The group's id.</param>
    public void Release(Guid groupId) => _live.TryRemove(LeaseName(groupId), out _);

    private sealed record Running(Action Wake, Func<SubscriptionGroupLive>? Describe);
}
