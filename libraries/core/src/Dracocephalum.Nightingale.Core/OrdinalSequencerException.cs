namespace Dracocephalum.Nightingale;

/// <summary>
/// A read or subscription asked for ordinal numbering while the server's ordinal sequencer is not
/// numbering: disabled by its operator, or faulted and trying again. The server's features call
/// says how it stands.
/// </summary>
public abstract class OrdinalSequencerException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="OrdinalSequencerException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">What made the sequencer stop, when it is known here.</param>
    protected OrdinalSequencerException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
