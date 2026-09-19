using System.Text.Json.Nodes;

namespace Dracocephalum.Nightingale;

/// <summary>
/// An event as a client proposes it for appending. The id is the retry key: an append
/// retried with the same ids at the same expected revision is recognised as already done.
/// </summary>
/// <param name="Id">The event's id. Required; the server never invents one.</param>
/// <param name="Type">The event type, stored verbatim.</param>
/// <param name="Data">The body, UTF-8 JSON.</param>
/// <param name="Metadata">User-supplied properties; keys starting with <c>$</c> are reserved.</param>
public sealed record EventData(Guid Id, string Type, ReadOnlyMemory<byte> Data, JsonObject? Metadata = null);
