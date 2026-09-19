using Dracocephalum.Nightingale;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The port between the gRPC server and an event-store backend. It speaks the contract's numbers,
/// zero-based revisions and global positions, so a backend does every conversion once, at its own
/// boundary. Reads are paged: the server asks for a page at a time and streams what it gets. A
/// backend reports expected failures as the domain exceptions in <c>Dracocephalum.Nightingale</c>
/// and never lets its own exception types cross this interface. Liveness is the
/// <see cref="IStoreTail"/>'s business, registered beside the store.
/// </summary>
public interface IStreamStore
{
    /// <summary>
    /// Appends events to one stream atomically under an expected state. A retry with the same ids at
    /// the same expected revision succeeds with the original result when the events are already there.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserts about the stream.</param>
    /// <param name="events">The events, in order. Never empty.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The revision and position of the last event written.</returns>
    /// <exception cref="RevisionConflictException">The stream is not in the expected state.</exception>
    /// <exception cref="StreamDeletedException">The stream was soft-deleted.</exception>
    Task<AppendResult> AppendAsync(string stream, StreamState expected, IReadOnlyList<EventData> events, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of a stream. A <see langword="null"/> result means the stream has no events.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction; <see langword="null"/> means the start
    /// for a forwards read and the end for a backwards read.</param>
    /// <param name="count">The most events to return. Positive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head and the events of the page, or <see langword="null"/>.</returns>
    /// <exception cref="StreamDeletedException">The stream was soft-deleted.</exception>
    Task<StreamSlice?> ReadAsync(string stream, Direction direction, long? from, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of <c>$all</c>: every live event in position order. Forwards, the page holds the
    /// events at positions from <paramref name="from"/> up to <paramref name="head"/>, both inclusive,
    /// so a caller that took the head from the tail never reads past what is committed. Backwards,
    /// the page descends from <paramref name="from"/> and <paramref name="head"/> is not used.
    /// </summary>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="head">The highest position a forwards page may hold.</param>
    /// <param name="count">The most events to return. Positive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events of the page, in the reading direction; fewer than asked means the end was reached.</returns>
    Task<IReadOnlyList<EventRecord>> ReadAllAsync(Direction direction, long from, long head, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the bounds of a virtual stream: the positions of its first and last live events at or
    /// below <paramref name="head"/>. Its last event is its head; the global head is never reported
    /// as a virtual stream's.
    /// </summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="head">The highest position to consider.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bounds, or <see langword="null"/> when the stream has no events at or below the head.</returns>
    Task<StreamHead?> VirtualHeadAsync(VirtualStreamName stream, long head, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of a virtual stream, with the same bounds as <see cref="ReadAllAsync"/>: forwards
    /// from <paramref name="from"/> up to <paramref name="head"/>, both inclusive, or backwards from
    /// <paramref name="from"/>.
    /// </summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="head">The highest position a forwards page may hold.</param>
    /// <param name="count">The most events to return. Positive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events of the page, in the reading direction; fewer than asked means the end was reached.</returns>
    Task<IReadOnlyList<EventRecord>> ReadVirtualAsync(VirtualStreamName stream, Direction direction, long from, long head, int count, CancellationToken cancellationToken);

    /// <summary>Counts the live events of a virtual stream after a position, up to the head.</summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="after">The position already delivered, exclusive.</param>
    /// <param name="head">The highest position to count, inclusive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many events lie between the two.</returns>
    Task<long> CountVirtualAsync(VirtualStreamName stream, long after, long head, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a value indicating whether the store was initialized with ordinals, so the virtual
    /// streams can be read under <see cref="Numbering.Ordinal"/>. Fixed for the store's life; the
    /// service refuses an ordinal read outright when this is false.
    /// </summary>
    bool OrdinalsEnabled { get; }

    /// <summary>
    /// Finds the bounds of a virtual stream under ordinal numbering: the lowest and highest ordinal
    /// assigned so far. Ordinals are assigned after commit, so the last one lags the stream's last
    /// event by the numberer's cadence; a numbered event that was since removed is still counted
    /// in the bounds and read as a hole.
    /// </summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bounds, or <see langword="null"/> when nothing is numbered yet.</returns>
    /// <exception cref="OrdinalsNotEnabledException">The store has no ordinals.</exception>
    Task<StreamHead?> OrdinalHeadAsync(VirtualStreamName stream, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of a virtual stream by ordinal, live events only, each record carrying its
    /// ordinal. Forwards from <paramref name="from"/> upwards, backwards from it downwards; no head
    /// bounds the page, because a batch of ordinals is assigned atomically and every assigned
    /// ordinal is committed.
    /// </summary>
    /// <param name="stream">The virtual stream.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">The ordinal to begin at, inclusive, in the reading direction.</param>
    /// <param name="count">The most events to return. Positive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events of the page, in the reading direction; fewer than asked means the end was reached.</returns>
    /// <exception cref="OrdinalsNotEnabledException">The store has no ordinals.</exception>
    Task<IReadOnlyList<EventRecord>> ReadByOrdinalAsync(VirtualStreamName stream, Direction direction, long from, int count, CancellationToken cancellationToken);

    /// <summary>
    /// The global position up to which every event has its ordinals, so a subscription under ordinal
    /// numbering can tell "nothing new for this stream" from "not numbered yet". 0 when nothing is.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The position.</returns>
    /// <exception cref="OrdinalsNotEnabledException">The store has no ordinals.</exception>
    Task<long> NumberedThroughAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a stream: its events leave every read and it cannot be appended to again, but its
    /// rows stay in the store. The expected state is checked first, against the stream's current
    /// revision.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserts about the stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the stream is deleted.</returns>
    /// <exception cref="StreamNotFoundException">The stream has no events.</exception>
    /// <exception cref="StreamDeletedException">The stream was already deleted.</exception>
    /// <exception cref="RevisionConflictException">The stream is not in the expected state.</exception>
    Task DeleteAsync(string stream, StreamState expected, CancellationToken cancellationToken);

    /// <summary>
    /// Tombstones a stream: it and its events are removed for good, and the name afterwards reads
    /// as one that never existed. A deleted stream can be tombstoned.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserts about the stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the stream is gone.</returns>
    /// <exception cref="StreamNotFoundException">The stream has no events.</exception>
    /// <exception cref="RevisionConflictException">The stream is not in the expected state.</exception>
    Task TombstoneAsync(string stream, StreamState expected, CancellationToken cancellationToken);
}
