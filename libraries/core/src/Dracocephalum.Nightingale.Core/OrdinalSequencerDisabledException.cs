namespace Dracocephalum.Nightingale;

/// <summary>
/// Ordinal numbering was asked for and the server's ordinal sequencer is disabled by its operator.
/// Nothing is numbered until it is enabled again, so a retry does not help.
/// </summary>
public sealed class OrdinalSequencerDisabledException : OrdinalSequencerException
{
    /// <summary>Initializes a new instance of the <see cref="OrdinalSequencerDisabledException"/> class.</summary>
    public OrdinalSequencerDisabledException()
        : base("The ordinal sequencer is disabled: nothing is read or followed by ordinal until the server's operator enables it again.", null)
    {
    }
}
