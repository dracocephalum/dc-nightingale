using Dracocephalum.Nightingale.Protocols.Grpc.V1;

namespace Dracocephalum.Nightingale.Client;

/// <summary>What the client writes into a read request.</summary>
internal static class ReadRequestExtensions
{
    /// <summary>Sets where the read starts: the end, the start, or a position.</summary>
    public static void SetFrom(this ReadRequest request, StreamPosition from)
    {
        if (from.IsEnd)
        {
            request.End = new Google.Protobuf.WellKnownTypes.Empty();
        }
        else if (from == StreamPosition.Start)
        {
            request.Start = new Google.Protobuf.WellKnownTypes.Empty();
        }
        else
        {
            request.Position = from.Value;
        }
    }
}
