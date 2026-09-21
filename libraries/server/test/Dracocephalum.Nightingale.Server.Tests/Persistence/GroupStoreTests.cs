using Dracocephalum.Nightingale.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Persistence;

/// <summary>
/// The group store's plain reads and writes on the in-memory provider: every operation of the
/// port, the moves between parked and the outbox, and the lease's answers as the concurrency
/// tokens settle them. What the in-memory provider cannot stand in for, a real duplicate key
/// and a real concurrent write, the integration project covers on SQL Server.
/// </summary>
public sealed class GroupStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryContexts _contexts = new();

    [Fact]
    public async Task Groups_ShouldBeCreatedOnceReadBackWithTheirSettingsAndDeletedWithTheirParkedMessagesAndOutbox()
    {
        // Arrange
        var sut = Store();
        var settings = GroupSettings.Default with { Start = StreamPosition.From(3), MaxRetryCount = 4, MessageTimeout = TimeSpan.FromSeconds(7), Numbering = Numbering.Ordinal };

        // Act
        await sut.CreateAsync(new GroupDefinition("orders-1", "billing", settings, -1), TestContext.Current.CancellationToken);
        await Should.ThrowAsync<GroupExistsException>(() => sut.CreateAsync(new GroupDefinition("orders-1", "billing", settings, -1), TestContext.Current.CancellationToken));
        await sut.SaveCheckpointAsync("orders-1", "billing", 41, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage("orders-1", "billing", 40, 40, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage("orders-1", "billing", 42, 42, null, Guid.NewGuid(), "poison", 2, Now), TestContext.Current.CancellationToken);
        await sut.ReplayAsync("orders-1", "billing", 42, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var read = await sut.GetAsync("orders-1", "billing", TestContext.Current.CancellationToken);
        var deleted = await sut.DeleteAsync("orders-1", "billing", TestContext.Current.CancellationToken);
        var again = await sut.DeleteAsync("orders-1", "billing", TestContext.Current.CancellationToken);

        // Assert
        read.ShouldNotBeNull().Settings.ShouldBe(settings);
        read.Checkpoint.ShouldBe(41);
        deleted.ShouldBeTrue();
        again.ShouldBeFalse();
        (await sut.GetAsync("orders-1", "billing", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await sut.DueAsync("orders-1", "billing", Now, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await sut.ReplayAsync("orders-1", "billing", null, ParkedNumber.Position, Now, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Groups_ShouldBeKeyedByStreamAndNameTogether()
    {
        // Arrange
        var sut = Store();
        await sut.CreateAsync(new GroupDefinition("orders-1", "billing", GroupSettings.Default, -1), TestContext.Current.CancellationToken);

        // Act
        await sut.CreateAsync(new GroupDefinition("orders-2", "billing", GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        await sut.CreateAsync(new GroupDefinition("orders-1", "shipping", GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        await sut.SaveCheckpointAsync("orders-1", "billing", 5, TestContext.Current.CancellationToken);

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
        await sut.CreateAsync(new GroupDefinition("orders-1", "billing", GroupSettings.Default, -1), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage("orders-1", "billing", 10, 10, null, Guid.NewGuid(), "a", 1, Now), TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage("orders-1", "billing", 12, 12, null, Guid.NewGuid(), "b", 1, Now.AddSeconds(1)), TestContext.Current.CancellationToken);

        // Act
        var before = await sut.DueAsync("orders-1", "billing", Now, TestContext.Current.CancellationToken);
        var one = await sut.ReplayAsync("orders-1", "billing", 12, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var missing = await sut.ReplayAsync("orders-1", "billing", 99, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var sameAgain = await sut.ReplayAsync("orders-1", "billing", 12, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var afterOne = await sut.DueAsync("orders-1", "billing", Now, TestContext.Current.CancellationToken);
        var rest = await sut.ReplayAsync("orders-1", "billing", null, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var afterAll = await sut.DueAsync("orders-1", "billing", Now, TestContext.Current.CancellationToken);
        await sut.DequeueAsync("orders-1", "billing", 10, TestContext.Current.CancellationToken);
        var afterDequeue = await sut.DueAsync("orders-1", "billing", Now, TestContext.Current.CancellationToken);

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
    public async Task Due_ShouldHoldBackWhatIsNotDueYet()
    {
        // Arrange: a message put on the outbox for later.
        var sut = Store();
        await sut.ParkAsync(new ParkedMessage("orders-1", "billing", 10, 10, null, Guid.NewGuid(), "a", 1, Now), TestContext.Current.CancellationToken);
        await sut.ReplayAsync("orders-1", "billing", null, ParkedNumber.Position, Now.AddMinutes(5), TestContext.Current.CancellationToken);

        // Act
        var now = await sut.DueAsync("orders-1", "billing", Now, TestContext.Current.CancellationToken);
        var later = await sut.DueAsync("orders-1", "billing", Now.AddMinutes(5), TestContext.Current.CancellationToken);

        // Assert
        now.ShouldBeEmpty();
        later.ShouldHaveSingleItem().Position.ShouldBe(10);
    }

    [Fact]
    public async Task Parked_ShouldBeAddressableByWhicheverNumberTheGroupSpeaks()
    {
        // Arrange: one event at position 40, revision 2 in its stream, ordinal 7 in its category.
        var sut = Store();
        await sut.ParkAsync(new ParkedMessage("$ce-orders", "billing", 40, 2, 7, Guid.NewGuid(), "poison", 1, Now), TestContext.Current.CancellationToken);

        // Act
        var byWrongNumber = await sut.ReplayAsync("$ce-orders", "billing", 40, ParkedNumber.Ordinal, Now, TestContext.Current.CancellationToken);
        var byOrdinal = await sut.ReplayAsync("$ce-orders", "billing", 7, ParkedNumber.Ordinal, Now, TestContext.Current.CancellationToken);
        var due = await sut.DueAsync("$ce-orders", "billing", Now, TestContext.Current.CancellationToken);
        await sut.ParkAsync(new ParkedMessage("$ce-orders", "billing", 40, 2, 7, Guid.NewGuid(), "poison again", 2, Now), TestContext.Current.CancellationToken);
        var backOnParked = await sut.DueAsync("$ce-orders", "billing", Now, TestContext.Current.CancellationToken);
        var byRevision = await sut.ReplayAsync("$ce-orders", "billing", 2, ParkedNumber.Revision, Now, TestContext.Current.CancellationToken);

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
        await sut.ParkAsync(new ParkedMessage("orders-1", "billing", 10, 10, null, id, "first", 1, Now), TestContext.Current.CancellationToken);

        // Act
        await sut.ParkAsync(new ParkedMessage("orders-1", "billing", 10, 10, null, id, "second", 3, Now.AddMinutes(1)), TestContext.Current.CancellationToken);
        await sut.ReplayAsync("orders-1", "billing", null, ParkedNumber.Position, Now, TestContext.Current.CancellationToken);
        var due = await sut.DueAsync("orders-1", "billing", Now, TestContext.Current.CancellationToken);

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
        var rival = new GroupStore(_contexts, "tenant", _time);
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

    private GroupStore Store() => new(_contexts, "tenant", _time);

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

        public NightingaleDbContext CreateDbContext() => new(_options, new NightingaleTables("dbo"));

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
