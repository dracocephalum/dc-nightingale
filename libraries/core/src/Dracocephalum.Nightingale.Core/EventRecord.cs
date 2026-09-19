using System.Text.Json.Nodes;

namespace Dracocephalum.Nightingale;

/// <summary>
/// An event as the store holds it. <see cref="Revision"/> is zero-based within the stream;
/// <see cref="Position"/> is the store's global sequence, the same number wherever the event appears;
/// <see cref="Ordinal"/> is its place within the virtual stream it was read from, present only
/// under <see cref="Numbering.Ordinal"/>.
/// </summary>
/// <param name="Id">The event's id.</param>
/// <param name="Stream">The stream it was appended to.</param>
/// <param name="Revision">Zero-based revision within the stream.</param>
/// <param name="Position">Global position.</param>
/// <param name="Type">The event type, as the client sent it.</param>
/// <param name="Created">Server clock at commit.</param>
/// <param name="Data">The body, UTF-8 JSON.</param>
/// <param name="Metadata">User-supplied properties; empty when none were supplied.</param>
/// <param name="Ordinal">The event's zero-based, dense place within the virtual stream it was read from, or
/// <see langword="null"/> when the read was numbered globally.</param>
public sealed record EventRecord(
    Guid Id,
    string Stream,
    long Revision,
    long Position,
    string Type,
    DateTimeOffset Created,
    ReadOnlyMemory<byte> Data,
    JsonObject Metadata,
    long? Ordinal = null);
