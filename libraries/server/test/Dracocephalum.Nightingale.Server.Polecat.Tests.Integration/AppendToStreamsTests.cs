using System.Text;

using Microsoft.Data.SqlClient;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// The append to several streams against the local SQL Server: one commit, positioned stream by
/// stream; nothing written when any condition fails, with every failed condition reported; checks
/// on streams not written, held to the commit; metadata beside events; and what a single append
/// retries, a reset stream continued and an answer that was lost. Every test uses streams of its
/// own in the shared database.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class AppendToStreamsTests(SqlServerTestDatabase database)
{
    [Fact]
    public async Task AppendToStreams_ShouldWriteEveryStreamInOneCommitStreamByStreamInTheOrderGiven()
    {
        // Arrange: one stream that exists, one that does not.
        var sut = database.Store;
        var (orders, payments) = (NewName("orders"), NewName("payments"));
        await sut.AppendAsync(orders, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Act
        var results = await sut.AppendToStreamsAsync(
            [new StreamAppend.OfEvents(payments, StreamState.NoStream, [Event(), Event()]), new StreamAppend.OfEvents(orders, StreamState.StreamRevision(0), [Event()])],
            [],
            TestContext.Current.CancellationToken);
        var paid = (await sut.ReadAsync(payments, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events;
        var ordered = (await sut.ReadAsync(orders, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events;

        // Assert: the results in the order given, each its stream's last event; the streams one
        // after the other, the first given first. Another append committing at the same moment may
        // take a position between them, so the order is what is held, not the gap.
        results.Select(result => (result.Stream, result.Revision)).ShouldBe([(payments, 1L), (orders, 1L)]);
        results[0].Position.ShouldBe(paid[1].Position);
        results[1].Position.ShouldBe(ordered[1].Position);
        paid[0].Position.ShouldBeLessThan(paid[1].Position);
        paid[1].Position.ShouldBeLessThan(ordered[1].Position);
    }

    [Fact]
    public async Task AppendToStreams_WithAsManyEventsInEachStreamAsOneAppendTakes_ShouldCommitThemAll()
    {
        // Arrange: two streams each as full as one append may be. The store writes each stream of
        // a commit as a command of its own, so the database's limit on parameters binds a
        // stream's events, not the commit's; a version that writes a commit as one command fails
        // here, and the limit must then bind the total.
        var sut = database.Store;
        var (orders, payments) = (NewName("orders"), NewName("payments"));
        List<EventData> Full() => Enumerable.Range(0, PolecatStreamStore.MaxEventsPerAppend).Select(_ => Event()).ToList();

        // Act
        var results = await sut.AppendToStreamsAsync(
            [new StreamAppend.OfEvents(orders, StreamState.NoStream, Full()), new StreamAppend.OfEvents(payments, StreamState.NoStream, Full())],
            [],
            TestContext.Current.CancellationToken);

        // Assert
        results.Select(result => result.Revision).ShouldBe([PolecatStreamStore.MaxEventsPerAppend - 1L, PolecatStreamStore.MaxEventsPerAppend - 1L]);
    }

    [Fact]
    public async Task AppendToStreams_WhenStreamsAreNotInTheirExpectedStates_ShouldWriteNothingAndReportEveryOneInOrder()
    {
        // Arrange: a stream at revision 1 expected at 0, one that exists expected not to, and one
        // that is fine.
        var sut = database.Store;
        var (behind, existing, fine) = (NewName("orders"), NewName("orders"), NewName("orders"));
        await sut.AppendAsync(behind, StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        await sut.AppendAsync(existing, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Act
        var refused = await Should.ThrowAsync<StreamConflictsException>(() => sut.AppendToStreamsAsync(
            [
                new StreamAppend.OfEvents(fine, StreamState.NoStream, [Event()]),
                new StreamAppend.OfEvents(behind, StreamState.StreamRevision(0), [Event()]),
                new StreamAppend.OfEvents(existing, StreamState.NoStream, [Event()]),
            ],
            [],
            TestContext.Current.CancellationToken));

        // Assert: both, in the order given, with the caller's expectation and the stream's revision.
        refused.Conflicts.ShouldBe([new StreamConflict(1, behind, StreamState.StreamRevision(0), 1), new StreamConflict(2, existing, StreamState.NoStream, 0)]);
        (await sut.ReadAsync(fine, Direction.Forwards, null, 1, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task AppendToStreams_WithChecksOnStreamsNotWritten_ShouldWriteOnlyWhenEveryCheckHolds()
    {
        // Arrange: a ledger at revision 0, and a name nobody has used.
        var sut = database.Store;
        var (ledger, unused, written) = (NewName("ledger"), NewName("ledger"), NewName("orders"));
        await sut.AppendAsync(ledger, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Act
        var holding = await sut.AppendToStreamsAsync(
            [new StreamAppend.OfEvents(written, StreamState.NoStream, [Event()])],
            [new StreamCheck(ledger, StreamState.StreamRevision(0)), new StreamCheck(unused, StreamState.NoStream)],
            TestContext.Current.CancellationToken);
        var refused = await Should.ThrowAsync<StreamConflictsException>(() => sut.AppendToStreamsAsync(
            [new StreamAppend.OfEvents(written, StreamState.StreamRevision(0), [Event()])],
            [new StreamCheck(ledger, StreamState.StreamExists), new StreamCheck(unused, StreamState.StreamExists), new StreamCheck(ledger + "-x", StreamState.StreamRevision(4))],
            TestContext.Current.CancellationToken));

        // Assert: the checks are numbered after the appends; the stream written is untouched by
        // the refused append, and neither checked stream was written by either.
        holding.ShouldHaveSingleItem().Revision.ShouldBe(0);
        refused.Conflicts.ShouldBe([new StreamConflict(2, unused, StreamState.StreamExists, -1), new StreamConflict(3, ledger + "-x", StreamState.StreamRevision(4), -1)]);
        (await sut.ReadAsync(written, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events.Count.ShouldBe(1);
        (await sut.ReadAsync(ledger, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AppendToStreams_WithACheck_ShouldHoldTheCheckedStreamUntilItCommits()
    {
        // Arrange: another transaction holds the checked stream's row as an append to it would.
        var sut = database.Store;
        var (ledger, written) = (NewName("ledger"), NewName("orders"));
        await sut.AppendAsync(ledger, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await using var other = new SqlConnection(database.ConnectionString);
        await other.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqlTransaction)await other.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var hold = new SqlCommand("SELECT version FROM dbo.pc_streams WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant AND id = @stream", other, transaction))
        {
            hold.Parameters.AddWithValue("@tenant", JasperFx.StorageConstants.DefaultTenantId);
            hold.Parameters.AddWithValue("@stream", ledger);
            await hold.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        // Act: the append waits for the row; released, it goes through.
        var appending = sut.AppendToStreamsAsync(
            [new StreamAppend.OfEvents(written, StreamState.NoStream, [Event()])],
            [new StreamCheck(ledger, StreamState.StreamRevision(0))],
            TestContext.Current.CancellationToken);
        var waited = await Task.WhenAny(appending, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)) != appending;
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        var results = await appending.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert: the check took the row's lock, as an append to the checked stream does, so no
        // such append can commit between the check and this append's commit.
        waited.ShouldBeTrue("the check waits for the lock on the checked stream's row");
        results.ShouldHaveSingleItem().Stream.ShouldBe(written);
    }

    [Fact]
    public async Task AppendToStreams_WithAStreamsMetadataBesideItsEvents_ShouldWriteBothAndReportTheMetadataStream()
    {
        // Arrange
        var sut = database.Store;
        var orders = NewName("orders");

        // Act: the metadata keeps two events, given before the three events it applies to.
        var results = await sut.AppendToStreamsAsync(
            [new StreamAppend.OfMetadata(orders, StreamState.Any, new StreamMetadata { MaxCount = 2 }), new StreamAppend.OfEvents(orders, StreamState.NoStream, [Event(), Event(), Event()])],
            [],
            TestContext.Current.CancellationToken);
        var read = (await sut.ReadAsync(orders, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        var metadata = await sut.GetStreamMetadataAsync(orders, TestContext.Current.CancellationToken);

        // Assert
        results.Select(result => (result.Stream, result.Revision)).ShouldBe([("$$" + orders, 0L), (orders, 2L)]);
        metadata.Metadata.MaxCount.ShouldBe(2);
        read.Events.Select(e => e.Revision).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task AppendToStreams_ToAResetStreamExpectingNoStream_ShouldContinueItsRevisions()
    {
        // Arrange: a stream reset at revision 1, which continues from 2.
        var sut = database.Store;
        var (orders, payments) = (NewName("orders"), NewName("payments"));
        await sut.AppendAsync(orders, StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        await sut.ResetAsync(orders, StreamState.Any, TestContext.Current.CancellationToken);

        // Act
        var results = await sut.AppendToStreamsAsync(
            [new StreamAppend.OfEvents(payments, StreamState.NoStream, [Event()]), new StreamAppend.OfEvents(orders, StreamState.NoStream, [Event()])],
            [],
            TestContext.Current.CancellationToken);
        var read = (await sut.ReadAsync(orders, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull();

        // Assert: as a single append to it would, the stream comes back from 2.
        results.Select(result => (result.Stream, result.Revision)).ShouldBe([(payments, 0L), (orders, 2L)]);
        read.Events.ShouldHaveSingleItem().Revision.ShouldBe(2);
    }

    [Fact]
    public async Task AppendToStreams_WhenTheSameAppendIsSentAgain_ShouldAnswerWithWhatTheFirstWrote()
    {
        // Arrange: an answer lost on the way, so the caller sends the same events again.
        var sut = database.Store;
        var (orders, payments) = (NewName("orders"), NewName("payments"));
        await sut.AppendAsync(orders, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        StreamAppend[] appends = [new StreamAppend.OfEvents(orders, StreamState.StreamRevision(0), [Event(), Event()]), new StreamAppend.OfEvents(payments, StreamState.NoStream, [Event()])];
        var first = await sut.AppendToStreamsAsync(appends, [], TestContext.Current.CancellationToken);

        // Act
        var again = await sut.AppendToStreamsAsync(appends, [], TestContext.Current.CancellationToken);

        // Assert: the same results, and the events once.
        again.ShouldBe(first);
        (await sut.ReadAsync(orders, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events.Count.ShouldBe(3);
    }

    [Fact]
    public async Task AppendToStreams_WhenAStreamWrittenIsArchived_ShouldRefuseAndWriteNothing()
    {
        // Arrange
        var sut = database.Store;
        var (archived, fine) = (NewName("orders"), NewName("orders"));
        await sut.AppendAsync(archived, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await sut.ArchiveAsync(archived, StreamState.Any, TestContext.Current.CancellationToken);

        // Act
        var refused = await Should.ThrowAsync<StreamDeletedException>(() => sut.AppendToStreamsAsync(
            [new StreamAppend.OfEvents(fine, StreamState.NoStream, [Event()]), new StreamAppend.OfEvents(archived, StreamState.Any, [Event()])],
            [],
            TestContext.Current.CancellationToken));

        // Assert
        refused.Stream.ShouldBe(archived);
        (await sut.ReadAsync(fine, Direction.Forwards, null, 1, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task AppendToStreams_WithBinaryDataInSeveralStreams_ShouldKeepEachEventsOwnBytes()
    {
        // Arrange
        var sut = database.Store;
        var (orders, payments) = (NewName("orders"), NewName("payments"));

        // Act
        await sut.AppendToStreamsAsync(
            [new StreamAppend.OfEvents(orders, StreamState.NoStream, [Event() with { BinaryData = new byte[] { 1 } }]), new StreamAppend.OfEvents(payments, StreamState.NoStream, [Event(), Event() with { BinaryData = new byte[] { 2, 2 } }])],
            [],
            TestContext.Current.CancellationToken);
        var ordered = (await sut.ReadAsync(orders, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events;
        var paid = (await sut.ReadAsync(payments, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events;

        // Assert
        ordered.Single().BinaryData.ToArray().ShouldBe([1]);
        paid.Select(e => e.BinaryData.ToArray()).ShouldBe([[], [2, 2]]);
    }

    [Fact]
    public async Task AppendToStreams_WhenTheRequestBreaksARule_ShouldRefuseItBeforeWritingAnything()
    {
        // Arrange
        var sut = database.Store;
        var orders = NewName("orders");

        // Act
        var refused = await Should.ThrowAsync<ArgumentException>(() => sut.AppendToStreamsAsync(
            [new StreamAppend.OfEvents(orders, StreamState.NoStream, [Event()]), new StreamAppend.OfEvents(orders, StreamState.Any, [Event()])],
            [],
            TestContext.Current.CancellationToken));

        // Assert
        refused.Message.ShouldContain("writes " + orders + " again");
        (await sut.ReadAsync(orders, Direction.Forwards, null, 1, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    private static string NewName(string category) => category + "-" + Guid.NewGuid().ToString("N");

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Encoding.UTF8.GetBytes("{\"orderId\":1}"));
}
