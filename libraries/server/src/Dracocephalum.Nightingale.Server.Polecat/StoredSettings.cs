namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// What a store was initialized with, as it is kept in the settings rows and read back: the
/// settings that shaped it, in the very type the configuration binds them into, and who
/// initialized it and when. The server compares the stored settings with the configured ones on
/// every start and refuses to serve a store whose record disagrees, so a setting that is fixed
/// at initialization cannot drift under a running system.
/// </summary>
internal sealed class StoredSettings
{
    /// <summary>Gets or sets the settings the store was initialized with; the collation is the database's actual one.</summary>
    public NightingaleOptions.StoreSettings Store { get; set; } = new();

    /// <summary>Gets or sets when the store was initialized.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the version of the server that initialized the store.</summary>
    public string CreatedBy { get; set; } = "unknown";

    /// <summary>Gets or sets the version of the event-store library whose schema was last applied; for information.</summary>
    public string StoreLibrary { get; set; } = "unknown";

    /// <summary>Finds where the configuration disagrees with what the store was initialized with.</summary>
    /// <param name="settings">The configured settings.</param>
    /// <returns>One sentence per difference; empty when the store can be served.</returns>
    public IReadOnlyList<string> DifferencesFrom(NightingaleOptions.StoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var differences = new List<string>();
        if (Store.Partitioning != settings.Partitioning)
        {
            differences.Add($"Nightingale:Store:Partitioning is {settings.Partitioning} but the store was initialized with {Store.Partitioning}");
        }

        if (Store.AssignOrdinals != settings.AssignOrdinals)
        {
            differences.Add(Store.AssignOrdinals
                ? "Nightingale:Store:AssignOrdinals is false but the store was initialized with ordinals"
                : "Nightingale:Store:AssignOrdinals is true but the store was initialized without ordinals, and there is no backfill yet");
        }

        if (!string.Equals(Store.Schema, settings.Schema, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"Nightingale:Store:Schema is {settings.Schema} but the store was initialized in {Store.Schema}");
        }

        if (settings.Collation is not null && !string.Equals(settings.Collation, Store.Collation, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"Nightingale:Store:Collation is {settings.Collation} but the database's collation is {Store.Collation}");
        }

        return differences;
    }
}
