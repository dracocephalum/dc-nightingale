using System.Globalization;

using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;

using Status = Google.Rpc.Status;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// Builds the failures a service raises, in the one shape the contract promises: a rich status whose
/// details hold a single <see cref="ErrorInfo"/> with the reason and the metadata that reason lists.
/// Services throw what this class returns and never construct an <see cref="RpcException"/> directly,
/// so the client can map on the reason alone.
/// </summary>
public static class NightingaleErrors
{
    /// <summary>The stream is in a different state than the caller asserted.</summary>
    /// <param name="conflict">The conflict the store reported.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException RevisionConflict(RevisionConflictException conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        return Build(
            StatusCode.FailedPrecondition,
            ErrorReason.RevisionConflict,
            conflict.Message,
            ("stream", conflict.Stream),
            ("expected", conflict.Expected.ToInt64().ToString(CultureInfo.InvariantCulture)),
            ("actual", conflict.ActualRevision.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The stream was soft-deleted.</summary>
    /// <param name="stream">The stream name.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException StreamDeleted(string stream) =>
        Build(StatusCode.FailedPrecondition, ErrorReason.StreamDeleted, $"Stream '{stream}' has been deleted.", ("stream", stream));

    /// <summary>The stream does not exist, where the operation needs it to.</summary>
    /// <param name="stream">The stream name.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException StreamNotFound(string stream) =>
        Build(StatusCode.NotFound, ErrorReason.StreamNotFound, $"Stream '{stream}' was not found.", ("stream", stream));

    /// <summary>The stream name is empty, too long, or reserved where a plain stream is required.</summary>
    /// <param name="stream">The stream name as received.</param>
    /// <param name="why">What is wrong with it.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException InvalidStreamName(string stream, string why) =>
        Build(StatusCode.InvalidArgument, ErrorReason.InvalidStreamName, $"Invalid stream name: {why}.", ("stream", stream));

    /// <summary>A filter was sent for a stream other than <c>$all</c>.</summary>
    /// <param name="stream">The stream name.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException FilterNotAllowed(string stream) =>
        Build(StatusCode.InvalidArgument, ErrorReason.FilterNotAllowed, "A filter is only allowed on $all.", ("stream", stream));

    /// <summary>Anything else wrong with a request.</summary>
    /// <param name="message">What is wrong.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException InvalidArgument(string message) =>
        Build(StatusCode.InvalidArgument, ErrorReason.InvalidArgument, message);

    /// <summary>The host has not enabled this kind of deletion.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="operation">What was refused: delete or tombstone.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException DeletionDisabled(string stream, string operation) =>
        Build(StatusCode.PermissionDenied, ErrorReason.DeletionDisabled, $"{operation} is disabled on this server; the host enables it under Nightingale:Deletion.", ("stream", stream), ("operation", operation));

    /// <summary>A part of the contract this server does not implement yet. Not a reason: the status code says it all.</summary>
    /// <param name="feature">What was asked for.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException NotImplemented(string feature) =>
        new(new Grpc.Core.Status(StatusCode.Unimplemented, $"{feature} is not implemented yet."));

    private static RpcException Build(StatusCode code, ErrorReason reason, string message, params (string Key, string Value)[] metadata)
    {
        var info = new ErrorInfo { Domain = ErrorReasons.Domain, Reason = ErrorReasons.NameOf(reason) };
        foreach (var (key, value) in metadata)
        {
            info.Metadata[key] = value;
        }

        var status = new Status { Code = (int)code, Message = message, Details = { Any.Pack(info) } };
        return status.ToRpcException();
    }
}
