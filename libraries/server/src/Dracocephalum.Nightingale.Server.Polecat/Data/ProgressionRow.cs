namespace Dracocephalum.Nightingale.Server.Polecat.Data;

/// <summary>
/// A row of the store's progression table. The one the gateway reads is the high-water mark:
/// the position every event up to which is committed, written by the store's daemon after the
/// events it covers and replicated with them.
/// </summary>
internal sealed class ProgressionRow
{
    /// <summary>Gets or sets the row's name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the position.</summary>
    public long LastSeqId { get; set; }
}
