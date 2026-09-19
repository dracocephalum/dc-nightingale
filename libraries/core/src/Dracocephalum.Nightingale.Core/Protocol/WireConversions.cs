using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Dracocephalum.Nightingale.Protocol;

/// <summary>
/// Explicit mappings between the domain types and the generated wire messages, shared by the client
/// and the server so both sides agree on every conversion. Nothing here interprets the values: the
/// numbers pass through unchanged, and metadata travels as the JSON text of the object, exactly as
/// the body does, so a client gets back what it sent.
/// </summary>
public static class WireConversions
{
    // Non-ASCII stays as written. The default encoder would escape it as \uXXXX, which is the same
    // JSON but not the same bytes, and fidelity is the promise here.
    private static readonly JsonSerializerOptions MetadataOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Maps a proposed event to its wire form.</summary>
    /// <param name="eventData">The event.</param>
    /// <returns>The wire message.</returns>
    public static ProposedEvent ToProposedEvent(this EventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        return new ProposedEvent
        {
            Id = eventData.Id.ToString("D"),
            EventType = eventData.Type,
            Data = ByteString.CopyFrom(eventData.Data.Span),
            Metadata = ToMetadataBytes(eventData.Metadata),
        };
    }

    /// <summary>Maps a wire proposed event to the domain type.</summary>
    /// <param name="proposed">The wire message.</param>
    /// <returns>The event.</returns>
    /// <exception cref="FormatException">The id is not a UUID.</exception>
    /// <exception cref="JsonException">The metadata is not a JSON object.</exception>
    public static EventData ToEventData(this ProposedEvent proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        return new EventData(
            Guid.ParseExact(proposed.Id, "D"),
            proposed.EventType,
            proposed.Data.Memory,
            proposed.Metadata.IsEmpty ? null : ParseMetadata(proposed.Metadata));
    }

    /// <summary>Maps a stored event to its wire form.</summary>
    /// <param name="record">The event.</param>
    /// <returns>The wire message.</returns>
    public static RecordedEvent ToRecordedEvent(this EventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new RecordedEvent
        {
            Id = record.Id.ToString("D"),
            Stream = record.Stream,
            Revision = record.Revision,
            Position = record.Position,
            EventType = record.Type,
            Created = Timestamp.FromDateTimeOffset(record.Created),
            Data = ByteString.CopyFrom(record.Data.Span),
            Metadata = ToMetadataBytes(record.Metadata),
        };
    }

    /// <summary>Maps a wire recorded event to the domain type.</summary>
    /// <param name="recorded">The wire message.</param>
    /// <returns>The event.</returns>
    /// <exception cref="FormatException">The id is not a UUID.</exception>
    /// <exception cref="JsonException">The metadata is not a JSON object.</exception>
    public static EventRecord ToEventRecord(this RecordedEvent recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        return new EventRecord(
            Guid.ParseExact(recorded.Id, "D"),
            recorded.Stream,
            recorded.Revision,
            recorded.Position,
            recorded.EventType,
            recorded.Created.ToDateTimeOffset(),
            recorded.Data.Memory,
            recorded.Metadata.IsEmpty ? [] : ParseMetadata(recorded.Metadata));
    }

    /// <summary>The wire form of a metadata object: its JSON text, or empty when there is nothing to say.</summary>
    /// <param name="metadata">The object, possibly null or empty.</param>
    /// <returns>The bytes.</returns>
    public static ByteString ToMetadataBytes(JsonObject? metadata) =>
        metadata is null || metadata.Count == 0
            ? ByteString.Empty
            : ByteString.CopyFromUtf8(metadata.ToJsonString(MetadataOptions));

    /// <summary>Parses wire metadata back into an object.</summary>
    /// <param name="metadata">The bytes; must hold a JSON object.</param>
    /// <returns>The object.</returns>
    /// <exception cref="JsonException">The bytes are not a JSON object.</exception>
    public static JsonObject ParseMetadata(ByteString metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return JsonNode.Parse(metadata.Span) as JsonObject
            ?? throw new JsonException("Metadata is a JSON object.");
    }
}
