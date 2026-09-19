using System.Reflection;

using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The server-features service: liveness now, capability discovery later. Placeholder surface: <c>Ping</c> only.
/// </summary>
public sealed class ServerFeaturesService : ServerFeatures.ServerFeaturesBase
{
    private static readonly string Version =
        typeof(ServerFeaturesService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <inheritdoc/>
    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context) =>
        Task.FromResult(new PingResponse { Version = Version });
}
