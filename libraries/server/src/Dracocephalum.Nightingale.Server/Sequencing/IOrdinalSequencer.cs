namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// The ordinal sequencer, the one part of the sequencing a backend supplies: it sequences a batch of
/// a sequence. Everything around it is the server's own, in <see cref="OrdinalSequencing"/>: the
/// lease and the loop that walks every sequence to its head, and the follower that keeps another
/// instance's numbering current; a backend writes the statement for its
/// database, over its events table and the gateway's links and progress rows.
/// </summary>
public interface IOrdinalSequencer
{
    /// <summary>
    /// Sequences one batch of a sequence, numbering it from the position numbered through up to the head or the
    /// backend's batch size, whichever comes first: every event of the range gets its links, the
    /// type's and the category's always, the correlation's and the listings' when it is in them,
    /// each key continuing from its last ordinal, and the new progress is recorded in the same
    /// transaction. The progress row is read under a lock that a second sequencer, whose lease
    /// overlapped a takeover, waits on, so it continues from what the first committed.
    /// </summary>
    /// <param name="tenantId">The tenant whose sequence it is, or <see langword="null"/> for the shared sequence.</param>
    /// <param name="head">The sequence head; nothing beyond it is numbered.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The position numbered through after the batch.</returns>
    Task<long> SequenceBatchAsync(string? tenantId, long head, CancellationToken cancellationToken);
}
