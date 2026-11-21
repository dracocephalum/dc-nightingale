using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using FakeItEasy;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>
/// A call invoker whose <c>Read</c> answers with a scripted response stream, so the client's
/// pumps are tested without a server. The request the client sent is captured for assertions.
/// </summary>
internal static class FakeStreamsCall
{
    /// <summary>Builds an invoker that answers every <c>Read</c> with the given responses, then the given status.</summary>
    public static (CallInvoker Invoker, Func<ReadRequest?> Request) Read(IEnumerable<ReadResponse> responses, Status? endStatus = null) =>
        Read(responses, endStatus is { StatusCode: not StatusCode.OK } status ? new RpcException(status) : null);

    /// <summary>Builds an invoker that answers every <c>Read</c> with the given responses, then the given failure, which can carry a reason.</summary>
    public static (CallInvoker Invoker, Func<ReadRequest?> Request) Read(IEnumerable<ReadResponse> responses, RpcException? failure)
    {
        ReadRequest? captured = null;
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncServerStreamingCall(A<Method<ReadRequest, ReadResponse>>._, A<string?>._, A<CallOptions>._, A<ReadRequest>._))
            .ReturnsLazily(call =>
            {
                captured = call.GetArgument<ReadRequest>(3);
                var reader = new ScriptedReader(responses, failure);
                return new AsyncServerStreamingCall<ReadResponse>(reader, Task.FromResult(new Metadata()), () => reader.Status, () => new Metadata(), () => { });
            });
        return (invoker, () => captured);
    }

    private sealed class ScriptedReader(IEnumerable<ReadResponse> responses, RpcException? failure) : IAsyncStreamReader<ReadResponse>
    {
        private readonly IEnumerator<ReadResponse> _responses = responses.GetEnumerator();

        public Status Status { get; private set; } = Status.DefaultSuccess;

        public ReadResponse Current => _responses.Current;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.MoveNext())
            {
                return Task.FromResult(true);
            }

            if (failure is not null)
            {
                Status = failure.Status;
                throw failure;
            }

            return Task.FromResult(false);
        }
    }
}
