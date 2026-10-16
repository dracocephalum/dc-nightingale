namespace Dracocephalum.Nightingale;

/// <summary>
/// Ordinal numbering was asked for and the ordinal sequencer has failed, again and again, to number
/// the sequence the read is in. It keeps trying and recovers by itself, so a retry with a back-off
/// succeeds once it has.
/// </summary>
public sealed class OrdinalSequencerFaultedException : OrdinalSequencerException
{
    /// <summary>Initializes a new instance of the <see cref="OrdinalSequencerFaultedException"/> class.</summary>
    /// <param name="cause">Why it fails, as the sequencer last saw it.</param>
    /// <param name="innerException">The failure itself, where it was seen.</param>
    public OrdinalSequencerFaultedException(string cause, Exception? innerException = null)
        : base("The ordinal sequencer is failing to number this sequence and is trying again: " + cause, innerException)
    {
        Cause = cause;
    }

    /// <summary>Gets why it fails, as the sequencer last saw it.</summary>
    public string Cause { get; }
}
