using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using FakeItEasy;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>
/// Changing a group's settings over scripted unary calls: what the client sends and leaves out,
/// that it follows the owner's address once, and what a consumer is told when its group changes.
/// </summary>
public sealed class UpdatePersistentSubscriptionTests
{
    [Fact]
    public async Task Update_ShouldSendTheTunablesAndLeaveOutTheStartAndTheNumbering()
    {
        // Arrange: the settings object says ordinal from the start; neither is the update's to change.
        var asked = new List<UpdateRequest>();
        var after = GroupSettings.Default with { Start = StreamPosition.From(3), MaxRetryCount = 7 };
        var invoker = Answering(asked, new UpdateResponse { Settings = after.ToWire() });
        var settings = GroupSettings.Default with { Start = StreamPosition.Start, Numbering = Numbering.Ordinal, MaxRetryCount = 7, MessageTimeout = TimeSpan.FromSeconds(5) };
        await using var sut = new NightingaleClient(invoker);

        // Act
        var result = await sut.UpdatePersistentSubscriptionAsync("orders-1", "billing", settings, TestContext.Current.CancellationToken);

        // Assert
        var request = asked.ShouldHaveSingleItem();
        request.Stream.ShouldBe("orders-1");
        request.Group.ShouldBe("billing");
        request.Settings.StartCase.ShouldBe(Protocol.V1.GroupSettings.StartOneofCase.None);
        request.Settings.Numbering.ShouldBe(Protocol.V1.Numbering.Unspecified);
        request.Settings.MaxRetryCount.ShouldBe(7);
        request.Settings.MessageTimeout.ToTimeSpan().ShouldBe(TimeSpan.FromSeconds(5));
        request.Settings.BufferSize.ShouldBe(GroupSettings.Default.BufferSize);
        result.ShouldBe(after);
    }

    [Fact]
    public async Task Update_WhenTheGroupRunsElsewhereWithAnAddress_ShouldMakeTheChangeThereOnce()
    {
        // Arrange
        var front = A.Fake<CallInvoker>();
        A.CallTo(() => front.AsyncUnaryCall(A<Method<UpdateRequest, UpdateResponse>>._, A<string?>._, A<CallOptions>._, A<UpdateRequest>._))
            .ReturnsLazily(_ => Unary(Task.FromException<UpdateResponse>(Failure(StatusCode.Unavailable, "GROUP_OWNED_ELSEWHERE", ("stream", "orders-1"), ("group", "billing"), ("owner", "node-2"), ("address", "http://node-2:5000/")))));
        var askedOfOwner = new List<UpdateRequest>();
        var owner = Answering(askedOfOwner, new UpdateResponse { Settings = GroupSettings.Default.ToWire() });
        var redirectedTo = new List<Uri>();
        await using var sut = new NightingaleClient(front, address =>
        {
            redirectedTo.Add(address);
            return owner;
        });

        // Act
        await sut.UpdatePersistentSubscriptionAsync("orders-1", "billing", GroupSettings.Default, TestContext.Current.CancellationToken);

        // Assert
        redirectedTo.ShouldBe([new Uri("http://node-2:5000/")]);
        askedOfOwner.ShouldHaveSingleItem().Group.ShouldBe("billing");
    }

    [Fact]
    public void GroupUpdated_ShouldMapToItsExceptionWithTheGroupsNames()
    {
        // Arrange: what a consumer's call ends with when its group's settings change.
        var failure = Failure(StatusCode.Aborted, "GROUP_UPDATED", ("stream", "orders-1"), ("group", "billing"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        var updated = mapped.ShouldBeOfType<GroupUpdatedException>();
        updated.Stream.ShouldBe("orders-1");
        updated.Group.ShouldBe("billing");
    }

    private static CallInvoker Answering(List<UpdateRequest> asked, UpdateResponse response)
    {
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<UpdateRequest, UpdateResponse>>._, A<string?>._, A<CallOptions>._, A<UpdateRequest>._))
            .ReturnsLazily(call =>
            {
                asked.Add(call.GetArgument<UpdateRequest>(3)!);
                return Unary(Task.FromResult(response));
            });
        return invoker;
    }

    private static AsyncUnaryCall<TResponse> Unary<TResponse>(Task<TResponse> response) =>
        new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

    private static RpcException Failure(StatusCode code, string reason, params (string Key, string Value)[] metadata)
    {
        var info = new Google.Rpc.ErrorInfo { Domain = "nightingale", Reason = reason };
        foreach (var (key, value) in metadata)
        {
            info.Metadata[key] = value;
        }

        var status = new Google.Rpc.Status { Code = (int)code, Message = reason, Details = { Any.Pack(info) } };
        return status.ToRpcException();
    }
}
