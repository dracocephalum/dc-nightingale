using Dracocephalum.Nightingale.Client;
using Grpc.Net.Client;

namespace Dracocephalum.Nightingale.Examples.Common;

/// <summary>A client and the channel it speaks over, disposed together.</summary>
public sealed class ExampleConnection : IAsyncDisposable
{
    private readonly GrpcChannel _channel;

    internal ExampleConnection(GrpcChannel channel, NightingaleClient client)
    {
        _channel = channel;
        Client = client;
    }

    /// <summary>Gets the client.</summary>
    public NightingaleClient Client { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        _channel.Dispose();
    }
}
