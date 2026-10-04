namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// What the tables in a database should be, and bringing them there: the half of initialization
/// that <see cref="StoreInitializer"/> decides about and does not do itself.
/// </summary>
internal interface IStoreSchema
{
    /// <summary>Reports where the database stands; changes nothing.</summary>
    /// <param name="trustStoreHash">
    /// Whether a recorded hash equal to this server's stands in for comparing the event store's
    /// tables. It says the tables were applied from the script this server would apply; it
    /// cannot say nothing has touched them since.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    Task<SchemaReport> ReportAsync(bool trustStoreHash, CancellationToken cancellationToken);

    /// <summary>Applies what is pending: the event store's tables first, then the gateway's migrations.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the database is current.</returns>
    Task ApplyAsync(CancellationToken cancellationToken);

    /// <summary>Records what an empty database was initialized with, and by whom and when.</summary>
    /// <param name="settings">The settings, with the database's actual collation.</param>
    /// <param name="createdAt">When the store was initialized.</param>
    /// <param name="createdBy">The version of the server that initialized it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    Task WriteInitializationAsync(IStoreSettings settings, DateTimeOffset createdAt, string createdBy, CancellationToken cancellationToken);

    /// <summary>Reads which server version initialized the store.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The version, or "unknown" when the store does not say.</returns>
    Task<string> ReadCreatedByAsync(CancellationToken cancellationToken);
}
