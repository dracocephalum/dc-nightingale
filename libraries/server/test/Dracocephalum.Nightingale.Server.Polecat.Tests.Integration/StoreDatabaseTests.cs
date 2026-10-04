using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The database itself, against a real server and with no store in it: what the initializer is
/// told when it asks whether the database is there, what collation it has and whether it holds
/// anything, and that a database is created with the collation asked for. The initializer's
/// decisions from those answers are tested without a database.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class StoreDatabaseTests : IAsyncLifetime
{
    private const int Utf8 = 65001;
    private readonly string _name = TestDatabases.NewName();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await TestDatabases.DropAsync(_name);

    [Fact]
    public async Task Create_WithACollation_ShouldMakeAnEmptyDatabaseThatHasIt()
    {
        // Arrange
        const string collation = "Latin1_General_100_CI_AS_SC_UTF8";
        var database = new StoreDatabase(TestDatabases.ConnectionStringFor(_name));
        var before = await database.ExistsAsync(TestContext.Current.CancellationToken);

        // Act
        await database.CreateAsync(collation, TestContext.Current.CancellationToken);

        // Assert
        database.Name.ShouldBe(_name);
        before.ShouldBeFalse();
        (await database.ExistsAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await database.ReadCollationAsync(TestContext.Current.CancellationToken)).ShouldBe((collation, Utf8));
        (await database.CountTablesAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_WithoutACollation_ShouldTakeTheServersDefaultAndCountTheTablesItIsGiven()
    {
        // Arrange
        var database = new StoreDatabase(TestDatabases.ConnectionStringFor(_name));
        var serverDefault = await TestDatabases.ScalarAsync<string>("master", "SELECT CONVERT(varchar(128), SERVERPROPERTY('Collation'))");

        // Act
        await database.CreateAsync(collation: null, TestContext.Current.CancellationToken);
        await TestDatabases.ExecuteAsync(_name, "CREATE TABLE dbo.somebody_elses (id int)");

        // Assert
        var (collation, codePage) = await database.ReadCollationAsync(TestContext.Current.CancellationToken);
        collation.ShouldBe(serverDefault);
        codePage.ShouldBe(await database.ReadCodePageAsync(serverDefault, TestContext.Current.CancellationToken));
        (await database.CountTablesAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Theory]
    [InlineData(NightingaleOptions.StoreOptions.DefaultCollation, Utf8)]
    [InlineData("SQL_Latin1_General_CP1_CI_AS", 1252)]
    [InlineData("No_Such_Collation", null)]
    public async Task ReadCodePage_ShouldSayWhichCodePageACollationHasOrThatTheServerKnowsNone(string collation, int? expected)
    {
        // Arrange: nothing is created; the catalog is the server's.
        var database = new StoreDatabase(TestDatabases.ConnectionStringFor(_name));

        // Act
        var codePage = await database.ReadCodePageAsync(collation, TestContext.Current.CancellationToken);

        // Assert
        codePage.ShouldBe(expected);
    }
}
