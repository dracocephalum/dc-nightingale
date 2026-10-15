namespace Dracocephalum.Nightingale;

/// <summary>One stream of an append to several streams that was not in the state its caller expected.</summary>
/// <param name="Index">Where the condition was given: the appends in order, then the checks in order after them.</param>
/// <param name="Stream">The stream the condition was on: the stream written, a metadata stream for a metadata set, or the stream checked.</param>
/// <param name="Expected">What the caller asserted.</param>
/// <param name="ActualRevision">The stream's revision, or <c>-1</c> when it does not exist.</param>
public sealed record StreamConflict(int Index, string Stream, StreamState Expected, long ActualRevision);
