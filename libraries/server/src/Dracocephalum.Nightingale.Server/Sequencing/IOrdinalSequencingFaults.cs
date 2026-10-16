namespace Dracocephalum.Nightingale.Server.Sequencing;

/// <summary>
/// Where the sequencer's host records that it is failing to number a sequence, and that it numbers
/// it again, on the sequence's progress row, so that every instance answers a read by ordinal the
/// same way: <see cref="OrdinalSequencingFaults"/> in the gateway's own table; a unit test of the
/// host stands it in.
/// </summary>
internal interface IOrdinalSequencingFaults
{
    /// <summary>Reads which sequences are recorded as faulted, so a host that takes the lease over clears them as it numbers them again.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tenants whose sequences are faulted; <see langword="null"/> among them for the shared sequence.</returns>
    Task<IReadOnlyCollection<string?>> ReadFaultedAsync(CancellationToken cancellationToken);

    /// <summary>Records a sequence as faulted, or as numbering again.</summary>
    /// <param name="tenantId">The tenant whose sequence it is, or <see langword="null"/> for the shared sequence.</param>
    /// <param name="fault">Why it fails, or <see langword="null"/> when it numbers again.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the row is written.</returns>
    Task WriteAsync(string? tenantId, string? fault, DateTimeOffset at, CancellationToken cancellationToken);
}
