using Dracocephalum.Nightingale.Server.Polecat.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The tails' two queries on the in-memory provider, reading as the transaction horizon's
/// strategy does: the ceiling of each sequence asked for, the whole table's for the shared one,
/// and the positions after a head in order and within the window. The positions do not overlap
/// across tenants here, because the mirror's key is the store's, the position alone; that they may
/// on the database, and the locking reads of the best-effort strategy, are the integration suite's.
/// </summary>
public sealed class SqlServerSequencePositionsTests
{
    private static readonly EventStoreSchema Schema = new("dbo");
    private static readonly SequenceKey A = new("a");
    private static readonly SequenceKey B = new("b");

    private readonly SqlServerSequencePositions _sut;

    public SqlServerSequencePositionsTests()
    {
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<EventsDbContext>().UseInMemoryDatabase("events", root).Options;
        _sut = new SqlServerSequencePositions(new ContextFactory(options), lockingReads: false);
        using var context = new Seeding(root);
        context.Set<EventRow>().AddRange(Row("a", 1), Row("a", 2), Row("a", 4), Row("a", 5), Row("b", 7));
        context.SaveChanges();
    }

    [Fact]
    public async Task ReadCeilings_ShouldAnswerTheHighestPositionOfEachSequenceAskedForThatHasOne()
    {
        // Act
        var ceilings = await _sut.ReadCeilingsAsync([A, B, new SequenceKey("c"), SequenceKey.Shared], TestContext.Current.CancellationToken);

        // Assert: the shared sequence is every tenant's events.
        ceilings.ShouldBe(new Dictionary<SequenceKey, long> { [A] = 5, [B] = 7, [SequenceKey.Shared] = 7 }, ignoreOrder: true);
    }

    [Fact]
    public async Task ReadFollowing_ShouldAnswerTheSequencesPositionsAfterTheHeadInOrderWithinTheWindow()
    {
        // Act
        var all = await _sut.ReadFollowingAsync(A, 0, 10, TestContext.Current.CancellationToken);
        var windowed = await _sut.ReadFollowingAsync(A, 1, 2, TestContext.Current.CancellationToken);
        var other = await _sut.ReadFollowingAsync(B, 0, 10, TestContext.Current.CancellationToken);
        var shared = await _sut.ReadFollowingAsync(SequenceKey.Shared, 4, 10, TestContext.Current.CancellationToken);

        // Assert
        all.ShouldBe([1L, 2L, 4L, 5L]);
        windowed.ShouldBe([2L, 4L]);
        other.ShouldBe([7L]);
        shared.ShouldBe([5L, 7L]);
    }

    private static EventRow Row(string tenant, long position) =>
        new()
        {
            SeqId = position,
            Id = Guid.NewGuid(),
            StreamId = "orders-1",
            Version = position,
            Data = "{}",
            Type = "order_placed",
            Timestamp = DateTimeOffset.UnixEpoch,
            TenantId = tenant,
            Headers = null,
            IsArchived = false,
            Category = "orders",
        };

    private sealed class ContextFactory(DbContextOptions<EventsDbContext> options) : IDbContextFactory<EventsDbContext>
    {
        public EventsDbContext CreateDbContext() => new(options, Schema);
    }

    /// <summary>Writes the rows the mirror reads, through a context of the test's own over the same in-memory root.</summary>
    private sealed class Seeding(InMemoryDatabaseRoot root) : DbContext(new DbContextOptionsBuilder<Seeding>().UseInMemoryDatabase("events", root).Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<EventRow>().HasKey(row => row.SeqId);
    }
}
