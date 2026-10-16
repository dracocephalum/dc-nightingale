namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// One event's ordinal in a virtual stream: a category, an event type, a correlation, the listing
/// of every stream, or the listing of a category's streams. It is an extension of the store's
/// events table, not an entity of the gateway's: the numbers describe the event's row, and are kept
/// here rather than in columns of it because a store provider's migrations drop the columns they
/// do not know, and an update of an event's row writes the whole row again on some databases. So it
/// is keyed as the store's data is, by what it describes: the stream, by type, tenant and key, and
/// the ordinal within it. Every event has its type's link and its category's; the sequencer writes
/// an event's links in one batch, so the type's is the witness that the event is numbered. Nothing
/// else writes here, and nothing removes a row, so an event archived or deleted after it was
/// numbered is a hole a read skips, and the last ordinal of a key is never reused.
/// </summary>
[StoreExtension]
public sealed class OrdinalLink
{
    /// <summary>Gets or sets which virtual stream the ordinal is in.</summary>
    public OrdinalLinkType Type { get; set; }

    /// <summary>Gets or sets the id the store keeps for the event's tenant.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Gets or sets the stream's key: the category, the event type, the correlation id, or empty for the listing of every stream.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the event's ordinal in the stream, from 0 and dense as numbered.</summary>
    public long Ordinal { get; set; }

    /// <summary>Gets or sets the event's position, which with the tenant names its row in the events table.</summary>
    public long SeqId { get; set; }
}
