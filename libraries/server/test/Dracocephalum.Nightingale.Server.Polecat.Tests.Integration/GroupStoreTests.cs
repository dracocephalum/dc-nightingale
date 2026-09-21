using Dracocephalum.Nightingale.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The group store's tables against the real database, through the context the host registered:
/// groups and their checkpoints, parked messages and the replay mark, and the lease's three
/// answers, with the concurrency tokens that settle a lease doing so on SQL Server. Built with a
/// clock the test moves, so lease expiry is a fact rather than a wait.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class GroupStoreTests(SqlServerTestDatabase database)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    [Fact]
    public async Task Groups_ShouldBeCreatedOnceReadBackWithTheirSettingsAndDeletedWithTheirParkedMessages()
    {
        // Arrange
        var sut = Store();
        var (stream, group) = Names();
        var settings = GroupSettings.Default with { Start = StreamPosition.From(3), MaxRetryCount = 4, MessageTimeout = TimeSpan.FromSeconds(7), Numbering = Numbering.Ordinal };

        // Act
        await sut.CreateAsync(new GroupDefinition(stream, group, settings, -1), TestContext.Current.CancellationToken);
        await Should.ThrowAsync<GroupExistsException>(() => sut.CreateAsync(new GroupDefinition(stream, group, settings, -1), TestContext.Current.CancellationToken));
        await sut.SaveCheckpointAsync(stream, group, 41, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage(stream, group, 40, 40, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage(stream, group, 42, 42, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ReplayAsync(stream, group, 42, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var read = await sut.GetAsync(stream, group, TestContext.Current.CancellationToken);
        var deleted = await sut.DeleteAsync(stream, group, TestContext.Current.CancellationToken);
        var again = await sut.DeleteAsync(stream, group, TestContext.Current.CancellationToken);

        // Assert
        read.ShouldNotBeNull().Settings.ShouldBe(settings);
        read.Checkpoint.ShouldBe(41);
        deleted.ShouldBeTrue();
        again.ShouldBeFalse();
        (await sut.GetAsync(stream, group, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await sut.DueAsync(stream, group, Now, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await sut.ReplayAsync(stream, group, null, ParkedNumber.Position, Now, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Replay_ShouldMoveParkedMessagesToTheOutboxAndParkingShouldMoveThemBack()
    {
        // Arrange
        var sut = Store();
        var (stream, group) = Names();
        await sut.CreateAsync(new GroupDefinition(stream, group, GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage(stream, group, 10, 10, null, Guid.NewGuid(), "a", 1, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage(stream, group, 12, 12, null, Guid.NewGuid(), "b", 1, Now.AddSeconds(1)), TestContext.Current.CancellationToken);

        // Act
        var before = await sut.DueAsync(stream, group, Now, TestContext.Current.CancellationToken);
        var one = await sut.ReplayAsync(stream, group, 12, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var missing = await sut.ReplayAsync(stream, group, 99, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var sameAgain = await sut.ReplayAsync(stream, group, 12, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var afterOne = await sut.DueAsync(stream, group, Now, TestContext.Current.CancellationToken);
        var rest = await sut.ReplayAsync(stream, group, null, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var afterAll = await sut.DueAsync(stream, group, Now, TestContext.Current.CancellationToken);
        await sut.DequeueAsync(stream, group, 10, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage(stream, group, 12, 12, null, Guid.NewGuid(), "b again", 2, Now.AddSeconds(2)), TestContext.Current.CancellationToken);
        var afterBoth = await sut.DueAsync(stream, group, Now, TestContext.Current.CancellationToken);
        var backAgain = await sut.ReplayAsync(stream, group, null, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);

        // Assert
        before.ShouldBeEmpty();
        one.ShouldBe(1);
        missing.ShouldBe(0);
        sameAgain.ShouldBe(1);
        afterOne.ShouldHaveSingleItem().Position.ShouldBe(12);
        rest.ShouldBe(2);
        afterAll.Select(message => message.Position).ShouldBe([10, 12]);
        afterBoth.ShouldBeEmpty("one dequeued, one parked again");
        backAgain.ShouldBe(1);
    }

    [Fact]
    public async Task Leases_ShouldBeGrantedWhenFreeRenewedByTheOwnerRefusedToOthersAndTakenOverOnceExpired()
    {
        // Arrange
        var sut = Store();
        var name = "group:lease-" + Guid.NewGuid().ToString("N");

        // Act
        var first = await sut.AcquireLeaseAsync(name, "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var renewed = await sut.AcquireLeaseAsync(name, "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var refused = await sut.AcquireLeaseAsync(name, "two", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(31));
        var takenOver = await sut.AcquireLeaseAsync(name, "two", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await sut.ReleaseLeaseAsync(name, "one", TestContext.Current.CancellationToken);
        var stillTwo = await sut.AcquireLeaseAsync(name, "three", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await sut.ReleaseLeaseAsync(name, "two", TestContext.Current.CancellationToken);
        var free = await sut.AcquireLeaseAsync(name, "three", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        first.ShouldBeNull();
        renewed.ShouldBeNull();
        refused!.Owner.ShouldBe("one");
        takenOver.ShouldBeNull();
        stillTwo!.Owner.ShouldBe("two");
        free.ShouldBeNull();
    }

    private static (string Stream, string Group) Names()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ("orders-" + suffix, "g-" + suffix);
    }

    private GroupStore Store() => new(database.Services.GetRequiredService<IDbContextFactory<NightingaleDbContext>>(), JasperFx.StorageConstants.DefaultTenantId, _time);
}
