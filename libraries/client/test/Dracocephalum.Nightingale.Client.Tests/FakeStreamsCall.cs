using Dracocephalum.Nightingale.Protocol.V1;
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
    public static (CallInvoker Invoker, Func<ReadRequest?> Request) Read(IEnumerable<ReadResponse> responses, Status? endStatus = null)
    {
        ReadRequest? captured = null;
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncServerStreamingCall(A<Method<ReadRequest, ReadResponse>>._, A<string?>._, A<CallOptions>._, A<ReadRequest>._))
            .ReturnsLazily(call =>
            {
                captured = call.GetArgument<ReadRequest>(3);
                var reader = new ScriptedReader(responses, endStatus ?? Status.DefaultSuccess);
                return new AsyncServerStreamingCall<ReadResponse>(reader, Task.FromResult(new Metadata()), () => reader.Status, () => new Metadata(), () => { });
            });
        return (invoker, () => captured);
    }

    private sealed class ScriptedReader(IEnumerable<ReadResponse> responses, Status endStatus) : IAsyncStreamReader<ReadResponse>
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

            if (endStatus.StatusCode != StatusCode.OK)
            {
                Status = endStatus;
                throw new RpcException(endStatus);
            }

            return Task.FromResult(false);
        }
    }
}
