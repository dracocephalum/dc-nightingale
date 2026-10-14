namespace Dracocephalum.Nightingale;

/// <summary>
/// Filters on the name of an event's stream, for a read or subscription of <c>$all</c>; the prefix
/// and the regular expression named as the reference client names them, so a consumer reads the
/// same either way. A category and its hyphen, <c>orders-</c>, is the common prefix, and the
/// server answers it through its category index; the stream named just <c>orders</c> does not
/// begin with the prefix and is not kept. <see cref="Category"/> keeps whole categories, that
/// stream included, through the same index.
/// </summary>
public static class StreamFilter
{
    /// <summary>
    /// Keeps the events of the streams in one of the categories, by <see cref="StreamNames.CategoryOf"/>:
    /// <c>orders</c> keeps <c>orders-1</c> and the stream named just <c>orders</c>, as
    /// <c>$ce-orders</c> does.
    /// </summary>
    /// <param name="categories">The categories, at least one, none empty.</param>
    /// <returns>The filter.</returns>
    public static EventFilter Category(params string[] categories) => EventFilter.ByExactValues(EventFilterTarget.StreamName, categories);

    /// <summary>Keeps the events whose stream name begins with one of the prefixes.</summary>
    /// <param name="prefixes">The prefixes, at least one, none empty.</param>
    /// <returns>The filter.</returns>
    public static EventFilter Prefix(params string[] prefixes) => EventFilter.ByPrefixes(EventFilterTarget.StreamName, prefixes);

    /// <summary>Keeps the events whose stream name matches a regular expression, which runs without backtracking.</summary>
    /// <param name="regex">The expression; anchor it to match from the start.</param>
    /// <returns>The filter.</returns>
    public static EventFilter RegularExpression(string regex) => EventFilter.ByRegularExpression(EventFilterTarget.StreamName, regex);
}
