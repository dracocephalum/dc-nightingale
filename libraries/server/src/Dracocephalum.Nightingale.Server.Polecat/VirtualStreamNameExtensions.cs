using Dracocephalum.Nightingale.Server.Data;

namespace Dracocephalum.Nightingale.Server.Polecat;

/// <summary>What the backend reads off a virtual stream's name.</summary>
internal static class VirtualStreamNameExtensions
{
    /// <summary>Which links hold a virtual stream's ordinals; every virtual stream with ordinals has its links.</summary>
    public static OrdinalLinkType ToOrdinalLinkType(this VirtualStreamName stream) => stream.Kind switch
    {
        VirtualStreamKind.Category => OrdinalLinkType.Category,
        VirtualStreamKind.EventType => OrdinalLinkType.EventType,
        VirtualStreamKind.Correlation => OrdinalLinkType.Correlation,
        VirtualStreamKind.Streams => OrdinalLinkType.Streams,
        VirtualStreamKind.CategoryStreams => OrdinalLinkType.CategoryStreams,
        _ => throw new ArgumentException($"{stream.Name} has no ordinals.", nameof(stream)),
    };
}
