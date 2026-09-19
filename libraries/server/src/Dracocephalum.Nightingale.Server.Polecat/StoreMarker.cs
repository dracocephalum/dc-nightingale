namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// What the server wrote into the store when it initialized it: the settings that shaped the
/// schema, the schema version it built, and when and by which version. One row in the
/// <c>nightingale_store</c> table. It is what lets a later start tell an owned store from a
/// foreign database, and a compatible configuration from a changed one.
/// </summary>
/// <param name="SchemaVersion">The version of the gateway's schema additions.</param>
/// <param name="Partitioning">How the events table is partitioned.</param>
/// <param name="Ordinals">Whether the events table carries the ordinal columns; false for a store initialized before the feature existed.</param>
/// <param name="Collation">The database's collation, as SQL Server reports it.</param>
/// <param name="CreatedAt">When the store was initialized.</param>
/// <param name="CreatedBy">The informational version of the server that initialized it.</param>
internal sealed record StoreMarker(
    int SchemaVersion,
    NightingaleOptions.StoreSettings.PartitioningMode Partitioning,
    bool Ordinals,
    string Collation,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    /// <summary>The schema version this server builds and understands.</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>
    /// Compares the record with a configuration and names each setting that cannot be reconciled:
    /// a partitioning mode that differs, ordinals turned on or off since, a configured collation the
    /// database does not have, or a schema version newer than this server. An empty result means
    /// the store can be served.
    /// </summary>
    /// <param name="settings">The configured settings.</param>
    /// <returns>One sentence per difference; empty when compatible.</returns>
    public IReadOnlyList<string> DifferencesFrom(NightingaleOptions.StoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var differences = new List<string>();
        if (SchemaVersion > CurrentSchemaVersion)
        {
            differences.Add($"the store has schema version {SchemaVersion}, newer than the {CurrentSchemaVersion} this server understands");
        }

        if (Partitioning != settings.Partitioning)
        {
            differences.Add($"Nightingale:Store:Partitioning is {settings.Partitioning} but the store was initialized with {Partitioning}");
        }

        if (Ordinals != settings.Ordinals)
        {
            differences.Add(Ordinals
                ? "Nightingale:Store:Ordinals is false but the store was initialized with ordinals"
                : "Nightingale:Store:Ordinals is true but the store was initialized without ordinals, and there is no backfill yet");
        }

        if (settings.Collation is not null && !string.Equals(settings.Collation, Collation, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"Nightingale:Store:Collation is {settings.Collation} but the database's collation is {Collation}");
        }

        return differences;
    }
}
