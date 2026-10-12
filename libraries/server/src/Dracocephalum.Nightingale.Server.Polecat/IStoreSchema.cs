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

    /// <summary>
    /// The tables this server creates, in both halves: the event store's, with the gateway's
    /// additions, and the gateway's own with its migrations history. Read from the definitions,
    /// not the database.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tables, by schema and name.</returns>
    Task<IReadOnlySet<TableName>> ListOwnTablesAsync(CancellationToken cancellationToken);

    /// <summary>Applies what the tables are behind on: the event store's first, then the gateway's migrations. Writes no row of the store's record.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the tables are current.</returns>
    Task ApplyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records, on an initialized store, what this server applied: the version of the store's
    /// library and the hash of its creation script, and the default tenant's row when it is
    /// missing. What a start writes after applying changes, and again when it finds the record
    /// behind this server's.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    Task RecordStoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records what an empty database was initialized with, and by whom and when: every row of
    /// the store's record, the default tenant's included, in one transaction, so that the row
    /// saying who initialized the store is there exactly when all of them are.
    /// </summary>
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
