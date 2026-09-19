namespace Dracocephalum.Nightingale;

/// <summary>
/// What an append wrote: the revision and position of its last event. The next append to the same
/// stream expects <see cref="Revision"/>.
/// </summary>
/// <param name="Revision">Zero-based revision of the last event written.</param>
/// <param name="Position">Global position of the last event written.</param>
public sealed record AppendResult(long Revision, long Position);
