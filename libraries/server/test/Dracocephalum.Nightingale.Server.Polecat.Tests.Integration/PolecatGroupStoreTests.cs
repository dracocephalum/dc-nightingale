using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The group store's tables against the real database: groups and their checkpoints, parked
/// messages and the replay mark, and the lease's three answers. Built directly with a clock the
/// test moves, so lease expiry is a fact rather than a wait.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class PolecatGroupStoreTests(SqlServerTestDatabase database)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    [Fact]
    public async Task Groups_ShouldBeCreatedOnceReadBackWithTheirSettingsAndDeletedWithTheirParkedMessages()
    {
        // Arrange
        var sut = Store();
        var (stream, group) = Names();
        var settings = GroupSettings.Default with { Start = StreamPosition.From(3), MaxRetryCount = 4, MessageTimeout = TimeSpan.FromSeconds(7) };

        // Act
        await sut.CreateAsync(new GroupDefinition(stream, group, settings, -1), TestContext.Current.CancellationToken);
        await Should.ThrowAsync<GroupExistsException>(() => sut.CreateAsync(new GroupDefinition(stream, group, settings, -1), TestContext.Current.CancellationToken));
        await sut.SaveCheckpointAsync(stream, group, 41, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage(stream, group, 40, Guid.NewGuid(), "poison", 2, Now, false), TestContext.Current.CancellationToken);
        var read = await sut.GetAsync(stream, group, TestContext.Current.CancellationToken);
        var deleted = await sut.DeleteAsync(stream, group, TestContext.Current.CancellationToken);
        var again = await sut.DeleteAsync(stream, group, TestContext.Current.CancellationToken);

        // Assert
        read.ShouldNotBeNull().Settings.ShouldBe(settings);
        read.Checkpoint.ShouldBe(41);
        deleted.ShouldBeTrue();
        again.ShouldBeFalse();
        (await sut.GetAsync(stream, group, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await sut.ReplayableAsync(stream, group, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Parked_ShouldBeReplayableOnlyOnceMarkedOneOrAllAndGoneOnceUnparked()
    {
        // Arrange
        var sut = Store();
        var (stream, group) = Names();
        await sut.CreateAsync(new GroupDefinition(stream, group, GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage(stream, group, 10, Guid.NewGuid(), "a", 1, Now, false), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage(stream, group, 12, Guid.NewGuid(), "b", 1, Now.AddSeconds(1), false), TestContext.Current.CancellationToken);

        // Act
        var before = await sut.ReplayableAsync(stream, group, TestContext.Current.CancellationToken);
        var one = await sut.MarkForReplayAsync(stream, group, 12, TestContext.Current.CancellationToken);
        var missing = await sut.MarkForReplayAsync(stream, group, 99, TestContext.Current.CancellationToken);
        var afterOne = await sut.ReplayableAsync(stream, group, TestContext.Current.CancellationToken);
        var rest = await sut.MarkForReplayAsync(stream, group, null, TestContext.Current.CancellationToken);
        var afterAll = await sut.ReplayableAsync(stream, group, TestContext.Current.CancellationToken);
        await sut.UnparkAsync(stream, group, 10, TestContext.Current.CancellationToken);
        var afterUnpark = await sut.ReplayableAsync(stream, group, TestContext.Current.CancellationToken);

        // Assert
        before.ShouldBeEmpty();
        one.ShouldBe(1);
        missing.ShouldBe(0);
        afterOne.ShouldHaveSingleItem().Position.ShouldBe(12);
        rest.ShouldBe(1);
        afterAll.Select(parked => parked.Position).ShouldBe([10, 12]);
        afterUnpark.ShouldHaveSingleItem().Position.ShouldBe(12);
    }

    [Fact]
    public async Task Leases_ShouldBeGrantedWhenFreeRenewedByTheOwnerRefusedToOthersAndTakenOverOnceExpired()
    {
        // Arrange
        var sut = Store();
        var name = "group:lease-" + Guid.NewGuid().ToString("N");

        // Act
        var first = await sut.AcquireLeaseAsync(name, "one", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var renewed = await sut.AcquireLeaseAsync(name, "one", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var refused = await sut.AcquireLeaseAsync(name, "two", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(31));
        var takenOver = await sut.AcquireLeaseAsync(name, "two", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await sut.ReleaseLeaseAsync(name, "one", TestContext.Current.CancellationToken);
        var stillTwo = await sut.AcquireLeaseAsync(name, "three", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await sut.ReleaseLeaseAsync(name, "two", TestContext.Current.CancellationToken);
        var free = await sut.AcquireLeaseAsync(name, "three", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        first.ShouldBeNull();
        renewed.ShouldBeNull();
        refused.ShouldBe("one");
        takenOver.ShouldBeNull();
        stillTwo.ShouldBe("two");
        free.ShouldBeNull();
    }

    private static (string Stream, string Group) Names()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ("orders-" + suffix, "g-" + suffix);
    }

    private PolecatGroupStore Store() => new(database.ConnectionString, "dbo", JasperFx.StorageConstants.DefaultTenantId, _time);
}
