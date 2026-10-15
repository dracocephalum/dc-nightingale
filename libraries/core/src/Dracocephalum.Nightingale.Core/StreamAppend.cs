namespace Dracocephalum.Nightingale;

/// <summary>
/// One stream's part of an append to several streams: events for the stream, or new metadata for
/// it, each under its own expected state. The parts are written together or not at all.
/// </summary>
public abstract record StreamAppend
{
    private StreamAppend(string stream, StreamState expected)
    {
        Stream = stream;
        Expected = expected;
    }

    /// <summary>Gets the plain stream's name: the stream appended to, or the stream whose metadata is set.</summary>
    public string Stream { get; }

    /// <summary>Gets what the caller asserts about the stream written: the stream itself, or its metadata stream.</summary>
    public StreamState Expected { get; }

    /// <summary>Gets the name of the stream written: the stream itself, or its metadata stream.</summary>
    public abstract string Written { get; }

    /// <summary>Events appended to a plain stream, in order.</summary>
    public sealed record OfEvents : StreamAppend
    {
        /// <summary>Initializes a new instance of the <see cref="OfEvents"/> class.</summary>
        /// <param name="stream">The stream name.</param>
        /// <param name="expected">What the caller asserts about the stream.</param>
        /// <param name="events">The events, in order. Never empty.</param>
        public OfEvents(string stream, StreamState expected, IReadOnlyList<EventData> events)
            : base(stream, expected)
        {
            Events = events;
        }

        /// <summary>Gets the events, in order.</summary>
        public IReadOnlyList<EventData> Events { get; }

        /// <inheritdoc/>
        public override string Written => Stream;
    }

    /// <summary>
    /// A stream's metadata set, as a set of it alone sets it: replaced as a whole, checked against
    /// the expected state of the metadata stream, never of the stream itself, which need not exist.
    /// A reset is not a set here: a <c>$tb</c> at its largest value is refused, and the reset is
    /// its own call.
    /// </summary>
    public sealed record OfMetadata : StreamAppend
    {
        /// <summary>Initializes a new instance of the <see cref="OfMetadata"/> class.</summary>
        /// <param name="stream">The plain stream's name, whose metadata is set.</param>
        /// <param name="expected">What the caller asserts about the metadata stream.</param>
        /// <param name="metadata">The metadata.</param>
        public OfMetadata(string stream, StreamState expected, StreamMetadata metadata)
            : base(stream, expected)
        {
            Metadata = metadata;
        }

        /// <summary>Gets the metadata.</summary>
        public StreamMetadata Metadata { get; }

        /// <inheritdoc/>
        public override string Written => StreamNames.ToMetadataStream(Stream);
    }
}
