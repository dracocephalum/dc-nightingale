namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>
/// A database's collation as SQL Server describes it: its name, its code page, and what its
/// comparison ignores, from which the server builds the comparer it matches names with in process.
/// </summary>
/// <param name="Name">The collation's name.</param>
/// <param name="CodePage">Its code page, or <see langword="null"/> when the server reports none.</param>
/// <param name="ComparisonStyle">Its comparison style: a mask of what it ignores, 0 for one that ignores nothing; <see langword="null"/> when the server reports none.</param>
/// <param name="Lcid">Its locale, or <see langword="null"/> when the server reports none.</param>
public sealed record DatabaseCollation(string Name, int? CodePage, int? ComparisonStyle, int? Lcid);
