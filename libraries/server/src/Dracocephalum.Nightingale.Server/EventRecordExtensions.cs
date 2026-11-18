using System.Text.Json.Nodes;

namespace Dracocephalum.Nightingale.Server;

/// <summary>What the gateway reads off an event beyond its own members.</summary>
internal static class EventRecordExtensions
{
    /// <summary>The event's correlation id: the reserved metadata key when it holds a string, or null.</summary>
    public static string? GetCorrelationId(this EventRecord record) =>
        record.Metadata.TryGetPropertyValue(MetadataKeys.CorrelationId, out var value) && value is JsonValue text && text.TryGetValue<string>(out var id) ? id : null;

    /// <summary>What an event is pinned by under correlation pinning: its correlation id, or its stream name when it has none.</summary>
    public static string GetPinKey(this EventRecord record) =>
        record.GetCorrelationId() is { Length: > 0 } correlation ? correlation : record.Stream;

    /// <summary>The event's ordinal, which an ordinal read always brings.</summary>
    public static long RequireOrdinal(this EventRecord record) =>
        record.Ordinal ?? throw new InvalidOperationException("The store returned an event without its ordinal from an ordinal read.");
}
