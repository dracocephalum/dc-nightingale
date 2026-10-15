namespace Dracocephalum.Nightingale;

/// <summary>
/// What one stream's part of an append to several streams wrote: the revision and position of its
/// last event. The next append to the stream expects <see cref="Revision"/>.
/// </summary>
/// <param name="Stream">The stream written: the stream itself, or for a metadata set its metadata stream.</param>
/// <param name="Revision">Zero-based revision of the last event written to it.</param>
/// <param name="Position">Global position of the last event written to it.</param>
public sealed record StreamAppendResult(string Stream, long Revision, long Position);
