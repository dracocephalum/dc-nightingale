namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The choices made once, when a store is initialized on an empty database, because each one
/// shapes the events table or where it lives and changing it afterwards means rebuilding the
/// table. The server records them in the store's settings rows and refuses to start against a
/// store whose record disagrees with the configuration, so a setting cannot drift under a running
/// system. Every backend has these four; what is configured and what is stored are both seen
/// through this interface, which is what the comparison between them is written against.
/// </summary>
public interface IStoreSettings
{
    /// <summary>
    /// Gets the schema the event store's tables live in. Fixed at initialization: a store
    /// initialized in one schema is found nowhere else.
    /// </summary>
    string Schema { get; }

    /// <summary>
    /// Gets the collation of the database: as configured, the one the database is created with,
    /// or null for the server's default; as stored, the one the database actually has. It decides
    /// whether two stream names that differ only in case are the same stream, and which
    /// characters a name can hold at all.
    /// </summary>
    string? Collation { get; }

    /// <summary>Gets how the events table is partitioned.</summary>
    PartitioningMode Partitioning { get; }

    /// <summary>
    /// Gets a value indicating whether the virtual streams are numbered: each event's dense,
    /// zero-based place within its category stream and its event-type stream, assigned after
    /// commit by one sequencer per cluster. Fixed at initialization because the columns and their
    /// indexes shape the table.
    /// </summary>
    bool AssignOrdinals { get; }
}
