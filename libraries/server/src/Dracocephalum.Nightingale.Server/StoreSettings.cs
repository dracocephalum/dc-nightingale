namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The settings a store is initialized with, as a plain object: what the settings rows are read
/// into, and the base of what a backend binds from configuration, which adds whatever is its own
/// to configure. Settable throughout, because both the configuration binder and the settings rows
/// fill it property by property.
/// </summary>
public class StoreSettings : IStoreSettings
{
    /// <inheritdoc/>
    public string Schema { get; set; } = "dbo";

    /// <inheritdoc/>
    public string? Collation { get; set; }

    /// <inheritdoc/>
    public PartitioningMode Partitioning { get; set; } = PartitioningMode.None;

    /// <inheritdoc/>
    public bool AssignOrdinals { get; set; }
}
