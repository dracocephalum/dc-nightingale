using Dracocephalum.Nightingale.Protocol.V1;
using FakeItEasy;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>
/// Listing, skipping and replaying a group's parked messages over scripted unary calls: what the
/// client asks and what it makes of the answer.
/// </summary>
public sealed class ParkedMessagesTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid EventId = Guid.Parse("0198f3a2-0000-7000-8000-000000000042");

    [Fact]
    public async Task ListParked_ShouldAskForAPageAndReturnItsMessages()
    {
        // Arrange
        var asked = new List<ListMessagesRequest>();
        var invoker = Answering(asked, new ListParkedResponse
        {
            Messages = { new ParkedMessage { Number = 8, EventId = EventId.ToString("D"), Reason = "poison", RetryCount = 2, ParkedAt = Timestamp.FromDateTimeOffset(At), Position = 900, Revision = 3 } },
        });
        await using var sut = new NightingaleClient(invoker);

        // Act
        var page = await sut.ListParkedMessagesAsync("$ce-orders", "billing", after: 7, limit: 25, TestContext.Current.CancellationToken);
        await sut.ListParkedMessagesAsync("$ce-orders", "billing", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        page.ShouldHaveSingleItem().ShouldBe(new ParkedMessageInfo(8, EventId, "poison", 2, At, 900, 3));
        asked[0].ShouldBe(new ListMessagesRequest { Stream = "$ce-orders", Group = "billing", After = 7, Limit = 25 });
        asked[1].HasAfter.ShouldBeFalse();
        asked[1].Limit.ShouldBe(100);
    }

    [Fact]
    public async Task ListOutbox_ShouldReturnItsMessagesWithWhenTheyAreDue()
    {
        // Arrange
        var invoker = Answering(new List<ListMessagesRequest>(), new ListOutboxResponse
        {
            Messages = { new OutboxMessage { Number = 3, EventId = EventId.ToString("D"), Reason = "poison", RetryCount = 2, DueAt = Timestamp.FromDateTimeOffset(At), Position = 900, Revision = 3 } },
        });
        await using var sut = new NightingaleClient(invoker);

        // Act
        var page = await sut.ListOutboxMessagesAsync("orders-1", "billing", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        page.ShouldHaveSingleItem().ShouldBe(new OutboxMessageInfo(3, EventId, "poison", 2, At, 900, 3));
    }

    [Fact]
    public async Task Skip_ShouldAskForAllOneOrThoseBelowANumberAndReturnHowManyWent()
    {
        // Arrange
        var asked = new List<SkipParkedRequest>();
        var invoker = Answering(asked, new SkipParkedResponse { Skipped = 2 });
        await using var sut = new NightingaleClient(invoker);

        // Act
        var all = await sut.SkipParkedMessagesAsync("orders-1", "billing", cancellationToken: TestContext.Current.CancellationToken);
        await sut.SkipParkedMessagesAsync("orders-1", "billing", position: 7, TestContext.Current.CancellationToken);
        await sut.SkipParkedMessagesBeforeAsync("orders-1", "billing", 4, TestContext.Current.CancellationToken);

        // Assert
        all.ShouldBe(2);
        asked.Select(request => request.WhichCase).ShouldBe([SkipParkedRequest.WhichOneofCase.All, SkipParkedRequest.WhichOneofCase.Position, SkipParkedRequest.WhichOneofCase.Before]);
        asked[1].Position.ShouldBe(7);
        asked[2].Before.ShouldBe(4);
    }

    [Fact]
    public async Task Skip_WhenTheOneIsNotParked_ShouldFailAsNotFound()
    {
        // Arrange
        var info = new Google.Rpc.ErrorInfo { Domain = "nightingale", Reason = "PARKED_MESSAGE_NOT_FOUND", Metadata = { ["stream"] = "orders-1", ["group"] = "billing", ["position"] = "9" } };
        var failure = new Google.Rpc.Status { Code = (int)StatusCode.NotFound, Message = "PARKED_MESSAGE_NOT_FOUND", Details = { Any.Pack(info) } }.ToRpcException();
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<SkipParkedRequest, SkipParkedResponse>>._, A<string?>._, A<CallOptions>._, A<SkipParkedRequest>._))
            .ReturnsLazily(_ => Unary(Task.FromException<SkipParkedResponse>(failure)));
        await using var sut = new NightingaleClient(invoker);

        // Act & Assert
        await Should.ThrowAsync<ParkedMessageNotFoundException>(() => sut.SkipParkedMessagesAsync("orders-1", "billing", position: 9, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplayBefore_ShouldAskForThoseBelowTheNumber()
    {
        // Arrange
        var asked = new List<ReplayParkedRequest>();
        var invoker = Answering(asked, new ReplayParkedResponse { Replayed = 3 });
        await using var sut = new NightingaleClient(invoker);

        // Act
        var replayed = await sut.ReplayParkedMessagesBeforeAsync("orders-1", "billing", 5, TestContext.Current.CancellationToken);

        // Assert
        replayed.ShouldBe(3);
        asked.ShouldHaveSingleItem().ShouldBe(new ReplayParkedRequest { Stream = "orders-1", Group = "billing", Before = 5 });
    }

    private static CallInvoker Answering<TRequest, TResponse>(List<TRequest> asked, TResponse response)
        where TRequest : class
        where TResponse : class
    {
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<TRequest, TResponse>>._, A<string?>._, A<CallOptions>._, A<TRequest>._))
            .ReturnsLazily(call =>
            {
                asked.Add(call.GetArgument<TRequest>(3)!);
                return Unary(Task.FromResult(response));
            });
        return invoker;
    }

    private static AsyncUnaryCall<TResponse> Unary<TResponse>(Task<TResponse> response) =>
        new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
}
