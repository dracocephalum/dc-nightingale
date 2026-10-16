using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>Reads the ordinal sequencer's progress rows: how far each sequence is numbered, as committed.</summary>
internal interface IOrdinalSequencingProgressReader
{
    /// <summary>Reads the progress of the sequences of some tenants.</summary>
    /// <param name="tenantIds">The ids the store keeps for the tenants whose sequences they are; <see langword="null"/> for the shared sequence.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rows found; a sequence without a row yet has none.</returns>
    Task<IReadOnlyList<OrdinalSequencingProgress>> ReadAsync(IReadOnlyCollection<string?> tenantIds, CancellationToken cancellationToken);
}
