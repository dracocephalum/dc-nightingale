using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Dracocephalum.Nightingale.Server.Data;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Polecat;
using Weasel.Core.Migrations;
using Weasel.SqlServer.Tables;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The one place that knows what a database needs, and applies it. There are two mechanisms
/// underneath and they are kept in one order: the event store's tables, with the gateway's
/// additions to them, are compared and changed by the store's own schema tooling, which works
/// from the difference between the real tables and what this version of the store expects; the
/// gateway's own tables are created and changed by its migrations, whose history says which have
/// run. The store's go first, because the gateway's tables are about a store that exists.
/// Startup, the host's switches and a host's own code all come through here, so they agree.
/// </summary>
/// <param name="store">The store whose tenancy answers the patched database.</param>
/// <param name="contexts">Makes the gateway's own context.</param>
/// <param name="database">The database the tables are in.</param>
/// <param name="options">The host's options.</param>
/// <param name="time">The clock, for the default tenant's dates.</param>
internal sealed class StoreSchema(IDocumentStore store, IDbContextFactory<NightingaleDbContext> contexts, IStoreDatabase database, NightingaleOptions options, TimeProvider time) : IStoreSchema
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
        foreach (var tenant in await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            await tenant.ApplyAllConfiguredChangesToDatabaseAsync(AutoCreate.CreateOrUpdate, ct: cancellationToken).ConfigureAwait(false);
        }

        await MigrateAsync(cancellationToken).ConfigureAwait(false);
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
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>The default tenant's row when it is missing, the store library's version and the hash of its creation script.</summary>
    private async Task WriteStoreRecordAsync(NightingaleDbContext context, CancellationToken cancellationToken)
    {
        await context.EnsureDefaultTenantAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await context.WriteStorePropertyAsync(StoreLibraryRow, StoreLibraryVersion, cancellationToken).ConfigureAwait(false);
        await context.WriteStorePropertyAsync(StoreSchemaHashRow, await ComputeStoreHashAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string> ReadCreatedByAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ReadStorePropertyAsync(CreatedByRow, cancellationToken).ConfigureAwait(false) ?? "unknown";
    }

    /// <summary>The change the event store's tables need, or <see langword="null"/> when they match.</summary>
    private async Task<string?> StoreChangesAsync(CancellationToken cancellationToken)
    {
        var changes = new List<string>();
        foreach (var tenant in await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await tenant.AssertDatabaseMatchesConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DatabaseValidationException exception)
            {
                changes.Add(exception.Message);
            }
        }

        return changes.Count == 0 ? null : string.Join(Environment.NewLine, changes);
    }
}
