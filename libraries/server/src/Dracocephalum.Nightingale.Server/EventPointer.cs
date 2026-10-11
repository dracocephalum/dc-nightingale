namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// Where an event is, without what it is: the stream it belongs to, its revision there and its
/// position. What the router reads of an advanced range of the store before it reads the
/// events of the streams anyone follows.
/// </summary>
/// <param name="Stream">The stream name.</param>
/// <param name="Revision">The event's zero-based revision within the stream.</param>
/// <param name="Position">The event's global position.</param>
public sealed record EventPointer(string Stream, long Revision, long Position);
