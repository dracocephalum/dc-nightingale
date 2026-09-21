using System.Globalization;

using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using Google.Rpc;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// Turns a failed call into the exception a caller can act on. The server promises one
/// <see cref="ErrorInfo"/> detail with a reason from the contract; the mapping keys on that reason
/// and never on the status code or the message. A failure without the detail, or with a reason
/// this build does not know, is surfaced as the raw <see cref="RpcException"/>.
/// </summary>
public static class NightingaleErrorMapping
{
    /// <summary>Maps a failed call.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>The exception to throw: a domain exception, an argument exception, or the original.</returns>
    public static Exception ToException(RpcException exception)
    {
        var info = exception.GetRpcStatus()?.GetDetail<ErrorInfo>();
        if (info is null || info.Domain != ErrorReasons.Domain)
        {
            return exception;
        }

        var stream = info.Metadata.TryGetValue("stream", out var name) ? name : string.Empty;
        return ErrorReasons.Parse(info.Reason) switch
        {
            ErrorReason.RevisionConflict => new RevisionConflictException(
                stream,
                StreamState.FromInt64(Number(info, "expected", (long)ExpectedRevision.Any)),
                Number(info, "actual", -1)),
            ErrorReason.StreamDeleted => new StreamDeletedException(stream),
            ErrorReason.StreamNotFound => new StreamNotFoundException(stream),
            ErrorReason.DeletionDisabled => new DeletionDisabledException(exception.Status.Detail),
            ErrorReason.GroupExists => new GroupExistsException(stream, Text(info, "group")),
            ErrorReason.GroupNotFound => new GroupNotFoundException(stream, Text(info, "group")),
            ErrorReason.GroupOwnedElsewhere => new GroupOwnedElsewhereException(stream, Text(info, "group"), Text(info, "owner"), Address(info)),
            ErrorReason.ConsumerLimitReached => new ConsumerLimitReachedException(stream, Text(info, "group")),
            ErrorReason.ParkedMessageNotFound => new ParkedMessageNotFoundException(stream, Text(info, "group")),
            ErrorReason.OrdinalsNotEnabled => new OrdinalsNotEnabledException(stream),
            ErrorReason.InvalidStreamName or ErrorReason.FilterNotAllowed or ErrorReason.InvalidArgument or ErrorReason.AppendSizeExceeded =>
                new ArgumentException(exception.Status.Detail),
            _ => exception,
        };
    }

    private static string Text(ErrorInfo info, string key) =>
        info.Metadata.TryGetValue(key, out var text) ? text : string.Empty;

    private static Uri? Address(ErrorInfo info) =>
        info.Metadata.TryGetValue("address", out var text) && Uri.TryCreate(text, UriKind.Absolute, out var address) ? address : null;

    private static long Number(ErrorInfo info, string key, long fallback) =>
        info.Metadata.TryGetValue(key, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
