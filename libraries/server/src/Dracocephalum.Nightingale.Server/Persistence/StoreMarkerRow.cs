namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// The marker row: what shaped the store when it was initialized. Mapped so the model mirrors
/// every table of the gateway's; the initializer still reads and writes it as SQL, because it
/// does so before the schema has been brought up to date, when a column this model expects may
/// not exist yet.
/// </summary>
public sealed class StoreMarkerRow
{
    /// <summary>The id of the one row.</summary>
    public const int SingleRowId = 1;

    /// <summary>Gets or sets the id; always <see cref="SingleRowId"/>.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the schema version the store was built to.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets the partitioning mode's name.</summary>
    public required string Partitioning { get; set; }

    /// <summary>Gets or sets whether ordinals are assigned; null for a store initialized before the feature existed.</summary>
    public bool? AssignOrdinals { get; set; }

    /// <summary>Gets or sets the schema the store was initialized in; null for a store initialized before the setting existed, meaning the store's default.</summary>
    public string? SchemaName { get; set; }

    /// <summary>Gets or sets the database's collation.</summary>
    public required string Collation { get; set; }

    /// <summary>Gets or sets when the store was initialized.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the server version that initialized it.</summary>
    public required string CreatedBy { get; set; }
}
