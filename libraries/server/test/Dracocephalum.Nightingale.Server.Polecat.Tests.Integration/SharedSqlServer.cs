namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The collection every test that needs the database joins, so one database serves the project.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SharedSqlServer : ICollectionFixture<SqlServerTestDatabase>
{
    /// <summary>The collection name.</summary>
    public const string Name = "sql-server";
}
