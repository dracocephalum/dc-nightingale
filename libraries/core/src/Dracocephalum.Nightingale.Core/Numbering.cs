namespace Dracocephalum.Nightingale;

/// <summary>
/// How a read or subscription of a virtual stream is numbered. Under <see cref="Global"/> every
/// number in the conversation is a global position, sparse, and the events carry no ordinal. Under
/// <see cref="Ordinal"/> every number is the event's place within the virtual stream, dense, and
/// each event carries its ordinal beside the position it always carries. A consumer stores the
/// numbering with its checkpoint and never resumes under the other.
/// </summary>
public enum Numbering
{
    /// <summary>Global positions; the default, and the only numbering of a plain stream or <c>$all</c>.</summary>
    Global = 0,

    /// <summary>
    /// Ordinals: <c>$ce-</c> and <c>$et-</c> only, on a store initialized with the feature. Refused
    /// otherwise, before anything is delivered.
    /// </summary>
    Ordinal = 1,
}
