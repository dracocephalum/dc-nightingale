using System.Threading.Channels;

using Dracocephalum.Nightingale;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// A persistent-subscription group while one consumer is connected to it, in memory in the
/// instance that owns its lease. It reads the group's stream from the checkpoint, delivers each
/// event once the consumer has room, and keeps every delivered event in flight until it is
/// acknowledged, refused, or times out. The checkpoint is the last position every delivered
/// event up to which is done, acknowledged, skipped or parked, and is written, whenever it has
/// moved, on the group's policy and when the consumer leaves. A group over a stream counts in revisions; one over
/// <c>$all</c> or a virtual stream in positions. Replays come first: a parked message marked for
/// replay is delivered before the stream is read on, and a retry goes straight back, ahead of
/// anything new.
/// </summary>
internal sealed class PersistentGroup : IAsyncDisposable
{
    private readonly IStreamStore _store;
    private readonly IStoreTail _tail;
    private readonly IGroupStore _groups;
    private readonly TimeProvider _time;
    private readonly GroupDefinition _definition;
    private readonly bool _byPosition;
    private readonly long? _from;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, InFlight> _inFlight = [];
    private readonly SortedDictionary<long, bool> _delivered = [];
    private readonly Channel<PersistentSubscriptionMessage.Recorded> _outgoing = Channel.CreateUnbounded<PersistentSubscriptionMessage.Recorded>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _room;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;
    private long _checkpoint;
    private long _written;
    private int _doneSinceWrite;
    private DateTimeOffset _lastWrite;

    /// <summary>Initializes a new instance of the <see cref="PersistentGroup"/> class and starts delivering.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tail">The tail.</param>
    /// <param name="groups">The group store.</param>
    /// <param name="time">The clock.</param>
    /// <param name="definition">The group as read from the store.</param>
    /// <param name="consumerBuffer">How many delivered, unacknowledged events the consumer holds at once.</param>
    public PersistentGroup(IStreamStore store, IStoreTail tail, IGroupStore groups, TimeProvider time, GroupDefinition definition, int consumerBuffer)
    {
        _store = store;
        _tail = tail;
        _groups = groups;
        _time = time;
        _definition = definition;
        _checkpoint = definition.Checkpoint;
        _written = definition.Checkpoint;
        _lastWrite = time.GetUtcNow();
        _byPosition = StreamNames.IsReserved(definition.Stream);

        // Where the feed starts is settled now, not on the pump's thread: from the end of $all or a
        // virtual stream means after the head as it is at this moment. A plain stream from its end
        // needs the stream's own head, which is a read the pump does.
        _from = definition.Checkpoint >= 0 ? definition.Checkpoint + 1
            : !definition.Settings.Start.IsEnd ? definition.Settings.Start.Value
            : _byPosition ? tail.Head + 1
            : null;

        _room = new SemaphoreSlim(consumerBuffer, consumerBuffer);
        _pump = Task.Run(() => PumpAsync(_stopping.Token));
    }

    /// <summary>Gets the group's checkpoint as it stands.</summary>
    public long Checkpoint
    {
        get
        {
            lock (_gate)
            {
                return _checkpoint;
            }
        }
    }

    /// <summary>Gets the events to send to the consumer, in delivery order.</summary>
    public ChannelReader<PersistentSubscriptionMessage.Recorded> Outgoing => _outgoing.Reader;

    /// <summary>Gets a task that faults when delivery fails, so the consumer's call can end with the cause.</summary>
    public Task Delivery => _pump;

    /// <summary>The consumer is done with these events.</summary>
    /// <param name="ids">The event ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the checkpoint policy has been applied.</returns>
    public async Task AcknowledgeAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var toUnpark = new List<long>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!_inFlight.Remove(id, out var flight))
                {
                    continue;
                }

                _room.Release();
                MarkDone(Key(flight.Record));
                if (flight.Replayed)
                {
                    toUnpark.Add(Key(flight.Record));
                }
            }
        }

        foreach (var key in toUnpark)
        {
            await _groups.UnparkAsync(_definition.Stream, _definition.Group, key, cancellationToken).ConfigureAwait(false);
        }

        await WriteCheckpointIfDueAsync(false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The consumer could not process these events.</summary>
    /// <param name="ids">The event ids.</param>
    /// <param name="action">What to do with them.</param>
    /// <param name="reason">The consumer's reason, kept with a parked message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the events are redelivered, parked or dropped.</returns>
    public async Task RefuseAsync(IEnumerable<Guid> ids, NackAction action, string reason, CancellationToken cancellationToken)
    {
        var toPark = new List<InFlight>();
        var toUnpark = new List<long>();
        var toRedeliver = new List<InFlight>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!_inFlight.Remove(id, out var flight))
                {
                    continue;
                }

                switch (action)
                {
                    case NackAction.Skip:
                        _room.Release();
                        MarkDone(Key(flight.Record));
                        if (flight.Replayed)
                        {
                            toUnpark.Add(Key(flight.Record));
                        }

                        break;
                    case NackAction.Park:
                        _room.Release();
                        MarkDone(Key(flight.Record));
                        toPark.Add(flight);
                        break;
                    default:
                        Requeue(flight, toPark, toRedeliver);
                        break;
                }
            }
        }

        await SettleAsync(toRedeliver, toPark, reason, cancellationToken).ConfigureAwait(false);
        foreach (var key in toUnpark)
        {
            await _groups.UnparkAsync(_definition.Stream, _definition.Group, key, cancellationToken).ConfigureAwait(false);
        }

        await WriteCheckpointIfDueAsync(false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Redelivers whatever has been in flight longer than the group's message timeout.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the expired events are redelivered or parked.</returns>
    public async Task ExpireAsync(CancellationToken cancellationToken)
    {
        var toPark = new List<InFlight>();
        var toRedeliver = new List<InFlight>();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var flight in _inFlight.Values.Where(flight => flight.Deadline <= now).ToList())
            {
                _inFlight.Remove(flight.Record.Id);
                Requeue(flight, toPark, toRedeliver);
            }
        }

        await SettleAsync(toRedeliver, toPark, "Retry limit reached after the message timeout.", cancellationToken).ConfigureAwait(false);
        await WriteCheckpointIfDueAsync(false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopping is the point; a delivery failure was already surfaced through Delivery.
        }

        await WriteCheckpointIfDueAsync(true, CancellationToken.None).ConfigureAwait(false);
        _stopping.Dispose();
        _room.Dispose();
    }

    private long Key(EventRecord record) => _byPosition ? record.Position : record.Revision;

    private void MarkDone(long key)
    {
        _delivered[key] = true;
        _doneSinceWrite++;
        while (_delivered.Count > 0)
        {
            var first = _delivered.First();
            if (!first.Value)
            {
                break;
            }

            _delivered.Remove(first.Key);
            _checkpoint = first.Key;
        }
    }

    private void Requeue(InFlight flight, List<InFlight> toPark, List<InFlight> toRedeliver)
    {
        if (flight.Attempts >= _definition.Settings.MaxRetryCount)
        {
            _room.Release();
            MarkDone(Key(flight.Record));
            toPark.Add(flight);
            return;
        }

        // The consumer's slot stays taken: the event goes straight back, ahead of anything new.
        toRedeliver.Add(flight with { Attempts = flight.Attempts + 1 });
    }

    private async Task SettleAsync(List<InFlight> toRedeliver, List<InFlight> toPark, string reason, CancellationToken cancellationToken)
    {
        foreach (var flight in toRedeliver)
        {
            await SendAsync(flight, cancellationToken).ConfigureAwait(false);
        }

        foreach (var flight in toPark)
        {
            if (flight.Replayed)
            {
                await _groups.UnparkAsync(_definition.Stream, _definition.Group, Key(flight.Record), cancellationToken).ConfigureAwait(false);
            }

            await _groups.ParkAsync(new ParkedMessage(_definition.Stream, _definition.Group, Key(flight.Record), flight.Record.Id, reason, flight.Attempts, _time.GetUtcNow(), false), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteCheckpointIfDueAsync(bool force, CancellationToken cancellationToken)
    {
        long checkpoint;
        lock (_gate)
        {
            var settings = _definition.Settings;
            var elapsed = _time.GetUtcNow() - _lastWrite;
            var due = force
                || _doneSinceWrite >= settings.CheckpointUpperBound
                || (elapsed >= settings.CheckpointAfter && _doneSinceWrite >= settings.CheckpointLowerBound);
            if (!due || _checkpoint <= _written)
            {
                return;
            }

            checkpoint = _checkpoint;
            _written = checkpoint;
            _doneSinceWrite = 0;
            _lastWrite = _time.GetUtcNow();
        }

        await _groups.SaveCheckpointAsync(_definition.Stream, _definition.Group, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delivers, in order: replays first, then the stream from where the group stands.</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var replays = await _groups.ReplayableAsync(_definition.Stream, _definition.Group, cancellationToken).ConfigureAwait(false);
            foreach (var parked in replays)
            {
                var record = await ReadOneAsync(parked, cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    await _groups.UnparkAsync(_definition.Stream, _definition.Group, parked.Position, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await DeliverAsync(new InFlight(record, parked.Attempts, default, true), cancellationToken).ConfigureAwait(false);
            }

            var from = _from ?? await HeadRevisionAsync(cancellationToken).ConfigureAwait(false) + 1;
            await foreach (var record in EventSource.FollowAsync(_store, _tail, _definition.Stream, from, _definition.Settings.BufferSize, cancellationToken).ConfigureAwait(false))
            {
                await DeliverAsync(new InFlight(record, 0, default, false), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _outgoing.Writer.TryComplete();
        }
    }

    /// <summary>A slot is taken before an event is sent and given back when the event is done.</summary>
    private async Task DeliverAsync(InFlight flight, CancellationToken cancellationToken)
    {
        await _room.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync(flight, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(InFlight flight, CancellationToken cancellationToken)
    {
        var timed = flight with { Deadline = _time.GetUtcNow() + _definition.Settings.MessageTimeout };
        lock (_gate)
        {
            _inFlight[timed.Record.Id] = timed;
            _delivered.TryAdd(Key(timed.Record), false);
        }

        await _outgoing.Writer.WriteAsync(new PersistentSubscriptionMessage.Recorded(timed.Record, timed.Attempts), cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> HeadRevisionAsync(CancellationToken cancellationToken)
    {
        var page = await _store.ReadAsync(_definition.Stream, Direction.Forwards, long.MaxValue, 1, cancellationToken).ConfigureAwait(false);
        return page?.Head.Last ?? -1;
    }

    private async Task<EventRecord?> ReadOneAsync(ParkedMessage parked, CancellationToken cancellationToken)
    {
        if (_definition.Stream == StreamNames.All)
        {
            var page = await _store.ReadAllAsync(Direction.Forwards, parked.Position, parked.Position, 1, cancellationToken).ConfigureAwait(false);
            return page.Count == 1 ? page[0] : null;
        }

        if (StreamNames.TryParseVirtual(_definition.Stream, out var virtualStream))
        {
            var page = await _store.ReadVirtualAsync(virtualStream, Direction.Forwards, parked.Position, parked.Position, 1, cancellationToken).ConfigureAwait(false);
            return page.Count == 1 ? page[0] : null;
        }

        var slice = await _store.ReadAsync(_definition.Stream, Direction.Forwards, parked.Position, 1, cancellationToken).ConfigureAwait(false);
        return slice is { Events.Count: 1 } && slice.Events[0].Revision == parked.Position ? slice.Events[0] : null;
    }

    /// <summary>A delivered event the consumer has not answered for yet.</summary>
    private sealed record InFlight(EventRecord Record, int Attempts, DateTimeOffset Deadline, bool Replayed);
}
