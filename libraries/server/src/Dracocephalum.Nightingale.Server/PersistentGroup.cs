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
/// <c>$all</c> or a virtual stream in positions, or in ordinals when it was created under ordinal
/// numbering, which then keys its checkpoint and its parked messages too. The outbox comes
/// first: what is due on it is delivered before the stream is read on, and again whenever the
/// group is woken, which a replay does; a retry goes straight back, ahead of anything new. What
/// a consumer asks for, an acknowledgement, a refusal, is applied to the store to completion
/// even when the consumer leaves the moment it asked: its departure ends the delivery, never
/// the bookkeeping.
/// </summary>
internal sealed class PersistentGroup : IAsyncDisposable
{
    private readonly IStreamStore _store;
    private readonly IStoreTail _tail;
    private readonly IGroupStore _groups;
    private readonly TimeProvider _time;
    private readonly GroupDefinition _definition;
    private readonly bool _byPosition;
    private readonly bool _byOrdinal;
    private readonly VirtualStreamName _virtual;
    private readonly long? _from;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, InFlight> _inFlight = [];
    private readonly SortedDictionary<long, bool> _delivered = [];
    private readonly Channel<PersistentSubscriptionMessage.Recorded> _outgoing = Channel.CreateUnbounded<PersistentSubscriptionMessage.Recorded>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<bool> _wakes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _draining = new(1, 1);
    private readonly SemaphoreSlim _room;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;
    private readonly Task _drain;
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
        _byOrdinal = definition.Settings.Numbering == Numbering.Ordinal && StreamNames.TryParseVirtual(definition.Stream, out _virtual);
        _byPosition = StreamNames.IsReserved(definition.Stream) && !_byOrdinal;

        // Where the feed starts is settled now, not on the pump's thread: from the end of $all or a
        // virtual stream by position means after the head as it is at this moment. A plain stream
        // from its end needs the stream's own head, and a virtual stream by ordinal its last
        // ordinal, which are reads the pump does.
        _from = definition.Checkpoint >= 0 ? definition.Checkpoint + 1
            : !definition.Settings.Start.IsEnd ? definition.Settings.Start.Value
            : _byPosition ? tail.Head + 1
            : null;

        _room = new SemaphoreSlim(consumerBuffer, consumerBuffer);
        _pump = Task.Run(() => PumpAsync(_stopping.Token));
        _drain = Task.Run(() => DrainOnWakeAsync(_stopping.Token));
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

    /// <summary>Wakes the group: it looks at its outbox and delivers what is due. Coalesces.</summary>
    public void Wake() => _wakes.Writer.TryWrite(true);

    /// <summary>The consumer is done with these events.</summary>
    /// <param name="ids">The event ids.</param>
    /// <returns>A task that completes when the checkpoint policy has been applied.</returns>
    public async Task AcknowledgeAsync(IEnumerable<Guid> ids)
    {
        var toDequeue = new List<long>();
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
                if (flight.FromOutbox)
                {
                    toDequeue.Add(flight.Record.Position);
                }
            }
        }

        foreach (var position in toDequeue)
        {
            await _groups.DequeueAsync(_definition.Stream, _definition.Group, position, CancellationToken.None).ConfigureAwait(false);
        }

        await WriteCheckpointIfDueAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The consumer could not process these events.</summary>
    /// <param name="ids">The event ids.</param>
    /// <param name="action">What to do with them.</param>
    /// <param name="reason">The consumer's reason, kept with a parked message.</param>
    /// <returns>A task that completes when the events are redelivered, parked or dropped.</returns>
    public async Task RefuseAsync(IEnumerable<Guid> ids, NackAction action, string reason)
    {
        var toPark = new List<InFlight>();
        var toDequeue = new List<long>();
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
                        if (flight.FromOutbox)
                        {
                            toDequeue.Add(flight.Record.Position);
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

        await SettleAsync(toRedeliver, toPark, reason, CancellationToken.None).ConfigureAwait(false);
        foreach (var position in toDequeue)
        {
            await _groups.DequeueAsync(_definition.Stream, _definition.Group, position, CancellationToken.None).ConfigureAwait(false);
        }

        await WriteCheckpointIfDueAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Redelivers whatever has been in flight longer than the group's message timeout.</summary>
    /// <returns>A task that completes when the expired events are redelivered or parked.</returns>
    public async Task ExpireAsync()
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

        await SettleAsync(toRedeliver, toPark, "Retry limit reached after the message timeout.", CancellationToken.None).ConfigureAwait(false);
        await WriteCheckpointIfDueAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _wakes.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_pump, _drain).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopping is the point; a delivery failure was already surfaced through Delivery.
        }

        await WriteCheckpointIfDueAsync(true, CancellationToken.None).ConfigureAwait(false);
        _stopping.Dispose();
        _draining.Dispose();
        _room.Dispose();
    }

    private long Key(EventRecord record) =>
        _byOrdinal ? record.Ordinal ?? throw new InvalidOperationException("The store returned an event without its ordinal from an ordinal read.")
        : _byPosition ? record.Position
        : record.Revision;

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

            // A replayed message is done for the second time, below a checkpoint it once moved;
            // the checkpoint only ever advances.
            _delivered.Remove(first.Key);
            _checkpoint = Math.Max(_checkpoint, first.Key);
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
            // Parking moves a message off the outbox in the same write, so one that came from
            // there and failed again is back where it was, with its new reason and count.
            await _groups.ParkAsync(
                new ParkedMessage(_definition.Stream, _definition.Group, flight.Record.Position, flight.Record.Revision, flight.Record.Ordinal, flight.Record.Id, reason, flight.Attempts, _time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
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

    /// <summary>Delivers, in order: the outbox first, then the stream from where the group stands.</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DrainOutboxAsync(cancellationToken).ConfigureAwait(false);
            var from = _from ?? await HeadAsync(cancellationToken).ConfigureAwait(false) + 1;
            await foreach (var record in EventSource.FollowAsync(_store, _tail, _definition.Stream, from, _definition.Settings.BufferSize, _definition.Settings.Numbering, _time, cancellationToken).ConfigureAwait(false))
            {
                await DeliverAsync(new InFlight(record, 0, default, false), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _outgoing.Writer.TryComplete();
        }
    }

    /// <summary>Looks at the outbox on every wake, so a replay reaches a connected consumer at once.</summary>
    private async Task DrainOnWakeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _wakes.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _wakes.Reader.TryRead(out _);
                await DrainOutboxAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>
    /// Delivers what is due on the outbox, one drain at a time. A message already in flight is
    /// left alone: it is on the outbox until it is done, and a wake in the meantime must not
    /// deliver it twice. A message whose event is gone leaves the outbox.
    /// </summary>
    private async Task DrainOutboxAsync(CancellationToken cancellationToken)
    {
        await _draining.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var due = await _groups.DueAsync(_definition.Stream, _definition.Group, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            foreach (var message in due)
            {
                lock (_gate)
                {
                    if (_inFlight.ContainsKey(message.EventId))
                    {
                        continue;
                    }
                }

                var record = await ReadOneAsync(message, cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    await _groups.DequeueAsync(_definition.Stream, _definition.Group, message.Position, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await DeliverAsync(new InFlight(record, message.Attempts, default, true), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _draining.Release();
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

    /// <summary>The head a group from the end starts after: a plain stream's last revision, or a virtual stream's last ordinal.</summary>
    private async Task<long> HeadAsync(CancellationToken cancellationToken)
    {
        if (_byOrdinal)
        {
            var bounds = await _store.OrdinalHeadAsync(_virtual, cancellationToken).ConfigureAwait(false);
            return bounds?.Last ?? -1;
        }

        var page = await _store.ReadAsync(_definition.Stream, Direction.Forwards, long.MaxValue, 1, cancellationToken).ConfigureAwait(false);
        return page?.Head.Last ?? -1;
    }

    private async Task<EventRecord?> ReadOneAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (_byOrdinal)
        {
            if (message.Ordinal is not { } ordinal)
            {
                return null;
            }

            var page = await _store.ReadByOrdinalAsync(_virtual, Direction.Forwards, ordinal, 1, cancellationToken).ConfigureAwait(false);
            return page.Count == 1 && page[0].Ordinal == ordinal ? page[0] : null;
        }

        if (_definition.Stream == StreamNames.All)
        {
            var page = await _store.ReadAllAsync(Direction.Forwards, message.Position, message.Position, 1, cancellationToken).ConfigureAwait(false);
            return page.Count == 1 ? page[0] : null;
        }

        if (StreamNames.TryParseVirtual(_definition.Stream, out var virtualStream))
        {
            var page = await _store.ReadVirtualAsync(virtualStream, Direction.Forwards, message.Position, message.Position, 1, cancellationToken).ConfigureAwait(false);
            return page.Count == 1 ? page[0] : null;
        }

        var slice = await _store.ReadAsync(_definition.Stream, Direction.Forwards, message.Revision, 1, cancellationToken).ConfigureAwait(false);
        return slice is { Events.Count: 1 } && slice.Events[0].Revision == message.Revision ? slice.Events[0] : null;
    }

    /// <summary>A delivered event the consumer has not answered for yet.</summary>
    private sealed record InFlight(EventRecord Record, int Attempts, DateTimeOffset Deadline, bool FromOutbox);
}
