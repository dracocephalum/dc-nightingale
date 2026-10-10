using System.Globalization;

using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Google.Rpc;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Client;

/// <summary>
/// Turns a failed call into the exception a caller can act on. The server promises one
/// <see cref="ErrorInfo"/> detail with a reason from the contract; the mapping keys on that reason
/// and never on the status code or the message. A failure without the detail, or with a reason
/// this build does not know, is surfaced as the raw <see cref="RpcException"/>.
/// </summary>
internal static class NightingaleErrorMapping
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
                StreamState.FromInt64(GetNumber(info, "expected", (long)ExpectedRevision.Any)),
                GetNumber(info, "actual", -1)),
            ErrorReason.StreamDeleted => new StreamDeletedException(stream),
            ErrorReason.StreamNotFound => new StreamNotFoundException(stream),
            ErrorReason.DeletionDisabled => new DeletionDisabledException(exception.Status.Detail),
            ErrorReason.GroupExists => new GroupExistsException(stream, GetText(info, "group")),
            ErrorReason.GroupNotFound => new GroupNotFoundException(stream, GetText(info, "group")),
            ErrorReason.GroupOwnedElsewhere => new GroupOwnedElsewhereException(stream, GetText(info, "group"), GetText(info, "owner"), GetAddress(info)),
            ErrorReason.ConsumerLimitReached => new ConsumerLimitReachedException(stream, GetText(info, "group")),
            ErrorReason.AppendSizeExceeded => new AppendSizeExceededException(stream, (int)GetNumber(info, "limit", 0)),
            ErrorReason.GroupUpdated => new GroupUpdatedException(stream, GetText(info, "group")),
            ErrorReason.ParkedMessageNotFound => new ParkedMessageNotFoundException(stream, GetText(info, "group")),
            ErrorReason.OrdinalsNotEnabled => new OrdinalsNotEnabledException(stream),
            ErrorReason.AuthenticationFailed => new AuthenticationFailedException(exception.Status.Detail),
            ErrorReason.AccessDenied => Enum.TryParse<CredentialRole>(GetText(info, "required"), true, out var required) ? new AccessDeniedException(exception.Status.Detail, required) : new AccessDeniedException(exception.Status.Detail),
            ErrorReason.TenantNotFound => new TenantNotFoundException(Guid.TryParse(GetText(info, "tenant"), out var missing) ? missing : null),
            ErrorReason.CredentialExists => new CredentialExistsException(exception.Status.Detail),
            ErrorReason.CredentialNotFound => new CredentialNotFoundException(exception.Status.Detail),
            ErrorReason.TenantExists => new TenantExistsException(exception.Status.Detail),
            ErrorReason.TenantDisabled => new TenantDisabledException(Guid.TryParse(GetText(info, "tenant"), out var disabled) ? disabled : Guid.Empty),
            ErrorReason.InvalidStreamName or ErrorReason.FilterNotAllowed or ErrorReason.InvalidArgument =>
                new ArgumentException(exception.Status.Detail),
            _ => exception,
        };
    }

    private static string GetText(ErrorInfo info, string key) =>
        info.Metadata.TryGetValue(key, out var text) ? text : string.Empty;

    private static Uri? GetAddress(ErrorInfo info) =>
        info.Metadata.TryGetValue("address", out var text) && Uri.TryCreate(text, UriKind.Absolute, out var address) ? address : null;

    private static long GetNumber(ErrorInfo info, string key, long fallback) =>
        info.Metadata.TryGetValue(key, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
