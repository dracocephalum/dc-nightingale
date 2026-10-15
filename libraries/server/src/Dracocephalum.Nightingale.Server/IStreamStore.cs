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
    /// Appends to several streams atomically, each under its own expected state, all or nothing:
    /// events to a stream, or a stream's metadata set as <see cref="SetStreamMetadataAsync"/> sets
    /// it. Checks on streams not written hold at the commit or nothing is written. The events are
    /// positioned stream by stream, in the order the appends are given. Every condition that does
    /// not hold is reported, not only the first. A retry with the same ids, every append events
    /// under an explicit revision or no stream, succeeds with the original result when the events
    /// are already there; a metadata set is written with new ids each time, so a request with one
    /// is not recognised as its own retry. The request is valid by <see cref="StreamAppends.FindProblems"/>.
    /// </summary>
    /// <param name="appends">The appends, in order. Never empty; each stream written once.</param>
    /// <param name="checks">The checks on streams not written; empty for none.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What each append wrote, in the order they were given.</returns>
    /// <exception cref="ArgumentException">The request breaks a rule of <see cref="StreamAppends.FindProblems"/>.</exception>
    /// <exception cref="StreamConflictsException">Streams are not in the expected states; nothing was written.</exception>
    /// <exception cref="StreamDeletedException">A stream written is archived; nothing was written.</exception>
    Task<IReadOnlyList<StreamAppendResult>> AppendToStreamsAsync(IReadOnlyList<StreamAppend> appends, IReadOnlyList<StreamCheck> checks, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of a stream, with the stream's metadata applied as the reference applies it:
    /// nothing before the later of the last revision less <c>$maxCount</c> plus one and
    /// <c>$tb</c>, which the head reports as its first revision, and nothing older than
    /// <c>$maxAge</c>. Fewer events than asked for means nothing more is there to read, up to the
    /// head's last revision, so a reader that got fewer has read through it even when metadata
    /// hid what lay between. A stream whose metadata hides every event exists and reads empty. A
    /// metadata stream is read as it is stored. A <see langword="null"/> result means the stream
    /// has no events.
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
    /// Reads one page of a stream forwards as it is stored, whatever its metadata says: what the
    /// router fetches when the tail points at a stream's new events, which it hands to the
    /// stream's followers and to the followers of its category and event type, whom a stream's
    /// metadata does not concern. A <see langword="null"/> result means the stream has no events.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="from">The first revision, inclusive.</param>
    /// <param name="count">The most events to return. Positive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head and the events of the page, or <see langword="null"/>.</returns>
    /// <exception cref="StreamDeletedException">The stream was soft-deleted.</exception>
    Task<StreamSlice?> ReadAsStoredAsync(string stream, long from, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of a stream for a caller that accepts a view which may lag behind the latest
    /// append: a bounded read asked for by a client, never a subscription, which must see what the
    /// tail said is there. A backend with a read replica may serve it from there when the host
    /// asks it to; one without serves it as <see cref="ReadAsync"/> does, which is the default.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction; <see langword="null"/> means the start
    /// for a forwards read and the end for a backwards read.</param>
    /// <param name="count">The most events to return. Positive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head and the events of the page, or <see langword="null"/>.</returns>
    /// <exception cref="StreamDeletedException">The stream was soft-deleted.</exception>
    Task<StreamSlice?> ReadEventualAsync(string stream, Direction direction, long? from, int count, CancellationToken cancellationToken) =>
        ReadAsync(stream, direction, from, count, cancellationToken);

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
    /// Scans a stretch of <c>$all</c> for the events a filter keeps: forwards, the positions from
    /// <paramref name="from"/> up to the lower of <paramref name="head"/> and
    /// <paramref name="from"/> plus the window; backwards, from <paramref name="from"/> down through
    /// the window. The stretch bounds what one call examines, whatever the filter keeps of it, so a
    /// selective filter over a large store answers in bounded time and says how far it looked. A
    /// prefix filter is the database's to evaluate, through the indexes where a prefix is a whole
    /// category or type; a regular expression is evaluated in process over the stretch's names.
    /// </summary>
    /// <param name="filter">What to keep.</param>
    /// <param name="direction">The direction to read in.</param>
    /// <param name="from">Where to begin, inclusive, in the reading direction.</param>
    /// <param name="head">The highest position a forwards scan may examine.</param>
    /// <param name="window">The most positions one call examines. Positive.</param>
    /// <param name="count">The most events to return. Positive, at most the window.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The events kept and the last position examined.</returns>
    Task<ScannedSlice> ScanAllAsync(EventFilter filter, Direction direction, long from, long head, int window, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Reads where the live events of a range of <c>$all</c> are, in position order, without
    /// their bodies: the stream and revision of each event at positions from <paramref name="from"/>
    /// up to <paramref name="head"/>, both inclusive. What <see cref="StreamRouter"/> reads of each
    /// advance of the tail, once for every subscription to a plain stream, before it reads the
    /// events of the streams that moved and are followed.
    /// </summary>
    /// <param name="from">The first position, inclusive.</param>
    /// <param name="head">The highest position the page may hold.</param>
    /// <param name="count">The most pointers to return. Positive.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pointers of the page, in position order; fewer than asked means the end was reached.</returns>
    Task<IReadOnlyList<EventPointer>> ReadPointersAsync(long from, long head, int count, CancellationToken cancellationToken);

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
    /// event by the sequencer's cadence; a numbered event that was since removed is still counted
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
    /// Resets a stream, the reference's soft delete: one metadata event that makes the stream read
    /// as not found until its next append, which continues its revisions with every older event
    /// hidden from the stream's reads. Nothing is removed, and <c>$all</c> and the virtual streams
    /// keep the events. The reset replaces the metadata as a whole, as the reference's does. A
    /// stream with no events but with metadata has its metadata cleared, which is what a reset
    /// comes to when there is nothing to hide.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserts about the stream, checked against its last revision.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The revision and position of the metadata event.</returns>
    /// <exception cref="StreamNotFoundException">The stream has neither events nor metadata.</exception>
    /// <exception cref="StreamDeletedException">The stream is archived.</exception>
    /// <exception cref="RevisionConflictException">The stream is not in the expected state.</exception>
    Task<AppendResult> ResetAsync(string stream, StreamState expected, CancellationToken cancellationToken);

    /// <summary>
    /// Archives a stream: its events leave every read, <c>$all</c> and the virtual streams included,
    /// and it cannot be appended to again, but its rows stay in the store. The expected state is
    /// checked first, against the stream's current revision.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserts about the stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the stream is archived.</returns>
    /// <exception cref="StreamNotFoundException">The stream has no events.</exception>
    /// <exception cref="StreamDeletedException">The stream was already archived.</exception>
    /// <exception cref="RevisionConflictException">The stream is not in the expected state.</exception>
    Task ArchiveAsync(string stream, StreamState expected, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a stream's metadata as last set: the last event of its metadata stream. The stream
    /// itself is not read; its metadata may have been set before its first event.
    /// </summary>
    /// <param name="stream">The plain stream's name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The metadata and the metadata stream's revision, revision -1 and no settings when it was never set; after a reset, whether the stream is still reset, and its truncation as the revision it continues from.</returns>
    Task<StreamMetadataResult> GetStreamMetadataAsync(string stream, CancellationToken cancellationToken);

    /// <summary>
    /// Sets a stream's metadata: appends one <c>$metadata</c> event to its metadata stream, checked
    /// against the expected state of that metadata stream, never of the stream itself, which need
    /// not exist. The new metadata replaces the old as a whole. A <see cref="StreamMetadata.TruncateBefore"/>
    /// at its largest value resets the stream, as <see cref="ResetAsync"/> does, keeping the other
    /// settings. On a stream that is reset, the set brings it back in two events, as the
    /// reference's does: the metadata as given, then the same with the truncation the reset left;
    /// the result is the second's.
    /// </summary>
    /// <param name="stream">The plain stream's name.</param>
    /// <param name="expected">What the caller asserts about the metadata stream.</param>
    /// <param name="metadata">The metadata; checked with <see cref="StreamMetadata.Validate"/> before anything is written.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The revision and position of the metadata event.</returns>
    /// <exception cref="ArgumentException">The metadata is not valid.</exception>
    /// <exception cref="RevisionConflictException">The metadata stream is not in the expected state.</exception>
    Task<AppendResult> SetStreamMetadataAsync(string stream, StreamState expected, StreamMetadata metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a stream for good: it and its events are removed, its metadata stream with them,
    /// and the name afterwards reads as one that never existed. An archived stream can be deleted.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">What the caller asserts about the stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the stream is gone.</returns>
    /// <exception cref="StreamNotFoundException">The stream has no events.</exception>
    /// <exception cref="RevisionConflictException">The stream is not in the expected state.</exception>
    Task DeleteAsync(string stream, StreamState expected, CancellationToken cancellationToken);
}
