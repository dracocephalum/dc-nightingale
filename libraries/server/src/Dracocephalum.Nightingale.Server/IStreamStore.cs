using Dracocephalum.Nightingale;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The port between the gRPC server and an event-store backend. It speaks the contract's numbers,
/// zero-based revisions and global positions, so a backend does every conversion once, at its own
/// boundary. Reads are paged: the server asks for a page at a time and streams what it gets. A
/// backend reports expected failures as the domain exceptions in <c>Dracocephalum.Nightingale</c>
/// and never lets its own exception types cross this interface.
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
}
