using FakeItEasy;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>
/// Call invokers whose unary calls answer with a scripted response or failure, so the client's
/// calls are tested without a server, and the failure a server gives with a reason.
/// </summary>
internal static class FakeUnaryCall
{
    /// <summary>Builds an invoker that answers every call of one method with the response, keeping what was asked.</summary>
    public static CallInvoker Answering<TRequest, TResponse>(List<TRequest> asked, TResponse response)
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

    /// <summary>Builds an invoker that fails every call of one method.</summary>
    public static CallInvoker Failing<TRequest, TResponse>(RpcException failure)
        where TRequest : class
        where TResponse : class
    {
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<TRequest, TResponse>>._, A<string?>._, A<CallOptions>._, A<TRequest>._))
            .ReturnsLazily(_ => Unary(Task.FromException<TResponse>(failure)));
        return invoker;
    }

    /// <summary>Builds the failure a server gives: a status with the reason and its metadata in the error detail.</summary>
    public static RpcException Failure(StatusCode code, string reason, params (string Key, string Value)[] metadata)
    {
        var info = new Google.Rpc.ErrorInfo { Domain = "nightingale", Reason = reason };
        foreach (var (key, value) in metadata)
        {
            info.Metadata[key] = value;
        }

        return new Google.Rpc.Status { Code = (int)code, Message = "The server said why.", Details = { Any.Pack(info) } }.ToRpcException();
    }

    private static AsyncUnaryCall<TResponse> Unary<TResponse>(Task<TResponse> response) =>
        new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
}
