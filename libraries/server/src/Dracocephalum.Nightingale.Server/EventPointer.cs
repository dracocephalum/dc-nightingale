namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// Where an event is, without what it is: the stream it belongs to, its revision there, its
/// position and its type. What the router reads of an advanced range of the store to find which
/// followed streams, categories and types the range touches, before it reads the events of those.
/// The category is not carried: it is the stream name's, by <see cref="StreamNames.CategoryOf"/>.
/// </summary>
/// <param name="Stream">The stream name.</param>
/// <param name="Revision">The event's zero-based revision within the stream.</param>
/// <param name="Position">The event's global position.</param>
/// <param name="Type">The event type.</param>
public sealed record EventPointer(string Stream, long Revision, long Position, string Type);
