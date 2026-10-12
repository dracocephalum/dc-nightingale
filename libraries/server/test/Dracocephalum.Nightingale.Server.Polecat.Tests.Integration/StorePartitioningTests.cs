using System.Text;

using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// A store initialized with each partitioning of the events table serves appends and reads, and
/// records the partitioning; that the schema stays what the store expects after an append is
/// <c>StoreCreationTests</c>'. Every test uses a database of its own, dropped at the end whatever
/// happened.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StorePartitioningTests : IAsyncLifetime
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":1}");
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
