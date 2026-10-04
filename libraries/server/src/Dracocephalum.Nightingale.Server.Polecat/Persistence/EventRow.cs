namespace Dracocephalum.Nightingale.Server.Polecat.Persistence;

/// <summary>
/// An event as its row in the store's events table, with the columns the gateway reads: the
/// store's own, the persisted category the gateway adds, and the two ordinals a store
/// initialized with them has. Read, never written: the store appends events and the sequencer
/// numbers them.
/// </summary>
internal sealed class EventRow
{
    /// <summary>Gets or sets the global position.</summary>
    public long SeqId { get; set; }

    /// <summary>Gets or sets the event's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name of the stream the event belongs to.</summary>
    public required string StreamId { get; set; }

    /// <summary>Gets or sets the event's version within its stream, one-based.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets the body, a JSON document.</summary>
    public required string Data { get; set; }

    /// <summary>Gets or sets the event type name.</summary>
    public required string Type { get; set; }

    /// <summary>Gets or sets when the event was appended.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Gets or sets the tenant.</summary>
    public required string TenantId { get; set; }

    /// <summary>Gets or sets the correlation id, when the event carries one.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Gets or sets the causation id, when the event carries one.</summary>
    public string? CausationId { get; set; }

    /// <summary>Gets or sets the headers, a JSON object, when the event carries any.</summary>
    public string? Headers { get; set; }

    /// <summary>Gets or sets a value indicating whether the event's stream was deleted.</summary>
    public bool IsArchived { get; set; }

    /// <summary>Gets or sets the category: the stream name up to its first hyphen, computed by the database.</summary>
    public required string Category { get; set; }

    /// <summary>Gets or sets the event's place in its category stream, once the sequencer has numbered it.</summary>
    public long? CategoryOrdinal { get; set; }

    /// <summary>Gets or sets the event's place in its event-type stream, once the sequencer has numbered it.</summary>
    public long? TypeOrdinal { get; set; }
}
