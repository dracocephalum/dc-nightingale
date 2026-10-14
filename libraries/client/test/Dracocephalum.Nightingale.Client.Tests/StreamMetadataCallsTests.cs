using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using FakeItEasy;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>Setting and reading a stream's metadata over scripted unary calls: what the client sends, what it makes of the answer, and what a conflict becomes.</summary>
public sealed class StreamMetadataCallsTests
{
    [Fact]
    public async Task SetStreamMetadataAsync_ShouldSendTheSettingsUnderTheMetadataStreamsExpectation()
    {
        // Arrange
        SetStreamMetadataRequest? asked = null;
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<SetStreamMetadataRequest, SetStreamMetadataResponse>>._, A<string?>._, A<CallOptions>._, A<SetStreamMetadataRequest>._))
            .ReturnsLazily(call =>
            {
                asked = call.GetArgument<SetStreamMetadataRequest>(3);
                return Unary(Task.FromResult(new SetStreamMetadataResponse { Revision = 0, Position = 41 }));
            });
        await using var sut = new NightingaleClient(invoker);
        var metadata = new Dracocephalum.Nightingale.StreamMetadata { MaxAge = TimeSpan.FromHours(1), Custom = new JsonObject { ["owner"] = "billing" } };

        // Act
        var result = await sut.SetStreamMetadataAsync("orders-1", StreamState.NoStream, metadata, TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(new AppendResult(0, 41));
        var request = asked.ShouldNotBeNull();
        request.Stream.ShouldBe("orders-1");
        request.ExpectedRevision.ShouldBe(StreamState.NoStream.ToInt64());
        request.Metadata.MaxAge.ToTimeSpan().ShouldBe(TimeSpan.FromHours(1));
        request.Metadata.HasMaxCount.ShouldBeFalse();
        request.Metadata.Custom.ToStringUtf8().ShouldBe("{\"owner\":\"billing\"}");
    }

    [Fact]
    public async Task GetStreamMetadataAsync_ShouldGiveTheSettingsAndTheRevision()
    {
        // Arrange
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<GetStreamMetadataRequest, GetStreamMetadataResponse>>._, A<string?>._, A<CallOptions>._, A<GetStreamMetadataRequest>._))
            .Returns(Unary(Task.FromResult(new GetStreamMetadataResponse
            {
                Revision = 3,
                Metadata = new Protocols.Grpc.V1.StreamMetadata { MaxCount = 10, Custom = ByteString.CopyFromUtf8("{\"owner\":\"billing\"}") },
            })));
        await using var sut = new NightingaleClient(invoker);

        // Act
        var result = await sut.GetStreamMetadataAsync("orders-1", TestContext.Current.CancellationToken);

        // Assert
        result.Stream.ShouldBe("orders-1");
        result.Revision.ShouldBe(3);
        result.Exists.ShouldBeTrue();
        result.Metadata.MaxCount.ShouldBe(10);
        result.Metadata.MaxAge.ShouldBeNull();
        result.Metadata.Custom.ShouldNotBeNull()["owner"].ShouldNotBeNull().GetValue<string>().ShouldBe("billing");
        result.Reset.ShouldBeFalse();
    }

    [Fact]
    public async Task GetStreamMetadataAsync_WhenTheStreamIsReset_ShouldSaySoBesideTheTruncation()
    {
        // Arrange
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<GetStreamMetadataRequest, GetStreamMetadataResponse>>._, A<string?>._, A<CallOptions>._, A<GetStreamMetadataRequest>._))
            .Returns(Unary(Task.FromResult(new GetStreamMetadataResponse { Revision = 1, Metadata = new Protocols.Grpc.V1.StreamMetadata { TruncateBefore = 6 }, Reset = true })));
        await using var sut = new NightingaleClient(invoker);

        // Act
        var result = await sut.GetStreamMetadataAsync("orders-1", TestContext.Current.CancellationToken);

        // Assert
        result.Reset.ShouldBeTrue();
        result.Metadata.TruncateBefore.ShouldBe(6);
    }

    [Fact]
    public async Task ResetStreamAsync_ShouldSendTheStreamsExpectationAndGiveTheMetadataEvent()
    {
        // Arrange
        var invoker = A.Fake<CallInvoker>();
        ResetRequest? sent = null;
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<ResetRequest, ResetResponse>>._, A<string?>._, A<CallOptions>._, A<ResetRequest>._))
            .Invokes((Method<ResetRequest, ResetResponse> _, string? _, CallOptions _, ResetRequest request) => sent = request)
            .Returns(Unary(Task.FromResult(new ResetResponse { Revision = 2, Position = 40 })));
        await using var sut = new NightingaleClient(invoker);

        // Act
        var result = await sut.ResetStreamAsync("orders-1", StreamState.StreamRevision(5), TestContext.Current.CancellationToken);

        // Assert
        sent.ShouldNotBeNull().Stream.ShouldBe("orders-1");
        sent.ExpectedRevision.ShouldBe(5);
        result.ShouldBe(new AppendResult(2, 40));
    }

    [Fact]
    public async Task SetStreamMetadataAsync_WhenTheMetadataStreamMovedOn_ShouldThrowTheConflict()
    {
        // Arrange
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<SetStreamMetadataRequest, SetStreamMetadataResponse>>._, A<string?>._, A<CallOptions>._, A<SetStreamMetadataRequest>._))
            .Returns(Unary(Task.FromException<SetStreamMetadataResponse>(Failure(StatusCode.FailedPrecondition, "REVISION_CONFLICT", ("stream", "$$orders-1"), ("expected", "-1"), ("actual", "0")))));
        await using var sut = new NightingaleClient(invoker);

        // Act
        var conflict = await Should.ThrowAsync<RevisionConflictException>(() => sut.SetStreamMetadataAsync("orders-1", StreamState.NoStream, Dracocephalum.Nightingale.StreamMetadata.Empty, TestContext.Current.CancellationToken));

        // Assert
        conflict.Stream.ShouldBe("$$orders-1");
        conflict.ActualRevision.ShouldBe(0);
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
