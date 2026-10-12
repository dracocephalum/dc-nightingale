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
/// from the creation script instead. Every test here uses a
/// database of its own, dropped at the end whatever happened, and they run one at a time: each
/// start runs the store's catalog query, whose memory grant a small SQL Server hands out one at a
/// time.
/// </summary>
[Collection(OwnDatabase.Name)]
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
    public async Task Start_WhenSchemaDrifted_ShouldRefuseToVerifyAndRepairWhenMigrating()
    {
        // Arrange: something outside the server dropped one of the gateway's indexes.
        using (var first = await TestDatabases.StartHostAsync(_name))
        {
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        await TestDatabases.ExecuteAsync(_name, "DROP INDEX ix_pc_events_category_seq ON dbo.pc_events");

        // Act
        var refused = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, options => options.SchemaMode = SchemaMode.Verify));
        using var repaired = await TestDatabases.StartHostAsync(_name, options => options.SchemaMode = SchemaMode.Migrate);

        // Assert
        refused.Message.ShouldContain("Nightingale:SchemaMode is Verify");
        refused.Message.ShouldContain("--apply-schema");
        refused.Message.ShouldContain("ix_pc_events_category_seq");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'ix_pc_events_category_seq'")).ShouldBe(1);
        await repaired.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WhenAnIndexBatchRunsPastTheIndexTimeout_ShouldStopItAndNameIt()
    {
        // Arrange: an initialized store missing an index, and another session holding the events
        // table locked, as a long transaction would, so the batch cannot finish; the lock wait is
        // longer than the index timeout, so it is the latter that ends it.
        await TestDatabases.ProvisionAsync(_name);
        await TestDatabases.ExecuteAsync(_name, "DROP INDEX ix_pc_events_category_seq ON dbo.pc_events");
        await using var holder = await HoldTheEventsTableAsync();

        // Act
        var stopped = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, options =>
        {
            options.SchemaMode = SchemaMode.Migrate;
            options.IndexMigrationStatementTimeout = TimeSpan.FromSeconds(1);
            options.SchemaLockWaitTimeout = TimeSpan.FromSeconds(10);
        }));
        await holder.DisposeAsync();
        using var repaired = await TestDatabases.StartHostAsync(_name, options => options.SchemaMode = SchemaMode.Migrate);

        // Assert: stopped after the second, naming the setting and the batch; once the lock is
        // gone the same batch runs.
        stopped.Message.ShouldContain("did not finish within 00:00:01");
        stopped.Message.ShouldContain("Nightingale:IndexMigrationStatementTimeout");
        stopped.Message.ShouldContain("CREATE INDEX ix_pc_events_category_seq");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'ix_pc_events_category_seq'")).ShouldBe(1);
        repaired.Logged().ShouldContain(entry => entry.Message.Contains("Applied to the event store's tables", StringComparison.Ordinal) && entry.Message.Contains("ix_pc_events_category_seq", StringComparison.Ordinal));
        await repaired.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WhenABatchIsBlockedByAnotherSession_ShouldWaitNoLongerThanTheLockWaitAtATimeAndGiveUpAfterTheTries()
    {
        // Arrange: a store behind by one index, and the events table held by another session.
        await TestDatabases.ProvisionAsync(_name);
        await TestDatabases.ExecuteAsync(_name, "DROP INDEX ix_pc_events_category_seq ON dbo.pc_events");
        await using var holder = await HoldTheEventsTableAsync();

        // Act: the lock wait is a second, well inside the index timeout, so the wait is what ends
        // each try; between tries nothing of ours queues on the table.
        var started = TimeProvider.System.GetTimestamp();
        var blocked = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, options =>
        {
            options.SchemaMode = SchemaMode.Migrate;
            options.SchemaLockWaitTimeout = TimeSpan.FromSeconds(1);
        }));
        var tookAtLeast = TimeProvider.System.GetElapsedTime(started);

        // Assert
        blocked.Message.ShouldContain("blocked by a lock another session holds");
        blocked.Message.ShouldContain($"{StoreSchema.LockWaitAttempts} tries of 00:00:01");
        blocked.Message.ShouldContain("Nightingale:SchemaLockWaitTimeout");
        blocked.Message.ShouldContain("CREATE INDEX ix_pc_events_category_seq");
        tookAtLeast.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds((2 * StoreSchema.LockWaitAttempts) - 1), "each try waits a second and sleeps a second, but the last");
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'ix_pc_events_category_seq'")).ShouldBe(0);
    }

    /// <summary>A session holding the events table exclusively in an open transaction, as a long one would; disposing it rolls the transaction back and lets go.</summary>
    private async Task<TableHold> HoldTheEventsTableAsync()
    {
        var holder = new Microsoft.Data.SqlClient.SqlConnection(TestDatabases.ConnectionStringFor(_name));
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using var hold = holder.CreateCommand();
        hold.CommandText = "BEGIN TRANSACTION; SELECT TOP 1 seq_id FROM dbo.pc_events WITH (TABLOCKX, HOLDLOCK);";
        await hold.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new TableHold(holder);
    }

    private sealed class TableHold(Microsoft.Data.SqlClient.SqlConnection holder) : IAsyncDisposable
    {
        private bool _released;

        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            await using (var release = holder.CreateCommand())
            {
                release.CommandText = "ROLLBACK TRANSACTION;";
                await release.ExecuteNonQueryAsync();
            }

            await holder.DisposeAsync();
        }
    }

    [Fact]
    public async Task Start_WithFastBoot_ShouldTakeTheRecordedHashAndCompareWhenItIsNotThisServers()
    {
        // Arrange: an initialized store one of whose indexes was dropped outside the server.
        using (var first = await TestDatabases.StartHostAsync(_name))
        {
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        await TestDatabases.ExecuteAsync(_name, "DROP INDEX ix_pc_events_category_seq ON dbo.pc_events");

        // Act: the recorded hash is this server's, so a fast boot compares nothing and serves;
        // the report compares all the same.
        using (var trusting = await TestDatabases.StartHostAsync(_name, TestDatabases.FastBoot()))
        {
            var report = await trusting.Services.GetNightingaleSchemaReportAsync(TestContext.Current.CancellationToken);
            report.StoreChanges.ShouldNotBeNull().ShouldContain("ix_pc_events_category_seq");
            report.StoreComparisonSkipped.ShouldBeFalse();
            await trusting.StopAsync(TestContext.Current.CancellationToken);
        }

        // A hash that is not this server's, as after an upgrade of the store library: compared,
        // and refused by a start that may not change the database.
        await TestDatabases.ExecuteAsync(_name, "UPDATE nightingale.StoreProperty SET [Value] = 'another' WHERE [Name] = 'StoreSchemaHash'");
        var refused = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, TestDatabases.FastBoot(options => options.SchemaMode = SchemaMode.Verify)));

        // Assert
        refused.Message.ShouldContain("ix_pc_events_category_seq");
    }

    [Fact]
    public async Task Start_WhenTheRecordOfTheStoreIsNotThisServers_ShouldRewriteItWhenAllowedToChangeAndServeEitherWay()
    {
        // Arrange: an initialized, current store whose record says another library made it, as
        // after an upgrade that changed no table, or a start that died between applying and
        // recording.
        await TestDatabases.ProvisionAsync(_name);
        await TestDatabases.ExecuteAsync(_name, "UPDATE nightingale.StoreProperty SET [Value] = 'another' WHERE [Name] IN ('StoreSchemaHash', 'StoreLibrary')");

        // Act
        using (var verifying = await TestDatabases.StartHostAsync(_name, options => options.SchemaMode = SchemaMode.Verify))
        {
            await verifying.StopAsync(TestContext.Current.CancellationToken);
        }

        var stillAnother = await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'StoreLibrary'");
        using (var migrating = await TestDatabases.StartHostAsync(_name, options => options.SchemaMode = SchemaMode.Migrate))
        {
            migrating.Logged().ShouldContain(entry => entry.Message.Contains("whose tables were current under another record", StringComparison.Ordinal));
            await migrating.StopAsync(TestContext.Current.CancellationToken);
        }

        // Assert: fast boot trusts the rewritten hash again.
        stillAnother.ShouldBe("another");
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'StoreLibrary'")).ShouldBe(StoreSchema.StoreLibraryVersion);
        using var trusting = await TestDatabases.StartHostAsync(_name, TestDatabases.FastBoot(options => options.SchemaMode = SchemaMode.Verify));
        trusting.Logged().ShouldContain(entry => entry.Message.Contains("Fast boot", StringComparison.Ordinal));
        await trusting.StopAsync(TestContext.Current.CancellationToken);
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
        services.AddNightingalePolecat(TestDatabases.ConnectionStringFor(_name), TestAuth.Off());
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
    public async Task SchemaReport_WhenTheDatabaseIsMissing_ShouldSaySoAndOnlyInitializeShouldMakeIt()
    {
        // Arrange: the host's options say Verify, as a production host's would; the two applies
        // do what their modes do regardless.
        var services = new ServiceCollection();
        services.AddNightingalePolecat(TestDatabases.ConnectionStringFor(_name), TestAuth.Off());
        await using var provider = services.BuildServiceProvider();

        // Act
        var before = await provider.GetNightingaleSchemaReportAsync(TestContext.Current.CancellationToken);
        var applyRefused = await Should.ThrowAsync<StoreInitializationException>(() => provider.ApplyNightingaleSchemaAsync(TestContext.Current.CancellationToken));
        await provider.InitializeNightingaleStoreAsync(TestContext.Current.CancellationToken);
        var after = await provider.GetNightingaleSchemaReportAsync(TestContext.Current.CancellationToken);
        await provider.InitializeNightingaleStoreAsync(TestContext.Current.CancellationToken);

        // Assert: initializing again changes nothing.
        before.DatabaseExists.ShouldBeFalse();
        before.IsCurrent.ShouldBeFalse();
        before.PendingMigrations.ShouldNotBeEmpty();
        applyRefused.Message.ShouldContain("Nightingale:SchemaMode is Migrate");
        after.IsCurrent.ShouldBeTrue();
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.StoreProperty")).ShouldBe(SettingRowCount);
    }

    [Fact]
    public async Task Start_ByDefault_ShouldTellStreamNamesApartByCaseAndByScript()
    {
        // Arrange & Act: the default collation is binary and UTF-8.
        using var host = await TestDatabases.StartProvisionedHostAsync(_name);
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
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Collation'")).ShouldBe(NightingaleOptions.StoreOptions.DefaultCollation);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM dbo.pc_streams")).ShouldBe(4);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WithAnotherUtf8Collation_ShouldFollowItsRuleForCase()
    {
        // Arrange: a host may still choose that case does not tell names apart. That the server
        // creates a database with the collation it is given is StoreDatabaseTests'.
        const string collation = "Latin1_General_100_CI_AS_SC_UTF8";

        // Act
        using var host = await TestDatabases.StartProvisionedHostAsync(_name, options => options.Store.Collation = collation);
        var store = host.Store();
        await store.AppendAsync("Orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        var lower = await store.AppendAsync("orders-1", StreamState.Any, [Event()], TestContext.Current.CancellationToken);

        // Assert: one stream, and the record says which collation the database has.
        lower.Revision.ShouldBe(1);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Collation'")).ShouldBe(collation);
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

        // Act: served with it, and a second start finds it as it was left.
        using (var first = await TestDatabases.StartProvisionedHostAsync(_name, Ignoring))
        {
            var appended = await first.Store().AppendAsync("orders-1", StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
            appended.Revision.ShouldBe(0);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        using (var second = await TestDatabases.StartHostAsync(_name, TestDatabases.FastBoot(Ignoring)))
        {
            await second.StopAsync(TestContext.Current.CancellationToken);
        }

        var withoutTheChoice = await Should.ThrowAsync<StoreInitializationException>(() => TestDatabases.StartHostAsync(_name, TestDatabases.FastBoot(options => options.Store.Collation = collation)));

        // Assert: the choice is the host's, made each time, and not a property of the store.
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Collation'")).ShouldBe(collation);
        (await TestDatabases.ScalarAsync<int>(_name, "SELECT COUNT(*) FROM nightingale.StoreProperty WHERE [Name] LIKE '%IgnoreCollation%'")).ShouldBe(0);
        withoutTheChoice.Message.ShouldContain("which is not a UTF-8 collation");
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
    public async Task Start_WithTenantPartitioning_ShouldAppendAndRead()
    {
        // Arrange: that the schema stays what the store expects after an append is StoreCreationTests'.
        using var host = await TestDatabases.StartProvisionedHostAsync(_name, options => options.Store.Partitioning = PartitioningMode.Tenant);
        var store = host.Store();

        // Act: the first append provisions the default tenant's partition and sequence.
        var appended = await store.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        var slice = await store.ReadAsync("orders-1", Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        appended.Revision.ShouldBe(1);
        slice.ShouldNotBeNull().Events.Count.ShouldBe(2);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Partitioning'")).ShouldBe("Tenant");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WithArchivedStreamPartitioning_ShouldAppendAndRead()
    {
        // Arrange: that the schema stays what the store expects after an append is StoreCreationTests'.
        using var host = await TestDatabases.StartProvisionedHostAsync(_name, options => options.Store.Partitioning = PartitioningMode.ArchivedStream);
        var store = host.Store();

        // Act
        var appended = await store.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        var slice = await store.ReadAsync("orders-1", Direction.Backwards, null, 1, TestContext.Current.CancellationToken);

        // Assert
        appended.Revision.ShouldBe(1);
        slice.ShouldNotBeNull().Events.ShouldHaveSingleItem().Revision.ShouldBe(1);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Partitioning'")).ShouldBe("ArchivedStream");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Body);
}
