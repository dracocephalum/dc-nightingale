using System.Reflection;

using Dracocephalum.Nightingale.Server.Persistence;
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
            ? (await SettingRows.ReadAsync<StoredSettings>(context, cancellationToken).ConfigureAwait(false)).DifferencesFrom(options.Store)
            : [];

        // The store's tables are compared under the configured settings, which shape them; under
        // settings the store was not initialized with, the comparison says nothing true.
        return new SchemaReport(
            true,
            initialized,
            conflicts.Count > 0 ? null : await StoreChangesAsync(cancellationToken).ConfigureAwait(false),
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

        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await SettingRows.WriteAsync(context, new { StoreLibrary = StoreLibraryVersion }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records what an empty database was initialized with.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    public async Task WriteSettingsAsync(StoredSettings settings, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await SettingRows.WriteAsync(context, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads what the store was initialized with.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The settings.</returns>
    public async Task<StoredSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await SettingRows.ReadAsync<StoredSettings>(context, cancellationToken).ConfigureAwait(false);
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
