namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The store's head as it moves: the highest position up to which every event is committed, and
/// a way to wait for it to move. One tail serves every subscription in the process, so liveness
/// costs the store one poll per interval rather than one per subscriber. A backend registers its
/// implementation beside its <see cref="IStreamStore"/>.
/// </summary>
public interface IStoreTail
{
    /// <summary>
    /// Gets the high-water mark: the position up to which every event is committed. 0 when the
    /// store has no events yet. Never the highest sequence number in the table, because a number
    /// is taken before its transaction commits and a lower one can become visible after a higher.
    /// </summary>
    long Head { get; }

    /// <summary>
    /// Brings the head up to date now, rather than at the next poll: advances it through every
    /// committed position that follows it without a gap, and returns it. A gap is a number taken
    /// by a transaction that has not committed, and the head stops before it until it does or
    /// the poller gives it up as abandoned. Used where "now" matters: a subscription from the end,
    /// a bounded read, so that an append that has returned is in what follows.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head, current as of the call.</returns>
    ValueTask<long> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Waits until the head is beyond a position.</summary>
    /// <param name="beyond">The position the caller has already seen.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head, greater than <paramref name="beyond"/>.</returns>
    ValueTask<long> WaitForAdvanceAsync(long beyond, CancellationToken cancellationToken);
}
