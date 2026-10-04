namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// How far a background process of the gateway has come, by name: the sequencer's is the
/// position every event up to which has its ordinals.
/// </summary>
public sealed class ProgressMark
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
