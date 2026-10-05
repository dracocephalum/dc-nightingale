using Dracocephalum.Nightingale.Server.Data;
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
public sealed class SubscriptionGroupStoreTests(SqlServerTestDatabase database)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    [Fact]
    public async Task Groups_ShouldBeCreatedOnceReadBackWithTheirSettingsAndDeletedWithTheirParkedMessages()
    {
        // Arrange
        var sut = Store();
        var (stream, group) = Names();
        var id = Guid.CreateVersion7();
        var settings = GroupSettings.Default with { Start = StreamPosition.From(3), MaxRetryCount = 4, MessageTimeout = TimeSpan.FromSeconds(7), Numbering = Numbering.Ordinal };

        // Act
        await sut.CreateAsync(new SubscriptionGroupDefinition(stream, group, settings, -1) { Id = id }, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<GroupExistsException>(() => sut.CreateAsync(new SubscriptionGroupDefinition(stream, group, settings, -1) { Id = id }, TestContext.Current.CancellationToken));
        await sut.SaveCheckpointAsync(id, 41, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 40, 40, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 42, 42, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ReplayAsync(id, 42, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var read = await sut.GetAsync(stream, group, TestContext.Current.CancellationToken);
        var deleted = await sut.DeleteAsync(stream, group, TestContext.Current.CancellationToken);
        var again = await sut.DeleteAsync(stream, group, TestContext.Current.CancellationToken);

        // Assert
        read.ShouldNotBeNull().Settings.ShouldBe(settings);
        read.Checkpoint.ShouldBe(41);
        deleted.ShouldBeTrue();
        again.ShouldBeFalse();
        (await sut.GetAsync(stream, group, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await sut.DueAsync(id, Now, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await sut.ReplayAsync(id, null, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task DescribeAndList_ShouldCountParkedAndOutboxRowsPerGroupAndSayWhoHoldsEachLease()
    {
        // Arrange: two groups on one stream; the first has two parked, one of them on the outbox, and a lease.
        var sut = Store();
        var (stream, group) = Names();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await sut.CreateAsync(new SubscriptionGroupDefinition(stream, group, GroupSettings.Default, -1) { Id = first }, TestContext.Current.CancellationToken);
        await sut.CreateAsync(new SubscriptionGroupDefinition(stream, group + "-2", GroupSettings.Default, -1) { Id = second }, TestContext.Current.CancellationToken);
        await sut.SaveCheckpointAsync(first, 41, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(first, 40, 40, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(first, 42, 42, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ReplayAsync(first, 42, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        await sut.AcquireLeaseAsync(SubscriptionGroupRegistry.LeaseName(first), "one", new Uri("http://one:5000"), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var live = new SubscriptionGroupLive(Now.AddMinutes(-5), 4, 1, 10, 41, Now.AddSeconds(-20), "ipv4:10.0.0.7:51234", Now);
        await sut.SaveLiveAsync(first, live, TestContext.Current.CancellationToken);
        await sut.SaveLiveAsync(second, live, TestContext.Current.CancellationToken);

        // Act
        var described = await sut.DescribeAsync(stream, group, TestContext.Current.CancellationToken);
        var ofStream = await sut.ListAsync(stream, TestContext.Current.CancellationToken);
        var all = await sut.ListAsync(null, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(31));
        var lapsed = await sut.ListAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        var summary = described.ShouldNotBeNull();
        summary.Definition.Checkpoint.ShouldBe(41);
        summary.ParkedCount.ShouldBe(1);
        summary.OutboxCount.ShouldBe(1);
        summary.Holder.ShouldBe(new LeaseHolder("one", new Uri("http://one:5000")));

        // The snapshot counts only under a lease: the second group has the same columns and no holder.
        summary.Live.ShouldBe(live);
        ofStream[1].Live.ShouldBeNull();
        lapsed[0].Live.ShouldBeNull();
        ofStream.Select(listed => listed.Definition.Id).ShouldBe([first, second]);
        ofStream[0].ShouldBe(summary);
        ofStream[1].ParkedCount.ShouldBe(0);
        ofStream[1].OutboxCount.ShouldBe(0);
        ofStream[1].Holder.ShouldBeNull();
        all.Select(listed => listed.Definition.Id).ShouldContain(first);
        all.Select(listed => listed.Definition.Id).ShouldContain(second);
        lapsed[0].Holder.ShouldBeNull();
        await sut.DeleteAsync(stream, group, TestContext.Current.CancellationToken);
        await sut.DeleteAsync(stream, group + "-2", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ListSkipAndReplayBefore_ShouldGoByTheNumberTheGroupSpeaksAPageAtATime()
    {
        // Arrange: four parked messages whose revisions run the other way from their positions,
        // so the order shows which number was used.
        var sut = Store();
        var (stream, group) = Names();
        var id = Guid.CreateVersion7();
        await sut.CreateAsync(new SubscriptionGroupDefinition(stream, group, GroupSettings.Default, -1) { Id = id }, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 100, 4, null, Guid.NewGuid(), "a", 1, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 200, 3, null, Guid.NewGuid(), "b", 2, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 300, 2, null, Guid.NewGuid(), "c", 3, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 400, 1, null, Guid.NewGuid(), "d", 4, Now), TestContext.Current.CancellationToken);

        // Act
        var byPosition = await sut.ListParkedAsync(id, SubscriptionParkedNumber.Position, null, 10, TestContext.Current.CancellationToken);
        var firstPage = await sut.ListParkedAsync(id, SubscriptionParkedNumber.Revision, null, 3, TestContext.Current.CancellationToken);
        var secondPage = await sut.ListParkedAsync(id, SubscriptionParkedNumber.Revision, 3, 3, TestContext.Current.CancellationToken);
        var replayed = await sut.ReplayBeforeAsync(id, 3, SubscriptionParkedNumber.Revision, Now, TestContext.Current.CancellationToken);
        var replayedAgain = await sut.ReplayBeforeAsync(id, 3, SubscriptionParkedNumber.Revision, Now, TestContext.Current.CancellationToken);
        var outbox = await sut.ListOutboxAsync(id, SubscriptionParkedNumber.Revision, null, 10, TestContext.Current.CancellationToken);
        var outboxAfterOne = await sut.ListOutboxAsync(id, SubscriptionParkedNumber.Revision, 1, 10, TestContext.Current.CancellationToken);
        var skippedOne = await sut.SkipAsync(id, 4, null, SubscriptionParkedNumber.Revision, TestContext.Current.CancellationToken);
        var skippedMissing = await sut.SkipAsync(id, 4, null, SubscriptionParkedNumber.Revision, TestContext.Current.CancellationToken);
        var skippedBefore = await sut.SkipAsync(id, null, 3, SubscriptionParkedNumber.Revision, TestContext.Current.CancellationToken);
        var left = await sut.ListParkedAsync(id, SubscriptionParkedNumber.Revision, null, 10, TestContext.Current.CancellationToken);
        var skippedAll = await sut.SkipAsync(id, null, null, SubscriptionParkedNumber.Revision, TestContext.Current.CancellationToken);
        var none = await sut.ListParkedAsync(id, SubscriptionParkedNumber.Revision, null, 10, TestContext.Current.CancellationToken);

        // Assert: skipping takes parked messages only; what a replay put on the outbox stays there.
        byPosition.Select(message => message.Position).ShouldBe([100, 200, 300, 400]);
        firstPage.Select(message => message.Revision).ShouldBe([1, 2, 3]);
        firstPage[0].ShouldSatisfyAllConditions(
            message => message.Position.ShouldBe(400),
            message => message.Reason.ShouldBe("d"),
            message => message.Attempts.ShouldBe(4),
            message => message.ParkedAt.ShouldBe(Now));
        secondPage.Select(message => message.Revision).ShouldBe([4]);
        replayed.ShouldBe(2);
        replayedAgain.ShouldBe(2, "already on the outbox, counted and left there");
        outbox.Select(message => message.Revision).ShouldBe([1, 2]);
        outboxAfterOne.Select(message => message.Revision).ShouldBe([2]);
        skippedOne.ShouldBe(1);
        skippedMissing.ShouldBe(0);
        skippedBefore.ShouldBe(0, "the two below three are on the outbox, not parked");
        left.Select(message => message.Revision).ShouldBe([3]);
        skippedAll.ShouldBe(1);
        none.ShouldBeEmpty();
        (await sut.ListOutboxAsync(id, SubscriptionParkedNumber.Revision, null, 10, TestContext.Current.CancellationToken)).Count.ShouldBe(2);
        await sut.DeleteAsync(stream, group, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateSettings_ShouldReplaceTheGroupsSettingsAndLeaveItsCheckpointAndSayWhenThereIsNoGroup()
    {
        // Arrange
        var sut = Store();
        var (stream, group) = Names();
        var id = Guid.CreateVersion7();
        var created = GroupSettings.Default with { Start = StreamPosition.From(3), Numbering = Numbering.Ordinal };
        var changed = created with { MessageTimeout = TimeSpan.FromSeconds(7), MaxRetryCount = 4, CheckpointUpperBound = 50, CheckpointAfter = TimeSpan.FromSeconds(1), CheckpointLowerBound = 5, BufferSize = 20, MaxSubscriberCount = 1 };
        await sut.CreateAsync(new SubscriptionGroupDefinition(stream, group, created, -1) { Id = id }, TestContext.Current.CancellationToken);
        await sut.SaveCheckpointAsync(id, 41, TestContext.Current.CancellationToken);

        // Act
        var updated = await sut.UpdateSettingsAsync(id, changed, TestContext.Current.CancellationToken);
        var missing = await sut.UpdateSettingsAsync(Guid.NewGuid(), changed, TestContext.Current.CancellationToken);
        var read = await sut.GetAsync(stream, group, TestContext.Current.CancellationToken);

        // Assert
        updated.ShouldBeTrue();
        missing.ShouldBeFalse();
        read.ShouldNotBeNull().Settings.ShouldBe(changed);
        read.Checkpoint.ShouldBe(41);
        await sut.DeleteAsync(stream, group, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Replay_ShouldMoveParkedMessagesToTheOutboxAndParkingShouldMoveThemBack()
    {
        // Arrange
        var sut = Store();
        var (stream, group) = Names();
        var id = Guid.CreateVersion7();
        await sut.CreateAsync(new SubscriptionGroupDefinition(stream, group, GroupSettings.Default, -1) { Id = id }, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 10, 10, null, Guid.NewGuid(), "a", 1, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 12, 12, null, Guid.NewGuid(), "b", 1, Now.AddSeconds(1)), TestContext.Current.CancellationToken);

        // Act
        var before = await sut.DueAsync(id, Now, TestContext.Current.CancellationToken);
        var one = await sut.ReplayAsync(id, 12, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var missing = await sut.ReplayAsync(id, 99, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var sameAgain = await sut.ReplayAsync(id, 12, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var afterOne = await sut.DueAsync(id, Now, TestContext.Current.CancellationToken);
        var rest = await sut.ReplayAsync(id, null, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var afterAll = await sut.DueAsync(id, Now, TestContext.Current.CancellationToken);
        await sut.DequeueAsync(id, 10, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 12, 12, null, Guid.NewGuid(), "b again", 2, Now.AddSeconds(2)), TestContext.Current.CancellationToken);
        var afterBoth = await sut.DueAsync(id, Now, TestContext.Current.CancellationToken);
        var backAgain = await sut.ReplayAsync(id, null, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task Park_ForAGroupThatWasDeleted_ShouldLeaveNothingBehind()
    {
        // Arrange: a consumer still holds an event while its group is deleted under it.
        var sut = Store();
        var (stream, group) = Names();
        var id = Guid.CreateVersion7();
        await sut.CreateAsync(new SubscriptionGroupDefinition(stream, group, GroupSettings.Default, -1) { Id = id }, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 10, 10, null, Guid.NewGuid(), "poison", 1, Now), TestContext.Current.CancellationToken);
        await sut.DeleteAsync(stream, group, TestContext.Current.CancellationToken);

        // Act: the park arrives after the delete. The foreign key refuses a row for a group that
        // is gone, and the store takes that for what it is.
        await sut.ParkAsync(new SubscriptionParkedMessage(id, 11, 11, null, Guid.NewGuid(), "poison", 1, Now), TestContext.Current.CancellationToken);

        // Assert: the delete took the group's parked event with it, and the late one left no row.
        var orphans = await TestDatabases.ScalarAsync<int>(database.Name, $"SELECT COUNT(*) FROM nightingale.SubscriptionParkedEvent WHERE SubscriptionGroupId = '{id}'");
        orphans.ShouldBe(0);
    }

    [Fact]
    public async Task Groups_WithNamesInAnotherScript_ShouldBeTwoGroups()
    {
        // Arrange: two names no character of which is in the database's code page. In a column
        // that is not Unicode both would be stored as question marks, and be one name.
        var sut = Store();
        var (stream, _) = Names();
        const string billing = "计费";
        const string ledger = "账单";

        // Act
        await sut.CreateAsync(new SubscriptionGroupDefinition(stream, billing, GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        await sut.CreateAsync(new SubscriptionGroupDefinition(stream, ledger, GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        var first = await sut.GetAsync(stream, billing, TestContext.Current.CancellationToken);
        var second = await sut.GetAsync(stream, ledger, TestContext.Current.CancellationToken);

        // Assert
        first.ShouldNotBeNull().Group.ShouldBe(billing);
        second.ShouldNotBeNull().Group.ShouldBe(ledger);
        second.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public async Task Get_UnderAnotherSpelling_ShouldReturnTheGroupUnderTheNamesItWasCreatedWith()
    {
        // Arrange: whether another spelling is the same group is the database's to say.
        var sut = Store();
        var (stream, group) = Names();
        var id = Guid.CreateVersion7();
        await sut.CreateAsync(new SubscriptionGroupDefinition(stream, group, GroupSettings.Default, -1) { Id = id }, TestContext.Current.CancellationToken);
        var collation = await TestDatabases.ScalarAsync<string>(database.Name, "SELECT CONVERT(varchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))");

        // Act
        var read = await sut.GetAsync(stream.ToUpperInvariant(), group.ToUpperInvariant(), TestContext.Current.CancellationToken);

        // Assert: found, it goes by its own names, never the request's; on a database that tells
        // case apart there is no such group.
        if (collation.Contains("_CI", StringComparison.Ordinal))
        {
            read.ShouldNotBeNull().Stream.ShouldBe(stream);
            read.Group.ShouldBe(group);
        }
        else
        {
            read.ShouldBeNull();
        }
    }

    private static (string Stream, string Group) Names()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ("orders-" + suffix, "g-" + suffix);
    }

    private SubscriptionGroupStore Store() => new(database.Services.GetRequiredService<IDbContextFactory<NightingaleDbContext>>(), JasperFx.StorageConstants.DefaultTenantId, _time);
}
