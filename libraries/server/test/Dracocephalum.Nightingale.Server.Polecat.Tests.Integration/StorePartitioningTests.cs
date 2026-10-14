using System.Text;
using System.Text.RegularExpressions;

using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// A store initialized with each partitioning of the events table serves appends and reads, and
/// records the partitioning; that the schema stays what the store expects after an append is
/// <c>StoreCreationTests</c>'. Every test uses a database of its own, dropped at the end whatever
/// happened.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class StorePartitioningTests : IAsyncLifetime
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":1}");
    private static readonly byte[] Binary = [0x00, 0xFF, 0x80];
    private readonly string _name = TestDatabases.NewName();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await TestDatabases.DropAsync(_name);

    [Fact]
    public async Task Start_WithTenantPartitioning_ShouldAppendAndRead()
    {
        // Arrange: that the schema stays what the store expects after an append is StoreCreationTests'.
        using var host = await TestDatabases.StartProvisionedHostAsync(_name, options => options.Store.Partitioning = PartitioningMode.Tenant);
        var store = host.Store();

        // Act: the first append provisions the default tenant's partition and sequence.
        var appended = await store.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event() with { BinaryData = Binary }], TestContext.Current.CancellationToken);
        var slice = await store.ReadAsync("orders-1", Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        appended.Revision.ShouldBe(1);
        slice.ShouldNotBeNull().Events.Count.ShouldBe(2);
        slice.Events[1].BinaryData.ToArray().ShouldBe(Binary);
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
        var appended = await store.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event() with { BinaryData = Binary }], TestContext.Current.CancellationToken);
        var slice = await store.ReadAsync("orders-1", Direction.Backwards, null, 1, TestContext.Current.CancellationToken);

        // Assert
        appended.Revision.ShouldBe(1);
        slice.ShouldNotBeNull().Events.ShouldHaveSingleItem().Revision.ShouldBe(1);
        slice.Events[0].BinaryData.ToArray().ShouldBe(Binary);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT [Value] FROM nightingale.StoreProperty WHERE [Name] = 'Partitioning'")).ShouldBe("ArchivedStream");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Read_OnAStorePartitionedByTenant_ShouldBeAbleToSeekTheStreamsVersionsInTheTenantsPartition()
    {
        // Arrange: there the store's unique index of a stream's versions leads with the tenant's
        // partition key. Each statement of a stream's read is run with its events table hinted to
        // seek that index by the key and the stream; SQL Server refuses a hint its predicate
        // cannot satisfy, with error 8622, so a statement that runs is one that can seek it, on a
        // table of any size. The same statements without the key are refused, which is what the
        // read sent before it named the key.
        using var host = await TestDatabases.StartProvisionedHostAsync(_name, options => options.Store.Partitioning = PartitioningMode.Tenant);
        var store = host.Store();
        await store.AppendAsync("orders-1", StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        await store.SetStreamMetadataAsync("orders-1", StreamState.NoStream, new StreamMetadata { MaxCount = 1 }, TestContext.Current.CancellationToken);
        await using var context = await host.Services.GetRequiredService<IDbContextFactory<EventsDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        var tenant = JasperFx.StorageConstants.DefaultTenantId;
        var partitioned = new PlainStreamReader(() => context, tenant, partitionedByTenant: true, NullLogger.Instance);
        var plain = new PlainStreamReader(() => context, tenant, partitionedByTenant: false, NullLogger.Instance);

        // Act
        var keyed = new[]
        {
            partitioned.FindWithMetadata(context, "orders-1", "$$orders-1").ToQueryString(),
            partitioned.QueryRows(context, "orders-1").OrderBy(row => row.Version).Select(row => row.SeqId).ToQueryString(),
        };
        var unkeyed = new[]
        {
            plain.FindWithMetadata(context, "orders-1", "$$orders-1").ToQueryString(),
            plain.QueryRows(context, "orders-1").OrderBy(row => row.Version).Select(row => row.SeqId).ToQueryString(),
        };

        // Assert
        foreach (var sql in keyed)
        {
            await Should.NotThrowAsync(() => RunSeekingAsync(sql));
        }

        foreach (var sql in unkeyed)
        {
            (await Should.ThrowAsync<SqlException>(() => RunSeekingAsync(sql))).Number.ShouldBe(8622);
        }

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Runs a statement with every reference to the events table hinted to seek the unique index of a stream's versions by its two leading columns.</summary>
    private async Task RunSeekingAsync(string sql)
    {
        var hinted = EventsTable().Replace(sql, "$0 WITH (FORCESEEK([ix_pc_events_stream_and_version](tenant_ordinal, stream_id)))");
        hinted.ShouldNotBe(sql);
        await using var connection = new SqlConnection(TestDatabases.ConnectionStringFor(_name));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(hinted, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
        }
    }

    [GeneratedRegex(@"FROM \[\w+\]\.\[pc_events\] AS \[\w+\]")]
    private static partial Regex EventsTable();

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Body);
}
