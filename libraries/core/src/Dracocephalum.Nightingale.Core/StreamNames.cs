namespace Dracocephalum.Nightingale;

/// <summary>
/// The reserved stream names. A name that starts with <c>$</c> is never a plain stream: it is
/// <c>$all</c>, every event in position order; a virtual stream over a category or an event type,
/// or over the data events or the metadata events; or a stream's metadata stream, <c>$$</c> and
/// the stream's name.
/// </summary>
public static class StreamNames
{
    /// <summary>Every event, in position order.</summary>
    public const string All = "$all";

    /// <summary>The prefix of a category stream: <c>$ce-orders</c> holds every event of every <c>orders-*</c> stream.</summary>
    public const string CategoryPrefix = "$ce-";

    /// <summary>The prefix of an event-type stream: <c>$et-OrderPlaced</c> holds every event of that type.</summary>
    public const string EventTypePrefix = "$et-";

    /// <summary>
    /// The data events: every event of <c>$all</c> whose type does not begin with <c>$</c>, the
    /// reference's system events, which are the events its filter that excludes them leaves.
    /// </summary>
    public const string DataEvents = "$de";

    /// <summary>
    /// The metadata events: every event of type <see cref="MetadataEventType"/>, the event-type
    /// stream <c>$et-$metadata</c> under a shorter name.
    /// </summary>
    public const string MetadataEvents = "$mde";

    /// <summary>The prefix of a stream's metadata stream: <c>$$orders-1</c> holds the metadata of <c>orders-1</c>.</summary>
    public const string MetadataPrefix = "$$";

    /// <summary>The type of every event of a metadata stream.</summary>
    public const string MetadataEventType = "$metadata";

    /// <summary>Whether a name is reserved rather than a plain stream's.</summary>
    /// <param name="stream">The stream name.</param>
    /// <returns>True when the name starts with <c>$</c>.</returns>
    public static bool IsReserved(string stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.Length > 0 && stream[0] == '$';
    }

    /// <summary>Whether an event type is a system one: one that begins with <c>$</c>, as the reference's filter of system events has it.</summary>
    /// <param name="type">The event type.</param>
    /// <returns>True when the type starts with <c>$</c>.</returns>
    public static bool IsSystemEventType(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Length > 0 && type[0] == '$';
    }

    /// <summary>
    /// The category of a plain stream: its name up to the first hyphen, or the whole name when it
    /// has none. The rule the store's persisted category column computes, so what is matched in
    /// process is what a read by category matches.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <returns>The category.</returns>
    public static string CategoryOf(string stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var hyphen = stream.IndexOf('-', StringComparison.Ordinal);
        return hyphen < 0 ? stream : stream[..hyphen];
    }

    /// <summary>The metadata stream of a plain stream: <c>$$</c> and its name.</summary>
    /// <param name="stream">The plain stream's name.</param>
    /// <returns>The metadata stream's name.</returns>
    /// <exception cref="ArgumentException">The name is empty or reserved: only a plain stream has metadata.</exception>
    public static string ToMetadataStream(string stream)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);
        if (IsReserved(stream))
        {
            throw new ArgumentException($"'{stream}' is reserved; only a plain stream has metadata.", nameof(stream));
        }

        return MetadataPrefix + stream;
    }

    /// <summary>Recognises a metadata stream's name: <c>$$</c> followed by a plain stream's name.</summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="original">The plain stream whose metadata it holds, when the name is one.</param>
    /// <returns>True for <c>$$</c> followed by a name that does not itself start with <c>$</c>.</returns>
    public static bool TryParseMetadataStream(string stream, out string original)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length > MetadataPrefix.Length && stream.StartsWith(MetadataPrefix, StringComparison.Ordinal) && stream[MetadataPrefix.Length] != '$')
        {
            original = stream[MetadataPrefix.Length..];
            return true;
        }

        original = string.Empty;
        return false;
    }

    /// <summary>
    /// Recognises a virtual stream name: <c>$ce-</c> or <c>$et-</c> followed by a key,
    /// <see cref="DataEvents"/>, or <see cref="MetadataEvents"/>, which is read as the event-type
    /// stream of <see cref="MetadataEventType"/>.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="virtualStream">The virtual stream, when the name is one.</param>
    /// <returns>True for a virtual stream's name.</returns>
    public static bool TryParseVirtual(string stream, out VirtualStreamName virtualStream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length > CategoryPrefix.Length && stream.StartsWith(CategoryPrefix, StringComparison.Ordinal))
        {
            virtualStream = new VirtualStreamName(VirtualStreamKind.Category, stream[CategoryPrefix.Length..]);
            return true;
        }

        if (stream.Length > EventTypePrefix.Length && stream.StartsWith(EventTypePrefix, StringComparison.Ordinal))
        {
            virtualStream = new VirtualStreamName(VirtualStreamKind.EventType, stream[EventTypePrefix.Length..]);
            return true;
        }

        if (stream == DataEvents)
        {
            virtualStream = new VirtualStreamName(VirtualStreamKind.DataEvents, string.Empty);
            return true;
        }

        if (stream == MetadataEvents)
        {
            virtualStream = new VirtualStreamName(VirtualStreamKind.EventType, MetadataEventType);
            return true;
        }

        virtualStream = default;
        return false;
    }
}
