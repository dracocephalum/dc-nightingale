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
    /// <summary>One row per value of what a store is initialized with: four settings, who and when, and the store library.</summary>
    private const int SettingRowCount = 7;

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

        // Assert: one row per setting, recording what shaped the store and the database's actual
        // collation, and the gateway's migrations recorded in its own schema.
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.Setting")).ShouldBe(SettingRowCount);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.Setting WHERE [Name] = 'Store:Collation'")).ShouldNotBeNullOrWhiteSpace();
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.Setting WHERE [Name] = 'Store:Partitioning'")).ShouldBe("None");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.__EFMigrationsHistory")).ShouldBeGreaterThan(0);
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
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.Setting")).ShouldBe(SettingRowCount);
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
    public async Task Start_WhenSettingsDifferFromWhatTheStoreWasInitializedWith_ShouldRefuse()
    {
        // Arrange
        using (var first = await TestDatabases.StartHostAsync(_name))
        {
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(
            () => TestDatabases.StartHostAsync(_name, options => options.Store.Partitioning = PartitioningMode.Tenant));

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
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'gateway' AND name IN ('Setting', 'SubscriptionGroup', 'SubscriptionParkedEvent', 'SubscriptionOutboxEntry', 'Lease', 'SequencerProgress', '__EFMigrationsHistory')")).ShouldBe(7);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) IN ('dbo', 'nightingale')")).ShouldBe(0);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM gateway.SubscriptionGroup")).ShouldBe(1);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM gateway.Setting WHERE [Name] = 'Store:Schema'")).ShouldBe("events");
        notOurs.Message.ShouldContain("not initialized by Nightingale");
        otherStoreSchema.Message.ShouldContain("Schema is dbo but the store was initialized in events");
    }

    [Fact]
    public async Task Start_WhenTheStoreIsNewerThanTheServer_ShouldRefuse()
    {
        // Arrange: a store a newer server has migrated carries a migration this one does not have.
        using (var first = await TestDatabases.StartHostAsync(_name))
        {
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        await TestDatabases.ExecuteAsync(_name, "INSERT INTO nightingale.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29990101000000_FromTheFuture', '99.0.0')");

        // Act
        var refused = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name));
        var evenWhenToldToApply = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, options => options.ApplySchemaChanges = true));

        // Assert
        refused.Message.ShouldContain("newer than this server");
        refused.Message.ShouldContain("29990101000000_FromTheFuture");
        evenWhenToldToApply.Message.ShouldContain("newer than this server");
    }

    [Fact]
    public async Task SchemaReport_ShouldSayWhatStartupWouldRefuseAndApplyShouldBringItUpToDate()
    {
        // Arrange: an initialized store whose event table has drifted from what the server expects.
        using (var first = await TestDatabases.StartHostAsync(_name))
        {
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        await TestDatabases.ExecuteAsync(_name, "DROP INDEX ix_pc_events_category_seq ON dbo.pc_events");
        var services = new ServiceCollection();
        services.AddNightingalePolecat(TestDatabases.ConnectionStringFor(_name));
        await using var provider = services.BuildServiceProvider();

        // Act: the report and the apply a host calls without starting the server.
        var before = await provider.GetNightingaleSchemaReportAsync(TestContext.Current.CancellationToken);
        await provider.ApplyNightingaleSchemaAsync(TestContext.Current.CancellationToken);
        var after = await provider.GetNightingaleSchemaReportAsync(TestContext.Current.CancellationToken);

        // Assert
        before.IsCurrent.ShouldBeFalse();
        before.Initialized.ShouldBeTrue();
        before.PendingMigrations.ShouldBeEmpty();
        before.StoreChanges.ShouldNotBeNull().ShouldContain("ix_pc_events_category_seq");
        before.Describe().ShouldContain("Changes to the event store's tables to apply");
        after.IsCurrent.ShouldBeTrue();
        after.Describe().ShouldContain("is current");
    }

    [Fact]
    public async Task SchemaReport_WhenTheDatabaseIsMissing_ShouldSaySoAndApplyShouldInitializeIt()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNightingalePolecat(TestDatabases.ConnectionStringFor(_name));
        await using var provider = services.BuildServiceProvider();

        // Act
        var before = await provider.GetNightingaleSchemaReportAsync(TestContext.Current.CancellationToken);
        await provider.ApplyNightingaleSchemaAsync(TestContext.Current.CancellationToken);
        var after = await provider.GetNightingaleSchemaReportAsync(TestContext.Current.CancellationToken);

        // Assert
        before.DatabaseExists.ShouldBeFalse();
        before.IsCurrent.ShouldBeFalse();
        before.PendingMigrations.ShouldNotBeEmpty();
        after.IsCurrent.ShouldBeTrue();
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.Setting")).ShouldBe(SettingRowCount);
    }

    [Fact]
    public async Task Start_ByDefault_ShouldTellStreamNamesApartByCaseAndByScript()
    {
        // Arrange & Act: the default collation is binary and UTF-8.
        using var host = await TestDatabases.StartHostAsync(_name);
        var store = host.Store();
        await store.AppendAsync("Orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var lower = await store.AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await store.AppendAsync("订单-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var otherScript = await store.AppendAsync("账单-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var read = await store.ReadAsync("账单-1", Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert: four streams. Under a collation that is not UTF-8 the last two would be one,
        // both stored as question marks.
        lower.Revision.ShouldBe(0);
        otherScript.Revision.ShouldBe(0);
        read.ShouldNotBeNull().Events.ShouldHaveSingleItem().Stream.ShouldBe("账单-1");
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.Setting WHERE [Name] = 'Store:Collation'")).ShouldBe(NightingaleOptions.StoreOptions.DefaultCollation);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM dbo.pc_streams")).ShouldBe(4);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WithAnotherUtf8Collation_ShouldCreateTheDatabaseWithItAndFollowItsRuleForCase()
    {
        // Arrange: a host may still choose that case does not tell names apart.
        const string collation = "Latin1_General_100_CI_AS_SC_UTF8";

        // Act
        using var host = await TestDatabases.StartHostAsync(_name, options => options.Store.Collation = collation);
        var store = host.Store();
        await store.AppendAsync("Orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var lower = await store.AppendAsync("orders-1", StreamState.Any, [Event()], TestContext.Current.CancellationToken);

        // Assert: one stream, and the record says which collation the database has.
        lower.Revision.ShouldBe(1);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.Setting WHERE [Name] = 'Store:Collation'")).ShouldBe(collation);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM dbo.pc_streams")).ShouldBe(1);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("SQL_Latin1_General_CP1_CI_AS")]
    [InlineData("Latin1_General_100_BIN2")]
    public async Task Start_WithACollationThatIsNotUtf8_ShouldRefuseBeforeCreatingAnything(string collation)
    {
        // Act
        var exception = await Should.ThrowAsync<StoreInitializationException>(
            () => TestDatabases.StartHostAsync(_name, options => options.Store.Collation = collation));

        // Assert
        exception.Message.ShouldContain("is not a UTF-8 collation");
        exception.Message.ShouldContain(NightingaleOptions.StoreOptions.DefaultCollation);
        exception.Message.ShouldContain("Nightingale:Store:IgnoreCollationCompatibility");
        (await TestDatabases.ScalarAsync<object>("master", $"SELECT ISNULL(DB_ID('{_name}'), -1)")).ShouldBe(-1);
    }

    [Fact]
    public async Task Start_WhenTheHostIgnoresCollationCompatibility_ShouldServeADatabaseThatIsNotUtf8()
    {
        // Arrange: the host's own choice, for a collation the server would otherwise refuse.
        const string collation = "SQL_Latin1_General_CP1_CI_AS";
        static void Ignoring(NightingaleOptions options)
        {
            options.Store.Collation = collation;
            options.Store.IgnoreCollationCompatibility = true;
        }

        // Act: created with it, served with it, and a second start finds it as it was left.
        using (var first = await TestDatabases.StartHostAsync(_name, Ignoring))
        {
            var appended = await first.Store().AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
            appended.Revision.ShouldBe(0);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        using (var second = await TestDatabases.StartHostAsync(_name, Ignoring))
        {
            await second.StopAsync(TestContext.Current.CancellationToken);
        }

        var withoutTheChoice = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, options => options.Store.Collation = collation));

        // Assert: the choice is the host's, made each time, and not a property of the store.
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.Setting WHERE [Name] = 'Store:Collation'")).ShouldBe(collation);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.Setting WHERE [Name] LIKE '%IgnoreCollation%'")).ShouldBe(0);
        withoutTheChoice.Message.ShouldContain("which is not a UTF-8 collation");
    }

    [Fact]
    public async Task Start_WhenTheHostIgnoresCollationCompatibility_ShouldInitializeAProvisionedDatabaseThatIsNotUtf8()
    {
        // Arrange: a database created by hand with the server's own default collation.
        await TestDatabases.CreateEmptyAsync(_name, collation: null);

        // Act: no collation configured means whichever the database has.
        using var host = await TestDatabases.StartHostAsync(_name, options =>
        {
            options.Store.Collation = null;
            options.Store.IgnoreCollationCompatibility = true;
        });
        var appended = await host.Store().AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Assert
        appended.Revision.ShouldBe(0);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.Setting WHERE [Name] = 'Store:Collation'")).ShouldNotContain("UTF8");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WhenAProvisionedDatabaseIsNotUtf8_ShouldRefuseAndInitializeNothing()
    {
        // Arrange: a database created by hand with the server's own default collation.
        await TestDatabases.CreateEmptyAsync(_name, collation: null);

        // Act: whatever the host configures, the database's own collation decides.
        var asConfigured = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name));
        var whateverItHas = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, options => options.Store.Collation = null));

        // Assert
        asConfigured.Message.ShouldContain("which is not a UTF-8 collation");
        asConfigured.Message.ShouldContain("Nightingale:Store:IgnoreCollationCompatibility");
        whateverItHas.Message.ShouldContain("which is not a UTF-8 collation");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0")).ShouldBe(0);
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
        using var host = await TestDatabases.StartHostAsync(_name, options => options.Store.Partitioning = PartitioningMode.Tenant);
        var store = host.Store();

        // Act: the first append provisions the default tenant's partition and sequence.
        var appended = await store.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        var slice = await store.ReadAsync("orders-1", Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        appended.Revision.ShouldBe(1);
        slice.ShouldNotBeNull().Events.Count.ShouldBe(2);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.Setting WHERE [Name] = 'Store:Partitioning'")).ShouldBe("Tenant");
        await AssertNoSchemaDelta(host);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WithArchivedStreamPartitioning_ShouldAppendReadAndKeepTheSchemaStable()
    {
        // Arrange
        using var host = await TestDatabases.StartHostAsync(_name, options => options.Store.Partitioning = PartitioningMode.ArchivedStream);
        var store = host.Store();

        // Act
        var appended = await store.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        var slice = await store.ReadAsync("orders-1", Direction.Backwards, null, 1, TestContext.Current.CancellationToken);

        // Assert
        appended.Revision.ShouldBe(1);
        slice.ShouldNotBeNull().Events.ShouldHaveSingleItem().Revision.ShouldBe(1);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.Setting WHERE [Name] = 'Store:Partitioning'")).ShouldBe("ArchivedStream");
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
