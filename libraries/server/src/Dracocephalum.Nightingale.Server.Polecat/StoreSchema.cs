using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Dracocephalum.Nightingale.Server.Data;
using JasperFx;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Polecat;
using Polecat.Storage;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The one place that knows what a database needs, and applies it. There are two mechanisms
/// underneath and they are kept in one order: the event store's tables, with the gateway's
/// additions to them, are compared by the store's own schema tooling, which works from the
/// difference between the real tables and what this version of the store expects; the gateway's
/// own tables are compared by its migrations, whose history says which have run. The store's go
/// first, because the gateway's tables are about a store that exists. Neither tool applies: each
/// writes the statements it would run, and the server runs them itself, one batch at a time, the
/// store's per object as the tooling itself would and the migrations' per <c>GO</c>, so that each
/// batch is logged as it starts and when it ends, a batch that builds an index runs under the
/// host's index timeout rather than the connection's, and a failure names the batch. What is
/// applied is exactly what the report shows. A connection that fails under the apply, as the
/// store's tooling judges failures, has the whole apply run again from the top, which is safe
/// because both tools apply only what is missing. Startup, the host's switches and a host's own
/// code all come through here, so they agree.
/// </summary>
/// <param name="store">The store whose tenancy answers the patched database.</param>
/// <param name="contexts">Makes the gateway's own context, whose connection the statements run on.</param>
/// <param name="database">The database the tables are in.</param>
/// <param name="options">The host's options.</param>
/// <param name="time">The clock, for the default tenant's dates and the wait before a retry.</param>
/// <param name="logger">Where each batch is logged as it starts, ends or fails.</param>
internal sealed partial class StoreSchema(IDocumentStore store, IDbContextFactory<NightingaleDbContext> contexts, IStoreDatabase database, NightingaleOptions options, TimeProvider time, ILogger<StoreSchema> logger) : IStoreSchema
{
    /// <summary>The row that says when the store was initialized. Not a setting: nothing is compared with it.</summary>
    public const string CreatedAtRow = "CreatedAt";

    /// <summary>The row that says which server version initialized the store.</summary>
    public const string CreatedByRow = "CreatedBy";

    /// <summary>The row that says which version of the event-store library last applied its schema.</summary>
    public const string StoreLibraryRow = "StoreLibrary";

    /// <summary>
    /// The row that holds the hash of the event store's creation script as it stood when its
    /// tables were last applied. See <see cref="ComputeStoreHashAsync"/>.
    /// </summary>
    public const string StoreSchemaHashRow = "StoreSchemaHash";

    /// <summary>How many times an apply is begun when the connection fails under it.</summary>
    internal const int ApplyAttempts = 3;

    private const string StoreTables = "event store's tables";

    private const string GatewayTables = "gateway's tables";

    /// <summary>How many times a batch is tried while a lock another session holds keeps it waiting.</summary>
    internal const int LockWaitAttempts = 6;

    /// <summary>SQL Server's error number for a command that ran out of its timeout.</summary>
    private const int TimeoutError = -2;

    /// <summary>SQL Server's error number for a lock request that ran out of the session's lock timeout.</summary>
    private const int LockTimeoutError = 1222;

    /// <summary>The store's tooling's judgement of which failures are the connection's and worth another attempt: deliberately narrow, a command timeout is not one.</summary>
    private static readonly Migrator TransientFailures = new SqlServerMigrator();

    /// <summary>Gets the version of the event-store library this server was built with.</summary>
    public static string StoreLibraryVersion { get; } =
        typeof(IDocumentStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <inheritdoc/>
    public async Task<SchemaReport> ReportAsync(bool trustStoreHash, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var known = context.Database.GetMigrations().ToList();
        if (!await database.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SchemaReport(false, false, null, known, [], []);
        }

        // Initialized means the record of the store is complete: the row saying who initialized
        // it is written last, with the rest, in one transaction. Migrations applied without it
        // are an initialization that died before its record.
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var createdBy = applied.Count > 0 ? await context.ReadStorePropertyAsync(CreatedByRow, cancellationToken).ConfigureAwait(false) : null;
        var initialized = createdBy is not null;
        IReadOnlyList<string> conflicts = initialized
            ? (await context.ReadStoreSettingsAsync<StoreSettings>(cancellationToken).ConfigureAwait(false)).DifferencesFrom(options.Store)
            : [];

        // The store's tables are compared under the configured settings, which shape them; under
        // settings the store was not initialized with, the comparison says nothing true. Nor does
        // it of a database that holds no store yet: everything is missing, which "not initialized"
        // already says, and the comparison is the slow part of a start. Nor of a store newer than
        // this server, which would be measured against what an older server expects.
        var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
        var compare = initialized && conflicts.Count == 0 && unknown.Count == 0;
        var hashRecorded = compare && string.Equals(
            await context.ReadStorePropertyAsync(StoreSchemaHashRow, cancellationToken).ConfigureAwait(false),
            await ComputeStoreHashAsync(cancellationToken).ConfigureAwait(false),
            StringComparison.Ordinal);
        var libraryRecorded = compare && string.Equals(
            await context.ReadStorePropertyAsync(StoreLibraryRow, cancellationToken).ConfigureAwait(false),
            StoreLibraryVersion,
            StringComparison.Ordinal);
        var trusted = hashRecorded && trustStoreHash;
        return new SchemaReport(
            true,
            initialized,
            compare && !trusted ? await StoreChangesAsync(cancellationToken).ConfigureAwait(false) : null,
            known.Except(applied, StringComparer.Ordinal).ToList(),
            unknown,
            conflicts)
        {
            StoreComparisonSkipped = trusted,
            InitializationUnfinished = applied.Count > 0 && !initialized,
            StoreRecordStale = compare && !(hashRecorded && libraryRecorded),
        };
    }

    /// <summary>
    /// The hash of the event store's creation script: everything that decides the shape of its
    /// tables in one value, the store library's version, the schema library's under it, the
    /// gateway's additions and the settings. Building the script reads no database.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The hash, in hexadecimal.</returns>
    public async Task<string> ComputeStoreHashAsync(CancellationToken cancellationToken)
    {
        var script = new StringBuilder();
        script.Append(StoreLibraryVersion).Append('\n');
        script.Append(typeof(Table).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown").Append('\n');
        foreach (var tenant in await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            script.Append(tenant.ToDatabaseScript());
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.ToString())));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<TableName>> ListOwnTablesAsync(CancellationToken cancellationToken)
    {
        var tables = new HashSet<TableName>();
        foreach (var tenant in await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var table in tenant.AllObjects().OfType<Table>())
            {
                tables.Add(new TableName(table.Identifier.Schema, table.Identifier.Name));
            }
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entity in context.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is { } table)
            {
                tables.Add(new TableName(entity.GetSchema() ?? NightingaleSchema.Default, table));
            }
        }

        tables.Add(new TableName(options.Schema, NightingaleSchema.HistoryTable));
        return tables;
    }

    /// <inheritdoc/>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ApplyOnceAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SqlException exception) when (attempt < ApplyAttempts && TransientFailures.IsTransientConnectionFailure(exception))
            {
                // The connection, not the statement: what the store's tooling itself retries. The
                // apply is begun again from the top, both halves applying only what is missing.
                LogConnectionFailed(exception, database.Name, attempt, ApplyAttempts);
                await Task.Delay(TimeSpan.FromSeconds(attempt), time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Applies the gateway's migrations: the second half of <see cref="ApplyAsync"/>, apart so
    /// that a test which made the store's tables from the script can finish the way the server
    /// does. Writes no row; the record is <see cref="WriteInitializationAsync"/>'s and
    /// <see cref="RecordStoreAsync"/>'s.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the migrations are applied.</returns>
    internal async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RunAsync(context, GatewayTables, MigrationBatches(context), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RecordStoreAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await WriteStoreRecordAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task WriteInitializationAsync(IStoreSettings settings, DateTimeOffset createdAt, string createdBy, CancellationToken cancellationToken)
    {
        // One transaction, the row that says who initialized the store last: the next start
        // reads that row as "initialized", and finds the rest there exactly when it is.
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await WriteStoreRecordAsync(context, cancellationToken).ConfigureAwait(false);
        await context.WriteStoreSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        await context.WriteStorePropertyAsync(CreatedAtRow, createdAt.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await context.WriteStorePropertyAsync(CreatedByRow, createdBy, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string> ReadCreatedByAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ReadStorePropertyAsync(CreatedByRow, cancellationToken).ConfigureAwait(false) ?? "unknown";
    }

    /// <summary>The event store's tables first, then the gateway's migrations, each as the batches its tool writes.</summary>
    private async Task ApplyOnceAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var tenant in await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            var patch = await tenant.CreateMigrationAsync(cancellationToken).ConfigureAwait(false);
            await RunAsync(context, StoreTables, StoreBatches(tenant, patch), cancellationToken).ConfigureAwait(false);
        }

        await RunAsync(context, GatewayTables, MigrationBatches(context), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The batches the store's tooling would run for a difference, as it would run them: the
    /// schemas, one per object changed, then the foreign keys it defers. Empty when there is no
    /// difference.
    /// </summary>
    private static List<string> StoreBatches(PolecatDatabase tenant, SchemaMigration patch)
    {
        var batches = new List<string>();
        if (patch.Difference == SchemaPatchDifference.None)
        {
            return batches;
        }

        patch.AssertPatchingIsValid(AutoCreate.CreateOrUpdate);
        var migrator = tenant.Migrator;
        if (patch.Schemas.Length > 0)
        {
            var schemas = new StringWriter();
            migrator.WriteSchemaCreationSql(patch.Schemas, schemas);
            Add(batches, schemas);
        }

        foreach (var delta in patch.Deltas)
        {
            var writer = new StringWriter();
            migrator.WriteUpdate(writer, delta);
            Add(batches, writer);
        }

        var deferred = new StringWriter();
        patch.WriteDeferredForeignKeys(deferred, migrator);
        Add(batches, deferred);
        return batches;

        // The schema library ends a batch where SQL Server needs one, after a column a later
        // statement of the same delta names, with a GO line, which is the tooling's separator and
        // not SQL Server's; each piece is a batch of its own.
        static void Add(List<string> batches, StringWriter writer) => batches.AddRange(SchemaBatches.SplitOnGo(writer.ToString()));
    }

    /// <summary>
    /// The batches of the gateway's migrations not applied yet, as the migrations' tooling writes
    /// them: idempotent, each guarded by the history, and without the transactions the tooling
    /// would otherwise wrap each migration in, which its own apply forbids running inside one of
    /// ours and which the server does not want, so that a batch that fails is seen where it
    /// failed. The history table is made by the first batch when it is missing.
    /// </summary>
    private static IReadOnlyList<string> MigrationBatches(NightingaleDbContext context) =>
        SchemaBatches.SplitOnGo(context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent | MigrationsSqlGenerationOptions.NoTransactions));

    /// <summary>
    /// Runs batches one by one on the context's connection, each logged as it starts and ends,
    /// the ones that build an index under the host's index timeout, and every one under the
    /// host's lock wait: a batch kept waiting for a lock another session holds gives the wait up,
    /// sleeps as long, and is tried again, so that the table is never frozen behind it for longer
    /// than the wait at a time.
    /// </summary>
    private async Task RunAsync(NightingaleDbContext context, string tables, IReadOnlyList<string> batches, CancellationToken cancellationToken)
    {
        if (batches.Count == 0)
        {
            return;
        }

        var lockWait = options.SchemaLockWaitTimeout;
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = context.Database.GetDbConnection();
            await using (var session = connection.CreateCommand())
            {
                session.CommandText = string.Format(CultureInfo.InvariantCulture, "SET LOCK_TIMEOUT {0};", (int)Math.Ceiling(lockWait.TotalMilliseconds));
                await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var batch in batches)
            {
                var indexTimeout = SchemaBatches.BuildsAnIndex(batch);
                for (var attempt = 1; ; attempt++)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = batch;
                    if (indexTimeout)
                    {
                        command.CommandTimeout = (int)Math.Ceiling(options.IndexMigrationStatementTimeout.TotalSeconds);
                    }

                    LogApplying(database.Name, tables, batch);
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (SqlException exception) when (exception.Number == LockTimeoutError && attempt < LockWaitAttempts)
                    {
                        LogBlocked(database.Name, tables, lockWait, attempt, LockWaitAttempts, SchemaBatches.FirstLineOf(batch));
                        await Task.Delay(lockWait, time, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (SqlException exception) when (exception.Number == LockTimeoutError)
                    {
                        LogFailed(exception, database.Name, tables, watch.Elapsed, batch);
                        throw new StoreInitializationException(
                            $"A batch on the {tables} of database {database.Name} was blocked by a lock another session holds, through {LockWaitAttempts} tries of {lockWait} each (Nightingale:SchemaLockWaitTimeout), and was given up; sys.dm_exec_requests names the session holding the table. The batch:{Environment.NewLine}{batch}",
                            exception);
                    }
                    catch (SqlException exception) when (exception.Number == TimeoutError)
                    {
                        LogFailed(exception, database.Name, tables, watch.Elapsed, batch);
                        var timeout = TimeSpan.FromSeconds(command.CommandTimeout);
                        throw new StoreInitializationException(
                            $"A batch on the {tables} of database {database.Name} did not finish within {timeout}{(indexTimeout ? " (Nightingale:IndexMigrationStatementTimeout)" : string.Empty)} and was stopped; the change may be too large to run at startup, and is then one to run by hand. The batch:{Environment.NewLine}{batch}",
                            exception);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        LogFailed(exception, database.Name, tables, watch.Elapsed, batch);
                        throw;
                    }

                    var firstLine = SchemaBatches.FirstLineOf(batch);
                    LogApplied(database.Name, tables, watch.Elapsed, firstLine);
                    break;
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The default tenant's row when it is missing, the store library's version and the hash of its creation script.</summary>
    private async Task WriteStoreRecordAsync(NightingaleDbContext context, CancellationToken cancellationToken)
    {
        await context.EnsureDefaultTenantAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await context.WriteStorePropertyAsync(StoreLibraryRow, StoreLibraryVersion, cancellationToken).ConfigureAwait(false);
        await context.WriteStorePropertyAsync(StoreSchemaHashRow, await ComputeStoreHashAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The change the event store's tables need, as the batches that would be run, or <see langword="null"/> when they match.</summary>
    private async Task<string?> StoreChangesAsync(CancellationToken cancellationToken)
    {
        var batches = new List<string>();
        foreach (var tenant in await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            batches.AddRange(StoreBatches(tenant, await tenant.CreateMigrationAsync(cancellationToken).ConfigureAwait(false)));
        }

        return batches.Count == 0 ? null : string.Join(Environment.NewLine, batches);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying to the {Tables} of database {Database}:\n{Sql}")]
    private partial void LogApplying(string database, string tables, string sql);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied to the {Tables} of database {Database} in {Elapsed}: {FirstLine}")]
    private partial void LogApplied(string database, string tables, TimeSpan elapsed, string firstLine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A batch on the {Tables} of database {Database} waited {Wait} for a lock another session holds, try {Attempt} of {Attempts}; trying again after as long: {FirstLine}")]
    private partial void LogBlocked(string database, string tables, TimeSpan wait, int attempt, int attempts, string firstLine);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed applying to the {Tables} of database {Database} after {Elapsed}; the batch that failed:\n{Sql}")]
    private partial void LogFailed(Exception exception, string database, string tables, TimeSpan elapsed, string sql);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The connection failed while applying the schema to database {Database}, on attempt {Attempt} of {Attempts}; applying again from the top.")]
    private partial void LogConnectionFailed(Exception exception, string database, int attempt, int attempts);
}
