namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// The sequencer's progress: one row, the global position every event up to which has its
/// ordinals. Written by the sequencer's batch, which is raw in the backend because its shape is
/// the point; read here by whoever needs to know whether a position is numbered yet.
/// </summary>
public sealed class SequencingProgressRow
{
    /// <summary>The id of the one row.</summary>
    public const int SingleRowId = 1;

    /// <summary>Gets or sets the id; always <see cref="SingleRowId"/>.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the position numbered through.</summary>
    public long NumberedThrough { get; set; }
}
