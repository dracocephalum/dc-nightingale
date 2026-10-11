using System.Threading.Channels;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// One subscription's place at the router: the events of its stream as the router hands them
/// over, in the order the store holds them, buffered up to <see cref="StreamRouter.FollowerBuffer"/>.
/// A follower that leaves that many events unread has lapsed: the router stops handing it events
/// and ends its feed, and the follower reads its stream itself from where its delivery stands,
/// then follows afresh. A follower is told through its feed when its stream is deleted, or when
/// the router can route no more, with the cause. Disposing leaves the router.
/// </summary>
public sealed class StreamFollowing : IDisposable
{
    private readonly StreamRouter _router;
    private readonly Channel<EventRecord> _events = Channel.CreateBounded<EventRecord>(new BoundedChannelOptions(StreamRouter.FollowerBuffer) { SingleReader = true, SingleWriter = true });
    private bool _lapsed;

    internal StreamFollowing(StreamRouter router, string stream)
    {
        _router = router;
        Stream = stream;
    }

    /// <summary>Gets the stream followed.</summary>
    public string Stream { get; }

    /// <summary>Gets the events of the stream as the router hands them over; complete once the follower has lapsed, was disposed, or was told of a failure.</summary>
    public ChannelReader<EventRecord> Events => _events.Reader;

    /// <summary>Gets a value indicating whether the router stopped handing this follower events because it left too many unread.</summary>
    public bool Lapsed => Volatile.Read(ref _lapsed);

    /// <inheritdoc/>
    public void Dispose()
    {
        _router.Leave(this);
        _events.Writer.TryComplete();
    }

    /// <summary>Hands over an event; false when the follower's buffer is full, which is a lapse.</summary>
    internal bool TryDeliver(EventRecord record) => _events.Writer.TryWrite(record);

    /// <summary>Ends the feed because the follower left too many events unread.</summary>
    internal void Lapse()
    {
        Volatile.Write(ref _lapsed, true);
        _events.Writer.TryComplete();
    }

    /// <summary>Ends the feed with a cause: the stream was deleted, or the router can route no more.</summary>
    internal void Fail(Exception cause) => _events.Writer.TryComplete(cause);
}
