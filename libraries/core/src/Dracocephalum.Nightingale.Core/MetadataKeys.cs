namespace Dracocephalum.Nightingale;

/// <summary>
/// The metadata properties the server understands. Both are strings a caller writes into an
/// event's metadata; the store keeps them in columns of their own and gives them back in the
/// metadata, and a persistent-subscription group pinned by correlation goes by the first.
/// </summary>
public static class MetadataKeys
{
    /// <summary>The id of the workflow an event belongs to, shared by every event it caused.</summary>
    public const string CorrelationId = "$correlationId";

    /// <summary>The id of the event that caused this one.</summary>
    public const string CausationId = "$causationId";
}
