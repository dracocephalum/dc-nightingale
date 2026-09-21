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

    /// <summary>A group with that name exists on the stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException GroupExists(string stream, string group) =>
        Build(StatusCode.AlreadyExists, ErrorReason.GroupExists, $"Group '{group}' already exists on stream '{stream}'.", ("stream", stream), ("group", group));

    /// <summary>No such group.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException GroupNotFound(string stream, string group) =>
        Build(StatusCode.NotFound, ErrorReason.GroupNotFound, $"Group '{group}' was not found on stream '{stream}'.", ("stream", stream), ("group", group));

    /// <summary>Another instance owns the group; the answer names it and, when it advertised one, its address, so the client can go there.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="holder">The owning instance and where it is reached.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException GroupOwnedElsewhere(string stream, string group, LeaseHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        var message = holder.Address is null
            ? $"Group '{group}' on stream '{stream}' is owned by instance {holder.Owner}, which advertises no address."
            : $"Group '{group}' on stream '{stream}' is owned by instance {holder.Owner} at {holder.Address}.";
        return Build(StatusCode.Unavailable, ErrorReason.GroupOwnedElsewhere, message, ("stream", stream), ("group", group), ("owner", holder.Owner), ("address", holder.Address?.ToString() ?? string.Empty));
    }

    /// <summary>The group has as many consumers as it allows.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="limit">The limit.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException ConsumerLimitReached(string stream, string group, int limit) =>
        Build(StatusCode.ResourceExhausted, ErrorReason.ConsumerLimitReached, $"Group '{group}' on stream '{stream}' already has {limit} consumer(s).", ("stream", stream), ("group", group), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>No parked message at that position.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="group">The group.</param>
    /// <param name="position">The position.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException ParkedMessageNotFound(string stream, string group, long position) =>
        Build(StatusCode.NotFound, ErrorReason.ParkedMessageNotFound, $"Group '{group}' on stream '{stream}' has no parked message at position {position}.", ("stream", stream), ("group", group), ("position", position.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Ordinal numbering was asked for on a store initialized without ordinals.</summary>
    /// <param name="stream">The virtual stream.</param>
    /// <returns>The exception to throw.</returns>
    public static RpcException OrdinalsNotEnabled(string stream) =>
        Build(StatusCode.FailedPrecondition, ErrorReason.OrdinalsNotEnabled, $"Stream '{stream}' cannot be read by ordinal: the store was not initialized with ordinals.", ("stream", stream));

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
