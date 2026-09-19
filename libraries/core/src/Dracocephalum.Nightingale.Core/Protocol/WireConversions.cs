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
        var recorded = new RecordedEvent
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
        if (record.Ordinal is { } ordinal)
        {
            recorded.Ordinal = ordinal;
        }

        return recorded;
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
            recorded.Metadata.IsEmpty ? [] : ParseMetadata(recorded.Metadata),
            recorded.HasOrdinal ? recorded.Ordinal : null);
    }

    /// <summary>Maps a numbering to its wire form.</summary>
    /// <param name="numbering">The numbering.</param>
    /// <returns>The wire value.</returns>
    public static Protocol.V1.Numbering ToWire(this Numbering numbering) =>
        numbering == Numbering.Ordinal ? Protocol.V1.Numbering.Ordinal : Protocol.V1.Numbering.Global;

    /// <summary>Maps a wire numbering to the domain type; unspecified is global.</summary>
    /// <param name="numbering">The wire value.</param>
    /// <returns>The numbering.</returns>
    public static Numbering ToNumbering(this Protocol.V1.Numbering numbering) =>
        numbering == Protocol.V1.Numbering.Ordinal ? Numbering.Ordinal : Numbering.Global;

    /// <summary>Maps group settings from their wire form; unset fields take the defaults.</summary>
    /// <param name="settings">The wire message.</param>
    /// <returns>The settings.</returns>
    public static GroupSettings ToGroupSettings(this Protocol.V1.GroupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var defaults = GroupSettings.Default;
        var start = settings.StartCase switch
        {
            Protocol.V1.GroupSettings.StartOneofCase.FromStart => StreamPosition.Start,
            Protocol.V1.GroupSettings.StartOneofCase.FromPosition => StreamPosition.From(settings.FromPosition),
            _ => StreamPosition.End,
        };
        return new GroupSettings(
            start,
            settings.MessageTimeout?.ToTimeSpan() ?? defaults.MessageTimeout,
            settings.MaxRetryCount > 0 ? settings.MaxRetryCount : defaults.MaxRetryCount,
            settings.CheckpointUpperBound > 0 ? settings.CheckpointUpperBound : defaults.CheckpointUpperBound,
            settings.CheckpointAfter?.ToTimeSpan() ?? defaults.CheckpointAfter,
            settings.CheckpointLowerBound > 0 ? settings.CheckpointLowerBound : defaults.CheckpointLowerBound,
            settings.BufferSize > 0 ? settings.BufferSize : defaults.BufferSize,
            settings.MaxSubscriberCount > 0 ? settings.MaxSubscriberCount : defaults.MaxSubscriberCount);
    }

    /// <summary>Maps group settings to their wire form.</summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The wire message.</returns>
    public static Protocol.V1.GroupSettings ToWire(this GroupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var wire = new Protocol.V1.GroupSettings
        {
            MessageTimeout = Duration.FromTimeSpan(settings.MessageTimeout),
            MaxRetryCount = settings.MaxRetryCount,
            CheckpointUpperBound = settings.CheckpointUpperBound,
            CheckpointAfter = Duration.FromTimeSpan(settings.CheckpointAfter),
            CheckpointLowerBound = settings.CheckpointLowerBound,
            BufferSize = settings.BufferSize,
            MaxSubscriberCount = settings.MaxSubscriberCount,
        };
        if (settings.Start.IsEnd)
        {
            wire.FromEnd = new Empty();
        }
        else if (settings.Start == StreamPosition.Start)
        {
            wire.FromStart = new Empty();
        }
        else
        {
            wire.FromPosition = settings.Start.Value;
        }

        return wire;
    }

    /// <summary>The wire form of a metadata object: its JSON text, or empty when there is nothing to say.</summary>
    /// <param name="metadata">The object, possibly null or empty.</param>
    /// <returns>The bytes.</returns>
    public static ByteString ToMetadataBytes(JsonObject? metadata) =>
        metadata is null || metadata.Count == 0
            ? ByteString.Empty
            : ByteString.CopyFromUtf8(metadata.ToJsonString(NightingaleJson.Options));

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
