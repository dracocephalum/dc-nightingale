using System.Text;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Protocol.V1;
using FakeItEasy;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>
/// The service over fakes of both stores: the unary calls and their errors, and one consumer's
/// conversation from options to acknowledgement.
/// </summary>
public sealed class PersistentSubscriptionsServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Created = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private readonly IStreamStore _store = A.Fake<IStreamStore>(options => options.Strict());
    private readonly IGroupStore _groups = A.Fake<IGroupStore>();
    private readonly FakeTail _tail = new();
    private WebApplication? _app;
    private GrpcChannel? _channel;

    public async ValueTask InitializeAsync()
    {
        A.CallTo(() => _groups.AcquireLeaseAsync(A<string>._, A<string>._, A<TimeSpan>._, A<CancellationToken>._)).Returns((string?)null);
        A.CallTo(() => _groups.ReplayableAsync(A<string>._, A<string>._, A<CancellationToken>._)).Returns(new List<ParkedMessage>());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNightingaleServer();
        builder.Services.AddNightingaleOptions(new TestOptions());
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(_groups);
        builder.Services.AddSingleton<IStoreTail>(_tail);
        _app = builder.Build();
        _app.MapNightingaleServer();
        await _app.StartAsync();
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _app.GetTestServer().CreateHandler() });
    }

    public async ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Create_ShouldStoreTheGroupWithItsSettingsAndNoCheckpoint()
    {
        // Arrange
        GroupDefinition? captured = null;
        A.CallTo(() => _groups.CreateAsync(A<GroupDefinition>._, A<CancellationToken>._)).Invokes(call => captured = call.GetArgument<GroupDefinition>(0));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        await client.CreateAsync(new CreateRequest { Stream = "orders-1", Group = "billing", Settings = new Protocol.V1.GroupSettings { FromStart = new(), MaxRetryCount = 3 } }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var group = captured.ShouldNotBeNull();
        group.Stream.ShouldBe("orders-1");
        group.Group.ShouldBe("billing");
        group.Checkpoint.ShouldBe(-1);
        group.Settings.Start.ShouldBe(StreamPosition.Start);
        group.Settings.MaxRetryCount.ShouldBe(3);
        group.Settings.MessageTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Create_UnderOrdinalNumbering_ShouldStoreTheNumberingWithTheGroup()
    {
        // Arrange
        GroupDefinition? captured = null;
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _groups.CreateAsync(A<GroupDefinition>._, A<CancellationToken>._)).Invokes(call => captured = call.GetArgument<GroupDefinition>(0));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        await client.CreateAsync(new CreateRequest { Stream = "$ce-orders", Group = "billing", Settings = new Protocol.V1.GroupSettings { FromStart = new(), Numbering = Protocol.V1.Numbering.Ordinal } }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        captured.ShouldNotBeNull().Settings.Numbering.ShouldBe(Numbering.Ordinal);
    }

    [Fact]
    public async Task Create_UnderOrdinalNumberingOnAPlainStream_ShouldRejectBeforeTouchingTheStore()
    {
        // Arrange
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.CreateAsync(new CreateRequest { Stream = "orders-1", Group = "billing", Settings = new Protocol.V1.GroupSettings { Numbering = Protocol.V1.Numbering.Ordinal } }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        A.CallTo(() => _groups.CreateAsync(A<GroupDefinition>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Create_UnderOrdinalNumberingWithoutOrdinals_ShouldFailAsNotEnabled()
    {
        // Arrange
        A.CallTo(() => _store.OrdinalsEnabled).Returns(false);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.CreateAsync(new CreateRequest { Stream = "$et-order_placed", Group = "billing", Settings = new Protocol.V1.GroupSettings { Numbering = Protocol.V1.Numbering.Ordinal } }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("ORDINALS_NOT_ENABLED");
        A.CallTo(() => _groups.CreateAsync(A<GroupDefinition>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Create_WhenTheGroupExists_ShouldFailAsAlreadyExists()
    {
        // Arrange
        A.CallTo(() => _groups.CreateAsync(A<GroupDefinition>._, A<CancellationToken>._)).ThrowsAsync(new GroupExistsException("orders-1", "billing"));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.CreateAsync(new CreateRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.AlreadyExists);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("GROUP_EXISTS");
    }

    [Fact]
    public async Task Delete_WhenTheGroupIsMissing_ShouldFailAsNotFound()
    {
        // Arrange
        A.CallTo(() => _groups.DeleteAsync("orders-1", "billing", A<CancellationToken>._)).Returns(false);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.DeleteAsync(new DeleteGroupRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("GROUP_NOT_FOUND");
    }

    [Fact]
    public async Task ReplayParked_ShouldMarkAllOrOneAndFailWhenTheOneIsMissing()
    {
        // Arrange
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(new GroupDefinition("orders-1", "billing", GroupSettings.Default, -1));
        A.CallTo(() => _groups.MarkForReplayAsync("orders-1", "billing", null, ParkedNumber.Revision, A<CancellationToken>._)).Returns(3);
        A.CallTo(() => _groups.MarkForReplayAsync("orders-1", "billing", 7, ParkedNumber.Revision, A<CancellationToken>._)).Returns(0);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var all = await client.ReplayParkedAsync(new ReplayParkedRequest { Stream = "orders-1", Group = "billing", All = new() }, cancellationToken: TestContext.Current.CancellationToken);
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.ReplayParkedAsync(new ReplayParkedRequest { Stream = "orders-1", Group = "billing", Position = 7 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        all.Replayed.ShouldBe(3);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("PARKED_MESSAGE_NOT_FOUND");
    }

    [Fact]
    public async Task Read_ShouldConfirmDeliverAndTurnAcknowledgementsIntoTheCheckpoint()
    {
        // Arrange: the group starts at the beginning of a two-event stream, with the checkpoint written on every acknowledgement.
        var settings = GroupSettings.Default with { Start = StreamPosition.Start, CheckpointUpperBound = 1, CheckpointLowerBound = 1 };
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(new GroupDefinition("orders-1", "billing", settings, -1));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, settings.BufferSize, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 1), [Record("orders-1", 0, 10), Record("orders-1", 1, 11)]));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 5 } }, TestContext.Current.CancellationToken);
        var messages = await Next(call, 3);
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Ack = new Ack { Ids = { messages[1].Event.Event.Id } } }, TestContext.Current.CancellationToken);
        await Until(() => A.CallTo(() => _groups.SaveCheckpointAsync("orders-1", "billing", 0, A<CancellationToken>._)).MustHaveHappened());
        await call.RequestStream.CompleteAsync();

        // Assert
        messages[0].Confirmed.Checkpoint.ShouldBe(-1);
        messages[0].Confirmed.SubscriptionId.ShouldNotBeNullOrEmpty();
        messages[1].Event.Event.Revision.ShouldBe(0);
        messages[1].Event.RetryCount.ShouldBe(0);
        messages[2].Event.Event.Revision.ShouldBe(1);
        A.CallTo(() => _groups.AcquireLeaseAsync("group:orders-1:billing", A<string>._, PersistentSubscriptionsService.LeaseDuration, A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task Read_WhenAnotherInstanceOwnsTheGroup_ShouldFailNamingTheOwner()
    {
        // Arrange
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(new GroupDefinition("orders-1", "billing", GroupSettings.Default, -1));
        A.CallTo(() => _groups.AcquireLeaseAsync("group:orders-1:billing", A<string>._, A<TimeSpan>._, A<CancellationToken>._)).Returns("other-instance");
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing" } }, TestContext.Current.CancellationToken);
        var exception = await Should.ThrowAsync<RpcException>(async () => await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.Unavailable);
        var info = exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("GROUP_OWNED_ELSEWHERE");
        info.Metadata["owner"].ShouldBe("other-instance");
    }

    [Fact]
    public async Task Read_WhenTheGroupIsMissing_ShouldFailAsNotFound()
    {
        // Arrange
        A.CallTo(() => _groups.GetAsync("orders-1", "nobody", A<CancellationToken>._)).Returns((GroupDefinition?)null);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "nobody" } }, TestContext.Current.CancellationToken);
        var exception = await Should.ThrowAsync<RpcException>(async () => await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("GROUP_NOT_FOUND");
    }

    private static EventRecord Record(string stream, long revision, long position) =>
        new(Guid.NewGuid(), stream, revision, position, "order_placed", Created, Encoding.UTF8.GetBytes("{}"), new JsonObject());

    private static async Task<List<PersistentReadResponse>> Next(AsyncDuplexStreamingCall<PersistentReadRequest, PersistentReadResponse> call, int count)
    {
        var messages = new List<PersistentReadResponse>();
        while (messages.Count < count && await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken))
        {
            messages.Add(call.ResponseStream.Current);
        }

        return messages;
    }

    /// <summary>Retries an assertion for a moment: the acknowledgement is applied on the server's own task.</summary>
    private static async Task Until(Action assertion)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                assertion();
                return;
            }
            catch (Exception) when (attempt < 50)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            }
        }
    }

    private sealed class TestOptions : NightingaleOptionsBase
    {
    }
}
