namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The database itself, apart from anything in it: whether it is there, making it, what its
/// collation is and which tables it holds. <see cref="StoreInitializer"/> decides from the
/// answers; what the tables should be is <see cref="IStoreSchema"/>'s.
/// </summary>
internal interface IStoreDatabase
{
    /// <summary>Gets the name of the database the connection string points at; empty when it names none.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the database is there, asked of the server rather than found out by connecting to
    /// it: a connection that fails is remembered by the pool for a while, and whatever connects
    /// next, to a database created in between, is told it is still missing.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the database exists.</returns>
    Task<bool> ExistsAsync(CancellationToken cancellationToken);

    /// <summary>The code page of a collation, as the server's catalog has it.</summary>
    /// <param name="collation">The collation's name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The code page, or <see langword="null"/> when the server knows no collation by that name.</returns>
    Task<int?> ReadCodePageAsync(string collation, CancellationToken cancellationToken);

    /// <summary>Creates the database, empty.</summary>
    /// <param name="collation">A collation the server knows, or <see langword="null"/> for the server's default.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the database exists.</returns>
    Task CreateAsync(string? collation, CancellationToken cancellationToken);

    /// <summary>The collation the database has: its name, its code page, and what its comparison ignores.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The collation as the server describes it.</returns>
    Task<DatabaseCollation> ReadCollationAsync(CancellationToken cancellationToken);

    /// <summary>The tables the database holds, of anyone's, and whether each holds a row.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tables, by schema and name; empty for an empty database.</returns>
    Task<IReadOnlyList<DatabaseTable>> ListTablesAsync(CancellationToken cancellationToken);
}
