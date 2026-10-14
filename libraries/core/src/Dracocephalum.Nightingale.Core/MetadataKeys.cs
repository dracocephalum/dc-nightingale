namespace Dracocephalum.Nightingale;

/// <summary>
/// The metadata properties the server understands, all strings a caller writes into an event's
/// metadata. The first two the store keeps in columns of their own and gives back in the
/// metadata, and a persistent-subscription group pinned by correlation goes by the first; the
/// third stays among the other keys and describes the event's binary data.
/// </summary>
public static class MetadataKeys
{
    /// <summary>The id of the workflow an event belongs to, shared by every event it caused.</summary>
    public const string CorrelationId = "$correlationId";

    /// <summary>The id of the event that caused this one.</summary>
    public const string CausationId = "$causationId";

    /// <summary>
    /// The media type of the event's binary data, such as <c>application/x-protobuf</c>; without
    /// it, the binary data is <see cref="DefaultBinaryContentType"/>. Only an event with binary
    /// data may carry it.
    /// </summary>
    public const string BinaryContentType = "$binaryContentType";

    /// <summary>What binary data is when its event says nothing of its type.</summary>
    public const string DefaultBinaryContentType = "application/octet-stream";
}
