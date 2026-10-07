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

    /// <summary>Maps a consumer strategy to its wire form.</summary>
    /// <param name="strategy">The strategy.</param>
    /// <returns>The wire value.</returns>
    public static Protocol.V1.ConsumerStrategy ToWire(this ConsumerStrategy strategy) => strategy switch
    {
        ConsumerStrategy.Pinned => Protocol.V1.ConsumerStrategy.Pinned,
        ConsumerStrategy.DispatchToSingle => Protocol.V1.ConsumerStrategy.DispatchToSingle,
        ConsumerStrategy.PinnedByCorrelation => Protocol.V1.ConsumerStrategy.PinnedByCorrelation,
        _ => Protocol.V1.ConsumerStrategy.RoundRobin,
    };

    /// <summary>Maps a consumer strategy from its wire form; unspecified is round robin.</summary>
    /// <param name="strategy">The wire value.</param>
    /// <returns>The strategy.</returns>
    public static ConsumerStrategy ToConsumerStrategy(this Protocol.V1.ConsumerStrategy strategy) => strategy switch
    {
        Protocol.V1.ConsumerStrategy.Pinned => ConsumerStrategy.Pinned,
        Protocol.V1.ConsumerStrategy.DispatchToSingle => ConsumerStrategy.DispatchToSingle,
        Protocol.V1.ConsumerStrategy.PinnedByCorrelation => ConsumerStrategy.PinnedByCorrelation,
        _ => ConsumerStrategy.RoundRobin,
    };

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
            settings.MaxSubscriberCount > 0 ? settings.MaxSubscriberCount : defaults.MaxSubscriberCount,
            settings.Numbering.ToNumbering(),
            settings.ConsumerStrategy.ToConsumerStrategy());
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
            Numbering = settings.Numbering.ToWire(),
            ConsumerStrategy = settings.ConsumerStrategy.ToWire(),
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

    /// <summary>Maps a group's description from its wire form.</summary>
    /// <param name="info">The wire message.</param>
    /// <returns>The description.</returns>
    public static PersistentSubscriptionInfo ToPersistentSubscriptionInfo(this GroupInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new PersistentSubscriptionInfo(
            info.Stream,
            info.Group,
            (info.Settings ?? new Protocol.V1.GroupSettings()).ToGroupSettings(),
            info.CreatedAt?.ToDateTimeOffset() ?? default,
            info.HasCheckpoint ? info.Checkpoint : null,
            info.ParkedCount,
            info.OutboxCount,
            info.Running,
            info.OwnerAddress.Length == 0 ? null : new Uri(info.OwnerAddress, UriKind.Absolute),
            info.HasLastKnownPosition ? info.LastKnownPosition : null,
            info.Live is null
                ? null
                : new PersistentSubscriptionLiveInfo(
                    info.Live.ConnectedAt?.ToDateTimeOffset() ?? default,
                    info.Live.InFlightCount,
                    info.Live.AwaitingRetryCount,
                    info.Live.ConsumerBufferSize,
                    info.Live.HasCheckpoint ? info.Live.Checkpoint : null,
                    info.Live.OldestInFlightAt?.ToDateTimeOffset(),
                    info.Live.ConsumerAddress.Length == 0 ? null : info.Live.ConsumerAddress,
                    info.Live.AsOf?.ToDateTimeOffset() ?? default,
                    info.Live.FromOwner,
                    info.Live.ConsumerCount,
                    info.Live.Consumers.Select(consumer => new PersistentSubscriptionConsumerInfo(consumer.ConnectedAt?.ToDateTimeOffset() ?? default, consumer.Address.Length == 0 ? null : consumer.Address, consumer.BufferSize, consumer.InFlightCount)).ToList()));
    }

    /// <summary>Maps a parked message from its wire form.</summary>
    /// <param name="message">The wire message.</param>
    /// <returns>The parked message.</returns>
    public static ParkedMessageInfo ToParkedMessageInfo(this ParkedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new ParkedMessageInfo(message.Number, Guid.Parse(message.EventId), message.Reason, message.RetryCount, message.ParkedAt?.ToDateTimeOffset() ?? default, message.Position, message.Revision);
    }

    /// <summary>Maps an outbox message from its wire form.</summary>
    /// <param name="message">The wire message.</param>
    /// <returns>The outbox message.</returns>
    public static OutboxMessageInfo ToOutboxMessageInfo(this OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new OutboxMessageInfo(message.Number, Guid.Parse(message.EventId), message.Reason, message.RetryCount, message.DueAt?.ToDateTimeOffset() ?? default, message.Position, message.Revision);
    }

    /// <summary>The wire form of a metadata object: its JSON text, or empty when there is nothing to say.</summary>
    /// <param name="metadata">The object, possibly null or empty.</param>
    /// <returns>The bytes.</returns>
    public static ByteString ToMetadataBytes(JsonObject? metadata) =>
        metadata is null || metadata.Count == 0
            ? ByteString.Empty
            : ByteString.CopyFromUtf8(metadata.ToJsonString(NightingaleJson.Default));

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
