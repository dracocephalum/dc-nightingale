namespace Dracocephalum.Nightingale;

/// <summary>
/// Filters on an event's type, for a read or subscription of <c>$all</c>; named as the reference
/// client names them, so a consumer reads the same either way. The reference's exclusion of
/// system events has no counterpart here: <c>$all</c> holds application events only.
/// </summary>
public static class EventTypeFilter
{
    /// <summary>Keeps the events whose type begins with one of the prefixes.</summary>
    /// <param name="prefixes">The prefixes, at least one, none empty.</param>
    /// <returns>The filter.</returns>
    public static EventFilter Prefix(params string[] prefixes) => EventFilter.ByPrefixes(EventFilterTarget.EventType, prefixes);

    /// <summary>Keeps the events whose type matches a regular expression, which runs without backtracking.</summary>
    /// <param name="regex">The expression; anchor it to match from the start.</param>
    /// <returns>The filter.</returns>
    public static EventFilter RegularExpression(string regex) => EventFilter.ByRegularExpression(EventFilterTarget.EventType, regex);
}
