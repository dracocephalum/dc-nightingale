using System.Text;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Protocol.V1;
using FakeItEasy;
using Google.Protobuf;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

public sealed class StreamsServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Created = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    private readonly IStreamStore _store = A.Fake<IStreamStore>(options => options.Strict());
    private WebApplication? _app;
    private GrpcChannel? _channel;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNightingaleServer();
        builder.Services.AddSingleton(_store);
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
    public async Task Append_WhenStoreAccepts_ShouldPassEventsThroughAndReturnLastRevisionAndPosition()
    {
        // Arrange
        var id = Guid.NewGuid();
        IReadOnlyList<EventData>? captured = null;
        A.CallTo(() => _store.AppendAsync("orders-1", StreamState.NoStream, A<IReadOnlyList<EventData>>._, A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<IReadOnlyList<EventData>>(2))
            .Returns(new AppendResult(1, 40));
        var client = new Streams.StreamsClient(_channel);

        // Act
        using var call = client.Append(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Options = new AppendOptions { Stream = "orders-1", ExpectedRevision = -1 } }, TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Event = Proposed(id, "order_placed", "{\"total\":1}", new JsonObject { ["$correlationId"] = "c-1" }) }, TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") }, TestContext.Current.CancellationToken);
        await call.RequestStream.CompleteAsync();
        var response = await call.ResponseAsync;

        // Assert
        response.Revision.ShouldBe(1);
        response.Position.ShouldBe(40);
        var appended = captured.ShouldNotBeNull();
        appended.Count.ShouldBe(2);
        appended[0].Id.ShouldBe(id);
        appended[0].Type.ShouldBe("order_placed");
        Encoding.UTF8.GetString(appended[0].Data.Span).ShouldBe("{\"total\":1}");
        appended[0].Metadata.ShouldNotBeNull()["$correlationId"].ShouldNotBeNull().GetValue<string>().ShouldBe("c-1");
        appended[1].Metadata.ShouldBeNull();
    }

    [Fact]
    public async Task Append_WhenRevisionConflicts_ShouldFailWithTheReasonAndBothRevisions()
    {
        // Arrange
        A.CallTo(() => _store.AppendAsync("orders-1", StreamState.StreamRevision(3), A<IReadOnlyList<EventData>>._, A<CancellationToken>._))
            .ThrowsAsync(new RevisionConflictException("orders-1", StreamState.StreamRevision(3), 5));
        var client = new Streams.StreamsClient(_channel);

        // Act
        using var call = client.Append(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Options = new AppendOptions { Stream = "orders-1", ExpectedRevision = 3 } }, TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") }, TestContext.Current.CancellationToken);
        await call.RequestStream.CompleteAsync();
        var exception = await Should.ThrowAsync<RpcException>(async () => await call.ResponseAsync);

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        var info = exception.GetRpcStatus()?.GetDetail<ErrorInfo>();
        info.ShouldNotBeNull();
        info.Domain.ShouldBe("nightingale");
        info.Reason.ShouldBe("REVISION_CONFLICT");
        info.Metadata["stream"].ShouldBe("orders-1");
        info.Metadata["expected"].ShouldBe("3");
        info.Metadata["actual"].ShouldBe("5");
    }

    [Fact]
    public async Task Append_WhenFirstMessageIsAnEvent_ShouldRejectWithoutTouchingTheStore()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        using var call = client.Append(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") }, TestContext.Current.CancellationToken);
        await call.RequestStream.CompleteAsync();
        var exception = await Should.ThrowAsync<RpcException>(async () => await call.ResponseAsync);

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_ARGUMENT");
    }

    [Fact]
    public async Task Read_WhenStreamHasNoEvents_ShouldSendStreamNotFoundAndNothingElse()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-9", Direction.Forwards, null, 10, A<CancellationToken>._))
            .Returns((StreamSlice?)null);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "orders-9", Start = new(), Count = 10 });

        // Assert
        messages.Count.ShouldBe(1);
        messages[0].ContentCase.ShouldBe(ReadResponse.ContentOneofCase.StreamNotFound);
        messages[0].StreamNotFound.Stream.ShouldBe("orders-9");
    }

    [Fact]
    public async Task Read_WhenForwardsFromStart_ShouldSendHeadFirstThenEvents()
    {
        // Arrange
        var first = Record("orders-1", 0, 10, "order_placed");
        var second = Record("orders-1", 1, 11, "order_paid");
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, null, 2, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 1), [first, second]));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "orders-1", Start = new(), Count = 2 });

        // Assert
        messages.Count.ShouldBe(3);
        messages[0].Head.ShouldSatisfyAllConditions(head => head.First.ShouldBe(0), head => head.Last.ShouldBe(1));
        messages[1].Event.ShouldSatisfyAllConditions(
            record => record.Id.ShouldBe(first.Id.ToString("D")),
            record => record.Revision.ShouldBe(0),
            record => record.Position.ShouldBe(10),
            record => record.EventType.ShouldBe("order_placed"),
            record => record.Created.ToDateTimeOffset().ShouldBe(Created),
            record => record.Metadata.ToStringUtf8().ShouldBe("{\"source\":\"test\"}"));
        messages[2].Event.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task Read_WhenBackwardsFromEnd_ShouldAskTheStoreForTheEndAndStopAtRevisionZero()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Backwards, null, 5, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 1), [Record("orders-1", 1, 11, "order_paid"), Record("orders-1", 0, 10, "order_placed")]));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "orders-1", Direction = ReadDirection.Backwards, End = new(), Count = 5 });

        // Assert
        messages.Select(message => message.ContentCase).ShouldBe(
            [ReadResponse.ContentOneofCase.Head, ReadResponse.ContentOneofCase.Event, ReadResponse.ContentOneofCase.Event]);
        messages[1].Event.Revision.ShouldBe(1);
        messages[2].Event.Revision.ShouldBe(0);
    }

    [Fact]
    public async Task Read_WhenFilteredOnAPlainStream_ShouldRejectAsFilterNotAllowed()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);
        var request = new ReadRequest { Stream = "orders-1", Start = new(), Count = 1, Filter = new Filter { EventType = new Expression { Prefix = { "order" } } } };

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => ReadAll(client, request));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("FILTER_NOT_ALLOWED");
    }

    [Fact]
    public async Task Read_WhenSubscribing_ShouldAnswerUnimplemented()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);
        var request = new ReadRequest { Stream = "orders-1", Start = new(), Subscription = new SubscriptionOptions() };

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => ReadAll(client, request));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.Unimplemented);
    }

    private static ProposedEvent Proposed(Guid id, string type, string json, JsonObject? metadata = null)
    {
        return new ProposedEvent
        {
            Id = id.ToString("D"),
            EventType = type,
            Data = ByteString.CopyFromUtf8(json),
            Metadata = metadata is null ? ByteString.Empty : ByteString.CopyFromUtf8(metadata.ToJsonString()),
        };
    }

    private static EventRecord Record(string stream, long revision, long position, string type) =>
        new(Guid.NewGuid(), stream, revision, position, type, Created, Encoding.UTF8.GetBytes("{}"), new JsonObject { ["source"] = "test" });

    private static async Task<List<ReadResponse>> ReadAll(Streams.StreamsClient client, ReadRequest request)
    {
        using var call = client.Read(request, cancellationToken: TestContext.Current.CancellationToken);
        var messages = new List<ReadResponse>();
        await foreach (var message in call.ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            messages.Add(message);
        }

        return messages;
    }
}
