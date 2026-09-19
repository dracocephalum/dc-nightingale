using System.Text.Json;
using System.Text.Json.Nodes;

using JasperFx.Events;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// The event the gateway appends: a JSON body with the client's own type name. The store wraps
/// every appended object in <c>Event&lt;T&gt;</c> and, on the way in, overwrites the type name with
/// the one derived from the CLR type, which for a body of <see cref="JsonElement"/> would be the
/// same for every event. This class re-implements the interface's type-name property so that
/// overwrite is a no-op, which is what lets the gateway stay free of event classes. The store
/// writes through the interface, and reads restore the name from its own column, so the round
/// trip holds; a test guards that it keeps holding across store releases.
/// </summary>
internal sealed class JsonEvent : Event<JsonElement>, IEvent
{
    private const string CorrelationKey = "$correlationId";
    private const string CausationKey = "$causationId";

    private readonly string _typeName;

    private JsonEvent(JsonElement data, string typeName)
        : base(data)
    {
        _typeName = typeName;
    }

    /// <inheritdoc/>
    string IEvent.EventTypeName
    {
        get => _typeName;
        set
        {
            // The store assigns its CLR-derived name here on every append. The client's name wins.
        }
    }

    /// <summary>Wraps a proposed event. The two reserved metadata keys move to their own columns.</summary>
    /// <param name="eventData">The event as proposed.</param>
    /// <returns>The event to append.</returns>
    public static JsonEvent From(EventData eventData)
    {
        using var document = JsonDocument.Parse(eventData.Data);
        var wrapped = new JsonEvent(document.RootElement.Clone(), eventData.Type) { Id = eventData.Id };
        if (eventData.Metadata is null || eventData.Metadata.Count == 0)
        {
            return wrapped;
        }

        var headers = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, node) in eventData.Metadata)
        {
            switch (key)
            {
                case CorrelationKey:
                    wrapped.CorrelationId = node?.GetValue<string>();
                    break;
                case CausationKey:
                    wrapped.CausationId = node?.GetValue<string>();
                    break;
                default:
                    headers[key] = node is null ? JsonValue.Create((string?)null)! : node.DeepClone();
                    break;
            }
        }

        if (headers.Count > 0)
        {
            wrapped.Headers = headers;
        }

        return wrapped;
    }

    /// <summary>Rebuilds the metadata object of a stored event from its headers and its two columns.</summary>
    /// <param name="stored">The event as the store returned it.</param>
    /// <returns>The metadata; empty when the event carries none.</returns>
    public static JsonObject MetadataOf(IEvent stored)
    {
        var metadata = stored.Headers is { Count: > 0 } headers
            ? JsonSerializer.SerializeToNode(headers) as JsonObject ?? []
            : [];
        if (stored.CorrelationId is { } correlation)
        {
            metadata[CorrelationKey] = correlation;
        }

        if (stored.CausationId is { } causation)
        {
            metadata[CausationKey] = causation;
        }

        return metadata;
    }
}
