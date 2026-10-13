using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// A group's filter as its column: one JSON object naming the target and either the prefixes or
/// the regular expression, so the row says what the group keeps in one value that reads back
/// into the same filter, and a group without one has no value.
/// </summary>
public static class FilterColumn
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The column's value for a filter.</summary>
    /// <param name="filter">The filter, or <see langword="null"/> for everything.</param>
    /// <returns>The JSON, or <see langword="null"/>.</returns>
    public static string? ToColumn(EventFilter? filter) =>
        filter is null ? null : JsonSerializer.Serialize(new Stored(filter.Target, filter.Regex is null ? [.. filter.Prefixes] : null, filter.Regex), Options);

    /// <summary>The filter a column's value is.</summary>
    /// <param name="column">The JSON, or <see langword="null"/>.</param>
    /// <returns>The filter, or <see langword="null"/> for everything.</returns>
    /// <exception cref="InvalidOperationException">The value is not one this writes.</exception>
    public static EventFilter? FromColumn(string? column)
    {
        if (column is null)
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<Stored>(column, Options) ?? throw new InvalidOperationException("A group's filter column holds no filter.");
        return (stored.Target, stored.Regex) switch
        {
            (EventFilterTarget.StreamName, null) => StreamFilter.Prefix(stored.Prefixes ?? []),
            (EventFilterTarget.StreamName, var regex) => StreamFilter.RegularExpression(regex),
            (EventFilterTarget.EventType, null) => EventTypeFilter.Prefix(stored.Prefixes ?? []),
            (EventFilterTarget.EventType, var regex) => EventTypeFilter.RegularExpression(regex),
            _ => throw new InvalidOperationException($"A group's filter column names a target this server does not know: {stored.Target}."),
        };
    }

    private sealed record Stored([property: JsonConverter(typeof(JsonStringEnumConverter))] EventFilterTarget Target, string[]? Prefixes, string? Regex);
}
