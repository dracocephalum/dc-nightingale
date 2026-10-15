namespace Dracocephalum.Nightingale;

/// <summary>
/// A condition on a stream an append to several streams does not write: the append is written only
/// if the stream is, at its commit, in the state given. A stream that is written states its
/// condition as its own expected state instead, so a check naming one is refused.
/// </summary>
/// <param name="Stream">The stream name.</param>
/// <param name="Expected">A revision, <see cref="StreamState.NoStream"/> or <see cref="StreamState.StreamExists"/>; <see cref="StreamState.Any"/> asserts nothing and is refused.</param>
public sealed record StreamCheck(string Stream, StreamState Expected);
