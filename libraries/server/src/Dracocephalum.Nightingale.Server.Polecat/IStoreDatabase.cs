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

    /// <summary>
    /// Creates the database, empty. A database another instance created in the meantime is taken
    /// as made: nothing can hold a lock on a database that does not exist yet, so this is the one
    /// step two instances can race, and the race is settled by the server, not by them.
    /// </summary>
    /// <param name="collation">A collation the server knows, or <see langword="null"/> for the server's default.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the database exists.</returns>
    Task CreateAsync(string? collation, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the schema lock, held until what is returned is disposed: exclusively to change the
    /// database, shared to look at it, so a start that verifies never reads a database another
    /// instance is half-way through changing, and two that would change it do so one after the
    /// other, the second finding the first's work done. The lock lives with the connection it is
    /// taken on, which is nobody else's and never pooled: a process that dies releases it when the
    /// server notices the connection gone. A wait behind another instance is logged.
    /// </summary>
    /// <param name="exclusive">Whether the lock is taken to change the database, or to look at it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lock; disposing it releases it.</returns>
    Task<IAsyncDisposable> LockSchemaAsync(bool exclusive, CancellationToken cancellationToken);

    /// <summary>The collation the database has: its name, its code page, and what its comparison ignores.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The collation as the server describes it.</returns>
    Task<DatabaseCollation> ReadCollationAsync(CancellationToken cancellationToken);

    /// <summary>The tables the database holds, of anyone's, and whether each holds a row.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tables, by schema and name; empty for an empty database.</returns>
    Task<IReadOnlyList<DatabaseTable>> ListTablesAsync(CancellationToken cancellationToken);
}
