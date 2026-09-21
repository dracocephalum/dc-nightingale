using System.Collections.Concurrent;

using Dracocephalum.Nightingale.Client;
using Grpc.Core;
using Grpc.Net.Client;

namespace Dracocephalum.Nightingale.Examples.Common;

/// <summary>
/// A client and the channels it speaks over, disposed together: the one to the server it was
/// connected to, and any it opened on its own to another instance the server sent it to. Every
/// channel is opened without the machine's HTTP proxy, which cannot carry cleartext HTTP/2 to a
/// loopback port.
/// </summary>
public sealed class ExampleConnection : IAsyncDisposable
{
    private readonly ConcurrentBag<GrpcChannel> _channels = [];

    internal ExampleConnection(Uri address)
    {
        Client = new NightingaleClient(Open(address), Open);
    }

    /// <summary>Gets the client.</summary>
    public NightingaleClient Client { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }

    private CallInvoker Open(Uri address)
    {
        var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler { UseProxy = false } });
        _channels.Add(channel);
        return channel.CreateCallInvoker();
    }
}
