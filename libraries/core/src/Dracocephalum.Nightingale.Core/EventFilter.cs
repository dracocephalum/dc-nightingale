using System.Text.RegularExpressions;

namespace Dracocephalum.Nightingale;

/// <summary>
/// What a read or subscription of <c>$all</c> keeps: the events whose stream name, or whose event
/// type, begins with one of some prefixes, or matches a regular expression. Made through
/// <see cref="StreamFilter"/> and <see cref="EventTypeFilter"/>, named as the reference client
/// names them. One target and one kind per filter, as the reference protocol has it; <c>$all</c>
/// only, since a stream and a virtual stream are already a selection. The regular expression runs without backtracking, so
/// a pattern from a client can take only time linear in what it reads; the constructs that need
/// backtracking, lookarounds and backreferences, are refused. Names are matched ordinally, as the
/// server matches them in process: the database compares them under its own collation, which a
/// host may have chosen case-insensitive; see <c>VARIANCES.md</c>.
/// </summary>
public sealed class EventFilter
{
    private readonly Regex? _regex;

    private EventFilter(EventFilterTarget target, IReadOnlyList<string> prefixes, string? regex)
    {
        Target = target;
        Prefixes = prefixes;
        Regex = regex;
        if (regex is not null)
        {
            try
            {
                _regex = new Regex(regex, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"The filter's regular expression is not one the server runs: {exception.Message}", nameof(regex), exception);
            }
            catch (NotSupportedException exception)
            {
                throw new ArgumentException($"The filter's regular expression uses a construct the server does not run, since it matches without backtracking: {exception.Message}", nameof(regex), exception);
            }
        }
    }

    /// <summary>Gets what the filter looks at.</summary>
    public EventFilterTarget Target { get; }

    /// <summary>Gets the prefixes one of which the name must begin with; empty for a regular-expression filter.</summary>
    public IReadOnlyList<string> Prefixes { get; }

    /// <summary>Gets the regular expression the name must match, or <see langword="null"/> for a prefix filter.</summary>
    public string? Regex { get; }

    /// <summary>Keeps the events whose target begins with one of the prefixes.</summary>
    /// <param name="target">What to look at.</param>
    /// <param name="prefixes">The prefixes, at least one, none empty.</param>
    /// <returns>The filter.</returns>
    internal static EventFilter ByPrefixes(EventFilterTarget target, string[] prefixes) => new(target, RequirePrefixes(prefixes), null);

    /// <summary>Keeps the events whose target matches a regular expression.</summary>
    /// <param name="target">What to look at.</param>
    /// <param name="regex">The expression; anchor it to match from the start.</param>
    /// <returns>The filter.</returns>
    internal static EventFilter ByRegularExpression(EventFilterTarget target, string regex) => new(target, [], RequireRegex(regex));

    /// <summary>Whether the filter keeps an event, by its stream name and type.</summary>
    /// <param name="stream">The event's stream name.</param>
    /// <param name="type">The event's type.</param>
    /// <returns>True when the event is kept.</returns>
    public bool Matches(string stream, string type)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(type);
        var name = Target == EventFilterTarget.StreamName ? stream : type;
        if (_regex is not null)
        {
            return _regex.IsMatch(name);
        }

        foreach (var prefix in Prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Regex is not null ? $"{Target} matches /{Regex}/" : $"{Target} begins with {string.Join(" | ", Prefixes)}";

    private static string[] RequirePrefixes(string[] prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        if (prefixes.Length == 0)
        {
            throw new ArgumentException("A prefix filter needs at least one prefix.", nameof(prefixes));
        }

        foreach (var prefix in prefixes)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                throw new ArgumentException("A prefix is never empty.", nameof(prefixes));
            }
        }

        return prefixes.ToArray();
    }

    private static string RequireRegex(string regex)
    {
        ArgumentException.ThrowIfNullOrEmpty(regex);
        return regex;
    }
}
