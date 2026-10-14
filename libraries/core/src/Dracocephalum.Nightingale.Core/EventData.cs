using System.Text.Json.Nodes;

namespace Dracocephalum.Nightingale;

/// <summary>
/// An event as a client proposes it for appending. The id is the retry key: an append
/// retried with the same ids at the same expected revision is recognised as already done.
/// An event carries JSON data, binary data, or both: the JSON is what stays readable, by
/// filters, by people and by clients that know nothing of the binary format, and the binary
/// data is stored as it is sent, such as a Protobuf message or personal data encrypted under a
/// key that can be destroyed.
/// </summary>
/// <param name="Id">The event's id. Required; the server never invents one.</param>
/// <param name="Type">The event type, stored verbatim.</param>
/// <param name="Data">The JSON data, UTF-8; empty when the event carries binary data only, which stores <c>{}</c>.</param>
/// <param name="Metadata">User-supplied properties; keys starting with <c>$</c> are reserved, and <see cref="MetadataKeys.BinaryContentType"/> says what the binary data is.</param>
/// <param name="BinaryData">The binary data, stored as it is; empty for none.</param>
public sealed record EventData(Guid Id, string Type, ReadOnlyMemory<byte> Data, JsonObject? Metadata = null, ReadOnlyMemory<byte> BinaryData = default);
