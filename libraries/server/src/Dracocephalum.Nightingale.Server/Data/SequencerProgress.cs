namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// How far the sequencer has come: the position every event up to which has its ordinals. One
/// row, found by its name.
/// </summary>
public sealed class SequencerProgress
{
    /// <summary>The name of the sequencer's row.</summary>
    public const string Ordinals = "Ordinals";

    /// <summary>Gets or sets the row's id, its key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the process the row belongs to; unique.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the position reached.</summary>
    public long Position { get; set; }
}
