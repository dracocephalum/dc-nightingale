namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// What the first message of a stream read said: the stream exists, or it has no events.
/// </summary>
public enum ReadState
{
    /// <summary>The stream has no events; enumerating the result as events throws.</summary>
    StreamNotFound = 0,

    /// <summary>The stream exists and its head is known.</summary>
    Ok = 1,
}
