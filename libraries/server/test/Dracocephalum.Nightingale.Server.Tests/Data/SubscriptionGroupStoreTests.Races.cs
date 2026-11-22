using Dracocephalum.Nightingale.Server.Data;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Data;

/// <content>Who holds a lease, the listings by ordinal, and what the store answers when a row changes between its read and its write.</content>
public sealed partial class SubscriptionGroupStoreTests
{
    [Fact]
    public async Task LeaseHolder_ShouldNameTheHolderWhileTheLeaseLastsAndNoOneAfter()
    {
        // Arrange
        var sut = Store();
        await sut.AcquireLeaseAsync("group:x", "one", new Uri("http://one:5000"), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Act
        var held = await sut.LeaseHolderAsync("group:x", TestContext.Current.CancellationToken);
        var neverTaken = await sut.LeaseHolderAsync("group:y", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(30));
        var lapsed = await sut.LeaseHolderAsync("group:x", TestContext.Current.CancellationToken);

        // Assert
        held.ShouldBe(new LeaseHolder("one", new Uri("http://one:5000")));
        neverTaken.ShouldBeNull();
        lapsed.ShouldBeNull();
    }

    [Fact]
    public async Task ListParkedAndOutbox_ByOrdinal_ShouldGoInOrdinalOrderAPageAtATime()
    {
        // Arrange: three parked messages whose ordinals run the other way from their positions,
        // so the order shows which number was used; all three are then put on the outbox too.
        var sut = Store();
        await sut.CreateAsync(new SubscriptionGroupDefinition("$ce-orders", "billing", GroupSettings.Default with { Numbering = Numbering.Ordinal }, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await HoldAsync(sut, Billing);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 100, 0, 3, Guid.NewGuid(), "a", 1, Now), Owner, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 200, 0, 2, Guid.NewGuid(), "b", 1, Now), Owner, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new SubscriptionParkedMessage(Billing, 300, 0, 1, Guid.NewGuid(), "c", 1, Now), Owner, TestContext.Current.CancellationToken);

        // Act
        var parked = await sut.ListParkedAsync(Billing, SubscriptionParkedNumber.Ordinal, null, 10, TestContext.Current.CancellationToken);
        var parkedAfterOne = await sut.ListParkedAsync(Billing, SubscriptionParkedNumber.Ordinal, 1, 1, TestContext.Current.CancellationToken);
        await sut.ReplayAsync(Billing, null, SubscriptionParkedNumber.Ordinal, Now, TestContext.Current.CancellationToken);
        var outbox = await sut.ListOutboxAsync(Billing, SubscriptionParkedNumber.Ordinal, null, 10, TestContext.Current.CancellationToken);
        var outboxAfterOne = await sut.ListOutboxAsync(Billing, SubscriptionParkedNumber.Ordinal, 1, 1, TestContext.Current.CancellationToken);
        var outboxByPosition = await sut.ListOutboxAsync(Billing, SubscriptionParkedNumber.Position, 100, 10, TestContext.Current.CancellationToken);

        // Assert
        parked.Select(message => message.Ordinal).ShouldBe([1, 2, 3]);
        parkedAfterOne.Select(message => message.Ordinal).ShouldBe([2]);
        outbox.Select(message => message.Ordinal).ShouldBe([1, 2, 3]);
        outboxAfterOne.Select(message => message.Ordinal).ShouldBe([2]);
        outboxByPosition.Select(message => message.Position).ShouldBe([200, 300]);
    }

    [Fact]
    public async Task UpdateSettings_WhenTheGroupIsDeletedUnderTheWrite_ShouldSayThereIsNoGroup()
    {
        // Arrange: the group is there when the update reads it and deleted before the update writes.
        var sut = Store();
        var rival = Store();
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        _contexts.BeforeSave = async () =>
        {
            _contexts.BeforeSave = null;
            await rival.DeleteAsync("orders-1", "billing", TestContext.Current.CancellationToken);
        };

        // Act
        var updated = await sut.UpdateSettingsAsync(Billing, GroupSettings.Default with { MaxRetryCount = 2 }, TestContext.Current.CancellationToken);

        // Assert
        updated.ShouldBeFalse();
        (await sut.GetAsync("orders-1", "billing", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Skip_WhenAReplayTakesOneOfThemUnderTheWrite_ShouldLookAgainAndSkipWhatIsLeft()
    {
        // Arrange: three parked messages when the skip reads them; a replay moves one to the
        // outbox before the skip writes.
        var sut = Store();
        var rival = Store();
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await HoldAsync(sut, Billing);
        foreach (var revision in new long[] { 1, 2, 3 })
        {
            await sut.ParkAsync(new SubscriptionParkedMessage(Billing, revision * 10, revision, null, Guid.NewGuid(), "poison", 1, Now), Owner, TestContext.Current.CancellationToken);
        }

        _contexts.BeforeSave = async () =>
        {
            _contexts.BeforeSave = null;
            await rival.ReplayAsync(Billing, 2, SubscriptionParkedNumber.Revision, Now, TestContext.Current.CancellationToken);
        };

        // Act
        var skipped = await sut.SkipAsync(Billing, null, null, SubscriptionParkedNumber.Revision, TestContext.Current.CancellationToken);

        // Assert: the replayed message is the outbox's now, and the skip took the rest. How many
        // it says it took is left to the integration project: this provider removes row by row
        // and has taken one before it refuses the write, where a database refuses the whole write.
        skipped.ShouldBeInRange(1, 2);
        (await sut.ListParkedAsync(Billing, SubscriptionParkedNumber.Revision, null, 10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await sut.ListOutboxAsync(Billing, SubscriptionParkedNumber.Revision, null, 10, TestContext.Current.CancellationToken)).Select(message => message.Revision).ShouldBe([2]);
    }

    [Fact]
    public async Task FencedWrites_WhenTheLeaseChangesUnderEveryAttempt_ShouldGiveUpAndBeRefused()
    {
        // Arrange: the writer's own renewal lands under each attempt of the write, so the lease
        // is its own every time it is read and never the same when the write commits.
        var sut = Store();
        var renewing = Store();
        await sut.CreateAsync(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1) { Id = Billing }, TestContext.Current.CancellationToken);
        await HoldAsync(sut, Billing);
        var attempts = 0;
        Func<Task>? renewal = null;
        renewal = async () =>
        {
            // The renewal saves too: the hook is taken away while it does, and put back after.
            attempts++;
            _contexts.BeforeSave = null;
            _time.Advance(TimeSpan.FromSeconds(1));
            await HoldAsync(renewing, Billing);
            _contexts.BeforeSave = renewal;
        };
        _contexts.BeforeSave = renewal;

        // Act
        var written = await sut.SaveCheckpointAsync(Billing, 5, Owner, TestContext.Current.CancellationToken);
        _contexts.BeforeSave = null;

        // Assert
        written.ShouldBeFalse();
        attempts.ShouldBe(3);
        (await sut.GetAsync("orders-1", "billing", TestContext.Current.CancellationToken))!.Checkpoint.ShouldBe(-1);
    }

    [Fact]
    public async Task Leases_WhenTheRowChangesUnderTheWriteTwice_ShouldReportWhoHoldsItNow()
    {
        // Arrange: a lapsed lease is taken by a second instance under the first attempt, for a
        // second only; it has lapsed again when looked at again, and a third instance takes it
        // under the second attempt.
        var sut = Store();
        var rival = Store();
        await sut.AcquireLeaseAsync("group:x", "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(31));
        _contexts.BeforeSave = async () =>
        {
            _contexts.BeforeSave = null;
            await rival.AcquireLeaseAsync("group:x", "two", null, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromSeconds(2));
            _contexts.BeforeSave = async () =>
            {
                _contexts.BeforeSave = null;
                await rival.AcquireLeaseAsync("group:x", "three", new Uri("http://three:5000"), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            };
        };

        // Act
        var lost = await sut.AcquireLeaseAsync("group:x", "four", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        lost.ShouldBe(new LeaseHolder("three", new Uri("http://three:5000")));
    }

    [Fact]
    public async Task ReleaseLease_WhenTheLeaseIsTakenOverUnderTheRelease_ShouldLeaveItWithItsNewHolder()
    {
        // Arrange: the lease is the releaser's when read; it lapses and another takes it before the release writes.
        var sut = Store();
        var rival = Store();
        await sut.AcquireLeaseAsync("group:x", "one", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _contexts.BeforeSave = async () =>
        {
            _contexts.BeforeSave = null;
            _time.Advance(TimeSpan.FromSeconds(31));
            await rival.AcquireLeaseAsync("group:x", "two", null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        };

        // Act
        await sut.ReleaseLeaseAsync("group:x", "one", TestContext.Current.CancellationToken);

        // Assert
        (await sut.LeaseHolderAsync("group:x", TestContext.Current.CancellationToken)).ShouldBe(new LeaseHolder("two", null));
    }
}
