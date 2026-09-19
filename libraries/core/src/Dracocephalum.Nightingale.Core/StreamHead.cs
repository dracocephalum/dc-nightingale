namespace Dracocephalum.Nightingale;

/// <summary>
/// The bounds of a stream at the moment it was read: the first revision still held and the last one
/// written. For a virtual stream both are positions. Every read reports the head before any event,
/// which is what lets a consumer measure how far behind it is.
/// </summary>
/// <param name="First">The first revision still held; greater than zero after compaction.</param>
/// <param name="Last">The last revision written.</param>
public sealed record StreamHead(long First, long Last);
