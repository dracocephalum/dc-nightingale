using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server.Grpc;

/// <summary>What the <c>Streams</c> service says of a read request and writes to a read's response more than once.</summary>
internal static class StreamsWireExtensions
{
    /// <summary>
    /// Gets where a subscription counted in positions starts: the position asked for, the one
    /// after the head when asked from the end, and otherwise the beginning.
    /// </summary>
    /// <param name="request">The read request.</param>
    /// <param name="head">The head the subscription was confirmed against.</param>
    /// <returns>The first position to deliver.</returns>
    public static long GetStartPosition(this ReadRequest request, long head) => request.FromCase switch
    {
        ReadRequest.FromOneofCase.Position => request.Position,
        ReadRequest.FromOneofCase.End => head + 1,
        _ => 0,
    };

    /// <summary>Writes an event to a read's response.</summary>
    /// <param name="responseStream">The response.</param>
    /// <param name="record">The event.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the event is written.</returns>
    public static Task WriteEventAsync(this IServerStreamWriter<ReadResponse> responseStream, EventRecord record, CancellationToken cancellationToken) =>
        responseStream.WriteAsync(new ReadResponse { Event = record.ToRecordedEvent() }, cancellationToken);
}
