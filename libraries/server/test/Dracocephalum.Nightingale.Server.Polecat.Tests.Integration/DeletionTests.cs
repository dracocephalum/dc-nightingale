using System.Text;

using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// Delete archives a stream, tombstone removes it; what each does to reads, to <c>$all</c> and the
/// virtual streams, and to a later append, against the real store.
/// </summary>
[Collection(SharedSqlServer.Name)]
[Trait("Category", "Integration")]
public sealed class DeletionTests(SqlServerTestDatabase database)
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"orderId\":1}");

    [Fact]
    public async Task Delete_ShouldHideTheStreamFromEveryReadAndRefuseAppends()
    {
        // Arrange
        var (stream, category) = Names();
        var sut = database.Store;
        var appended = await sut.AppendAsync(stream, StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);

        // Act
        await sut.DeleteAsync(stream, StreamState.StreamRevision(1), TestContext.Current.CancellationToken);

        // Assert
        await Should.ThrowAsync<StreamDeletedException>(() => sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<StreamDeletedException>(() => sut.AppendAsync(stream, StreamState.Any, [Event()], TestContext.Current.CancellationToken));
        (await sut.ReadAllAsync(Direction.Forwards, appended.Position - 1, appended.Position, 10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await sut.VirtualHeadAsync(new VirtualStreamName(VirtualStreamKind.Category, category), appended.Position, TestContext.Current.CancellationToken)).ShouldBeNull();
        await Should.ThrowAsync<StreamDeletedException>(() => sut.DeleteAsync(stream, StreamState.Any, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_WhenTheExpectedRevisionIsStale_ShouldConflictAndLeaveTheStreamAlone()
    {
        // Arrange
        var (stream, _) = Names();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Event(), Event()], TestContext.Current.CancellationToken);

        // Act
        var conflict = await Should.ThrowAsync<RevisionConflictException>(() => sut.DeleteAsync(stream, StreamState.StreamRevision(0), TestContext.Current.CancellationToken));
        var noStream = await Should.ThrowAsync<RevisionConflictException>(() => sut.DeleteAsync(stream, StreamState.NoStream, TestContext.Current.CancellationToken));

        // Assert
        conflict.ActualRevision.ShouldBe(1);
        noStream.ActualRevision.ShouldBe(1);
        (await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldNotBeNull().Events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Delete_WhenTheStreamDoesNotExist_ShouldFailAsNotFound()
    {
        // Arrange
        var (stream, _) = Names();

        // Act & Assert
        await Should.ThrowAsync<StreamNotFoundException>(() => database.Store.DeleteAsync(stream, StreamState.Any, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<StreamNotFoundException>(() => database.Store.TombstoneAsync(stream, StreamState.Any, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tombstone_ShouldRemoveTheStreamAndFreeItsName()
    {
        // Arrange: a deleted stream can be tombstoned too.
        var (stream, _) = Names();
        var sut = database.Store;
        await sut.AppendAsync(stream, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        await sut.DeleteAsync(stream, StreamState.Any, TestContext.Current.CancellationToken);

        // Act
        await sut.TombstoneAsync(stream, StreamState.StreamRevision(0), TestContext.Current.CancellationToken);

        // Assert
        (await sut.ReadAsync(stream, Direction.Forwards, null, 10, TestContext.Current.CancellationToken)).ShouldBeNull();
        var revived = await sut.AppendAsync(stream, StreamState.NoStream, [Event()], TestContext.Current.CancellationToken);
        revived.Revision.ShouldBe(0);
    }

    private static (string Stream, string Category) Names()
    {
        var category = "del" + Guid.NewGuid().ToString("N")[..8];
        return (category + "-1", category);
    }

    private static EventData Event() => new(Guid.NewGuid(), "order_placed", Body);
}
