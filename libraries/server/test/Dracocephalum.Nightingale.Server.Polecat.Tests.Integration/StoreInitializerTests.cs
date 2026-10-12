using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The server owns its database as far as its mode allows: in Initialize mode it creates or
/// initializes an empty one, finishes one left half-way, and records the settings that shaped it;
/// it refuses a database that is not its own or whose record disagrees with the configuration,
/// and in the default mode it changes nothing and refuses whatever is not ready. Hosts here start
/// in Initialize mode unless a test says otherwise. The decisions themselves are
/// tested without a database, in the unit tests of the initializer; what is here is what only a
/// real database shows, and a test whose subject is not the first initialization makes its store
/// from the creation script instead. What a start does to an initialized store's schema is
/// <c>SchemaApplyTests</c>', the collation <c>StoreCollationTests</c>' and the partitioning
/// <c>StorePartitioningTests</c>'. Every test here uses a database of its own, dropped at the end
/// whatever happened.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StoreInitializerTests : IAsyncLifetime
{
    /// <summary>One row per value of what a store is initialized with: four settings, who and when, the store library and the hash of its creation script.</summary>
    private const int SettingRowCount = 8;

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":1}");
    private readonly string _name = TestDatabases.NewName();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await TestDatabases.DropAsync(_name);

    [Fact]
    public async Task Start_WhenDatabaseIsMissing_ShouldCreateItInitializeItLogWhatItAppliedAndServeItAgain()
    {
        // Arrange & Act
        List<FakeLogRecord> firstApplied;
        using (var first = await TestDatabases.StartHostAsync(_name))
        {
            firstApplied = StoreSchemaEntries(first);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        using var second = await TestDatabases.StartHostAsync(_name);

        // Assert: one row per setting, recording what shaped the store and the database's actual
        // collation, and the gateway's migrations recorded in its own schema. The statements that
        // made the store's tables are in the first start's log, one entry per table, at the level
        // the migrations are logged at, and the second start, which applied nothing, logged none.
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.StoreProperty")).ShouldBe(SettingRowCount);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Collation'")).ShouldNotBeNullOrWhiteSpace();
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Partitioning'")).ShouldBe("None");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.__EFMigrationsHistory")).ShouldBeGreaterThan(0);
        firstApplied.ShouldNotBeEmpty();
        firstApplied.ShouldAllBe(entry => entry.Level == LogLevel.Information);
        firstApplied.ShouldAllBe(entry => entry.Message.Contains($"database {_name}", StringComparison.Ordinal));
        firstApplied.ShouldContain(entry => entry.Message.Contains("CREATE TABLE dbo.pc_events", StringComparison.Ordinal));
        firstApplied.ShouldContain(entry => entry.Message.Contains("Applied to the event store's tables", StringComparison.Ordinal) && entry.Message.Contains("pc_events", StringComparison.Ordinal));
        firstApplied.ShouldContain(entry => entry.Message.Contains("Applying to the gateway's tables", StringComparison.Ordinal) && entry.Message.Contains("__EFMigrationsHistory", StringComparison.Ordinal));
        firstApplied.ShouldContain(entry => entry.Message.Contains("Applied to the gateway's tables", StringComparison.Ordinal));
        StoreSchemaEntries(second).ShouldBeEmpty();
        await second.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WhenAnEarlierStartDiedBetweenTheStoresTablesAndTheGatewaysMigrations_ShouldInitializeTheRestOfTheWay()
    {
        // Arrange: the store's tables from the creation script and nothing of the gateway's, as
        // such a start leaves the database.
        await TestDatabases.CreateEmptyAsync(_name);
        await TestDatabases.CreateStoreTablesAsync(_name);

        // Act
        using var host = await TestDatabases.StartHostAsync(_name);

        // Assert: served, with the gateway's half and the record of the store complete, and the
        // start said what it found.
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.StoreProperty")).ShouldBe(SettingRowCount);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.__EFMigrationsHistory")).ShouldBeGreaterThan(0);
        var appended = await host.Store().AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        appended.Revision.ShouldBe(0);
        host.Logged().ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("initializing it the rest of the way", StringComparison.Ordinal));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WhenAnEarlierStartDiedBeforeTheRecordOfTheStore_ShouldRefuseToVerifyAndFinishWhenInitializing()
    {
        // Arrange: both sets of tables, the migrations' history with its rows, and no record.
        await TestDatabases.LeaveInitializationUnfinishedAsync(_name);

        // Act
        var verifying = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, options => options.SchemaMode = SchemaMode.Verify));
        using var host = await TestDatabases.StartHostAsync(_name);

        // Assert: the whole record is there, the store serves, and the start said what it found.
        verifying.Message.ShouldContain("did not finish");
        verifying.Message.ShouldContain("--initialize");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.StoreProperty")).ShouldBe(SettingRowCount);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.Tenant")).ShouldBe(1);
        var appended = await host.Store().AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        appended.Revision.ShouldBe(0);
        host.Logged().ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("initializing it the rest of the way", StringComparison.Ordinal));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WhenTwoInstancesInitializeTheSameMissingDatabaseAtOnce_ShouldBothServeItAndMakeItOnce()
    {
        // Act: two hosts, neither knowing of the other, against a database that does not exist.
        var hosts = await Task.WhenAll(TestDatabases.StartHostAsync(_name), TestDatabases.StartHostAsync(_name));

        // Assert: one made the store's tables under the lock; the other found them made, whichever
        // of the two created the database. Both serve.
        try
        {
            (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.StoreProperty")).ShouldBe(SettingRowCount);
            hosts.Count(host => StoreSchemaEntries(host).Count > 0).ShouldBe(1);
            foreach (var host in hosts)
            {
                var appended = await host.Store().AppendAsync("orders-" + Guid.NewGuid().ToString("N"), StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
                appended.Revision.ShouldBe(0);
                await host.StopAsync(TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            foreach (var host in hosts)
            {
                host.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(SchemaMode.Verify)]
    [InlineData(SchemaMode.Migrate)]
    public async Task Start_WhenDatabaseIsMissingAndTheModeDoesNotInitialize_ShouldRefuse(SchemaMode mode)
    {
        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(
            () => TestDatabases.StartHostAsync(_name, options => options.SchemaMode = mode));

        // Assert
        exception.Message.ShouldContain($"Nightingale:SchemaMode is {mode}");
        exception.Message.ShouldContain("--initialize");
        (await TestDatabases.ScalarAsync<object>("master", $"SELECT ISNULL(DB_ID('{_name}'), -1)")).ShouldBe(-1);
    }

    [Fact]
    public async Task Start_WhenAProvisionedDatabaseIsEmptyAndTheModeDoesNotInitialize_ShouldRefuseAndLeaveItEmpty()
    {
        // Arrange
        await TestDatabases.CreateEmptyAsync(_name);

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(
            () => TestDatabases.StartHostAsync(_name, options => options.SchemaMode = SchemaMode.Migrate));

        // Assert
        exception.Message.ShouldContain("has not been initialized by Nightingale");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0")).ShouldBe(0);
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
        exception.Message.ShouldContain("dbo.somebody_elses are not tables this server creates");
    }

    private static List<FakeLogRecord> StoreSchemaEntries(IHost host) =>
        host.Logged().Where(entry => string.Equals(entry.Category, typeof(StoreSchema).FullName, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task Start_WhenSettingsDifferFromWhatTheStoreWasInitializedWith_ShouldRefuse()
    {
        // Arrange
        await TestDatabases.ProvisionAsync(_name);

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(
            () => TestDatabases.StartHostAsync(_name, options => options.Store.Partitioning = PartitioningMode.Tenant));

        // Assert
        exception.Message.ShouldContain("Partitioning is Tenant but the store was initialized with None");
    }

    [Fact]
    public async Task Start_WithSchemas_ShouldPutEachSetOfTablesInItsOwnServeThemAgainAndRefuseOthers()
    {
        // Arrange & Act: the event store's tables land in the schema named for them and the
        // gateway's in the one named for those; a second start finds both, and a group is created
        // and read through the gateway's schema.
        static void Schemas(NightingaleOptions options)
        {
            options.Store.Schema = "events";
            options.Schema = "gateway";
        }

        using (var first = await TestDatabases.StartHostAsync(_name, Schemas))
        {
            var appended = await first.Store().AppendAsync("orders-1", StreamState.NoStream, [new EventData(Guid.NewGuid(), "order_placed", Body)], TestContext.Current.CancellationToken);
            appended.Position.ShouldBe(1);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        using (var second = await TestDatabases.StartHostAsync(_name, Schemas))
        {
            var groups = second.Services.GetRequiredService<ISubscriptionGroupStore>();
            await groups.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1), TestContext.Current.CancellationToken);
            (await groups.GetAsync("orders-1", "billing", TestContext.Current.CancellationToken)).ShouldNotBeNull();
            await second.StopAsync(TestContext.Current.CancellationToken);
        }

        // A host that names neither schema finds tables and none of its migrations; one that names
        // the gateway's schema finds the store, and that it was initialized in another schema.
        var notOurs = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name));
        var otherStoreSchema = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, options => options.Schema = "gateway"));

        // Assert
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'events' AND name = 'pc_events'")).ShouldBe(1);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'gateway' AND name IN ('StoreProperty', 'SubscriptionGroup', 'SubscriptionParkedEvent', 'SubscriptionOutboxEntry', 'Lease', 'SequencerProgress', '__EFMigrationsHistory')")).ShouldBe(7);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) IN ('dbo', 'nightingale')")).ShouldBe(0);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM gateway.SubscriptionGroup")).ShouldBe(1);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM gateway.StoreProperty WHERE [Name] = 'Schema'")).ShouldBe("events");
        notOurs.Message.ShouldContain("not initialized by Nightingale");
        otherStoreSchema.Message.ShouldContain("Schema is dbo but the store was initialized in events");
    }

    [Fact]
    public async Task Start_WhenTheStoreIsNewerThanTheServer_ShouldRefuse()
    {
        // Arrange: a store a newer server has migrated carries a migration this one does not have.
        await TestDatabases.ProvisionAsync(_name);

        await TestDatabases.ExecuteAsync(_name, "INSERT INTO nightingale.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29990101000000_FromTheFuture', '99.0.0')");

        // Act
        var refused = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, options => options.SchemaMode = SchemaMode.Verify));
        var evenWhenAllowedToChange = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name));

        // Assert
        refused.Message.ShouldContain("newer than this server");
        refused.Message.ShouldContain("29990101000000_FromTheFuture");
        evenWhenAllowedToChange.Message.ShouldContain("newer than this server");
    }

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Body);
}
