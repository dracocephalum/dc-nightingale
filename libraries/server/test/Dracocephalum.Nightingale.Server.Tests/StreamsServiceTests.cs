using System.Text;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Dracocephalum.Nightingale.Server.Grpc;
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
    private readonly FakeTail _tail = new();
    private readonly TestOptions _serverOptions = new() { Auth = { Enabled = false } };
    private WebApplication? _app;
    private GrpcChannel? _channel;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNightingaleServer();
        builder.Services.AddNightingaleOptions(_serverOptions);
        builder.Services.AddSingleton(_store);
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

    [Theory]
    [InlineData("stream name", "INVALID_STREAM_NAME")]
    [InlineData("event type", "INVALID_ARGUMENT")]
    public async Task Append_WhenTheStoreSaysAValueIsTooLong_ShouldFailAsTheCallersMistake(string what, string reason)
    {
        // Arrange: the store is what measures a name; the service only translates its answer.
        A.CallTo(() => _store.AppendAsync(A<string>._, A<StreamState>._, A<IReadOnlyList<EventData>>._, A<CancellationToken>._))
            .Throws(new ValueTooLongException(what));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Append(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Options = new AppendOptions { Stream = "orders-1", ExpectedRevision = -2 } }, TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Event = new ProposedEvent { Id = Guid.NewGuid().ToString("D"), EventType = "order_placed", Data = Google.Protobuf.ByteString.CopyFromUtf8("{}") } }, TestContext.Current.CancellationToken);
        await call.RequestStream.CompleteAsync();

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await call.ResponseAsync);

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull().Reason.ShouldBe(reason);
        exception.Status.Detail.ShouldContain("longer than the store holds");
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
    public async Task AppendToStreams_ShouldPassThePartsToTheStoreInOrderAndAnswerEachStream()
    {
        // Arrange
        IReadOnlyList<StreamAppend>? appends = null;
        IReadOnlyList<StreamCheck>? checks = null;
        A.CallTo(() => _store.AppendToStreamsAsync(A<IReadOnlyList<StreamAppend>>._, A<IReadOnlyList<StreamCheck>>._, A<CancellationToken>._))
            .Invokes((IReadOnlyList<StreamAppend> given, IReadOnlyList<StreamCheck> checked_, CancellationToken _) => (appends, checks) = (given, checked_))
            .Returns([new StreamAppendResult("orders-1", 5, 41), new StreamAppendResult("$$payments-1", 0, 42), new StreamAppendResult("refunds-1", 0, 43)]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        using var call = client.AppendToStreams(cancellationToken: TestContext.Current.CancellationToken);
        AppendToStreamsRequest[] parts =
        [
            new() { Stream = new AppendStreamHeader { Stream = "orders-1", ExpectedRevision = 3 } },
            new() { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") },
            new() { Event = Proposed(Guid.NewGuid(), "order_shipped", "{}") },
            new() { Metadata = new AppendMetadata { Stream = "payments-1", ExpectedRevision = (long)ExpectedRevision.Any, Metadata = new Protocols.Grpc.V1.StreamMetadata { MaxCount = 5 } } },
            new() { Stream = new AppendStreamHeader { Stream = "refunds-1", ExpectedRevision = (long)ExpectedRevision.NoStream } },
            new() { Event = Proposed(Guid.NewGuid(), "refund_issued", "{}") },
            new() { Check = new AppendCheck { Stream = "ledger-1", ExpectedRevision = (long)ExpectedRevision.NoStream } },
        ];
        foreach (var part in parts)
        {
            await call.RequestStream.WriteAsync(part, TestContext.Current.CancellationToken);
        }

        await call.RequestStream.CompleteAsync();
        var response = await call.ResponseAsync;

        // Assert
        var events = appends.ShouldNotBeNull()[0].ShouldBeOfType<StreamAppend.OfEvents>();
        (events.Stream, events.Expected).ShouldBe(("orders-1", StreamState.StreamRevision(3)));
        events.Events.Select(e => e.Type).ShouldBe(["order_paid", "order_shipped"]);
        var metadata = appends[1].ShouldBeOfType<StreamAppend.OfMetadata>();
        (metadata.Stream, metadata.Expected, metadata.Metadata.MaxCount).ShouldBe(("payments-1", StreamState.Any, 5));
        appends[2].ShouldBeOfType<StreamAppend.OfEvents>().Events.ShouldHaveSingleItem().Type.ShouldBe("refund_issued");
        checks.ShouldNotBeNull().ShouldBe([new StreamCheck("ledger-1", StreamState.NoStream)]);
        response.Appended.Select(appended => (appended.Stream, appended.Revision, appended.Position)).ShouldBe([("orders-1", 5L, 41L), ("$$payments-1", 0L, 42L), ("refunds-1", 0L, 43L)]);
    }

    [Fact]
    public async Task AppendToStreams_WhenTheStoreReportsConflicts_ShouldAnswerWithEveryOneAsATypedDetail()
    {
        // Arrange
        A.CallTo(() => _store.AppendToStreamsAsync(A<IReadOnlyList<StreamAppend>>._, A<IReadOnlyList<StreamCheck>>._, A<CancellationToken>._))
            .ThrowsAsync(new StreamConflictsException([new StreamConflict(0, "orders-1", StreamState.StreamRevision(3), 5), new StreamConflict(1, "ledger-1", StreamState.NoStream, 0)]));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refused = await Should.ThrowAsync<RpcException>(() => AppendToStreamsAsync(
            client,
            new() { Stream = new AppendStreamHeader { Stream = "orders-1", ExpectedRevision = 3 } },
            new() { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") },
            new() { Check = new AppendCheck { Stream = "ledger-1", ExpectedRevision = (long)ExpectedRevision.NoStream } }));

        // Assert
        refused.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        var status = refused.GetRpcStatus().ShouldNotBeNull();
        var info = status.GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("STREAM_CONFLICTS");
        info.Metadata["count"].ShouldBe("2");
        status.GetDetail<AppendConflicts>().ShouldNotBeNull().Conflicts.Select(conflict => (conflict.Index, conflict.Stream, conflict.ExpectedRevision, conflict.ActualRevision))
            .ShouldBe([(0, "orders-1", 3L, 5L), (1, "ledger-1", (long)ExpectedRevision.NoStream, 0L)]);
    }

    [Fact]
    public async Task AppendToStreams_WhenTheRequestBreaksRules_ShouldListEveryProblemBeforeTheStore()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act: a stream written twice, a reset through metadata, and a check that asserts nothing.
        var refused = await Should.ThrowAsync<RpcException>(() => AppendToStreamsAsync(
            client,
            new() { Stream = new AppendStreamHeader { Stream = "orders-1", ExpectedRevision = (long)ExpectedRevision.Any } },
            new() { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") },
            new() { Stream = new AppendStreamHeader { Stream = "orders-1", ExpectedRevision = (long)ExpectedRevision.Any } },
            new() { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") },
            new() { Metadata = new AppendMetadata { Stream = "orders-2", ExpectedRevision = (long)ExpectedRevision.Any, Metadata = new Protocols.Grpc.V1.StreamMetadata { TruncateBefore = long.MaxValue } } },
            new() { Check = new AppendCheck { Stream = "ledger-1", ExpectedRevision = (long)ExpectedRevision.Any } }));

        // Assert
        refused.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        refused.Status.Detail.ShouldContain("Append 1 writes orders-1 again");
        refused.Status.Detail.ShouldContain("a reset is its own call");
        refused.Status.Detail.ShouldContain("Check 0 on ledger-1 expects any state");
        A.CallTo(() => _store.AppendToStreamsAsync(A<IReadOnlyList<StreamAppend>>._, A<IReadOnlyList<StreamCheck>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AppendToStreams_WhenAnEventComesBeforeAnyHeader_ShouldRefuseIt()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refused = await Should.ThrowAsync<RpcException>(() => AppendToStreamsAsync(client, new AppendToStreamsRequest { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") }));

        // Assert
        refused.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        refused.Status.Detail.ShouldContain("follows the header of its stream");
    }

    [Fact]
    public async Task AppendToStreams_WithMoreEventsInAllThanOneAppendMay_ShouldRefuseThemBeforeTheStore()
    {
        // Arrange: three events allowed, two streams of two.
        _serverOptions.MaxEventsPerAppend = 3;
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refused = await Should.ThrowAsync<RpcException>(() => AppendToStreamsAsync(
            client,
            new() { Stream = new AppendStreamHeader { Stream = "orders-1", ExpectedRevision = (long)ExpectedRevision.Any } },
            new() { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") },
            new() { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") },
            new() { Stream = new AppendStreamHeader { Stream = "orders-2", ExpectedRevision = (long)ExpectedRevision.Any } },
            new() { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") },
            new() { Event = Proposed(Guid.NewGuid(), "order_paid", "{}") }));

        // Assert: refused at the fourth event, in the stream it belongs to.
        var info = refused.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("APPEND_SIZE_EXCEEDED");
        info.Metadata["stream"].ShouldBe("orders-2");
        A.CallTo(() => _store.AppendToStreamsAsync(A<IReadOnlyList<StreamAppend>>._, A<IReadOnlyList<StreamCheck>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Append_WithAsManyEventsAsOneAppendMay_ShouldBeStoredAndWithOneMoreRefusedBeforeTheStore()
    {
        // Arrange: the host allows three events in one append.
        _serverOptions.MaxEventsPerAppend = 3;
        A.CallTo(() => _store.AppendAsync("orders-1", StreamState.NoStream, A<IReadOnlyList<EventData>>.That.Matches(events => events.Count == 3), A<CancellationToken>._))
            .Returns(new AppendResult(2, 40));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var stored = await AppendOf(client, "orders-1", 3);
        var refused = await Should.ThrowAsync<RpcException>(() => AppendOf(client, "orders-2", 4));

        // Assert: nothing of the fourth event's append reaches the store, and the caller is told the limit.
        stored.Revision.ShouldBe(2);
        refused.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        var info = refused.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("APPEND_SIZE_EXCEEDED");
        info.Metadata["stream"].ShouldBe("orders-2");
        info.Metadata["limit"].ShouldBe("3");
        A.CallTo(() => _store.AppendAsync("orders-2", A<StreamState>._, A<IReadOnlyList<EventData>>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Append_WhenTheStoreTakesFewerEventsThanTheLimitAllowed_ShouldStillAnswerWithTheSizeError()
    {
        // Arrange: a store version that takes less in one write than the limit was set for.
        A.CallTo(() => _store.AppendAsync("orders-1", StreamState.NoStream, A<IReadOnlyList<EventData>>._, A<CancellationToken>._))
            .Throws(new AppendSizeExceededException("orders-1", 0));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var refused = await Should.ThrowAsync<RpcException>(() => AppendOf(client, "orders-1", 2));

        // Assert
        var info = refused.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("APPEND_SIZE_EXCEEDED");
        info.Metadata["limit"].ShouldBe(NightingaleOptionsBase.DefaultMaxEventsPerAppend.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
    public async Task Append_WithBinaryData_ShouldPassItThroughBesideTheJsonAndAloneWithEmptyJson()
    {
        // Arrange
        byte[] binary = [0x00, 0xFF, 0x80];
        IReadOnlyList<EventData>? captured = null;
        A.CallTo(() => _store.AppendAsync("people-1", StreamState.NoStream, A<IReadOnlyList<EventData>>._, A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<IReadOnlyList<EventData>>(2))
            .Returns(new AppendResult(1, 40));
        var client = new Streams.StreamsClient(_channel);
        var both = Proposed(Guid.NewGuid(), "person_registered", "{\"country\":\"NZ\"}", new JsonObject { [MetadataKeys.BinaryContentType] = "application/x-protobuf" });
        both.BinaryData = ByteString.CopyFrom(binary);
        var binaryOnly = new ProposedEvent { Id = Guid.NewGuid().ToString("D"), EventType = "person_scanned", BinaryData = ByteString.CopyFrom(binary) };

        // Act
        await AppendAsync(client, "people-1", -1, both, binaryOnly);

        // Assert
        var appended = captured.ShouldNotBeNull();
        appended[0].BinaryData.ToArray().ShouldBe(binary);
        Encoding.UTF8.GetString(appended[0].Data.Span).ShouldBe("{\"country\":\"NZ\"}");
        appended[1].BinaryData.ToArray().ShouldBe(binary);
        appended[1].Data.IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", null, "JSON data, binary data, or both")]
    [InlineData("{}", "\"application/x-protobuf\"", "the event carries none")]
    [InlineData("", "42", "a non-empty string")]
    [InlineData("", "\"\"", "a non-empty string")]
    public async Task Append_WhenAnEventsBodiesDoNotAgree_ShouldRefuseItBeforeTheStore(string json, string? contentType, string reason)
    {
        // Arrange: the last two carry binary data, so only the content type is wrong.
        var client = new Streams.StreamsClient(_channel);
        var proposed = new ProposedEvent { Id = Guid.NewGuid().ToString("D"), EventType = "person_registered", Data = ByteString.CopyFromUtf8(json) };
        if (contentType is not null)
        {
            proposed.Metadata = ByteString.CopyFromUtf8($"{{\"{MetadataKeys.BinaryContentType}\":{contentType}}}");
        }

        if (json.Length == 0 && contentType is not null)
        {
            proposed.BinaryData = ByteString.CopyFrom(0x01);
        }

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => AppendAsync(client, "people-1", -1, proposed));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        exception.Status.Detail.ShouldContain(reason);
        A.CallTo(() => _store.AppendAsync(A<string>._, A<StreamState>._, A<IReadOnlyList<EventData>>._, A<CancellationToken>._)).MustNotHaveHappened();
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
    public async Task Append_WhenStreamIsReserved_ShouldRejectTheName()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        using var call = client.Append(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Options = new AppendOptions { Stream = "$all", ExpectedRevision = -2 } }, TestContext.Current.CancellationToken);

        // The server refuses on the first message, so completing the request can be what fails.
        var exception = await Should.ThrowAsync<RpcException>(async () =>
        {
            await call.RequestStream.CompleteAsync();
            await call.ResponseAsync;
        });

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_STREAM_NAME");
    }

    [Fact]
    public async Task Read_WhenStreamHasNoEvents_ShouldSendStreamNotFoundAndNothingElse()
    {
        // Arrange
        A.CallTo(() => _store.ReadEventualAsync("orders-9", Direction.Forwards, null, 10, A<CancellationToken>._))
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
        A.CallTo(() => _store.ReadEventualAsync("orders-1", Direction.Forwards, null, 2, A<CancellationToken>._))
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
        A.CallTo(() => _store.ReadEventualAsync("orders-1", Direction.Backwards, null, 5, A<CancellationToken>._))
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
    public async Task Read_WhenTheFilterIsNotOneTheServerRuns_ShouldAnswerInvalidArgument()
    {
        // Arrange: prefixes and a regular expression at once.
        var client = new Streams.StreamsClient(_channel);
        var request = new ReadRequest { Stream = "$all", Start = new(), Count = 1, Filter = new Filter { EventType = new Expression { Prefix = { "order" }, Regex = "^order" } } };

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => ReadAll(client, request));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        exception.Status.Detail.ShouldContain("one of the three");
    }

    [Fact]
    public async Task Read_WhenReadingAllWithAFilter_ShouldScanStretchByStretchToTheHead()
    {
        // Arrange: the store's scan keeps two of the first stretch and says it looked through 4095,
        // then one of the rest up to the head.
        _tail.Advance(5000);
        A.CallTo(() => _store.ScanAllAsync(A<EventFilter>.That.Matches(filter => filter.Target == EventFilterTarget.EventType && filter.Prefixes.Count == 1 && filter.Prefixes[0] == "order"), Direction.Forwards, 0, 5000, StreamsService.ScanWindow, 10, A<CancellationToken>._))
            .Returns(new ScannedSlice([Record("orders-1", 0, 7, "order_placed"), Record("orders-2", 0, 900, "order_paid")], 4095));
        A.CallTo(() => _store.ScanAllAsync(A<EventFilter>._, Direction.Forwards, 4096, 5000, StreamsService.ScanWindow, 8, A<CancellationToken>._))
            .Returns(new ScannedSlice([Record("orders-3", 0, 4500, "order_placed")], 5000));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$all", Start = new(), Count = 10, Filter = new Filter { EventType = new Expression { Prefix = { "order" } } } });

        // Assert
        messages[0].Head.Last.ShouldBe(5000);
        messages.Skip(1).Select(message => message.Event.Position).ShouldBe([7, 900, 4500]);
    }

    [Fact]
    public async Task Read_WhenSubscribingToAllWithAFilter_ShouldCheckpointOverQuietStretchesThenKeepOfTheLiveWhatTheFilterKeeps()
    {
        // Arrange: nothing of the first three thousand positions is an order; the checkpoint
        // interval is two positions. Then the tail advances by three events, one of them an order.
        _tail.Advance(3000);
        A.CallTo(() => _store.ScanAllAsync(A<EventFilter>._, Direction.Forwards, 0, 3000, StreamsService.ScanWindow, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new ScannedSlice([], 3000));
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 3001, 3003, StreamsService.PageSize, A<CancellationToken>._))
            .Returns([Record("orders-1", 0, 3001, "order_placed"), Record("shipments-1", 0, 3002, "shipment_sent"), Record("shipments-1", 1, 3003, "shipment_sent")]);
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(
            new ReadRequest { Stream = "$all", Start = new(), Subscription = new SubscriptionOptions(), Filter = new Filter { EventType = new Expression { Prefix = { "order" } }, CheckpointInterval = 2 } },
            cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var catchUp = await Next(call, 3);
        _tail.Advance(3003);
        var live = await Next(call, 3);

        // Assert: the quiet stretch is checkpointed at its end before caught up; live, the order is
        // delivered, the two shipments count towards a checkpoint, and caught up names the last
        // position examined.
        catchUp[0].Confirmed.Head.ShouldBe(3000);
        catchUp[1].Checkpoint.Position.ShouldBe(3000);
        catchUp[2].CaughtUp.Head.ShouldBe(3000);
        live[0].Event.Position.ShouldBe(3001);
        live[1].Checkpoint.Position.ShouldBe(3003);
        live[2].CaughtUp.Head.ShouldBe(3003);
    }

    [Fact]
    public async Task Read_WhenReadingAllForwards_ShouldSendTheHeadThenEventsNeverPastIt()
    {
        // Arrange: the head is taken once; the store is asked for one page bounded by it.
        _tail.Advance(20);
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 0, 20, 3, A<CancellationToken>._))
            .Returns([Record("orders-1", 0, 5, "a"), Record("orders-2", 0, 7, "b"), Record("orders-1", 1, 9, "c")]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$all", Start = new(), Count = 3 });

        // Assert
        messages[0].Head.ShouldSatisfyAllConditions(head => head.First.ShouldBe(0), head => head.Last.ShouldBe(20));
        messages.Skip(1).Select(message => message.Event.Position).ShouldBe([5, 7, 9]);
    }

    [Fact]
    public async Task Read_WhenReadingAllBackwardsFromEnd_ShouldStartAtTheHead()
    {
        // Arrange
        _tail.Advance(20);
        A.CallTo(() => _store.ReadAllAsync(Direction.Backwards, 20, 20, 2, A<CancellationToken>._))
            .Returns([Record("orders-3", 0, 20, "z"), Record("orders-1", 4, 18, "y")]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$all", Direction = ReadDirection.Backwards, End = new(), Count = 2 });

        // Assert
        messages.Skip(1).Select(message => message.Event.Position).ShouldBe([20, 18]);
    }

    [Fact]
    public async Task Read_WhenReadingAllOfAnEmptyStore_ShouldSendOnlyTheHead()
    {
        // Arrange: head 0 means no events; the store is never asked.
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$all", Start = new(), Count = 10 });

        // Assert
        messages.ShouldHaveSingleItem().Head.Last.ShouldBe(0);
    }

    [Fact]
    public async Task Read_WhenSubscribingToAStream_ShouldConfirmCatchUpThenDeliverWhatTheTailBrings()
    {
        // Arrange
        // Arrange: the live event comes through the router, which reads where the advanced range's
        // events are, then the stream's own events from the first revision pointed at.
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 1), [Record("orders-1", 0, 10, "a"), Record("orders-1", 1, 11, "b")]));
        A.CallTo(() => _store.ReadPointersAsync(1, 15, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("shipments-1", 0, 14, "shipment_sent"), new EventPointer("orders-1", 2, 15, "c")]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 2, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record("orders-1", 2, 15, "c")]));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "orders-1", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var catchUp = await Next(call, 4);
        _tail.Advance(15);
        var live = await Next(call, 2);

        // Assert: the other stream's event was pointed at and never read.
        catchUp[0].Confirmed.Head.ShouldBe(1);
        catchUp[0].Confirmed.SubscriptionId.ShouldNotBeNullOrEmpty();
        catchUp[1].Event.Revision.ShouldBe(0);
        catchUp[2].Event.Revision.ShouldBe(1);
        catchUp[3].CaughtUp.Head.ShouldBe(1);
        live[0].Event.Revision.ShouldBe(2);
        live[1].CaughtUp.Head.ShouldBe(2);
        A.CallTo(() => _store.ReadAsStoredAsync("shipments-1", A<long>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Read_WhenTheRouterBringsAnEventAheadOfTheNextRevision_ShouldReadTheStreamAgainAndDeliverInOrder()
    {
        // Arrange: revision 2 took a number below the mark and revision 1 one above it, which an
        // append whose number landed past a gap does; the router brings 2 first.
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10, "a")]));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 1, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record("orders-1", 1, 22, "b"), Record("orders-1", 2, 20, "c")]));
        A.CallTo(() => _store.ReadPointersAsync(1, 20, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-1", 2, 20, "c")]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 2, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record("orders-1", 2, 20, "c")]));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "orders-1", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var catchUp = await Next(call, 3);
        _tail.Advance(20);
        var live = await Next(call, 3);

        // Assert: nothing out of order, nothing twice.
        catchUp[2].CaughtUp.Head.ShouldBe(0);
        live[0].Event.Revision.ShouldBe(1);
        live[1].Event.Revision.ShouldBe(2);
        live[2].CaughtUp.Head.ShouldBe(2);
    }

    [Fact]
    public async Task Read_WhenSubscribingToAStreamThatDoesNotExistYet_ShouldConfirmWithMinusOneAndWaitForIt()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .Returns((StreamSlice?)null);
        A.CallTo(() => _store.ReadPointersAsync(1, 10, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-1", 0, 10, "a")]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 0, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10, "a")]));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "orders-1", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var confirmed = await Next(call, 2);
        _tail.Advance(10);
        var live = await Next(call, 2);

        // Assert
        confirmed[0].Confirmed.Head.ShouldBe(-1);
        confirmed[1].CaughtUp.Head.ShouldBe(-1);
        live[0].Event.Revision.ShouldBe(0);
        live[1].CaughtUp.Head.ShouldBe(0);
    }

    [Fact]
    public async Task Read_WhenSubscribingToAStreamItsMetadataHidesEntirely_ShouldCatchUpAtTheHeadAndDeliverWhatComesNext()
    {
        // Arrange: revisions 0 to 4 are there and the metadata hides them all, so the catch-up
        // read from the start is empty; revision 5 is then the next to deliver, not an event ahead
        // of revision 0 to read the stream again for.
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(10, 4), []));
        A.CallTo(() => _store.ReadPointersAsync(1, 30, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-1", 5, 30, "f")]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 5, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 5), [Record("orders-1", 5, 30, "f")]));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "orders-1", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var confirmed = await Next(call, 2);
        _tail.Advance(30);
        var live = await Next(call, 2);

        // Assert
        confirmed[0].Confirmed.Head.ShouldBe(4);
        confirmed[1].CaughtUp.Head.ShouldBe(4);
        live[0].Event.Revision.ShouldBe(5);
        live[1].CaughtUp.Head.ShouldBe(5);
        A.CallTo(() => _store.ReadAsync("orders-1", A<Direction>._, A<long?>._, A<int>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Read_WhenSubscribingToAStreamFromEnd_ShouldDeliverOnlyWhatComesAfter()
    {
        // Arrange
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, long.MaxValue, StreamsService.PageSize, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 4), []));
        A.CallTo(() => _store.ReadPointersAsync(1, 30, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-1", 5, 30, "f")]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-1", 5, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 5), [Record("orders-1", 5, 30, "f")]));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "orders-1", End = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var confirmed = await Next(call, 2);
        _tail.Advance(30);
        var live = await Next(call, 2);

        // Assert
        confirmed[0].Confirmed.Head.ShouldBe(4);
        confirmed[1].CaughtUp.Head.ShouldBe(4);
        live[0].Event.Revision.ShouldBe(5);
        live[1].CaughtUp.Head.ShouldBe(5);
    }

    [Fact]
    public async Task Read_WhenSubscribingToAll_ShouldCatchUpToTheTailThenFollowIt()
    {
        // Arrange
        _tail.Advance(2);
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 0, 2, StreamsService.PageSize, A<CancellationToken>._))
            .Returns([Record("orders-1", 0, 1, "a"), Record("orders-2", 0, 2, "b")]);
        A.CallTo(() => _store.ReadAllAsync(Direction.Forwards, 3, 3, StreamsService.PageSize, A<CancellationToken>._))
            .Returns([Record("orders-1", 1, 3, "c")]);
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "$all", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var catchUp = await Next(call, 4);
        _tail.Advance(3);
        var live = await Next(call, 2);

        // Assert
        catchUp[0].Confirmed.Head.ShouldBe(2);
        catchUp.Skip(1).Take(2).Select(message => message.Event.Position).ShouldBe([1, 2]);
        catchUp[3].CaughtUp.Head.ShouldBe(2);
        live[0].Event.Position.ShouldBe(3);
        live[1].CaughtUp.Head.ShouldBe(3);
    }

    [Fact]
    public async Task Read_WhenReadingACategoryStream_ShouldSendItsOwnBoundsThenItsEvents()
    {
        // Arrange: the global head is 20; the category's own bounds are what the client learns.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        A.CallTo(() => _store.VirtualHeadAsync(orders, 20, A<CancellationToken>._)).Returns(new StreamHead(5, 17));
        A.CallTo(() => _store.ReadVirtualAsync(orders, Direction.Forwards, 5, 20, 10, A<CancellationToken>._))
            .Returns([Record("orders-1", 0, 5, "a"), Record("orders-2", 0, 9, "b"), Record("orders-1", 1, 17, "c")]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$ce-orders", Start = new(), Count = 10 });

        // Assert
        messages[0].Head.ShouldSatisfyAllConditions(head => head.First.ShouldBe(5), head => head.Last.ShouldBe(17));
        messages.Skip(1).Select(message => message.Event.Position).ShouldBe([5, 9, 17]);
    }

    [Fact]
    public async Task Read_WhenReadingAnEventTypeStreamWithNoEvents_ShouldSendZeroBoundsOnly()
    {
        // Arrange
        var placed = new VirtualStreamName(VirtualStreamKind.EventType, "OrderPlaced");
        _tail.Advance(20);
        A.CallTo(() => _store.VirtualHeadAsync(placed, 20, A<CancellationToken>._)).Returns((StreamHead?)null);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$et-OrderPlaced", Start = new(), Count = 10 });

        // Assert
        messages.ShouldHaveSingleItem().Head.ShouldSatisfyAllConditions(head => head.First.ShouldBe(0), head => head.Last.ShouldBe(0));
    }

    [Fact]
    public async Task Read_WhenSubscribingToACategory_ShouldReportItsOwnHeadAndStaySilentWhenTheStoreMovesWithoutIt()
    {
        // Arrange: the store's head is 20 but the category's last event is at 17; an advance to 30
        // brings the category nothing, another stream's event only, and only the advance to 40
        // does. The two advances may be seen as one, so the range 21..40 is configured as well.
        // The router matches the category in process from where the range's events are.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        A.CallTo(() => _store.VirtualHeadAsync(orders, 20, A<CancellationToken>._)).Returns(new StreamHead(5, 17));
        A.CallTo(() => _store.ReadVirtualAsync(orders, Direction.Forwards, 0, 20, StreamsService.PageSize, A<CancellationToken>._))
            .Returns([Record("orders-1", 0, 5, "a"), Record("orders-1", 1, 17, "b")]);
        A.CallTo(() => _store.ReadPointersAsync(21, 30, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("shipments-1", 0, 25, "shipment_sent")]);
        A.CallTo(() => _store.ReadPointersAsync(31, 40, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("orders-2", 0, 35, "c")]);
        A.CallTo(() => _store.ReadPointersAsync(21, 40, StreamRouter.PointerPage, A<CancellationToken>._))
            .Returns([new EventPointer("shipments-1", 0, 25, "shipment_sent"), new EventPointer("orders-2", 0, 35, "c")]);
        A.CallTo(() => _store.ReadAsStoredAsync("orders-2", 0, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-2", 0, 35, "c")]));
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "$ce-orders", Start = new(), Subscription = new SubscriptionOptions() }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var catchUp = await Next(call, 4);
        _tail.Advance(30);
        _tail.Advance(40);
        var live = await Next(call, 2);

        // Assert: never the global head, 20, 30 or 40.
        catchUp[0].Confirmed.Head.ShouldBe(17);
        catchUp.Skip(1).Take(2).Select(message => message.Event.Position).ShouldBe([5, 17]);
        catchUp[3].CaughtUp.Head.ShouldBe(17);
        live[0].Event.Position.ShouldBe(35);
        live[1].CaughtUp.Head.ShouldBe(35);
    }

    [Fact]
    public async Task Read_WhenTheReservedNameIsUnknown_ShouldRejectTheName()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => ReadAll(client, new ReadRequest { Stream = "$streams", Start = new(), Count = 1 }));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_STREAM_NAME");
    }

    [Fact]
    public async Task Read_WhenSubscribingBackwards_ShouldReject()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);
        var request = new ReadRequest { Stream = "orders-1", Direction = ReadDirection.Backwards, End = new(), Subscription = new SubscriptionOptions() };

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => ReadAll(client, request));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Archive_ByDefault_ShouldBeRefusedAsDisabledWithoutTouchingTheStore()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.ArchiveAsync(new ArchiveRequest { Stream = "orders-1", ExpectedRevision = -2 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        var info = exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("DELETION_DISABLED");
        info.Metadata["operation"].ShouldBe("Archive");
        info.Metadata["allowed"].ShouldBe("None");
    }

    [Fact]
    public async Task Delete_WhenTheLevelIsArchive_ShouldBeRefusedWhileArchiveIsAllowed()
    {
        // Arrange: each level allows what the levels below it allow, and no more.
        _serverOptions.Deletion.Allowed = DeletionLevel.Archive;
        A.CallTo(() => _store.ArchiveAsync("orders-1", StreamState.Any, A<CancellationToken>._)).Returns(Task.CompletedTask);
        var client = new Streams.StreamsClient(_channel);

        // Act
        await client.ArchiveAsync(new ArchiveRequest { Stream = "orders-1", ExpectedRevision = -2 }, cancellationToken: TestContext.Current.CancellationToken);
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.DeleteAsync(new DeleteRequest { Stream = "orders-1", ExpectedRevision = -2 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        A.CallTo(() => _store.ArchiveAsync("orders-1", StreamState.Any, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        var info = exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("DELETION_DISABLED");
        info.Metadata["operation"].ShouldBe("Delete");
        info.Metadata["allowed"].ShouldBe("Archive");
    }

    [Fact]
    public async Task Archive_WhenAllowed_ShouldPassTheExpectedStateToTheStore()
    {
        // Arrange: the delete level allows archiving too.
        _serverOptions.Deletion.Allowed = DeletionLevel.Delete;
        A.CallTo(() => _store.ArchiveAsync("orders-1", StreamState.StreamRevision(4), A<CancellationToken>._)).Returns(Task.CompletedTask);
        var client = new Streams.StreamsClient(_channel);

        // Act
        await client.ArchiveAsync(new ArchiveRequest { Stream = "orders-1", ExpectedRevision = 4 }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        A.CallTo(() => _store.ArchiveAsync("orders-1", StreamState.StreamRevision(4), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Archive_WhenTheStreamIsMissing_ShouldFailAsNotFound()
    {
        // Arrange
        _serverOptions.Deletion.Allowed = DeletionLevel.Archive;
        A.CallTo(() => _store.ArchiveAsync("orders-9", StreamState.Any, A<CancellationToken>._)).ThrowsAsync(new StreamNotFoundException("orders-9"));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.ArchiveAsync(new ArchiveRequest { Stream = "orders-9", ExpectedRevision = -2 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.NotFound);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("STREAM_NOT_FOUND");
    }

    [Fact]
    public async Task Delete_WhenAllowedAndTheRevisionIsStale_ShouldFailWithTheConflict()
    {
        // Arrange
        _serverOptions.Deletion.Allowed = DeletionLevel.Delete;
        A.CallTo(() => _store.DeleteAsync("orders-1", StreamState.StreamRevision(1), A<CancellationToken>._))
            .ThrowsAsync(new RevisionConflictException("orders-1", StreamState.StreamRevision(1), 3));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.DeleteAsync(new DeleteRequest { Stream = "orders-1", ExpectedRevision = 1 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull().Metadata["actual"].ShouldBe("3");
    }

    /// <summary>The common settings alone; the service reads nothing a backend adds.</summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous unit test.</returns>
    [Fact]
    public async Task Read_WhenReadingACategoryByOrdinal_ShouldSendOrdinalBoundsThenEventsWithTheirOrdinals()
    {
        // Arrange: three numbered events at sparse positions. The tail is never consulted: what is
        // numbered is committed by construction.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _store.OrdinalHeadAsync(orders, A<CancellationToken>._)).Returns(new StreamHead(0, 2));
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 0, 10, A<CancellationToken>._))
            .Returns([Numbered("orders-1", 0, 5, 0), Numbered("orders-2", 0, 9, 1), Numbered("orders-1", 1, 17, 2)]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$ce-orders", Start = new(), Count = 10, Numbering = Protocols.Grpc.V1.Numbering.Ordinal });

        // Assert
        messages[0].Head.ShouldSatisfyAllConditions(head => head.First.ShouldBe(0), head => head.Last.ShouldBe(2));
        messages.Skip(1).Select(message => message.Event.Ordinal).ShouldBe([0, 1, 2]);
        messages.Skip(1).Select(message => message.Event.Position).ShouldBe([5, 9, 17]);
        messages.Skip(1).ShouldAllBe(message => message.Event.HasOrdinal);
    }

    [Fact]
    public async Task Read_WhenReadingByOrdinalBackwardsFromTheEnd_ShouldStartAtTheLastOrdinal()
    {
        // Arrange
        var placed = new VirtualStreamName(VirtualStreamKind.EventType, "OrderPlaced");
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _store.OrdinalHeadAsync(placed, A<CancellationToken>._)).Returns(new StreamHead(0, 7));
        A.CallTo(() => _store.ReadByOrdinalAsync(placed, Direction.Backwards, 7, 2, A<CancellationToken>._))
            .Returns([Numbered("orders-9", 0, 40, 7), Numbered("orders-8", 0, 31, 6)]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$et-OrderPlaced", Direction = ReadDirection.Backwards, End = new(), Count = 2, Numbering = Protocols.Grpc.V1.Numbering.Ordinal });

        // Assert
        messages.Skip(1).Select(message => message.Event.Ordinal).ShouldBe([7, 6]);
    }

    [Fact]
    public async Task Read_WhenOrdinalsAreNotEnabled_ShouldRefuseBeforeTouchingTheStore()
    {
        // Arrange
        A.CallTo(() => _store.OrdinalsEnabled).Returns(false);
        var client = new Streams.StreamsClient(_channel);
        var request = new ReadRequest { Stream = "$ce-orders", Start = new(), Count = 1, Numbering = Protocols.Grpc.V1.Numbering.Ordinal };

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => ReadAll(client, request));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("ORDINALS_NOT_ENABLED");
    }

    [Theory]
    [InlineData("orders-1")]
    [InlineData("$all")]
    public async Task Read_WhenOrdinalNumberingIsAskedOutsideAVirtualStream_ShouldReject(string stream)
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);
        var request = new ReadRequest { Stream = stream, Start = new(), Count = 1, Numbering = Protocols.Grpc.V1.Numbering.Ordinal };

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => ReadAll(client, request));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_ARGUMENT");
    }

    [Fact]
    public async Task Read_WhenSubscribingByOrdinal_ShouldConfirmWithTheLastOrdinalAndLookAgainWhenTheSequencerCatchesUp()
    {
        // Arrange: two numbered events; the tail then advances to 30 before the sequencer has
        // reached it, so the first look after the wake finds nothing, and the next one, woken by
        // the numbering reaching 30, finds the event the sequencer assigned meanwhile.
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        _tail.Number(20);
        var reads = 0;
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _store.OrdinalHeadAsync(orders, A<CancellationToken>._)).Returns(new StreamHead(0, 1));
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 0, StreamsService.PageSize, A<CancellationToken>._))
            .Returns([Numbered("orders-1", 0, 5, 0), Numbered("orders-1", 1, 17, 1)]);
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 2, StreamsService.PageSize, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                // The catch-up's page from 0 was short, so the first look from 2 is the one after
                // the wake, which the sequencer finishes numbering as it returns.
                var read = Interlocked.Increment(ref reads);
                if (read == 1)
                {
                    _tail.Number(30);
                }

                return Task.FromResult<IReadOnlyList<EventRecord>>(read < 2 ? [] : [Numbered("orders-2", 0, 25, 2)]);
            });
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "$ce-orders", Start = new(), Subscription = new SubscriptionOptions(), Numbering = Protocols.Grpc.V1.Numbering.Ordinal }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var catchUp = await Next(call, 4);
        _tail.Advance(30);
        var live = await Next(call, 2);

        // Assert: every number an ordinal, never a position.
        catchUp[0].Confirmed.Head.ShouldBe(1);
        catchUp.Skip(1).Take(2).Select(message => message.Event.Ordinal).ShouldBe([0, 1]);
        catchUp[3].CaughtUp.Head.ShouldBe(1);
        live[0].Event.Ordinal.ShouldBe(2);
        live[0].Event.Position.ShouldBe(25);
        live[1].CaughtUp.Head.ShouldBe(2);
        reads.ShouldBe(2);
    }

    [Fact]
    public async Task Read_WhenAnEventIsNumberedBetweenTheSubscriptionsReadAndItsWait_ShouldStillDeliverIt()
    {
        // Arrange: subscribed from the end at ordinal 7, the head at 20 and staying there, since
        // nothing more is appended. The event at 20 is committed and not numbered when the
        // subscription reads, and numbered the moment the read returns: taken before that read
        // the numbering says "through 19", after it "through 20".
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        _tail.Number(19);
        var reads = 0;
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _store.OrdinalHeadAsync(orders, A<CancellationToken>._)).Returns(new StreamHead(0, 7));
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 8, StreamsService.PageSize, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    _tail.Number(20);
                    return Task.FromResult<IReadOnlyList<EventRecord>>([]);
                }

                return Task.FromResult<IReadOnlyList<EventRecord>>([Numbered("orders-3", 0, 20, 8)]);
            });
        var client = new Streams.StreamsClient(_channel);
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        giveUp.CancelAfter(TimeSpan.FromSeconds(20));
        using var call = client.Read(new ReadRequest { Stream = "$ce-orders", End = new(), Subscription = new SubscriptionOptions(), Numbering = Protocols.Grpc.V1.Numbering.Ordinal }, cancellationToken: giveUp.Token);

        // Act: the confirmation, the caught-up note, then the event.
        var messages = await Next(call, 3);

        // Assert: found on the next look, not waited for behind an append that never comes.
        messages.Count.ShouldBe(3);
        messages[^1].Event.Ordinal.ShouldBe(8);
        messages[^1].Event.Position.ShouldBe(20);
    }

    [Fact]
    public async Task Read_WhenSubscribingByOrdinalFromEnd_ShouldStartAfterTheLastOrdinal()
    {
        // Arrange
        var orders = new VirtualStreamName(VirtualStreamKind.Category, "orders");
        _tail.Advance(20);
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _store.OrdinalHeadAsync(orders, A<CancellationToken>._)).Returns(new StreamHead(0, 7));
        A.CallTo(() => _store.ReadByOrdinalAsync(orders, Direction.Forwards, 8, StreamsService.PageSize, A<CancellationToken>._)).Returns([]);
        _tail.Number(20);
        var client = new Streams.StreamsClient(_channel);
        using var call = client.Read(new ReadRequest { Stream = "$ce-orders", End = new(), Subscription = new SubscriptionOptions(), Numbering = Protocols.Grpc.V1.Numbering.Ordinal }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var confirmed = await Next(call, 1);

        // Assert
        confirmed.ShouldHaveSingleItem().Confirmed.Head.ShouldBe(7);
    }

    [Fact]
    public async Task SetStreamMetadata_ShouldPassTheSettingsAndTheCustomKeysToTheStoreUnderTheMetadataStreamsExpectation()
    {
        // Arrange
        StreamMetadata? captured = null;
        A.CallTo(() => _store.SetStreamMetadataAsync("orders-1", StreamState.NoStream, A<StreamMetadata>._, A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<StreamMetadata>(2))
            .Returns(new AppendResult(0, 41));
        var client = new Streams.StreamsClient(_channel);
        var metadata = new Protocols.Grpc.V1.StreamMetadata
        {
            MaxAge = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromHours(1)),
            MaxCount = 10,
            TruncateBefore = 3,
            TempStream = true,
            Custom = ByteString.CopyFromUtf8("{\"owner\":\"billing\"}"),
        };

        // Act
        var response = await client.SetStreamMetadataAsync(new SetStreamMetadataRequest { Stream = "orders-1", ExpectedRevision = -1, Metadata = metadata }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        response.Revision.ShouldBe(0);
        response.Position.ShouldBe(41);
        var stored = captured.ShouldNotBeNull();
        stored.MaxAge.ShouldBe(TimeSpan.FromHours(1));
        stored.MaxCount.ShouldBe(10);
        stored.TruncateBefore.ShouldBe(3);
        stored.CacheControl.ShouldBeNull();
        stored.TempStream.ShouldBe(true);
        stored.Custom.ShouldNotBeNull()["owner"].ShouldNotBeNull().GetValue<string>().ShouldBe("billing");
    }

    [Theory]
    [InlineData("{\"$acl\":{\"$r\":\"$all\"}}", "$acl is not supported")]
    [InlineData("{\"$maxCount\":5}", "set through its own property")]
    [InlineData("[1]", "a JSON object")]
    public async Task SetStreamMetadata_WhenTheCustomKeysAreRefused_ShouldAnswerInvalidArgumentWithoutTouchingTheStore(string custom, string why)
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);
        var request = new SetStreamMetadataRequest { Stream = "orders-1", ExpectedRevision = -2, Metadata = new Protocols.Grpc.V1.StreamMetadata { Custom = ByteString.CopyFromUtf8(custom) } };

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.SetStreamMetadataAsync(request, cancellationToken: TestContext.Current.CancellationToken));

        // Assert: the strict store would have thrown on any call.
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_ARGUMENT");
        exception.Status.Detail.ShouldContain(why);
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(null, -1L)]
    public async Task SetStreamMetadata_WhenASettingIsOutOfRange_ShouldAnswerInvalidArgument(long? maxCount, long? truncateBefore)
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);
        var metadata = new Protocols.Grpc.V1.StreamMetadata();
        if (maxCount is { } count)
        {
            metadata.MaxCount = count;
        }

        if (truncateBefore is { } before)
        {
            metadata.TruncateBefore = before;
        }

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.SetStreamMetadataAsync(new SetStreamMetadataRequest { Stream = "orders-1", ExpectedRevision = -2, Metadata = metadata }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_ARGUMENT");
    }

    [Theory]
    [InlineData("$all")]
    [InlineData("$$orders-1")]
    [InlineData("$ce-orders")]
    public async Task SetStreamMetadata_WhenTheStreamIsNotPlain_ShouldRejectTheName(string stream)
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.SetStreamMetadataAsync(new SetStreamMetadataRequest { Stream = stream, ExpectedRevision = -2 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_STREAM_NAME");
    }

    [Fact]
    public async Task SetStreamMetadata_WhenTheNameLeavesNoRoomForTheMetadataStreamsPrefix_ShouldRejectTheName()
    {
        // Arrange: a name that fits the column, whose metadata stream's would not.
        var client = new Streams.StreamsClient(_channel);
        var stream = new string('o', StreamsService.MaxStreamNameLength - 1);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.SetStreamMetadataAsync(new SetStreamMetadataRequest { Stream = stream, ExpectedRevision = -2 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_STREAM_NAME");
    }

    [Fact]
    public async Task SetStreamMetadata_WhenTheMetadataStreamMovedOn_ShouldFailWithTheConflict()
    {
        // Arrange
        A.CallTo(() => _store.SetStreamMetadataAsync("orders-1", StreamState.FromInt64(0), A<StreamMetadata>._, A<CancellationToken>._))
            .Throws(new RevisionConflictException("$$orders-1", StreamState.FromInt64(0), 1));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.SetStreamMetadataAsync(new SetStreamMetadataRequest { Stream = "orders-1", ExpectedRevision = 0 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert: the conflict names the metadata stream, whose revision the caller expected.
        var info = exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("REVISION_CONFLICT");
        info.Metadata["stream"].ShouldBe("$$orders-1");
        info.Metadata["actual"].ShouldBe("1");
    }

    [Fact]
    public async Task GetStreamMetadata_ShouldAnswerWithTheMetadataAndItsRevision()
    {
        // Arrange
        A.CallTo(() => _store.GetStreamMetadataAsync("orders-1", A<CancellationToken>._))
            .Returns(new StreamMetadataResult("orders-1", 2, new StreamMetadata { MaxCount = 5, Custom = new JsonObject { ["owner"] = "billing" } }));
        A.CallTo(() => _store.GetStreamMetadataAsync("orders-2", A<CancellationToken>._))
            .Returns(new StreamMetadataResult("orders-2", -1, StreamMetadata.Empty));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var set = await client.GetStreamMetadataAsync(new GetStreamMetadataRequest { Stream = "orders-1" }, cancellationToken: TestContext.Current.CancellationToken);
        var never = await client.GetStreamMetadataAsync(new GetStreamMetadataRequest { Stream = "orders-2" }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        set.Revision.ShouldBe(2);
        set.Metadata.MaxCount.ShouldBe(5);
        set.Metadata.HasTruncateBefore.ShouldBeFalse();
        set.Metadata.Custom.ToStringUtf8().ShouldBe("{\"owner\":\"billing\"}");
        never.Revision.ShouldBe(-1);
        never.Metadata.HasMaxCount.ShouldBeFalse();
        never.Metadata.Custom.IsEmpty.ShouldBeTrue();
        set.Reset.ShouldBeFalse();
    }

    [Fact]
    public async Task GetStreamMetadata_WhenTheStreamIsReset_ShouldSaySoBesideTheTruncation()
    {
        // Arrange
        A.CallTo(() => _store.GetStreamMetadataAsync("orders-1", A<CancellationToken>._))
            .Returns(new StreamMetadataResult("orders-1", 3, new StreamMetadata { TruncateBefore = 6 }, Reset: true));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var response = await client.GetStreamMetadataAsync(new GetStreamMetadataRequest { Stream = "orders-1" }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        response.Reset.ShouldBeTrue();
        response.Metadata.TruncateBefore.ShouldBe(6);
    }

    [Fact]
    public async Task Reset_AtTheDefaultDeletionLevel_ShouldBeAllowedAndAnswerTheMetadataEvent()
    {
        // Arrange: a reset hides and removes nothing, so no level stands in its way.
        A.CallTo(() => _store.ResetAsync("orders-1", StreamState.StreamRevision(5), A<CancellationToken>._)).Returns(new AppendResult(2, 40));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var response = await client.ResetAsync(new ResetRequest { Stream = "orders-1", ExpectedRevision = 5 }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        _serverOptions.Deletion.Allowed.ShouldBe(DeletionLevel.None);
        response.Revision.ShouldBe(2);
        response.Position.ShouldBe(40);
    }

    [Theory]
    [InlineData("missing", StatusCode.NotFound, "STREAM_NOT_FOUND")]
    [InlineData("archived", StatusCode.FailedPrecondition, "STREAM_DELETED")]
    public async Task Reset_WhenTheStoreRefuses_ShouldAnswerTheContractsError(string why, StatusCode status, string reason)
    {
        // Arrange
        Exception refusal = why == "missing" ? new StreamNotFoundException("orders-1") : new StreamDeletedException("orders-1");
        A.CallTo(() => _store.ResetAsync("orders-1", StreamState.Any, A<CancellationToken>._)).ThrowsAsync(refusal);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.ResetAsync(new ResetRequest { Stream = "orders-1", ExpectedRevision = -2 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(status);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe(reason);
    }

    [Theory]
    [InlineData("$all")]
    [InlineData("$$orders-1")]
    public async Task Reset_WhenTheStreamIsNotPlain_ShouldRejectTheName(string stream)
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.ResetAsync(new ResetRequest { Stream = stream, ExpectedRevision = -2 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert: the strict store would have thrown on any call.
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_STREAM_NAME");
    }

    [Fact]
    public async Task Read_WhenReadingAMetadataStream_ShouldReadItAsThePlainStreamItIsInTheStore()
    {
        // Arrange
        A.CallTo(() => _store.ReadEventualAsync("$$orders-1", Direction.Forwards, null, 10, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("$$orders-1", 0, 12, StreamNames.MetadataEventType)]));
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$$orders-1", Start = new(), Count = 10 });

        // Assert
        messages.Count.ShouldBe(2);
        messages[1].Event.EventType.ShouldBe("$metadata");
    }

    [Theory]
    [InlineData("$$")]
    [InlineData("$$$all")]
    public async Task Read_WhenTheMetadataStreamNamesNoPlainStream_ShouldRejectTheName(string stream)
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => ReadAll(client, new ReadRequest { Stream = stream, Start = new(), Count = 1 }));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_STREAM_NAME");
    }

    [Fact]
    public async Task Read_WhenReadingTheMetadataEvents_ShouldReadTheEventTypeStreamOfMetadata()
    {
        // Arrange: $mde is $et-$metadata under another name.
        var metadataEvents = new VirtualStreamName(VirtualStreamKind.EventType, StreamNames.MetadataEventType);
        _tail.Advance(20);
        A.CallTo(() => _store.VirtualHeadAsync(metadataEvents, 20, A<CancellationToken>._)).Returns(new StreamHead(12, 12));
        A.CallTo(() => _store.ReadVirtualAsync(metadataEvents, Direction.Forwards, 12, 20, 10, A<CancellationToken>._))
            .Returns([Record("$$orders-1", 0, 12, StreamNames.MetadataEventType)]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$mde", Start = new(), Count = 10 });

        // Assert
        messages[0].Head.ShouldSatisfyAllConditions(head => head.First.ShouldBe(12), head => head.Last.ShouldBe(12));
        messages.Skip(1).ShouldHaveSingleItem().Event.Stream.ShouldBe("$$orders-1");
    }

    [Fact]
    public async Task Read_WhenReadingTheDataEvents_ShouldReadThemAsAVirtualStreamByPosition()
    {
        // Arrange
        var dataEvents = new VirtualStreamName(VirtualStreamKind.DataEvents, string.Empty);
        _tail.Advance(20);
        A.CallTo(() => _store.VirtualHeadAsync(dataEvents, 20, A<CancellationToken>._)).Returns(new StreamHead(5, 17));
        A.CallTo(() => _store.ReadVirtualAsync(dataEvents, Direction.Forwards, 5, 20, 10, A<CancellationToken>._))
            .Returns([Record("orders-1", 0, 5, "a"), Record("orders-1", 1, 17, "b")]);
        var client = new Streams.StreamsClient(_channel);

        // Act
        var messages = await ReadAll(client, new ReadRequest { Stream = "$de", Start = new(), Count = 10 });

        // Assert
        messages.Skip(1).Select(message => message.Event.Position).ShouldBe([5, 17]);
    }

    [Fact]
    public async Task Read_WhenTheDataEventsAreAskedForByOrdinal_ShouldReject()
    {
        // Arrange
        var client = new Streams.StreamsClient(_channel);
        var request = new ReadRequest { Stream = "$de", Start = new(), Count = 1, Numbering = Protocols.Grpc.V1.Numbering.Ordinal };

        // Act
        var exception = await Should.ThrowAsync<RpcException>(() => ReadAll(client, request));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_ARGUMENT");
    }

    private sealed class TestOptions : NightingaleOptionsBase
    {
    }

    /// <summary>Sends the parts of an append to several streams; a refusal part way surfaces from the response.</summary>
    private static async Task<AppendToStreamsResponse> AppendToStreamsAsync(Streams.StreamsClient client, params AppendToStreamsRequest[] parts)
    {
        using var call = client.AppendToStreams(cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            foreach (var part in parts)
            {
                await call.RequestStream.WriteAsync(part, TestContext.Current.CancellationToken);
            }

            await call.RequestStream.CompleteAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or RpcException)
        {
            // The server ended the call at the part it would not take; the answer says why.
        }

        return await call.ResponseAsync;
    }

    private static async Task<AppendResponse> AppendOf(Streams.StreamsClient client, string stream, int count)
    {
        using var call = client.Append(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Options = new AppendOptions { Stream = stream, ExpectedRevision = -1 } }, TestContext.Current.CancellationToken);
        try
        {
            for (var i = 0; i < count; i++)
            {
                await call.RequestStream.WriteAsync(new AppendRequest { Event = Proposed(Guid.NewGuid(), "order_placed", "{}") }, TestContext.Current.CancellationToken);
            }

            await call.RequestStream.CompleteAsync();
        }
        catch (InvalidOperationException)
        {
            // The server ended the call at the first event too many; the answer says why.
        }
        catch (RpcException)
        {
            // The same, seen from the write.
        }

        return await call.ResponseAsync;
    }

    /// <summary>Appends these events under an expected revision; a refusal from the server is the answer's.</summary>
    private static async Task<AppendResponse> AppendAsync(Streams.StreamsClient client, string stream, long expected, params ProposedEvent[] events)
    {
        using var call = client.Append(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Options = new AppendOptions { Stream = stream, ExpectedRevision = expected } }, TestContext.Current.CancellationToken);
        try
        {
            foreach (var proposed in events)
            {
                await call.RequestStream.WriteAsync(new AppendRequest { Event = proposed }, TestContext.Current.CancellationToken);
            }

            await call.RequestStream.CompleteAsync();
        }
        catch (InvalidOperationException)
        {
            // The server ended the call at a refused event; the answer says why.
        }
        catch (RpcException)
        {
            // The same, seen from the write.
        }

        return await call.ResponseAsync;
    }

    private static EventRecord Numbered(string stream, long revision, long position, long ordinal) =>
        Record(stream, revision, position, "a") with { Ordinal = ordinal };

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

    /// <summary>Reads the next messages of a subscription, which never completes on its own.</summary>
    private static async Task<List<ReadResponse>> Next(AsyncServerStreamingCall<ReadResponse> call, int count)
    {
        var messages = new List<ReadResponse>();
        while (messages.Count < count && await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken))
        {
            messages.Add(call.ResponseStream.Current);
        }

        return messages;
    }
}
