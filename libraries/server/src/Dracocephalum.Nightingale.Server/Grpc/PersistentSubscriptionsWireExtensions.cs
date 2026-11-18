using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;

namespace Dracocephalum.Nightingale.Server.Grpc;

/// <summary>The wire forms of what the store and a running group say about a persistent-subscription group.</summary>
internal static class PersistentSubscriptionsWireExtensions
{
    /// <summary>What the store holds about a group, in its wire form.</summary>
    public static GroupInfo ToInfo(this SubscriptionGroupSummary summary)
    {
        var definition = summary.Definition;
        var info = new GroupInfo
        {
            Stream = definition.Stream,
            Group = definition.Group,
            Settings = definition.Settings.ToWire(),
            CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(summary.CreatedAt),
            ParkedCount = summary.ParkedCount,
            OutboxCount = summary.OutboxCount,
            Running = summary.Holder is not null,
            OwnerAddress = summary.Holder?.Address?.ToString() ?? string.Empty,
            Live = summary.Live is null ? null : summary.Live.ToLiveInfo(fromOwner: false),
        };
        if (definition.Checkpoint >= 0)
        {
            info.Checkpoint = definition.Checkpoint;
        }

        return info;
    }

    /// <summary>How a running group stands, in its wire form.</summary>
    public static GroupLiveInfo ToLiveInfo(this SubscriptionGroupLive live, bool fromOwner)
    {
        var info = new GroupLiveInfo
        {
            ConnectedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(live.ConnectedAt),
            InFlightCount = live.InFlightCount,
            AwaitingRetryCount = live.AwaitingRetryCount,
            ConsumerBufferSize = live.ConsumerBufferSize,
            ConsumerAddress = live.ConsumerAddress ?? string.Empty,
            AsOf = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(live.AsOf),
            FromOwner = fromOwner,
            ConsumerCount = live.ConsumerCount,
        };
        foreach (var consumer in live.Consumers)
        {
            info.Consumers.Add(new ConsumerInfo
            {
                ConnectedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(consumer.ConnectedAt),
                Address = consumer.Address ?? string.Empty,
                BufferSize = consumer.BufferSize,
                InFlightCount = consumer.InFlightCount,
            });
        }

        if (live.Checkpoint is { } checkpoint)
        {
            info.Checkpoint = checkpoint;
        }

        if (live.OldestInFlightAt is { } oldest)
        {
            info.OldestInFlightAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(oldest);
        }

        return info;
    }
}
