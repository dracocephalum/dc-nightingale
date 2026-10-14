namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// One event's ordinal in a virtual stream whose ordinals are not columns of the events table:
/// a correlation, the listing of every stream, or the listing of a category's streams. It is an
/// extension of the store's events table, not an entity of the gateway's: the numbers could be
/// columns of the event's row, as the category and type ordinals are, and are kept here only
/// because most events carry no correlation id and most begin no stream, so a column would spend
/// eight bytes on every row for numbers few rows have, while this table holds only the numbers
/// that exist. So it is keyed as the store's data is, by what it describes: the stream, by type,
/// tenant and key, and the ordinal within it. The sequencer writes a row in the batch that
/// numbers the event's category and type; nothing else writes here, and nothing removes a row, so
/// an event archived or deleted after it was numbered is a hole a read skips, and the last
/// ordinal of a key is never reused.
/// </summary>
[StoreExtension]
public sealed class OrdinalLink
{
    /// <summary>Gets or sets which virtual stream the ordinal is in.</summary>
    public OrdinalLinkType Type { get; set; }

    /// <summary>Gets or sets the id the store keeps for the event's tenant.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Gets or sets the stream's key: the correlation id or the category, and empty for the listing of every stream.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the event's ordinal in the stream, from 0 and dense as numbered.</summary>
    public long Ordinal { get; set; }

    /// <summary>Gets or sets the event's position, which with the tenant names its row in the events table.</summary>
    public long SeqId { get; set; }
}
