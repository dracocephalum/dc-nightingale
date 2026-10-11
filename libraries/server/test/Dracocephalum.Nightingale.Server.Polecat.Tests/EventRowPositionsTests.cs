using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// The poller's three queries on the in-memory provider: the ceiling of each tenant asked for,
/// the positions after a mark in order and within the window, and the last position appended
/// before a moment. The positions do not overlap across tenants here, because the mirror's key
/// is the store's, the position alone; that they may on the database is the integration suite's.
/// </summary>
public sealed class EventRowPositionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly EventStoreSchema Schema = new("dbo");

    private readonly EventRowPositions _sut;

    public EventRowPositionsTests()
    {
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<EventsDbContext>().UseInMemoryDatabase("events", root).Options;
        _sut = new EventRowPositions(new ContextFactory(options));
        using var context = new Seeding(root);
        context.Set<EventRow>().AddRange(
            Row("a", 1, Now.AddMinutes(-10)),
            Row("a", 2, Now.AddMinutes(-10)),
            Row("a", 4, Now.AddMinutes(-1)),
            Row("a", 5, Now),
            Row("b", 7, Now.AddMinutes(-10)));
        context.SaveChanges();
    }

    [Fact]
    public async Task ReadCeilings_ShouldAnswerTheHighestPositionOfEachTenantAskedForThatHasOne()
    {
        // Act
        var ceilings = await _sut.ReadCeilingsAsync(["a", "b", "c"], TestContext.Current.CancellationToken);

        // Assert
        ceilings.ShouldBe(new Dictionary<string, long> { ["a"] = 5, ["b"] = 7 }, ignoreOrder: true);
    }

    [Fact]
    public async Task ReadFollowing_ShouldAnswerTheTenantsPositionsAfterTheMarkInOrderWithinTheWindow()
    {
        // Act
        var all = await _sut.ReadFollowingAsync("a", 0, 10, TestContext.Current.CancellationToken);
        var windowed = await _sut.ReadFollowingAsync("a", 1, 2, TestContext.Current.CancellationToken);
        var other = await _sut.ReadFollowingAsync("b", 0, 10, TestContext.Current.CancellationToken);

        // Assert
        all.ShouldBe([1L, 2L, 4L, 5L]);
        windowed.ShouldBe([2L, 4L]);
        other.ShouldBe([7L]);
    }

    [Fact]
    public async Task FindLastAppendedBefore_ShouldAnswerTheHighestPositionAfterTheMarkAppendedAtOrBeforeTheMoment()
    {
        // Act
        var old = await _sut.FindLastAppendedBeforeAsync("a", 2, Now.AddMinutes(-1), TestContext.Current.CancellationToken);
        var none = await _sut.FindLastAppendedBeforeAsync("a", 4, Now.AddMinutes(-1), TestContext.Current.CancellationToken);
        var recent = await _sut.FindLastAppendedBeforeAsync("a", 2, Now, TestContext.Current.CancellationToken);

        // Assert
        old.ShouldBe(4);
        none.ShouldBeNull();
        recent.ShouldBe(5);
    }

    private static EventRow Row(string tenant, long position, DateTimeOffset timestamp) =>
        new()
        {
            SeqId = position,
            Id = Guid.NewGuid(),
            StreamId = "orders-1",
            Version = position,
            Data = "{}",
            Type = "order_placed",
            Timestamp = timestamp,
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
