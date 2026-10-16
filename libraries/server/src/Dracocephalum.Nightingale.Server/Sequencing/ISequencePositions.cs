namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// The positions of a journal's events table as the tails read them: the highest visible position
/// of each sequence, and the positions that follow a head. A backend implements it for its events
/// table; how it reads, past an open transaction's row or waiting on it, is part of the
/// <see cref="HeadStrategy"/> it belongs to.
/// </summary>
public interface ISequencePositions
{
    /// <summary>Reads the highest visible position of each sequence that has one.</summary>
    /// <param name="sequences">The sequences.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The highest position by sequence; a sequence with no events is absent.</returns>
    Task<IReadOnlyDictionary<SequenceKey, long>> ReadCeilingsAsync(IReadOnlyCollection<SequenceKey> sequences, CancellationToken cancellationToken);

    /// <summary>Reads the positions of a sequence visible after a head, ascending, up to a window.</summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="after">The position to read after, exclusive.</param>
    /// <param name="window">The most positions to read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The positions.</returns>
    Task<IReadOnlyList<long>> ReadFollowingAsync(SequenceKey sequence, long after, int window, CancellationToken cancellationToken);
}
