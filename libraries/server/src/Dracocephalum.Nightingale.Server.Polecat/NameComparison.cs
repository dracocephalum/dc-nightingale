using System.Globalization;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// How the database compares names, as near as the process can: the comparer the router matches
/// stream names, categories and event types with, so that a name matches live as it matches on a
/// read. Under a binary collation, the default, the database compares code points and so does
/// <see cref="StringComparer.Ordinal"/>: exact. Under a linguistic collation the comparer is built
/// from what the collation says it ignores, case, accents, kana, width, and its locale, which is
/// the same flags on a different engine: SQL Server's weights and .NET's are not the same in every
/// corner, so a name could match on one and not the other there, which <c>VARIANCES.md</c> records.
/// Ordinal until the initializer has read the database's collation, which it does before anything
/// is served. Matching by the database itself, every followed name handed to one query, stays the
/// exact alternative should that corner matter; <c>DESIGN.md</c> keeps its shape.
/// </summary>
public sealed class NameComparison
{
    private const int IgnoreCase = 1;
    private const int IgnoreAccent = 2;
    private const int IgnoreKanaType = 65536;
    private const int IgnoreWidth = 131072;

    private IEqualityComparer<string> _comparer = StringComparer.Ordinal;

    /// <summary>Gets the comparer.</summary>
    public IEqualityComparer<string> Comparer => Volatile.Read(ref _comparer);

    /// <summary>Takes the database's collation as the one names are compared under.</summary>
    /// <param name="collation">The collation, as the database describes it.</param>
    public void Adopt(DatabaseCollation collation)
    {
        ArgumentNullException.ThrowIfNull(collation);
        Volatile.Write(ref _comparer, ForCollation(collation));
    }

    /// <summary>The comparer nearest to a collation: ordinal for a binary one, the collation's own flags and locale otherwise.</summary>
    /// <param name="collation">The collation.</param>
    /// <returns>The comparer.</returns>
    public static IEqualityComparer<string> ForCollation(DatabaseCollation collation)
    {
        ArgumentNullException.ThrowIfNull(collation);
        if (IsBinary(collation.Name) || collation.ComparisonStyle is not { } style)
        {
            return StringComparer.Ordinal;
        }

        var options = CompareOptions.None;
        if ((style & IgnoreCase) != 0)
        {
            options |= CompareOptions.IgnoreCase;
        }

        if ((style & IgnoreAccent) != 0)
        {
            options |= CompareOptions.IgnoreNonSpace;
        }

        if ((style & IgnoreKanaType) != 0)
        {
            options |= CompareOptions.IgnoreKanaType;
        }

        if ((style & IgnoreWidth) != 0)
        {
            options |= CompareOptions.IgnoreWidth;
        }

        return StringComparer.Create(GetCulture(collation.Lcid), options);
    }

    /// <summary>Whether a collation compares code points: its name carries a BIN or BIN2 part.</summary>
    private static bool IsBinary(string name) =>
        name.Split('_').Any(part => part.Equals("BIN", StringComparison.OrdinalIgnoreCase) || part.Equals("BIN2", StringComparison.OrdinalIgnoreCase));

    private static CultureInfo GetCulture(int? lcid)
    {
        if (lcid is null)
        {
            return CultureInfo.InvariantCulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(lcid.Value);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
