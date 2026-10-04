using System.Globalization;
using System.Reflection;

using Dracocephalum.Nightingale.Server.Data;
using JasperFx;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Polecat;
using Weasel.Core.Migrations;

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
/// <param name="connectionString">The connection string the store uses.</param>
/// <param name="options">The host's options.</param>
internal sealed class StoreSchema(IDocumentStore store, IDbContextFactory<NightingaleDbContext> contexts, string connectionString, NightingaleOptions options)
{
    /// <summary>The row that says when the store was initialized. Not a setting: nothing is compared with it.</summary>
    public const string CreatedAtRow = "CreatedAt";

    /// <summary>The row that says which server version initialized the store.</summary>
    public const string CreatedByRow = "CreatedBy";

    /// <summary>The row that says which version of the event-store library last applied its schema.</summary>
    public const string StoreLibraryRow = "StoreLibrary";

    /// <summary>Gets the version of the event-store library this server was built with.</summary>
    public static string StoreLibraryVersion { get; } =
        typeof(IDocumentStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Reports where the database stands; changes nothing.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    public async Task<SchemaReport> ReportAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var known = context.Database.GetMigrations().ToList();
        if (!await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SchemaReport(false, false, null, known, [], []);
        }

        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var initialized = applied.Count > 0;
        IReadOnlyList<string> conflicts = initialized
            ? (await context.ReadSettingsAsync<StoreSettings>(cancellationToken).ConfigureAwait(false)).DifferencesFrom(options.Store)
            : [];

        // The store's tables are compared under the configured settings, which shape them; under
        // settings the store was not initialized with, the comparison says nothing true. Nor does
        // it of a database that holds no store yet: everything is missing, which "not initialized"
        // already says, and listing it costs many times what creating it does.
        return new SchemaReport(
            true,
            initialized,
            !initialized || conflicts.Count > 0 ? null : await StoreChangesAsync(cancellationToken).ConfigureAwait(false),
            known.Except(applied, StringComparer.Ordinal).ToList(),
            applied.Except(known, StringComparer.Ordinal).ToList(),
            conflicts);
    }

    /// <summary>
    /// Whether the database is there, asked of the server rather than found out by connecting to
    /// it: a connection that fails is remembered by the pool for a while, and whatever connects
    /// next, to a database created in between, is told it is still missing.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the database exists.</returns>
    public async Task<bool> DatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name)";
        command.Parameters.AddWithValue("@name", target.InitialCatalog);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    /// <summary>Applies what is pending: the event store's tables first, then the gateway's migrations.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the database is current.</returns>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        foreach (var database in await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            await database.ApplyAllConfiguredChangesToDatabaseAsync(AutoCreate.CreateOrUpdate, ct: cancellationToken).ConfigureAwait(false);
        }

        await MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates everything in a database that holds nothing. The event store's tables come from
    /// its creation script, run as it stands, rather than from <see cref="ApplyAsync"/>, which
    /// first reads the catalog to find what is there: on a database just created that read is
    /// the slow part by far, and what it finds is known beforehand. Nothing compares the result
    /// here, for the same reason; the next start does, as every start of an initialized store
    /// does, and a test holds the script to what the comparison would have made.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the database is current.</returns>
    public async Task CreateAsync(CancellationToken cancellationToken)
    {
        foreach (var database in await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (var connection = database.CreateConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = database.ToDatabaseScript();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the gateway's migrations and records which store library the tables are for.</summary>
    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await context.WriteSettingAsync(StoreLibraryRow, StoreLibraryVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records what an empty database was initialized with, and by whom and when.</summary>
    /// <param name="settings">The settings, with the database's actual collation.</param>
    /// <param name="createdAt">When the store was initialized.</param>
    /// <param name="createdBy">The version of the server that initialized it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    public async Task WriteInitializationAsync(IStoreSettings settings, DateTimeOffset createdAt, string createdBy, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.WriteSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        await context.WriteSettingAsync(CreatedAtRow, createdAt.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await context.WriteSettingAsync(CreatedByRow, createdBy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads which server version initialized the store.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The version, or "unknown" when the store does not say.</returns>
    public async Task<string> ReadCreatedByAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ReadSettingAsync(CreatedByRow, cancellationToken).ConfigureAwait(false) ?? "unknown";
    }

    /// <summary>The change the event store's tables need, or <see langword="null"/> when they match.</summary>
    private async Task<string?> StoreChangesAsync(CancellationToken cancellationToken)
    {
        var changes = new List<string>();
        foreach (var database in await store.Options.Tenancy!.BuildDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await database.AssertDatabaseMatchesConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DatabaseValidationException exception)
            {
                changes.Add(exception.Message);
            }
        }

        return changes.Count == 0 ? null : string.Join(Environment.NewLine, changes);
    }
}
