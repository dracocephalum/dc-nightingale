namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// Where the tails keep their sequence heads between looks and between processes:
/// <see cref="SequenceHeadRows"/> in the gateway's own table; a unit test of the tails stands it in.
/// </summary>
internal interface ISequenceHeadRows
{
    /// <summary>Reads the heads kept for some sequences.</summary>
    /// <param name="sequences">The sequences.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The head by sequence; a sequence with no row yet is absent.</returns>
    Task<IReadOnlyDictionary<SequenceKey, long>> ReadAsync(IReadOnlyCollection<SequenceKey> sequences, CancellationToken cancellationToken);

    /// <summary>Raises each sequence's head to a position, never lowering one, and stamps when it was checked.</summary>
    /// <param name="heads">The position by sequence.</param>
    /// <param name="checkedAt">When the tail looked.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    Task WriteAsync(IReadOnlyDictionary<SequenceKey, long> heads, DateTimeOffset checkedAt, CancellationToken cancellationToken);
}
