using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The server owns its database: it creates or initializes an empty one, records the settings that
/// shaped it, refuses a database that is not its own or whose record disagrees with the
/// configuration, and never migrates while serving unless told to. Every test here uses a
/// database of its own, dropped at the end whatever happened, and they run one at a time: each
/// start runs the store's catalog query, whose memory grant a small SQL Server hands out one at a
/// time.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class StoreInitializerTests : IAsyncLifetime
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":1}");
    private readonly string _name = TestDatabases.NewName();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await TestDatabases.DropAsync(_name);

    [Fact]
    public async Task Start_WhenDatabaseIsMissing_ShouldCreateItInitializeItAndServeItAgain()
    {
        // Arrange & Act
        using (var first = await TestDatabases.StartHostAsync(_name))
        {
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        using var second = await TestDatabases.StartHostAsync(_name);

        // Assert: one marker row, recording the settings and the database's actual collation.
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM dbo.nightingale_store")).ShouldBe(1);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT collation FROM dbo.nightingale_store")).ShouldNotBeNullOrWhiteSpace();
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT partitioning FROM dbo.nightingale_store")).ShouldBe("None");
        await second.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WhenDatabaseIsMissingAndCreationIsOff_ShouldRefuse()
    {
        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(
            () => TestDatabases.StartHostAsync(_name, options => options.CreateDatabase = false));

        // Assert
        exception.Message.ShouldContain("CreateDatabase");
    }

    [Fact]
    public async Task Start_WhenDatabaseWasProvisionedEmptyByHand_ShouldInitializeIt()
    {
        // Arrange: a database administrator created the database; the login may not create one.
        await TestDatabases.CreateEmptyAsync(_name);

        // Act
        using var host = await TestDatabases.StartHostAsync(_name, options => options.CreateDatabase = false);

        // Assert
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM dbo.nightingale_store")).ShouldBe(1);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WhenDatabaseHoldsForeignTables_ShouldRefuse()
    {
        // Arrange
        await TestDatabases.CreateEmptyAsync(_name);
        await TestDatabases.ExecuteAsync(_name, "CREATE TABLE dbo.somebody_elses (id int)");

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name));

        // Assert
        exception.Message.ShouldContain("not initialized by Nightingale");
    }

    [Fact]
    public async Task Start_WhenSettingsDifferFromTheMarker_ShouldRefuse()
    {
        // Arrange
        using (var first = await TestDatabases.StartHostAsync(_name))
        {
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(
            () => TestDatabases.StartHostAsync(_name, options => options.Store.Partitioning = NightingaleOptions.StoreSettings.PartitioningMode.Tenant));

        // Assert
        exception.Message.ShouldContain("Partitioning is Tenant but the store was initialized with None");
    }

    [Fact]
    public async Task Start_WhenSchemaDrifted_ShouldRefuseUnlessToldToApplyChanges()
    {
        // Arrange: something outside the server dropped one of the gateway's indexes.
        using (var first = await TestDatabases.StartHostAsync(_name))
        {
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        await TestDatabases.ExecuteAsync(_name, "DROP INDEX ix_pc_events_category_seq ON dbo.pc_events");

        // Act
        var refused = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name));
        using var repaired = await TestDatabases.StartHostAsync(_name, options => options.ApplySchemaChanges = true);

        // Assert
        refused.Message.ShouldContain("ApplySchemaChanges");
        refused.Message.ShouldContain("ix_pc_events_category_seq");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'ix_pc_events_category_seq'")).ShouldBe(1);
        await repaired.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WithASchema_ShouldPutEveryTableInItServeItAgainAndRefuseAnotherSchema()
    {
        // Arrange & Act: the store's tables and the gateway's alike land in the configured schema,
        // a second start finds the store there, and a start configured for another schema does not.
        using (var first = await TestDatabases.StartHostAsync(_name, options => options.Store.Schema = "events"))
        {
            var appended = await first.Store().AppendAsync("orders-1", StreamState.NoStream, [new EventData(Guid.NewGuid(), "order_placed", Body)], TestContext.Current.CancellationToken);
            appended.Position.ShouldBe(1);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        using (var second = await TestDatabases.StartHostAsync(_name, options => options.Store.Schema = "events"))
        {
            await second.StopAsync(TestContext.Current.CancellationToken);
        }

        var refused = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name));

        // Assert
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'events' AND name IN ('pc_events', 'nightingale_store', 'nightingale_groups')")).ShouldBe(3);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'dbo'")).ShouldBe(0);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT schema_name FROM events.nightingale_store")).ShouldBe("events");
        refused.Message.ShouldContain("not initialized by Nightingale");
    }

    [Fact]
    public async Task Start_WithACollation_ShouldCreateTheDatabaseWithItAndMakeStreamNamesCaseSensitive()
    {
        // Arrange
        const string collation = "Latin1_General_100_BIN2";

        // Act
        using var host = await TestDatabases.StartHostAsync(_name, options => options.Store.Collation = collation);
        var store = host.Store();
        await store.AppendAsync("Orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var lower = await store.AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Assert: two streams, and the record says which collation the database has.
        lower.Revision.ShouldBe(0);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT collation FROM dbo.nightingale_store")).ShouldBe(collation);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM dbo.pc_streams")).ShouldBe(2);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WithAnUnknownCollation_ShouldRefuseBeforeCreatingAnything()
    {
        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(
            () => TestDatabases.StartHostAsync(_name, options => options.Store.Collation = "No_Such_Collation"));

        // Assert
        exception.Message.ShouldContain("fn_helpcollations");
        (await TestDatabases.ScalarAsync<object>("master", $"SELECT ISNULL(DB_ID('{_name}'), -1)")).ShouldBe(-1);
    }

    [Fact]
    public async Task Start_WithTenantPartitioning_ShouldAppendReadAndKeepTheSchemaStable()
    {
        // Arrange
        using var host = await TestDatabases.StartHostAsync(_name, options => options.Store.Partitioning = NightingaleOptions.StoreSettings.PartitioningMode.Tenant);
        var store = host.Store();

        // Act: the first append provisions the default tenant's partition and sequence.
        var appended = await store.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        var slice = await store.ReadAsync("orders-1", Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        appended.Revision.ShouldBe(1);
        slice.ShouldNotBeNull().Events.Count.ShouldBe(2);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT partitioning FROM dbo.nightingale_store")).ShouldBe("Tenant");
        await AssertNoSchemaDelta(host);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WithArchivedStreamPartitioning_ShouldAppendReadAndKeepTheSchemaStable()
    {
        // Arrange
        using var host = await TestDatabases.StartHostAsync(_name, options => options.Store.Partitioning = NightingaleOptions.StoreSettings.PartitioningMode.ArchivedStream);
        var store = host.Store();

        // Act
        var appended = await store.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        var slice = await store.ReadAsync("orders-1", Direction.Backwards, null, 1, TestContext.Current.CancellationToken);

        // Assert
        appended.Revision.ShouldBe(1);
        slice.ShouldNotBeNull().Events.ShouldHaveSingleItem().Revision.ShouldBe(1);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT partitioning FROM dbo.nightingale_store")).ShouldBe("ArchivedStream");
        await AssertNoSchemaDelta(host);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AssertNoSchemaDelta(Microsoft.Extensions.Hosting.IHost host)
    {
        var store = host.Services.GetRequiredService<global::Polecat.IDocumentStore>();
        var databases = await store.Options.Tenancy!.BuildDatabasesAsync(TestContext.Current.CancellationToken);
        await Should.NotThrowAsync(() => databases[0].AssertDatabaseMatchesConfigurationAsync(TestContext.Current.CancellationToken));
    }

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Body);
}
