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
        var described = await database.ReadCollationAsync(TestContext.Current.CancellationToken);
        described.Name.ShouldBe(collation);
        described.CodePage.ShouldBe(Utf8);
        described.ComparisonStyle.ShouldBe(1 + 65536 + 131072, "ignores case, and kana and width too, which a name that says nothing of them ignores");
        described.Lcid.ShouldBe(1033);
        (await database.ListTablesAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_WithoutACollation_ShouldTakeTheServersDefaultAndListTheTablesItIsGivenSayingWhichHoldRows()
    {
        // Arrange
        var database = new StoreDatabase(TestDatabases.ConnectionStringFor(_name));
        var serverDefault = await TestDatabases.ScalarAsync<string>("master", "SELECT CONVERT(varchar(128), SERVERPROPERTY('Collation'))");

        // Act: a table with a row, an empty one in a schema of its own, and one whose name needs quoting.
        await database.CreateAsync(collation: null, TestContext.Current.CancellationToken);
        await TestDatabases.ExecuteAsync(_name, "CREATE SCHEMA theirs");
        await TestDatabases.ExecuteAsync(_name, "CREATE TABLE dbo.somebody_elses (id int); INSERT INTO dbo.somebody_elses VALUES (1); CREATE TABLE theirs.[odd name] (id int); CREATE TABLE theirs.[it's] (id int);");

        // Assert
        var described = await database.ReadCollationAsync(TestContext.Current.CancellationToken);
        described.Name.ShouldBe(serverDefault);
        described.CodePage.ShouldBe(await database.ReadCodePageAsync(serverDefault, TestContext.Current.CancellationToken));
        (await database.ListTablesAsync(TestContext.Current.CancellationToken)).ShouldBe([
            new DatabaseTable(new("dbo", "somebody_elses"), HasRows: true),
            new DatabaseTable(new("theirs", "it's"), HasRows: false),
            new DatabaseTable(new("theirs", "odd name"), HasRows: false),
        ]);
    }

    [Fact]
    public async Task Create_WhenAnotherInstanceCreatedTheDatabaseInTheMeantime_ShouldTakeItAsMade()
    {
        // Arrange: the database appeared between an instance's look and its create.
        var database = new StoreDatabase(TestDatabases.ConnectionStringFor(_name));
        await TestDatabases.CreateEmptyAsync(_name);

        // Act
        await database.CreateAsync(NightingaleOptions.StoreOptions.DefaultCollation, TestContext.Current.CancellationToken);

        // Assert
        (await database.ExistsAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task LockSchema_ShouldLetLooksShareTheLockAndMakeAChangeWaitForThemAndThemForIt()
    {
        // Arrange
        var database = new StoreDatabase(TestDatabases.ConnectionStringFor(_name));
        await TestDatabases.CreateEmptyAsync(_name);
        var lookOne = await database.LockSchemaAsync(exclusive: false, TestContext.Current.CancellationToken);
        var lookTwo = await database.LockSchemaAsync(exclusive: false, TestContext.Current.CancellationToken);

        // Act: a change asked for while two looks hold the lock waits until both let go; a look
        // asked for while the change holds it waits for the change.
        var change = database.LockSchemaAsync(exclusive: true, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var changeWaitedForLooks = !change.IsCompleted;
        await lookOne.DisposeAsync();
        await lookTwo.DisposeAsync();
        var held = await change.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var lookThree = database.LockSchemaAsync(exclusive: false, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var lookWaitedForChange = !lookThree.IsCompleted;
        await held.DisposeAsync();
        await using var granted = await lookThree.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        changeWaitedForLooks.ShouldBeTrue();
        lookWaitedForChange.ShouldBeTrue();
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
