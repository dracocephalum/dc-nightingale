namespace Dracocephalum.Nightingale.Examples.Polecat.Tests;

/// <summary>
/// The collection for the scenario tests, each of which creates a database of its own and starts
/// a server on it. A collection runs its tests one at a time, which keeps the number of
/// databases being created at once to one.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OwnDatabase
{
    /// <summary>The collection name.</summary>
    public const string Name = "own-database";
}
