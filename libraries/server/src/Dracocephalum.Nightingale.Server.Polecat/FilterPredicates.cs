using System.Linq.Expressions;

using Dracocephalum.Nightingale.Server.Polecat.Data;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// A prefix or an exact filter as the database evaluates it: one predicate over the events table,
/// built so that the indexes answer it. A stream-name prefix that is a whole category,
/// <c>orders-</c>, is the category column's equality, which the category index seeks, less the
/// stream named just <c>orders</c>: that stream is in the category, its name being its category,
/// and its name does not begin with the prefix. Any other prefix is a <c>LIKE</c> the provider
/// writes and escapes. Several prefixes are one <c>OR</c>. Exact values are one <c>IN</c> over the
/// category column or the type column, nothing taken away. A regular expression is not the
/// database's: the reader matches it in process.
/// </summary>
internal static class FilterPredicates
{
    /// <summary>The predicate a prefix or an exact filter is, over the rows of the events table.</summary>
    /// <param name="filter">The filter; a prefix or an exact one.</param>
    /// <returns>The predicate.</returns>
    /// <exception cref="ArgumentException">The filter is a regular expression.</exception>
    public static Expression<Func<EventRow, bool>> ToPredicate(EventFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Regex is not null)
        {
            throw new ArgumentException("A regular expression is matched in process, not by the database.", nameof(filter));
        }

        if (filter.Exact.Count > 0)
        {
            var values = filter.Exact.ToArray();
            return filter.Target == EventFilterTarget.StreamName
                ? e => values.Contains(e.Category)
                : e => values.Contains(e.Type);
        }

        var row = Expression.Parameter(typeof(EventRow), "e");
        Expression? any = null;
        foreach (var prefix in filter.Prefixes)
        {
            var one = filter.Target == EventFilterTarget.StreamName ? StreamNameBeginsWith(row, prefix) : BeginsWith(Expression.Property(row, nameof(EventRow.Type)), prefix);
            any = any is null ? one : Expression.OrElse(any, one);
        }

        return Expression.Lambda<Func<EventRow, bool>>(any!, row);
    }

    /// <summary>Whether a stream-name prefix is a whole category followed by the hyphen, and which.</summary>
    /// <param name="prefix">The prefix.</param>
    /// <param name="category">The category, when it is one.</param>
    /// <returns>True when the prefix is a category and its hyphen.</returns>
    public static bool TryAsCategory(string prefix, out string category)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Length > 1 && prefix[^1] == '-' && prefix.IndexOf('-', StringComparison.Ordinal) == prefix.Length - 1)
        {
            category = prefix[..^1];
            return true;
        }

        category = string.Empty;
        return false;
    }

    private static Expression StreamNameBeginsWith(ParameterExpression row, string prefix)
    {
        var name = Expression.Property(row, nameof(EventRow.StreamId));
        if (!TryAsCategory(prefix, out var category))
        {
            return BeginsWith(name, prefix);
        }

        // The category's rows, less the one stream named just the category: its name is its
        // category, and it does not begin with the prefix. The only stream of the category that
        // does not, since every other one has the category and a hyphen at its start. The
        // residual costs nothing, since the row is fetched for its body anyway.
        var categoryIs = Expression.Equal(Expression.Property(row, nameof(EventRow.Category)), Expression.Constant(category));
        var notTheBareName = Expression.NotEqual(name, Expression.Constant(category));
        return Expression.AndAlso(categoryIs, notTheBareName);
    }

    /// <summary>The column begins with the prefix: a constant the provider turns into an escaped <c>LIKE</c>, and the in-memory provider evaluates as is.</summary>
    private static MethodCallExpression BeginsWith(Expression column, string prefix) =>
        Expression.Call(column, typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!, Expression.Constant(prefix));
}
