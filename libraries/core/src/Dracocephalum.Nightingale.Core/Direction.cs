namespace Dracocephalum.Nightingale;

/// <summary>
/// The direction of a read: from a position towards the head, or from a position towards the start.
/// </summary>
public enum Direction
{
    /// <summary>Oldest to newest.</summary>
    Forwards = 0,

    /// <summary>Newest to oldest.</summary>
    Backwards = 1,
}
