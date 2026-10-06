namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The groups this instance is serving right now, the consumers each has here, and the id the
/// instance leases them under. A group has one running copy per instance, shared by its
/// consumers there: the first to join starts it, under the group's lease, and the last to leave
/// stops it, so the group's limit on consumers holds within the instance and the lease holds
/// one instance per group across them. Once the group runs it can be woken, so a replay asked
/// for while consumers are connected reaches one at once instead of at the next connection;
/// asked how it stands, for whoever asks this instance about the group; and stopped, for a
/// change of settings its consumers have to connect again under.
/// </summary>
public sealed class SubscriptionGroupRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _groups = new(StringComparer.Ordinal);

    /// <summary>Gets the id this instance leases groups under, unique per process.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The lease name of a group.</summary>
    /// <param name="groupId">The group's id.</param>
    /// <returns>The name.</returns>
    public static string LeaseName(Guid groupId) => "group:" + groupId.ToString("N");

    /// <summary>Wakes a group running here, so it looks at its outbox.</summary>
    /// <param name="groupId">The group's id.</param>
    /// <returns>True when a running group was woken; false when none runs here.</returns>
    public bool Wake(Guid groupId)
    {
        if (Running(groupId) is not { } host)
        {
            return false;
        }

        host.Runtime.Wake();
        return true;
    }

    /// <summary>Asks a group running here how it stands.</summary>
    /// <param name="groupId">The group's id.</param>
    /// <returns>What it says, or <see langword="null"/> when none runs here.</returns>
    public SubscriptionGroupLive? Describe(Guid groupId) => Running(groupId)?.Runtime.Describe();

    /// <summary>Ends the calls of every consumer of a group running here, with the cause: the group was updated.</summary>
    /// <param name="groupId">The group's id.</param>
    /// <param name="cause">What each consumer's call ends with.</param>
    /// <returns>True when a running group was told; false when none runs here.</returns>
    public bool Stop(Guid groupId, Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        if (Running(groupId) is not { } host)
        {
            return false;
        }

        host.Runtime.Fail(cause);
        return true;
    }

    /// <summary>
    /// Seats a consumer at a group in this instance. The first seat starts the group: its holder
    /// sets <see cref="SubscriptionGroupSeat.Host"/> once it runs, or fails it, and the others
    /// wait on that. A group being stopped by its last consumer is waited for, so the next
    /// consumer starts it afresh rather than joining what is going down.
    /// </summary>
    /// <param name="groupId">The group's id.</param>
    /// <param name="limit">How many consumers the group allows here at once; 0 for no limit.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>The seat, or <see langword="null"/> when the group is full.</returns>
    internal async Task<SubscriptionGroupSeat?> JoinAsync(Guid groupId, int limit, CancellationToken cancellationToken)
    {
        var name = LeaseName(groupId);
        while (true)
        {
            Task closed;
            lock (_gate)
            {
                if (!_groups.TryGetValue(name, out var entry))
                {
                    entry = new Entry();
                    _groups[name] = entry;
                    entry.Count = 1;
                    return new SubscriptionGroupSeat(entry, true);
                }

                if (!entry.Closing)
                {
                    if (limit > 0 && entry.Count >= limit)
                    {
                        return null;
                    }

                    entry.Count++;
                    return new SubscriptionGroupSeat(entry, false);
                }

                closed = entry.Closed.Task;
            }

            await closed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives a seat back. The last seat's holder stops the group, and says so with
    /// <see cref="Closed"/> once it has; until then nobody joins the group here.
    /// </summary>
    /// <param name="seat">The seat.</param>
    /// <returns>True when it was the last seat and the group is the holder's to stop.</returns>
    internal bool Leave(SubscriptionGroupSeat seat)
    {
        lock (_gate)
        {
            seat.Entry.Count--;
            if (seat.Entry.Count > 0)
            {
                return false;
            }

            seat.Entry.Closing = true;
            return true;
        }
    }

    /// <summary>The group the seat's holder was the last to leave has stopped; the next consumer starts it afresh.</summary>
    /// <param name="seat">The seat.</param>
    internal void Closed(SubscriptionGroupSeat seat)
    {
        lock (_gate)
        {
            var name = _groups.FirstOrDefault(pair => pair.Value == seat.Entry).Key;
            if (name is not null)
            {
                _groups.Remove(name);
            }
        }

        seat.Entry.Closed.TrySetResult();
    }

    private SubscriptionGroupHost? Running(Guid groupId)
    {
        lock (_gate)
        {
            return _groups.TryGetValue(LeaseName(groupId), out var entry) && !entry.Closing && entry.Ready.Task.IsCompletedSuccessfully
                ? entry.Ready.Task.Result
                : null;
        }
    }

    /// <summary>A group in this instance: how many consumers are seated and the running group, once the first has started it.</summary>
    internal sealed class Entry
    {
        public int Count { get; set; }

        public bool Closing { get; set; }

        public TaskCompletionSource<SubscriptionGroupHost> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
