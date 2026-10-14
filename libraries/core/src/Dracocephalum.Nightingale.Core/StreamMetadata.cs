using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dracocephalum.Nightingale;

/// <summary>
/// A stream's metadata: the reserved settings the reference defines, each a property of its own,
/// and the application's own keys beside them. Stored as the body of a <c>$metadata</c> event in
/// the stream's metadata stream, in the reference's shape, the last one in force. A setting left
/// <see langword="null"/> is not written.
/// </summary>
public sealed record StreamMetadata
{
    /// <summary>Gets metadata that sets nothing.</summary>
    public static StreamMetadata Empty { get; } = new();

    /// <summary>Gets how old an event may be and still be read; positive, in whole seconds.</summary>
    public TimeSpan? MaxAge { get; init; }

    /// <summary>Gets how many of the stream's last events are read; positive.</summary>
    public long? MaxCount { get; init; }

    /// <summary>Gets the first revision read, the events before it hidden; never negative.</summary>
    public long? TruncateBefore { get; init; }

    /// <summary>Gets a caching hint carried over from the reference; positive, in whole seconds, and without effect here (<see cref="StreamMetadataKeys.CacheControl"/>).</summary>
    public TimeSpan? CacheControl { get; init; }

    /// <summary>Gets a flag carried over from the reference, without effect here (<see cref="StreamMetadataKeys.TempStream"/>).</summary>
    public bool? TempStream { get; init; }

    /// <summary>Gets the application's own keys, or <see langword="null"/> for none. None of them is a reserved key.</summary>
    public JsonObject? Custom { get; init; }

    /// <summary>
    /// Checks the metadata as the gateway stores it: every setting in range, and no reserved key
    /// among the application's. The reference stores a setting out of range and then reads the
    /// stream's metadata as empty, every key dropped without a word; refusing it is what keeps the
    /// metadata in force the metadata that was set.
    /// </summary>
    /// <exception cref="ArgumentException">What is wrong, in the message.</exception>
    public void Validate()
    {
        if (MaxAge is { } maxAge)
        {
            RequireWholePositiveSeconds(maxAge, StreamMetadataKeys.MaxAge);
        }

        if (MaxCount <= 0)
        {
            throw new ArgumentException($"{StreamMetadataKeys.MaxCount} is a positive count.", nameof(MaxCount));
        }

        if (TruncateBefore < 0)
        {
            throw new ArgumentException($"{StreamMetadataKeys.TruncateBefore} is a revision, never negative.", nameof(TruncateBefore));
        }

        if (TruncateBefore == long.MaxValue)
        {
            // The reference's soft delete; served here once soft delete is.
            throw new ArgumentException($"{StreamMetadataKeys.TruncateBefore} at its largest value is the reference's soft delete, which this server does not do yet.", nameof(TruncateBefore));
        }

        if (CacheControl is { } cacheControl)
        {
            RequireWholePositiveSeconds(cacheControl, StreamMetadataKeys.CacheControl);
        }

        if (Custom is null)
        {
            return;
        }

        foreach (var (key, _) in Custom)
        {
            if (key == StreamMetadataKeys.Acl)
            {
                throw new ArgumentException($"{StreamMetadataKeys.Acl} is not supported: stream access control lists are not enforced by this server, and one stored would read as a protection the stream does not have.", nameof(Custom));
            }

            if (StreamMetadataKeys.Typed.Contains(key))
            {
                throw new ArgumentException($"{key} is reserved; it is set through its own property, not among the application's keys.", nameof(Custom));
            }
        }
    }

    /// <summary>
    /// The first revision a read of the stream returns under <see cref="MaxCount"/> and
    /// <see cref="TruncateBefore"/>: the later of the two bounds, the reference's lower bound for
    /// every read of a stream. Beyond <paramref name="last"/> when the two hide every event.
    /// <see cref="MaxAge"/> hides events by their age on top of it, which revisions do not tell.
    /// </summary>
    /// <param name="last">The stream's last revision.</param>
    /// <returns>The first revision a read returns.</returns>
    public long GetFirstRevision(long last)
    {
        var first = 0L;
        if (MaxCount is { } maxCount)
        {
            first = Math.Max(first, last - maxCount + 1);
        }

        if (TruncateBefore is { } truncateBefore)
        {
            first = Math.Max(first, truncateBefore);
        }

        return first;
    }

    /// <summary>The body of the <c>$metadata</c> event that stores this metadata: a JSON object in the reference's shape, the reserved keys first.</summary>
    /// <returns>The UTF-8 JSON.</returns>
    /// <exception cref="ArgumentException">The metadata is not valid; see <see cref="Validate"/>.</exception>
    public byte[] ToJsonBytes()
    {
        Validate();
        var body = new JsonObject();
        if (MaxAge is { } maxAge)
        {
            body[StreamMetadataKeys.MaxAge] = (long)maxAge.TotalSeconds;
        }

        if (MaxCount is { } maxCount)
        {
            body[StreamMetadataKeys.MaxCount] = maxCount;
        }

        if (TruncateBefore is { } truncateBefore)
        {
            body[StreamMetadataKeys.TruncateBefore] = truncateBefore;
        }

        if (CacheControl is { } cacheControl)
        {
            body[StreamMetadataKeys.CacheControl] = (long)cacheControl.TotalSeconds;
        }

        if (TempStream is { } tempStream)
        {
            body[StreamMetadataKeys.TempStream] = tempStream;
        }

        if (Custom is not null)
        {
            foreach (var (key, value) in Custom)
            {
                body[key] = value?.DeepClone();
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(body, NightingaleJson.Default);
    }

    /// <summary>Reads metadata back from the body of a <c>$metadata</c> event.</summary>
    /// <param name="json">The body.</param>
    /// <returns>The metadata.</returns>
    /// <exception cref="FormatException">The body is not a JSON object, or a reserved key holds a value of the wrong type.</exception>
    public static StreamMetadata FromJson(ReadOnlySpan<byte> json)
    {
        JsonObject body;
        try
        {
            body = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Stream metadata is a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new FormatException("Stream metadata is a JSON object.", exception);
        }

        var metadata = new StreamMetadata();
        JsonObject? custom = null;
        foreach (var (key, value) in body)
        {
            switch (key)
            {
                case StreamMetadataKeys.MaxAge:
                    metadata = metadata with { MaxAge = TimeSpan.FromSeconds(ReadInt64(key, value)) };
                    break;
                case StreamMetadataKeys.MaxCount:
                    metadata = metadata with { MaxCount = ReadInt64(key, value) };
                    break;
                case StreamMetadataKeys.TruncateBefore:
                    metadata = metadata with { TruncateBefore = ReadInt64(key, value) };
                    break;
                case StreamMetadataKeys.CacheControl:
                    metadata = metadata with { CacheControl = TimeSpan.FromSeconds(ReadInt64(key, value)) };
                    break;
                case StreamMetadataKeys.TempStream:
                    metadata = metadata with { TempStream = value is JsonValue flag && flag.TryGetValue(out bool b) ? b : throw WrongType(key, "true or false") };
                    break;
                default:
                    custom ??= [];
                    custom[key] = value?.DeepClone();
                    break;
            }
        }

        return metadata with { Custom = custom };
    }

    private static long ReadInt64(string key, JsonNode? value) =>
        value is JsonValue number && number.TryGetValue(out long result) ? result : throw WrongType(key, "a whole number");

    private static FormatException WrongType(string key, string expected) => new($"Stream metadata's {key} holds {expected}.");

    private static void RequireWholePositiveSeconds(TimeSpan value, string key)
    {
        if (value <= TimeSpan.Zero || value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException($"{key} is a positive number of whole seconds.", key);
        }
    }
}
