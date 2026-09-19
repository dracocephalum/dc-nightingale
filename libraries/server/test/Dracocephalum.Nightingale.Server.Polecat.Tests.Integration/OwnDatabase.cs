namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The collection for tests that each create and drop a database of their own. A collection runs
/// its tests one at a time, which keeps the number of databases being created at once to one.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OwnDatabase
{
    /// <summary>The collection name.</summary>
    public const string Name = "own-database";
}
