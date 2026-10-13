namespace Dracocephalum.Nightingale;

/// <summary>What an <see cref="EventFilter"/> looks at: the name of an event's stream, or its event type.</summary>
public enum EventFilterTarget
{
    /// <summary>The name of the stream the event belongs to.</summary>
    StreamName,

    /// <summary>The event's type name.</summary>
    EventType,
}
