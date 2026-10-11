namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The positions of the store's events table as the per-tenant poller reads them: what each
/// tenant has reached, what follows a tenant's mark, and what was appended before a moment.
/// <see cref="EventRowPositions"/> reads them over the main connection; a unit test of the
/// poller stands them in.
/// </summary>
internal interface ITenantEventPositions
{
    /// <summary>Reads the highest visible position of each tenant that has one.</summary>
    /// <param name="tenantIds">The ids the store keeps for the tenants.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The highest position by tenant; a tenant with no events is absent.</returns>
    Task<IReadOnlyDictionary<string, long>> ReadCeilingsAsync(IReadOnlyCollection<string> tenantIds, CancellationToken cancellationToken);

    /// <summary>Reads the positions of a tenant visible after a mark, ascending, up to a window.</summary>
    /// <param name="tenantId">The id the store keeps for the tenant.</param>
    /// <param name="mark">The position every event of the tenant up to which is known to be committed.</param>
    /// <param name="window">The most positions to read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The positions.</returns>
    Task<IReadOnlyList<long>> ReadFollowingAsync(string tenantId, long mark, int window, CancellationToken cancellationToken);

    /// <summary>Finds the highest position of a tenant after a mark that was appended at or before a moment.</summary>
    /// <param name="tenantId">The id the store keeps for the tenant.</param>
    /// <param name="mark">The position to look after, exclusive.</param>
    /// <param name="cutoff">The moment.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The position, or <see langword="null"/> when there is none.</returns>
    Task<long?> FindLastAppendedBeforeAsync(string tenantId, long mark, DateTimeOffset cutoff, CancellationToken cancellationToken);
}
