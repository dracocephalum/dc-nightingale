using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// What a start does to an initialized store's schema, against a real database: drift refused by
/// Verify and repaired by Migrate, an index batch stopped by the index timeout and a blocked batch
/// given up after the lock waits, fast boot trusting the recorded hash, a record that is not this
/// server's rewritten, and the report and the two applies a host calls without starting the
/// server. Every test uses a database of its own, dropped at the end whatever happened; hosts start
/// in Initialize mode unless a test says otherwise.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaApplyTests : IAsyncLifetime
{
    /// <summary>One row per value of what a store is initialized with: four settings, who and when, the store library and the hash of its creation script.</summary>
    private const int SettingRowCount = 8;

    private readonly string _name = TestDatabases.NewName();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await TestDatabases.DropAsync(_name);

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
}
