using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

public sealed class NightingaleErrorMappingTests
{
    [Fact]
    public void ToException_WhenRevisionConflict_ShouldCarryStreamExpectedAndActual()
    {
        // Arrange
        var failure = Failure(StatusCode.FailedPrecondition, "REVISION_CONFLICT", ("stream", "orders-1"), ("expected", "3"), ("actual", "5"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        var conflict = mapped.ShouldBeOfType<RevisionConflictException>();
        conflict.Stream.ShouldBe("orders-1");
        conflict.Expected.ShouldBe(StreamState.StreamRevision(3));
        conflict.ActualRevision.ShouldBe(5);
    }

    [Fact]
    public void ToException_WhenRevisionConflictOnNoStream_ShouldMapTheNamedState()
    {
        // Arrange
        var failure = Failure(StatusCode.FailedPrecondition, "REVISION_CONFLICT", ("stream", "orders-1"), ("expected", "-1"), ("actual", "0"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeOfType<RevisionConflictException>().Expected.ShouldBe(StreamState.NoStream);
    }

    [Theory]
    [InlineData("STREAM_DELETED", typeof(StreamDeletedException))]
    [InlineData("STREAM_NOT_FOUND", typeof(StreamNotFoundException))]
    [InlineData("INVALID_STREAM_NAME", typeof(ArgumentException))]
    [InlineData("FILTER_NOT_ALLOWED", typeof(ArgumentException))]
    [InlineData("INVALID_ARGUMENT", typeof(ArgumentException))]
    public void ToException_WhenReasonIsKnown_ShouldMapToItsException(string reason, System.Type expected)
    {
        // Arrange
        var failure = Failure(StatusCode.InvalidArgument, reason, ("stream", "orders-1"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeOfType(expected);
    }

    [Fact]
    public void ToException_WhenReasonIsUnknown_ShouldReturnTheOriginal()
    {
        // Arrange
        var failure = Failure(StatusCode.Internal, "SOMETHING_NEW");

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeSameAs(failure);
    }

    [Fact]
    public void ToException_WhenThereIsNoDetail_ShouldReturnTheOriginal()
    {
        // Arrange
        var failure = new RpcException(new Grpc.Core.Status(StatusCode.Unavailable, "gone"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeSameAs(failure);
    }

    private static RpcException Failure(StatusCode code, string reason, params (string Key, string Value)[] metadata)
    {
        var info = new ErrorInfo { Domain = "nightingale", Reason = reason };
        foreach (var (key, value) in metadata)
        {
            info.Metadata[key] = value;
        }

        return new Google.Rpc.Status { Code = (int)code, Message = "failed", Details = { Any.Pack(info) } }.ToRpcException();
    }
}
