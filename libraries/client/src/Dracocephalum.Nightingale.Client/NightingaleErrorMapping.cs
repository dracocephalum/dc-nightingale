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
            ErrorReason.InvalidStreamName or ErrorReason.FilterNotAllowed or ErrorReason.InvalidArgument or ErrorReason.AppendSizeExceeded =>
                new ArgumentException(exception.Status.Detail),
            _ => exception,
        };
    }

    private static long Number(ErrorInfo info, string key, long fallback) =>
        info.Metadata.TryGetValue(key, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
