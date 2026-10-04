namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// The schema the gateway's own tables live in. A host names it, the way it names the event
/// store's; the migrations are generated under <see cref="Default"/> and retargeted to the
/// host's choice as they run.
/// </summary>
/// <param name="Name">The schema's name.</param>
public sealed record NightingaleSchema(string Name)
{
    /// <summary>The schema used when a host names none, and the one migrations are generated under.</summary>
    public const string Default = "nightingale";

    /// <summary>The table the migrations that have been applied are recorded in, inside the schema.</summary>
    public const string HistoryTable = "__EFMigrationsHistory";
}
