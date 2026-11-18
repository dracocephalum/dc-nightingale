using Dracocephalum.Nightingale.Server.Polecat.Data;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>The filters the readers put on a query of event rows, each translated by the provider into the one statement.</summary>
internal static class EventRowQueryExtensions
{
    /// <summary>
    /// The most seconds a <c>$maxAge</c> is checked for: the database adds a whole number of
    /// seconds of the width of an <c>int</c>, some 68 years, and nothing in a store is older.
    /// </summary>
    private const double LongestCheckedAge = int.MaxValue;

    /// <summary>The page's rows: those of the stream, the database's clock deciding which are too old for a <c>$maxAge</c>.</summary>
    public static IQueryable<EventRow> FilterByAge(this IQueryable<EventRow> rows, TimeSpan? maxAge)
    {
        if (maxAge is not { TotalSeconds: <= LongestCheckedAge } age)
        {
            return rows;
        }

        // UtcNow is the database's, translated rather than sent: an event's timestamp is the
        // database's clock when it was appended, and every instance's own clock differs from it.
        // The ban on it is for clocks read in process, which this one never is.
        var seconds = -age.TotalSeconds;
#pragma warning disable RS0030
        return rows.Where(e => e.Timestamp >= DateTimeOffset.UtcNow.AddSeconds(seconds));
#pragma warning restore RS0030
    }

    /// <summary>The events from a position on, in the direction read: up to the head forwards, with no bound backwards.</summary>
    public static IQueryable<EventRow> FilterByPosition(this IQueryable<EventRow> events, Direction direction, long from, long head) =>
        direction == Direction.Forwards
            ? events.Where(e => e.SeqId >= from && e.SeqId <= head).OrderBy(e => e.SeqId)
            : events.Where(e => e.SeqId <= from).OrderByDescending(e => e.SeqId);

    /// <summary>
    /// The events of one virtual stream: a seek on the category, the type or the correlation
    /// index; the first event of each stream, a seek on the stream-starts index or, by category,
    /// on the category-starts one, which hold one row a stream; or, for the data events, the type
    /// checked on the rows a read of <c>$all</c> reads anyway, at the cost of <c>$all</c>, a
    /// prefix with no wildcard in it, so the database compares the first character.
    /// </summary>
    public static IQueryable<EventRow> FilterByKey(this IQueryable<EventRow> events, VirtualStreamName stream)
    {
        var key = stream.Key;
        return stream.Kind switch
        {
            VirtualStreamKind.Category => events.Where(e => e.Category == key),
            VirtualStreamKind.EventType => events.Where(e => e.Type == key),
            VirtualStreamKind.Correlation => events.Where(e => e.CorrelationId == key),
            VirtualStreamKind.Streams => events.Where(e => e.Version == 1),
            VirtualStreamKind.CategoryStreams => events.Where(e => e.Version == 1 && e.Category == key),
            _ => events.Where(e => !e.Type.StartsWith('$')),
        };
    }
}
