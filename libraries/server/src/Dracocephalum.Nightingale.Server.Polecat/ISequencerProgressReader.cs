using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>Reads the sequencer's progress rows: how far each named sequence is numbered, as committed.</summary>
internal interface ISequencerProgressReader
{
    /// <summary>Reads the progress of the named sequences.</summary>
    /// <param name="names">The rows' names, from <see cref="SequencerProgress.OrdinalsOf"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The position each named sequence is numbered through, by name; a sequence without a row yet is absent.</returns>
    Task<IReadOnlyDictionary<string, long>> ReadAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken);
}
