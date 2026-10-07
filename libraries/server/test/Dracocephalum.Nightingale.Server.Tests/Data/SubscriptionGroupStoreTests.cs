using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Data;

/// <summary>
/// The group store's plain reads and writes on the in-memory provider: every operation of the
/// port, the moves between parked and the outbox, and the lease's answers as the concurrency
/// tokens settle them. What the in-memory provider cannot stand in for, a real duplicate key
/// and a real concurrent write, the integration project covers on SQL Server.
/// </summary>
public sealed class SubscriptionGroupStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Billing = Guid.Parse("0198f3a2-0000-7000-8000-000000000001");

    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryContexts _contexts = new();

    [Fact]
    public async Task Groups_ShouldBeCreatedOnceReadBackWithTheirSettingsAndDeletedWithTheirParkedMessagesAndOutbox()
    {
        // Arrange
        var sut = Store();
        var settings = GroupSettings.Default with { Start = StreamPosition.From(3), MaxRetryCount = 4, MessageTimeout = TimeSpan.FromSeconds(7), Numbering = Numbering.Ordinal, ConsumerStrategy = ConsumerStrategy.Pinned };

        // Act
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", settings, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<GroupExistsException>(() => sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", settings, -1) { Id = Billing }, TestContext.Current.CancellationToken));
        await sut.SaveCheckpointAsync(Billing, 41, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 40, 40, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 42, 42, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ReplayAsync(Billing, 42, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var read = await sut.GetAsync("orders-1", "billing", TestContext.Current.CancellationToken);
        var deleted = await sut.DeleteAsync("orders-1", "billing", TestContext.Current.CancellationToken);
        var again = await sut.DeleteAsync("orders-1", "billing", TestContext.Current.CancellationToken);

        // Assert
        read.ShouldNotBeNull().Settings.ShouldBe(settings);
        read.Checkpoint.ShouldBe(41);
        deleted.ShouldBeTrue();
        again.ShouldBeFalse();
        (await sut.GetAsync("orders-1", "billing", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await sut.DueAsync(Billing, Now, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await sut.ReplayAsync(Billing, null, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Groups_ShouldBeKeyedByStreamAndNameTogether()
    {
        // Arrange
        var sut = Store();
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1) { Id = Billing }, TestContext.Current.CancellationToken);

        // Act
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-2", "billing", GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "shipping", GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        await sut.SaveCheckpointAsync(Billing, 5, TestContext.Current.CancellationToken);

        // Assert
        (await sut.GetAsync("orders-1", "billing", TestContext.Current.CancellationToken))!.Checkpoint.ShouldBe(5);
        (await sut.GetAsync("orders-2", "billing", TestContext.Current.CancellationToken))!.Checkpoint.ShouldBe(-1);
        (await sut.GetAsync("orders-1", "shipping", TestContext.Current.CancellationToken))!.Checkpoint.ShouldBe(-1);
    }

    [Fact]
    public async Task Replay_ShouldMoveOneOrAllToTheOutboxAndCountAgainWhatIsAlreadyThere()
    {
        // Arrange
        var sut = Store();
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 10, 10, null, Guid.NewGuid(), "a", 1, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 12, 12, null, Guid.NewGuid(), "b", 1, Now.AddSeconds(1)), TestContext.Current.CancellationToken);

        // Act
        var before = await sut.DueAsync(Billing, Now, TestContext.Current.CancellationToken);
        var one = await sut.ReplayAsync(Billing, 12, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var missing = await sut.ReplayAsync(Billing, 99, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var sameAgain = await sut.ReplayAsync(Billing, 12, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var afterOne = await sut.DueAsync(Billing, Now, TestContext.Current.CancellationToken);
        var rest = await sut.ReplayAsync(Billing, null, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var afterAll = await sut.DueAsync(Billing, Now, TestContext.Current.CancellationToken);
        await sut.DequeueAsync(Billing, 10, TestContext.Current.CancellationToken);
        var afterDequeue = await sut.DueAsync(Billing, Now, TestContext.Current.CancellationToken);

        // Assert
        before.ShouldBeEmpty();
        one.ShouldBe(1);
        missing.ShouldBe(0);
        sameAgain.ShouldBe(1, "replaying a message already on the outbox is a no-op, not a not-found");
        afterOne.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            message => message.Position.ShouldBe(12),
            message => message.Reason.ShouldBe("b"),
            message => message.Attempts.ShouldBe(1),
            message => message.DueAt.ShouldBe(Now));
        rest.ShouldBe(2, "one moved now, one already there");
        afterAll.Select(message => message.Position).ShouldBe([10, 12]);
        afterDequeue.ShouldHaveSingleItem().Position.ShouldBe(12);
    }

    [Fact]
    public async Task ListSkipAndReplayBefore_ShouldGoByTheNumberTheGroupSpeaksAPageAtATime()
    {
        // Arrange: four parked messages whose revisions run the other way from their positions,
        // so the order shows which number was used.
        var sut = Store();
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 100, 4, null, Guid.NewGuid(), "a", 1, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 200, 3, null, Guid.NewGuid(), "b", 2, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 300, 2, null, Guid.NewGuid(), "c", 3, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 400, 1, null, Guid.NewGuid(), "d", 4, Now), TestContext.Current.CancellationToken);

        // Act
        var byPosition = await sut.ListParkedAsync(Billing, SubscriptionParkedNumber.Position, null, 10, TestContext.Current.CancellationToken);
        var firstPage = await sut.ListParkedAsync(Billing, SubscriptionParkedNumber.Revision, null, 3, TestContext.Current.CancellationToken);
        var secondPage = await sut.ListParkedAsync(Billing, SubscriptionParkedNumber.Revision, 3, 3, TestContext.Current.CancellationToken);
        var replayed = await sut.ReplayBeforeAsync(Billing, 3, SubscriptionParkedNumber.Revision, Now, TestContext.Current.CancellationToken);
        var replayedAgain = await sut.ReplayBeforeAsync(Billing, 3, SubscriptionParkedNumber.Revision, Now, TestContext.Current.CancellationToken);
        var outbox = await sut.ListOutboxAsync(Billing, SubscriptionParkedNumber.Revision, null, 10, TestContext.Current.CancellationToken);
        var outboxAfterOne = await sut.ListOutboxAsync(Billing, SubscriptionParkedNumber.Revision, 1, 10, TestContext.Current.CancellationToken);
        var skippedOne = await sut.SkipAsync(Billing, 4, null, SubscriptionParkedNumber.Revision, TestContext.Current.CancellationToken);
        var skippedMissing = await sut.SkipAsync(Billing, 4, null, SubscriptionParkedNumber.Revision, TestContext.Current.CancellationToken);
        var skippedBefore = await sut.SkipAsync(Billing, null, 3, SubscriptionParkedNumber.Revision, TestContext.Current.CancellationToken);
        var left = await sut.ListParkedAsync(Billing, SubscriptionParkedNumber.Revision, null, 10, TestContext.Current.CancellationToken);
        var skippedAll = await sut.SkipAsync(Billing, null, null, SubscriptionParkedNumber.Revision, TestContext.Current.CancellationToken);
        var none = await sut.ListParkedAsync(Billing, SubscriptionParkedNumber.Revision, null, 10, TestContext.Current.CancellationToken);

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
        (await sut.ListOutboxAsync(Billing, SubscriptionParkedNumber.Revision, null, 10, TestContext.Current.CancellationToken)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task UpdateSettings_ShouldReplaceTheGroupsSettingsAndLeaveItsCheckpointAndSayWhenThereIsNoGroup()
    {
        // Arrange
        var sut = Store();
        var created = GroupSettings.Default with { Start = StreamPosition.From(3), Numbering = Numbering.Ordinal };
        var changed = created with { MessageTimeout = TimeSpan.FromSeconds(7), MaxRetryCount = 4, CheckpointUpperBound = 50, CheckpointAfter = TimeSpan.FromSeconds(1), CheckpointLowerBound = 5, BufferSize = 20, MaxSubscriberCount = 1 };
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", created, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await sut.SaveCheckpointAsync(Billing, 41, TestContext.Current.CancellationToken);

        // Act
        var updated = await sut.UpdateSettingsAsync(Billing, changed, TestContext.Current.CancellationToken);
        var missing = await sut.UpdateSettingsAsync(Guid.NewGuid(), changed, TestContext.Current.CancellationToken);
        var read = await sut.GetAsync("orders-1", "billing", TestContext.Current.CancellationToken);

        // Assert
        updated.ShouldBeTrue();
        missing.ShouldBeFalse();
        read.ShouldNotBeNull().Settings.ShouldBe(changed);
        read.Checkpoint.ShouldBe(41);
    }

    [Fact]
    public async Task Due_ShouldHoldBackWhatIsNotDueYet()
    {
        // Arrange: a message put on the outbox for later.
        var sut = Store();
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 10, 10, null, Guid.NewGuid(), "a", 1, Now), TestContext.Current.CancellationToken);
        await sut.ReplayAsync(Billing, null, SubscriptionParkedNumber.Position, Now.AddMinutes(5), TestContext.Current.CancellationToken);

        // Act
        var now = await sut.DueAsync(Billing, Now, TestContext.Current.CancellationToken);
        var later = await sut.DueAsync(Billing, Now.AddMinutes(5), TestContext.Current.CancellationToken);

        // Assert
        now.ShouldBeEmpty();
        later.ShouldHaveSingleItem().Position.ShouldBe(10);
    }

    [Fact]
    public async Task Parked_ShouldBeAddressableByWhicheverNumberTheGroupSpeaks()
    {
        // Arrange: one event at position 40, revision 2 in its stream, ordinal 7 in its category.
        var sut = Store();
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 40, 2, 7, Guid.NewGuid(), "poison", 1, Now), TestContext.Current.CancellationToken);

        // Act
        var byWrongNumber = await sut.ReplayAsync(Billing, 40, SubscriptionParkedNumber.Ordinal, Now, TestContext.Current.CancellationToken);
        var byOrdinal = await sut.ReplayAsync(Billing, 7, SubscriptionParkedNumber.Ordinal, Now, TestContext.Current.CancellationToken);
        var due = await sut.DueAsync(Billing, Now, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 40, 2, 7, Guid.NewGuid(), "poison again", 2, Now), TestContext.Current.CancellationToken);
        var backOnParked = await sut.DueAsync(Billing, Now, TestContext.Current.CancellationToken);
        var byRevision = await sut.ReplayAsync(Billing, 2, SubscriptionParkedNumber.Revision, Now, TestContext.Current.CancellationToken);

        // Assert
        byWrongNumber.ShouldBe(0);
        byOrdinal.ShouldBe(1);
        due.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            message => message.Position.ShouldBe(40),
            message => message.Revision.ShouldBe(2),
            message => message.Ordinal.ShouldBe(7));
        backOnParked.ShouldBeEmpty("parking a message on the outbox moves it back");
        byRevision.ShouldBe(1);
    }

    [Fact]
    public async Task Parked_WhenParkedAgain_ShouldKeepOneRowWithTheLatestAttempt()
    {
        // Arrange: a replayed message that fails again is parked under the same key.
        var sut = Store();
        var id = Guid.NewGuid();
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 10, 10, null, id, "first", 1, Now), TestContext.Current.CancellationToken);

        // Act
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 10, 10, null, id, "second", 3, Now.AddMinutes(1)), TestContext.Current.CancellationToken);
        await sut.ReplayAsync(Billing, null, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var due = await sut.DueAsync(Billing, Now, TestContext.Current.CancellationToken);

        // Assert
        var message = due.ShouldHaveSingleItem();
        message.Reason.ShouldBe("second");
        message.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task Leases_ShouldBeGrantedWhenFreeRenewedByTheOwnerRefusedToOthersAndTakenOverOnceExpired()
    {
        // Arrange
        var sut = Store();

        // Act
        var first = await sut.AcquireLeaseAsync("group:x", "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var renewed = await sut.AcquireLeaseAsync("group:x", "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var refused = await sut.AcquireLeaseAsync("group:x", "two", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(31));
        var takenOver = await sut.AcquireLeaseAsync("group:x", "two", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await sut.ReleaseLeaseAsync("group:x", "one", TestContext.Current.CancellationToken);
        var stillTwo = await sut.AcquireLeaseAsync("group:x", "three", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await sut.ReleaseLeaseAsync("group:x", "two", TestContext.Current.CancellationToken);
        var free = await sut.AcquireLeaseAsync("group:x", "three", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        first.ShouldBeNull();
        renewed.ShouldBeNull();
        refused!.Owner.ShouldBe("one");
        takenOver.ShouldBeNull();
        stillTwo!.Owner.ShouldBe("two");
        free.ShouldBeNull();
    }

    [Fact]
    public async Task Leases_WhenTheRowChangesUnderTheWrite_ShouldReportWhoWon()
    {
        // Arrange: the lease is free when read, and taken by another instance before the write
        // lands; the concurrency tokens make the write fail, and the loser learns who holds it.
        var sut = Store();
        var rival = new SubscriptionGroupStore(_contexts, "tenant", _time);
        await sut.AcquireLeaseAsync("group:x", "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(31));
        _contexts.BeforeSave = async () =>
        {
            _contexts.BeforeSave = null;
            await rival.AcquireLeaseAsync("group:x", "two", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        };

        // Act
        var lost = await sut.AcquireLeaseAsync("group:x", "three", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        lost!.Owner.ShouldBe("two");
    }

    [Fact]
    public async Task Delete_WhenTheConsumerTakesARowOffTheOutboxUnderIt_ShouldReadAgainAndStillDeleteTheGroup()
    {
        // Arrange: one message on the outbox when the delete reads it, delivered and taken off
        // by the group's consumer before the delete writes.
        var sut = Store();
        var consumer = new SubscriptionGroupStore(_contexts, "tenant", _time);
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 40, 40, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ReplayAsync(Billing, null, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        _contexts.BeforeSave = async () =>
        {
            _contexts.BeforeSave = null;
            await consumer.DequeueAsync(Billing, 40, TestContext.Current.CancellationToken);
        };

        // Act
        var deleted = await sut.DeleteAsync("orders-1", "billing", TestContext.Current.CancellationToken);

        // Assert
        deleted.ShouldBeTrue();
        (await sut.GetAsync("orders-1", "billing", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await sut.DueAsync(Billing, Now, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Describe_ShouldGiveTheGroupWithItsCountsAndWhoHoldsItsLeaseWhileTheLeaseLasts()
    {
        // Arrange: two parked, one of them replayed onto the outbox, and a lease that lasts thirty seconds.
        var sut = Store();
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await sut.SaveCheckpointAsync(Billing, 41, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 40, 40, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 42, 42, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ReplayAsync(Billing, 42, SubscriptionParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        await sut.AcquireLeaseAsync(SubscriptionGroupRegistry.GetLeaseName(Billing), "one", new Uri("http://one:5000"), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Act
        var held = await sut.DescribeAsync("orders-1", "billing", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(31));
        var lapsed = await sut.DescribeAsync("orders-1", "billing", TestContext.Current.CancellationToken);
        var missing = await sut.DescribeAsync("orders-1", "nobody", TestContext.Current.CancellationToken);

        // Assert
        var summary = held.ShouldNotBeNull();
        summary.Definition.Id.ShouldBe(Billing);
        summary.Definition.Checkpoint.ShouldBe(41);
        summary.CreatedAt.ShouldBe(Now);
        summary.ParkedCount.ShouldBe(1);
        summary.OutboxCount.ShouldBe(1);
        summary.Holder.ShouldBe(new LeaseHolder("one", new Uri("http://one:5000")));
        lapsed.ShouldNotBeNull().Holder.ShouldBeNull();
        missing.ShouldBeNull();
    }

    [Fact]
    public async Task SaveLive_ShouldBeReadBackWhileTheLeaseIsHeldAndSayNothingOnceItLapsesOrIsCleared()
    {
        // Arrange: a running group writes how it stands, under a lease that lasts thirty seconds.
        var sut = Store();
        var live = new SubscriptionGroupLive(Now.AddMinutes(-5), 4, 1, 10, 41, Now.AddSeconds(-20), "ipv4:10.0.0.7:51234", Now, 2, [new SubscriptionConsumerLive(Now.AddMinutes(-5), "ipv4:10.0.0.7:51234", 5, 3), new SubscriptionConsumerLive(Now.AddMinutes(-4), "ipv4:10.0.0.8:51234", 5, 1)]);
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await sut.AcquireLeaseAsync(SubscriptionGroupRegistry.GetLeaseName(Billing), "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Act
        await sut.SaveLiveAsync(Billing, live, TestContext.Current.CancellationToken);
        var described = await sut.DescribeAsync("orders-1", "billing", TestContext.Current.CancellationToken);
        var listed = await sut.ListAsync("orders-1", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(31));
        var lapsed = await sut.DescribeAsync("orders-1", "billing", TestContext.Current.CancellationToken);
        await sut.AcquireLeaseAsync(SubscriptionGroupRegistry.GetLeaseName(Billing), "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await sut.SaveLiveAsync(Billing, null, TestContext.Current.CancellationToken);
        var cleared = await sut.DescribeAsync("orders-1", "billing", TestContext.Current.CancellationToken);
        await sut.SaveLiveAsync(Guid.NewGuid(), live, TestContext.Current.CancellationToken);

        // Assert: the row keeps the count and the totals, not each consumer; without a holder the
        // columns are leftovers; cleared, there is nothing to read.
        var stored = live with { Consumers = [] };
        described.ShouldNotBeNull().Live.ShouldBe(stored);
        listed.ShouldHaveSingleItem().Live.ShouldBe(stored);
        lapsed.ShouldNotBeNull().Live.ShouldBeNull();
        cleared.ShouldNotBeNull().Holder.ShouldNotBeNull();
        cleared.Live.ShouldBeNull();
    }

    [Fact]
    public async Task List_ShouldGiveEveryGroupOrThoseOfOneStreamInOrderEachWithItsOwnCountsAndHolder()
    {
        // Arrange: three groups over two streams; one has parked messages, another a lease.
        var sut = Store();
        var shipping = Guid.CreateVersion7();
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-2", "billing", GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "shipping", GroupSettings.Default, -1) { Id = shipping }, TestContext.Current.CancellationToken);
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 40, 40, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 42, 42, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.AcquireLeaseAsync(SubscriptionGroupRegistry.GetLeaseName(shipping), "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await sut.AcquireLeaseAsync("ordinals", "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Act
        var all = await sut.ListAsync(null, TestContext.Current.CancellationToken);
        var ofOne = await sut.ListAsync("orders-1", TestContext.Current.CancellationToken);
        var ofNone = await sut.ListAsync("orders-9", TestContext.Current.CancellationToken);

        // Assert
        all.Select(summary => summary.Definition.Stream + "/" + summary.Definition.Group).ShouldBe(["orders-1/billing", "orders-1/shipping", "orders-2/billing"]);
        ofOne.Select(summary => summary.Definition.Group).ShouldBe(["billing", "shipping"]);
        ofOne[0].ParkedCount.ShouldBe(2);
        ofOne[0].Holder.ShouldBeNull();
        ofOne[1].ParkedCount.ShouldBe(0);
        ofOne[1].Holder.ShouldBe(new LeaseHolder("one", null));
        all[2].OutboxCount.ShouldBe(0);
        ofNone.ShouldBeEmpty();
    }

    private SubscriptionGroupStore Store() => new(_contexts, "tenant", _time);

    /// <summary>
    /// Contexts over one in-memory database per test, with a hook that runs just before a save so
    /// a test can slip a rival write in between a read and its write.
    /// </summary>
    private sealed class InMemoryContexts : IDbContextFactory<NightingaleDbContext>
    {
        private readonly DbContextOptions<NightingaleDbContext> _options;

        public InMemoryContexts()
        {
            _options = new DbContextOptionsBuilder<NightingaleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .AddInterceptors(new BeforeSaveInterceptor(this))
                .Options;
        }

        public Func<Task>? BeforeSave { get; set; }

        public NightingaleDbContext CreateDbContext() => new(_options, new NightingaleSchema(NightingaleSchema.Default));

        private sealed class BeforeSaveInterceptor(InMemoryContexts owner) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (owner.BeforeSave is { } hook)
                {
                    await hook().ConfigureAwait(false);
                }

                return result;
            }
        }
    }
}
