namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// Which of a parked message's numbers a caller addresses it by: the number the group speaks,
/// which is the revision for a group over a plain stream, the global position for one over
/// <c>$all</c> or a virtual stream, and the ordinal for one created under ordinal numbering.
/// </summary>
public enum ParkedNumber
{
    /// <summary>The event's global position.</summary>
    Position = 0,

    /// <summary>The event's revision within its stream.</summary>
    Revision = 1,

    /// <summary>The event's ordinal within the group's virtual stream.</summary>
    Ordinal = 2,
}
