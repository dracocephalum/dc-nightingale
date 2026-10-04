using System.Text;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// Where a database stands against what this server expects, in the three parts that can each
/// be behind: the event store's own tables with the gateway's additions to them, the gateway's
/// own tables, and the settings the store was initialized with. A server serves a store only
/// when all three are current.
/// </summary>
/// <param name="DatabaseExists">Whether the database is there at all.</param>
/// <param name="Initialized">Whether the gateway has initialized it: at least one of its migrations is applied.</param>
/// <param name="StoreChanges">The change the event store's tables need, as the statements that would be run, or <see langword="null"/> when they match, and when they were not compared: before the store is initialized, and while its settings conflict.</param>
/// <param name="PendingMigrations">The gateway's migrations not applied yet, oldest first.</param>
/// <param name="UnknownMigrations">Migrations applied to the database that this server does not have: the store is newer than the server.</param>
/// <param name="SettingConflicts">Where the configuration disagrees with the settings the store was initialized with.</param>
public sealed record SchemaReport(
    bool DatabaseExists,
    bool Initialized,
    string? StoreChanges,
    IReadOnlyList<string> PendingMigrations,
    IReadOnlyList<string> UnknownMigrations,
    IReadOnlyList<string> SettingConflicts)
{
    /// <summary>Gets a value indicating whether the database is exactly what this server expects.</summary>
    public bool IsCurrent =>
        DatabaseExists && Initialized && StoreChanges is null && PendingMigrations.Count == 0 && UnknownMigrations.Count == 0 && SettingConflicts.Count == 0;

    /// <summary>The report as text, one part after another, for a log or a console.</summary>
    /// <returns>The text.</returns>
    public string Describe()
    {
        if (!DatabaseExists)
        {
            return "The database does not exist.";
        }

        if (IsCurrent)
        {
            return "The database is current: the event store's tables, the gateway's tables and the settings all match.";
        }

        var text = new StringBuilder();
        if (!Initialized)
        {
            text.AppendLine("The database has not been initialized by Nightingale.");
        }

        if (UnknownMigrations.Count > 0)
        {
            text.AppendLine("Migrations applied to the database that this server does not have; the store is newer than the server:");
            AppendList(text, UnknownMigrations);
        }

        if (SettingConflicts.Count > 0)
        {
            text.AppendLine("Settings the store was initialized with that the configuration no longer matches:");
            AppendList(text, SettingConflicts);
        }

        if (PendingMigrations.Count > 0)
        {
            text.AppendLine("Migrations of the gateway's tables to apply:");
            AppendList(text, PendingMigrations);
        }

        if (StoreChanges is not null)
        {
            text.AppendLine("Changes to the event store's tables to apply:");
            text.AppendLine(StoreChanges);
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendList(StringBuilder text, IReadOnlyList<string> items)
    {
        foreach (var item in items)
        {
            text.Append("  ").AppendLine(item);
        }
    }
}
