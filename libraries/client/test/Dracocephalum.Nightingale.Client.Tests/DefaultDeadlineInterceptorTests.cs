using Dracocephalum.Nightingale.Protocol.V1;
using FakeItEasy;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

public sealed class DefaultDeadlineInterceptorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Method<DeleteRequest, DeleteResponse> Unary = Method<DeleteRequest, DeleteResponse>(MethodType.Unary);
    private static readonly Method<DeleteRequest, DeleteResponse> ServerStreaming = Method<DeleteRequest, DeleteResponse>(MethodType.ServerStreaming);

    private readonly CallInvoker _inner = A.Fake<CallInvoker>();
    private readonly CallInvoker _sut;

    public DefaultDeadlineInterceptorTests()
    {
        _sut = _inner.Intercept(new DefaultDeadlineInterceptor(TimeSpan.FromSeconds(30), new FakeTimeProvider(Now)));
    }

    [Fact]
    public void AsyncUnaryCall_WhenTheCallCarriesNoDeadline_ShouldGiveItTheDefault()
    {
        // Act
        _sut.AsyncUnaryCall(Unary, null, default, new DeleteRequest());

        // Assert
        A.CallTo(() => _inner.AsyncUnaryCall(Unary, null, A<CallOptions>.That.Matches(options => options.Deadline == Now.UtcDateTime.AddSeconds(30)), A<DeleteRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void AsyncUnaryCall_WhenTheCallCarriesADeadline_ShouldKeepIt()
    {
        // Arrange
        var own = Now.UtcDateTime.AddSeconds(5);

        // Act
        _sut.AsyncUnaryCall(Unary, null, new CallOptions(deadline: own), new DeleteRequest());

        // Assert
        A.CallTo(() => _inner.AsyncUnaryCall(Unary, null, A<CallOptions>.That.Matches(options => options.Deadline == own), A<DeleteRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void AsyncClientStreamingCall_WhenTheCallCarriesNoDeadline_ShouldGiveItTheDefault()
    {
        // Arrange
        var method = Method<DeleteRequest, DeleteResponse>(MethodType.ClientStreaming);

        // Act
        _sut.AsyncClientStreamingCall(method, null, default);

        // Assert
        A.CallTo(() => _inner.AsyncClientStreamingCall(method, null, A<CallOptions>.That.Matches(options => options.Deadline == Now.UtcDateTime.AddSeconds(30))))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void AsyncServerStreamingCall_ShouldBeLeftWithoutADeadline()
    {
        // Act: a read or a subscription runs as long as its caller wants.
        _sut.AsyncServerStreamingCall(ServerStreaming, null, default, new DeleteRequest());

        // Assert
        A.CallTo(() => _inner.AsyncServerStreamingCall(ServerStreaming, null, A<CallOptions>.That.Matches(options => options.Deadline == null), A<DeleteRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    private static Method<TRequest, TResponse> Method<TRequest, TResponse>(MethodType type)
        where TRequest : class, Google.Protobuf.IMessage<TRequest>, new()
        where TResponse : class, Google.Protobuf.IMessage<TResponse>, new() =>
        new(
            type,
            "test",
            type.ToString(),
            Marshallers.Create(message => Google.Protobuf.MessageExtensions.ToByteArray(message), bytes => new Google.Protobuf.MessageParser<TRequest>(() => new TRequest()).ParseFrom(bytes)),
            Marshallers.Create(message => Google.Protobuf.MessageExtensions.ToByteArray(message), bytes => new Google.Protobuf.MessageParser<TResponse>(() => new TResponse()).ParseFrom(bytes)));
}
