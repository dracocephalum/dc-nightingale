namespace Dracocephalum.Nightingale;

/// <summary>
/// The reserved stream names. A name that starts with <c>$</c> is never a plain stream: it is
/// <c>$all</c>, every event in position order, or a virtual stream over a category or an event
/// type.
/// </summary>
public static class StreamNames
{
    /// <summary>Every event, in position order.</summary>
    public const string All = "$all";

    /// <summary>The prefix of a category stream: <c>$ce-orders</c> holds every event of every <c>orders-*</c> stream.</summary>
    public const string CategoryPrefix = "$ce-";

    /// <summary>The prefix of an event-type stream: <c>$et-OrderPlaced</c> holds every event of that type.</summary>
    public const string EventTypePrefix = "$et-";

    /// <summary>Whether a name is reserved rather than a plain stream's.</summary>
    /// <param name="stream">The stream name.</param>
    /// <returns>True when the name starts with <c>$</c>.</returns>
    public static bool IsReserved(string stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.Length > 0 && stream[0] == '$';
    }
}
