namespace Dracocephalum.Nightingale;

/// <summary>
/// The reserved keys of a stream's metadata, as the body of a <c>$metadata</c> event writes them:
/// the reference's names, so that a metadata stream reads as the reference's does. Each has a
/// property of its own on <see cref="StreamMetadata"/>; an application's own keys go beside them
/// and must not be one of them.
/// </summary>
public static class StreamMetadataKeys
{
    /// <summary>How old an event may be and still be read, in whole seconds.</summary>
    public const string MaxAge = "$maxAge";

    /// <summary>How many of the stream's last events are read.</summary>
    public const string MaxCount = "$maxCount";

    /// <summary>The first revision read: the events before it are hidden.</summary>
    public const string TruncateBefore = "$tb";

    /// <summary>
    /// Carried over from the reference, without effect here: a caching hint for the reference's
    /// HTTP interface, in whole seconds, which this gateway does not have.
    /// </summary>
    public const string CacheControl = "$cacheControl";

    /// <summary>
    /// Carried over from the reference, without effect here: there, it asks the scavenge that
    /// removes a deleted stream's events to remove the stream's metadata too. It gets that meaning
    /// here when the events metadata hides are physically removed.
    /// </summary>
    public const string TempStream = "$tmp";

    /// <summary>
    /// The reference's access control list. Refused, until authentication carries stream ACLs:
    /// accepted and ignored, it would read as a protection the stream does not have.
    /// </summary>
    public const string Acl = "$acl";

    /// <summary>
    /// The keys carried over from the reference: stored and returned, so a client gets back what it
    /// set, and of no effect in the gateway. Supporting one is taking it out of this list, so the
    /// list is where to look for what is still only carried.
    /// </summary>
    public static IReadOnlyList<string> CarriedOver { get; } = [CacheControl, TempStream];

    /// <summary>Gets every reserved key that has a property of its own: all but <see cref="Acl"/>.</summary>
    public static IReadOnlyList<string> Typed { get; } = [MaxAge, MaxCount, TruncateBefore, CacheControl, TempStream];
}
