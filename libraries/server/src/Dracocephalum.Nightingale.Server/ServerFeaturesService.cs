using System.Reflection;

using Dracocephalum.Nightingale.Protocol.V1;
using Grpc.Core;

namespace Dracocephalum.Nightingale.Server;

/// <summary>
/// The server-features service: liveness, the server's version, and what the store was
/// initialized with, so a client can learn before its first read whether the virtual streams can
/// be read by ordinal.
/// </summary>
/// <param name="store">The backend, for what it was initialized with.</param>
/// <param name="options">The host's options, for whether calls are authenticated; none in a host that registered none.</param>
public sealed class ServerFeaturesService(IStreamStore store, NightingaleOptionsBase? options = null) : ServerFeatures.ServerFeaturesBase
{
    private static readonly string Version =
        typeof(ServerFeaturesService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <inheritdoc/>
    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context) =>
        Task.FromResult(new PingResponse { Version = Version, SupportsOrdinals = store.OrdinalsEnabled, AuthenticationRequired = options?.Auth.Enabled ?? false });
}
