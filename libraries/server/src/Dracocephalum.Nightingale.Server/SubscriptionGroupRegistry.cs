using System.Collections.Concurrent;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The groups this instance is serving right now, one consumer each, and the id the instance
/// leases them under. A group is claimed when its consumer connects and released when the
/// consumer leaves, so the limit of one consumer per group holds within the instance; the lease
/// holds it across instances. Once the group runs, it attaches a wake-up, so a replay asked for
/// while its consumer is connected reaches the consumer at once instead of at its next
/// connection, a way to ask how it stands, for whoever asks this instance about the group, and
/// a way to end its consumer's call, for a change of settings the consumer has to reconnect under.
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
    /// <param name="stop">What to call to end the consumer's call because the group was updated; none when it cannot be.</param>
    public void Attach(Guid groupId, Action wake, Func<SubscriptionGroupLive>? describe = null, Action? stop = null)
    {
        ArgumentNullException.ThrowIfNull(wake);
        var name = LeaseName(groupId);
        if (_live.ContainsKey(name))
        {
            _live[name] = new Running(wake, describe, stop);
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

    /// <summary>Ends the call of the consumer of a group running here, because the group was updated.</summary>
    /// <param name="groupId">The group's id.</param>
    /// <returns>True when a running group was told; false when none runs here.</returns>
    public bool Stop(Guid groupId)
    {
        if (!_live.TryGetValue(LeaseName(groupId), out var running) || running?.Stop is null)
        {
            return false;
        }

        running.Stop();
        return true;
    }

    /// <summary>Releases a group a consumer held here.</summary>
    /// <param name="groupId">The group's id.</param>
    public void Release(Guid groupId) => _live.TryRemove(LeaseName(groupId), out _);

    private sealed record Running(Action Wake, Func<SubscriptionGroupLive>? Describe, Action? Stop);
}
