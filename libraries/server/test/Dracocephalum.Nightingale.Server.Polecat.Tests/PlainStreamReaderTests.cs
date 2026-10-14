using Dracocephalum.Nightingale.Server.Polecat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests;

/// <summary>
/// A plain stream's read with its metadata applied, on the in-memory provider: where a read starts
/// and stops under <c>$maxCount</c> and <c>$tb</c>, what <c>$maxAge</c> leaves out, and that a
/// stream it hides entirely still exists. The stream has six events, revisions 0 to 5, the first
/// four a day old and the last two a minute old; the same name in another tenant and a deleted
/// stream sit beside it. The two statements' shape, the probe in the first and the database's
/// clock in the second, is read from the SQL the provider for SQL Server writes, which needs no
/// connection; that the database seeks is the integration suite's to say.
/// </summary>
public sealed class PlainStreamReaderTests
{
    private const string Tenant = "tenant-a";
    private const string Stream = "orders-1";
    private const string Metastream = "$$orders-1";

    private static readonly EventStoreSchema Schema = new("dbo");

    private readonly InMemoryDatabaseRoot _root = new();
    private readonly PlainStreamReader _sut;
    private readonly DateTimeOffset _now = TimeProvider.System.GetUtcNow();

    public PlainStreamReaderTests()
    {
        var options = new DbContextOptionsBuilder<EventsDbContext>().UseInMemoryDatabase("events", _root).Options;
        _sut = new PlainStreamReader(() => new EventsDbContext(options, Schema), Tenant, NullLogger.Instance);
        using var context = new Seeding(_root);
        for (var revision = 0; revision < 6; revision++)
        {
            context.Set<EventRow>().Add(Row(revision + 1, Stream, revision + 1, revision < 4 ? _now.AddDays(-1) : _now.AddMinutes(-1)));
        }

        context.Set<EventRow>().AddRange(
            Row(7, Stream, 1, _now, tenant: "tenant-b"),
            Row(8, "orders-9", 1, _now, archived: true));
        context.Set<StreamRow>().AddRange(
            new StreamRow { TenantId = Tenant, Id = Stream, Version = 6 },
            new StreamRow { TenantId = "tenant-b", Id = Stream, Version = 1 },
            new StreamRow { TenantId = Tenant, Id = "orders-9", Version = 1, IsArchived = true });
        context.SaveChanges();
    }

    [Fact]
    public async Task ReadAsync_WithoutMetadata_ShouldReadTheStreamAsStoredInEitherDirection()
    {
        // Act
        var forwards = await _sut.ReadAsync(Stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var backwards = await _sut.ReadAsync(Stream, Direction.Backwards, null, 4, TestContext.Current.CancellationToken);

        // Assert: the other tenant's event of the same name is not this stream's.
        forwards.ShouldNotBeNull().Head.ShouldBe(new StreamHead(0, 5));
        forwards.Events.Select(record => record.Revision).ShouldBe([0, 1, 2, 3, 4, 5]);
        backwards.ShouldNotBeNull().Events.Select(record => record.Revision).ShouldBe([5, 4, 3, 2]);
    }

    [Fact]
    public async Task ReadAsync_WithMaxCount_ShouldStartAtTheLastEventsItKeepsAndReportThatAsTheFirst()
    {
        // Arrange
        await SetMetadataAsync("""{"$maxCount":2}""");

        // Act
        var forwards = await _sut.ReadAsync(Stream, Direction.Forwards, 0, 10, TestContext.Current.CancellationToken);
        var backwards = await _sut.ReadAsync(Stream, Direction.Backwards, null, 10, TestContext.Current.CancellationToken);
        var below = await _sut.ReadAsync(Stream, Direction.Backwards, 3, 10, TestContext.Current.CancellationToken);

        // Assert
        forwards.ShouldNotBeNull().Head.ShouldBe(new StreamHead(4, 5));
        forwards.Events.Select(record => record.Revision).ShouldBe([4, 5]);
        backwards.ShouldNotBeNull().Events.Select(record => record.Revision).ShouldBe([5, 4]);
        below.ShouldNotBeNull().Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_WithMaxCountAndTruncateBefore_ShouldStartAtTheLaterOfTheTwo()
    {
        // Arrange: five kept by count would start at 1; the truncation at 3 is later.
        await SetMetadataAsync("""{"$maxCount":5,"$tb":3}""");

        // Act
        var page = await _sut.ReadAsync(Stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        page.ShouldNotBeNull().Head.First.ShouldBe(3);
        page.Events.Select(record => record.Revision).ShouldBe([3, 4, 5]);
    }

    [Fact]
    public async Task ReadAsync_WhenMetadataHidesEveryEvent_ShouldReadAnEmptyStreamThatExists()
    {
        // Arrange: the reference reads such a stream as there and empty, never as missing.
        await SetMetadataAsync("""{"$tb":10}""");

        // Act
        var forwards = await _sut.ReadAsync(Stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var backwards = await _sut.ReadAsync(Stream, Direction.Backwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        forwards.ShouldNotBeNull().Head.ShouldBe(new StreamHead(10, 5));
        forwards.Events.ShouldBeEmpty();
        backwards.ShouldNotBeNull().Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_WithMaxAge_ShouldLeaveOutOlderEventsBeforeCountingThePage()
    {
        // Arrange: an hour keeps the two events a minute old.
        await SetMetadataAsync("""{"$maxAge":3600}""");

        // Act
        var forwards = await _sut.ReadAsync(Stream, Direction.Forwards, 0, 1, TestContext.Current.CancellationToken);
        var backwards = await _sut.ReadAsync(Stream, Direction.Backwards, null, 10, TestContext.Current.CancellationToken);

        // Assert: a page of one is the first event young enough, not an empty page at revision 0.
        forwards.ShouldNotBeNull().Events.ShouldHaveSingleItem().Revision.ShouldBe(4);
        forwards.Head.ShouldBe(new StreamHead(0, 5));
        backwards.ShouldNotBeNull().Events.Select(record => record.Revision).ShouldBe([5, 4]);
    }

    [Fact]
    public async Task ReadAsync_ShouldApplyTheLastMetadataOnly()
    {
        // Arrange: a later set replaces the metadata as a whole, here with none of the settings.
        await SetMetadataAsync("""{"$maxCount":1}""", """{"note":"cleared"}""");

        // Act
        var page = await _sut.ReadAsync(Stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        page.ShouldNotBeNull().Events.Count.ShouldBe(6);
    }

    [Fact]
    public async Task ReadAsync_WhenTheMetadataDoesNotRead_ShouldReadTheStreamAsIfItHadNone()
    {
        // Arrange: only a store written some other way holds this; the reference reads it as none.
        await SetMetadataAsync("""{"$maxCount":"two"}""");

        // Act
        var page = await _sut.ReadAsync(Stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        page.ShouldNotBeNull().Events.Count.ShouldBe(6);
    }

    [Fact]
    public async Task ReadAsync_WhenAMetadataStream_ShouldReadItsWholeHistoryAsStored()
    {
        // Arrange
        await SetMetadataAsync("""{"$maxCount":1}""", """{"$maxCount":2}""");

        // Act
        var page = await _sut.ReadAsync(Metastream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        page.ShouldNotBeNull().Head.ShouldBe(new StreamHead(0, 1));
        page.Events.Select(record => record.Type).ShouldBe([StreamNames.MetadataEventType, StreamNames.MetadataEventType]);
    }

    [Fact]
    public async Task ReadAsync_WhileAResetMarkIsBeyondTheLastRevision_ShouldReadAsNotFound()
    {
        // Arrange: reset at revision 5, stored as the negative of 6; nothing appended since.
        await SetMetadataAsync("""{"$tb":-6}""");

        // Act
        var forwards = await _sut.ReadAsync(Stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var backwards = await _sut.ReadAsync(Stream, Direction.Backwards, null, 10, TestContext.Current.CancellationToken);
        var stored = await _sut.ReadAsStoredAsync(Stream, 0, 10, TestContext.Current.CancellationToken);

        // Assert: the router still gets what is stored.
        forwards.ShouldBeNull();
        backwards.ShouldBeNull();
        stored.ShouldNotBeNull().Events.Count.ShouldBe(6);
    }

    [Fact]
    public async Task ReadAsync_OnceAnAppendReachesTheResetMark_ShouldReadFromTheMarkOn()
    {
        // Arrange: reset at revision 3, stored as the negative of 4; revisions 4 and 5 came after.
        await SetMetadataAsync("""{"$tb":-4}""");

        // Act
        var page = await _sut.ReadAsync(Stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        page.ShouldNotBeNull().Head.ShouldBe(new StreamHead(4, 5));
        page.Events.Select(record => record.Revision).ShouldBe([4, 5]);
    }

    [Fact]
    public async Task ReadMetadataStateAsync_ShouldTellAResetFromATruncationByTheStreamsLastRevision()
    {
        // Arrange
        await SetMetadataAsync("""{"$maxCount":3}""", """{"$tb":-6}""");

        // Act
        var reset = await _sut.ReadMetadataStateAsync(Stream, TestContext.Current.CancellationToken);
        var none = await _sut.ReadMetadataStateAsync("orders-404", TestContext.Current.CancellationToken);

        // Assert
        reset.ShouldSatisfyAllConditions(
            state => state.StreamLast.ShouldBe(5),
            state => state.MetadataRevision.ShouldBe(1),
            state => state.ContinuesFrom.ShouldBe(6),
            state => state.IsReset.ShouldBeTrue());
        none.ShouldBe(new PlainStreamReader.MetadataState(-1, false, -1, StreamMetadata.Empty));
        none.IsReset.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsStoredAsync_ShouldReadEveryEventWhateverTheMetadataSays()
    {
        // Arrange
        await SetMetadataAsync("""{"$maxCount":1,"$tb":5,"$maxAge":1}""");

        // Act
        var page = await _sut.ReadAsStoredAsync(Stream, 2, 10, TestContext.Current.CancellationToken);

        // Assert
        page.ShouldNotBeNull().Head.ShouldBe(new StreamHead(0, 5));
        page.Events.Select(record => record.Revision).ShouldBe([2, 3, 4, 5]);
    }

    [Fact]
    public async Task ReadAsync_WhenTheStreamIsMissingOrDeleted_ShouldSaySo()
    {
        // Act
        var missing = await _sut.ReadAsync("orders-404", Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert
        missing.ShouldBeNull();
        await Should.ThrowAsync<StreamDeletedException>(() => _sut.ReadAsync("orders-9", Direction.Forwards, null, 10, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<StreamDeletedException>(() => _sut.ReadAsStoredAsync("orders-9", 0, 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Statements_ShouldProbeTheMetadataInTheFirstAndAgeEventsByTheDatabasesClock()
    {
        // Arrange
        using var context = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlServer("Server=example;Database=nightingale;Trusted_Connection=True").Options, Schema);

        // Act
        var first = _sut.FindWithMetadata(context, Stream, Metastream).ToQueryString();
        var aged = PlainStreamReader.FilterByAge(_sut.QueryRows(context, Stream), TimeSpan.FromHours(1)).Select(row => row.SeqId).ToQueryString();
        var unaged = PlainStreamReader.FilterByAge(_sut.QueryRows(context, Stream), null).Select(row => row.SeqId).ToQueryString();

        // Assert: the probe is a TOP 1 inside the statement that finds the stream, and the age is
        // the database's to measure, so no instance's clock is sent.
        first.ShouldContain("[pc_streams]");
        first.ShouldContain("TOP(1)");
        first.ShouldContain("ORDER BY [p0].[version] DESC");
        aged.ShouldContain("SYSUTCDATETIME()");
        aged.ShouldContain("DATEADD(second");
        unaged.ShouldNotContain("[timestamp]");
    }

    private async Task SetMetadataAsync(params string[] bodies)
    {
        await using var context = new Seeding(_root);
        for (var i = 0; i < bodies.Length; i++)
        {
            context.Set<EventRow>().Add(Row(100 + i, Metastream, i + 1, _now, type: StreamNames.MetadataEventType, data: bodies[i]));
        }

        context.Set<StreamRow>().Add(new StreamRow { TenantId = Tenant, Id = Metastream, Version = bodies.Length });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Writes the rows the mirror reads, through a context of the test's own, since the mirror refuses to save.</summary>
    private sealed class Seeding(InMemoryDatabaseRoot root) : DbContext(new DbContextOptionsBuilder<Seeding>().UseInMemoryDatabase("events", root).Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EventRow>().HasKey(row => row.SeqId);
            modelBuilder.Entity<StreamRow>().HasKey(row => new { row.TenantId, row.Id });
        }
    }

    private static EventRow Row(long position, string stream, long version, DateTimeOffset timestamp, string tenant = Tenant, bool archived = false, string type = "order_placed", string? data = null) =>
        new()
        {
            SeqId = position,
            Id = Guid.NewGuid(),
            StreamId = stream,
            Version = version,
            Data = data ?? $"{{\"n\":{position}}}",
            Type = type,
            Timestamp = timestamp,
            TenantId = tenant,
            IsArchived = archived,
            Category = stream.Contains('-', StringComparison.Ordinal) ? stream[..stream.IndexOf('-', StringComparison.Ordinal)] : stream,
        };
}
