using Dracocephalum.Nightingale.Server.Data;
using Dracocephalum.Nightingale.Server.Sequencing;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Sequencing;

/// <summary>
/// The kept heads over an in-memory table: a row per sequence, the shared one's and each tenant's
/// apart, made on the first write, raised and never lowered, stamped at every write, and refused
/// and taken again when another writer raised it in between.
/// </summary>
public sealed class SequenceHeadRowsTests
{
    private static readonly SequenceKey Tenant = new("7");
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly DbContextOptions<NightingaleDbContext> _options = new DbContextOptionsBuilder<NightingaleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
    private readonly SequenceHeadRows _sut;

    public SequenceHeadRowsTests()
    {
        _sut = new SequenceHeadRows(new Contexts(_options));
    }

    [Fact]
    public async Task Write_ShouldMakeARowPerSequenceRaiseItAndNeverLowerIt()
    {
        // Arrange
        await _sut.WriteAsync(new Dictionary<SequenceKey, long> { [SequenceKey.Shared] = 5, [Tenant] = 2 }, Now, TestContext.Current.CancellationToken);

        // Act
        await _sut.WriteAsync(new Dictionary<SequenceKey, long> { [SequenceKey.Shared] = 3, [Tenant] = 9 }, Now.AddSeconds(1), TestContext.Current.CancellationToken);
        var heads = await _sut.ReadAsync([SequenceKey.Shared, Tenant, new SequenceKey("other")], TestContext.Current.CancellationToken);

        // Assert
        heads.ShouldBe(new Dictionary<SequenceKey, long> { [SequenceKey.Shared] = 5, [Tenant] = 9 }, ignoreOrder: true);
        await using var context = Context();
        (await context.SequenceHeads.Select(row => row.CheckedAt).ToListAsync(TestContext.Current.CancellationToken)).ShouldAllBe(checkedAt => checkedAt == Now.AddSeconds(1));
    }

    [Fact]
    public async Task Read_ShouldReturnOnlyTheSequencesAskedFor()
    {
        // Arrange
        await _sut.WriteAsync(new Dictionary<SequenceKey, long> { [SequenceKey.Shared] = 5, [Tenant] = 2 }, Now, TestContext.Current.CancellationToken);

        // Act
        var shared = await _sut.ReadAsync([SequenceKey.Shared], TestContext.Current.CancellationToken);
        var tenant = await _sut.ReadAsync([Tenant], TestContext.Current.CancellationToken);

        // Assert
        shared.ShouldBe(new Dictionary<SequenceKey, long> { [SequenceKey.Shared] = 5 });
        tenant.ShouldBe(new Dictionary<SequenceKey, long> { [Tenant] = 2 });
    }

    [Fact]
    public async Task Write_WhenAnotherWriterRaisedTheRowSinceItWasRead_ShouldBeRefusedByTheRowsToken()
    {
        // Arrange: a row read by one writer, then raised by another.
        await _sut.WriteAsync(new Dictionary<SequenceKey, long> { [Tenant] = 2 }, Now, TestContext.Current.CancellationToken);
        await using var stale = Context();
        var row = await stale.SequenceHeads.SingleAsync(TestContext.Current.CancellationToken);
        await _sut.WriteAsync(new Dictionary<SequenceKey, long> { [Tenant] = 9 }, Now, TestContext.Current.CancellationToken);

        // Act: the first writer saves what it read, as a write of its own would.
        row.CheckedAt = Now.AddSeconds(1);

        // Assert: the token refuses it, which is what makes a write take the row again.
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(TestContext.Current.CancellationToken));
        (await _sut.ReadAsync([Tenant], TestContext.Current.CancellationToken))[Tenant].ShouldBe(9);
    }

    private NightingaleDbContext Context() => new(_options, new NightingaleSchema("nightingale"));

    private sealed class Contexts(DbContextOptions<NightingaleDbContext> options) : IDbContextFactory<NightingaleDbContext>
    {
        public NightingaleDbContext CreateDbContext() => new(options, new NightingaleSchema("nightingale"));
    }
}
