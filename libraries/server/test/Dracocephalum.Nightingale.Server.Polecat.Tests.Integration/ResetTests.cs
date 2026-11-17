using System.Text;
using System.Text.Json.Nodes;

using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// A reset against the real store, the reference's soft delete: the stream reads as not found
/// until its next append, which continues its revisions with the older events hidden, while
/// <c>$all</c> and the virtual streams keep them; an append that expects no stream is accepted
/// on the failure path; a set of the metadata brings the stream back in two events, and a second
/// set undoes the truncation; a set at the largest truncation resets, keeping the other settings.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class ResetTests(SqlServerTestDatabase database)
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":1}");

    [Fact]
    public async Task Reset_ShouldHideTheStreamUntilTheNextAppendWhichContinuesItsRevisions()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        var appended = await sut.AppendAsync(stream, StreamState.NoStream, [Event(), Event(), Event()], TestContext.Current.CancellationToken);
        var category = new VirtualStreamName(VirtualStreamKind.Category, Category(stream));

        // Act
        await sut.ResetAsync(stream, StreamState.StreamRevision(2), TestContext.Current.CancellationToken);
        var whileReset = await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var kept = await sut.ReadVirtualAsync(category, Direction.Forwards, 1, appended.Position, 10, TestContext.Current.CancellationToken);
        var recreated = await sut.AppendAsync(stream, StreamState.Any, [Event()], TestContext.Current.CancellationToken);
        var afterwards = await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var metadata = await sut.GetStreamMetadataAsync(stream, TestContext.Current.CancellationToken);

        // Assert: not found while reset, yet the category keeps every event; then back from 3.
        whileReset.ShouldBeNull();
        kept.Count.ShouldBe(3);
        recreated.Revision.ShouldBe(3);
        afterwards.ShouldNotBeNull().Head.ShouldBe(new StreamHead(3, 3));
        afterwards.Events.ShouldHaveSingleItem().Revision.ShouldBe(3);
        metadata.Reset.ShouldBeFalse();
        metadata.Metadata.TruncateBefore.ShouldBe(3);
    }

    [Fact]
    public async Task Reset_ShouldStoreItsMarkAndReportTheResetApartReplacingTheOtherSettings()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 5, Custom = new JsonObject { ["owner"] = "billing" } }, TestContext.Current.CancellationToken);

        // Act
        var reset = await sut.ResetAsync(stream, StreamState.Any, TestContext.Current.CancellationToken);
        var read = await sut.GetStreamMetadataAsync(stream, TestContext.Current.CancellationToken);
        var stored = await sut.ReadAsync(StreamNames.ToMetadataStream(stream), Direction.Backwards, null, 1, TestContext.Current.CancellationToken);

        // Assert: the reference's reset replaces the metadata with its mark alone.
        reset.Revision.ShouldBe(1);
        read.Reset.ShouldBeTrue();
        read.Revision.ShouldBe(1);
        read.Metadata.TruncateBefore.ShouldBe(2);
        read.Metadata.MaxCount.ShouldBeNull();
        read.Metadata.Custom.ShouldBeNull();
        Encoding.UTF8.GetString(stored.ShouldNotBeNull().Events.ShouldHaveSingleItem().Data.Span).ShouldBe("{\"$tb\":-2}");
    }

    [Fact]
    public async Task Append_ExpectingNoStreamOnAResetStream_ShouldBeAcceptedOnceAndContinueItsRevisions()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        await sut.ResetAsync(stream, StreamState.Any, TestContext.Current.CancellationToken);
        var recreation = Event();

        // Act
        var first = await sut.AppendAsync(stream, StreamState.NoStream, [recreation], TestContext.Current.CancellationToken);
        var retried = await sut.AppendAsync(stream, StreamState.NoStream, [recreation], TestContext.Current.CancellationToken);
        var conflict = await Should.ThrowAsync<RevisionConflictException>(() => sut.AppendAsync(stream, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken));

        // Assert: as the reference numbers it, from the revision the stream continues from; the
        // same events again are the same append, and other events find a stream that exists.
        first.Revision.ShouldBe(2);
        retried.ShouldBe(first);
        conflict.ActualRevision.ShouldBe(2);
        (await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events.ShouldHaveSingleItem().Id.ShouldBe(recreation.Id);
    }

    [Fact]
    public async Task SetStreamMetadata_OnAResetStream_ShouldBringItBackInTwoEventsAndASecondSetUndoTheTruncation()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Event(), Event(), Event()], TestContext.Current.CancellationToken);
        await sut.ResetAsync(stream, StreamState.Any, TestContext.Current.CancellationToken);

        // Act: the reference's way back, in two calls.
        var back = await sut.SetStreamMetadataAsync(stream, StreamState.Any, new StreamMetadata { TruncateBefore = 0, MaxCount = 10 }, TestContext.Current.CancellationToken);
        var brought = await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var history = await sut.ReadAsync(StreamNames.ToMetadataStream(stream), Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        await sut.SetStreamMetadataAsync(stream, StreamState.StreamRevision(back.Revision), new StreamMetadata { TruncateBefore = 0 }, TestContext.Current.CancellationToken);
        var undone = await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);

        // Assert: the first set brings the stream back with the reset's truncation, as the
        // reference's un-delete leaves it, in the events the reference writes; the second, on a
        // stream no longer reset, shows every event again.
        back.Revision.ShouldBe(2);
        brought.ShouldNotBeNull().Events.ShouldBeEmpty();
        brought.Head.ShouldBe(new StreamHead(3, 2));
        history.ShouldNotBeNull().Events.Select(record => Encoding.UTF8.GetString(record.Data.Span)).ShouldBe(
            ["{\"$tb\":-3}", "{\"$maxCount\":10,\"$tb\":0}", "{\"$maxCount\":10,\"$tb\":3}"]);
        undone.ShouldNotBeNull().Events.Select(record => record.Revision).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task SetStreamMetadata_WithTheLargestTruncation_ShouldResetKeepingTheOtherSettings()
    {
        // Arrange
        var stream = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);

        // Act
        await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 5, TruncateBefore = long.MaxValue }, TestContext.Current.CancellationToken);
        var read = await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken);
        var metadata = await sut.GetStreamMetadataAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        read.ShouldBeNull();
        metadata.Reset.ShouldBeTrue();
        metadata.Metadata.MaxCount.ShouldBe(5);
        metadata.Metadata.TruncateBefore.ShouldBe(2);
    }

    [Fact]
    public async Task Reset_WithoutEvents_ShouldClearTheMetadataOrFailWhenThereIsNoneEither()
    {
        // Arrange: metadata set before the stream's first event.
        var stream = NewStream();
        var sut = database.Store;
        await sut.SetStreamMetadataAsync(stream, StreamState.NoStream, new StreamMetadata { MaxCount = 5 }, TestContext.Current.CancellationToken);

        // Act
        await sut.ResetAsync(stream, StreamState.NoStream, TestContext.Current.CancellationToken);
        var metadata = await sut.GetStreamMetadataAsync(stream, TestContext.Current.CancellationToken);
        var first = await sut.AppendAsync(stream, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);

        // Assert: nothing to hide, so the reset clears the metadata, and the first append is 0.
        metadata.Revision.ShouldBe(1);
        metadata.Reset.ShouldBeFalse();
        metadata.Metadata.ShouldBe(StreamMetadata.Empty);
        first.Revision.ShouldBe(0);
        await Should.ThrowAsync<StreamNotFoundException>(() => sut.ResetAsync(NewStream(), StreamState.Any, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reset_WhenArchivedOrStale_ShouldRefuseAndWriteNothing()
    {
        // Arrange
        var archived = NewStream();
        var stale = NewStream();
        var sut = database.Store;
        await sut.AppendAsync(archived, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await sut.AppendAsync(stale, StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);
        await sut.ArchiveAsync(archived, StreamState.Any, TestContext.Current.CancellationToken);

        // Act
        await Should.ThrowAsync<StreamDeletedException>(() => sut.ResetAsync(archived, StreamState.Any, TestContext.Current.CancellationToken));
        var conflict = await Should.ThrowAsync<RevisionConflictException>(() => sut.ResetAsync(stale, StreamState.StreamRevision(0), TestContext.Current.CancellationToken));

        // Assert
        conflict.ActualRevision.ShouldBe(1);
        (await sut.GetStreamMetadataAsync(stale, TestContext.Current.CancellationToken)).Exists.ShouldBeFalse();
        (await sut.ReadAsync(stale, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events.Count.ShouldBe(2);
    }

    private static string NewStream() => "reset" + Guid.NewGuid().ToString("N")[..8] + "-1";

    private static string Category(string stream) => stream[..stream.IndexOf('-', StringComparison.Ordinal)];

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Body);
}
