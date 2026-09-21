using System.Net;

using Dracocephalum.Nightingale.Protocol.V1;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Polecat.Tests.Integration;

/// <summary>
/// Two instances over one store, each listening on a loopback port of its own. A group runs in
/// the instance its consumer connected to, whose derived address is written with the lease; the
/// other instance refuses a call for that group with that address, so a caller can go there,
/// and a replay that goes there wakes the consumer. Driven with the generated stubs, so the
/// server side is what is tested; the client's own following of the address is unit-tested in
/// its component.
/// </summary>
[Collection(OwnDatabase.Name)]
[Trait("Category", "Integration")]
public sealed class RedirectTests : IAsyncLifetime
{
    private readonly string _name = TestDatabases.NewName();
    private readonly List<WebApplication> _instances = [];
    private readonly List<GrpcChannel> _channels = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }

        foreach (var instance in _instances)
        {
            await instance.StopAsync();
            await instance.DisposeAsync();
        }

        await TestDatabases.DropAsync(_name);
    }

    [Fact]
    public async Task Read_AgainstTheInstanceThatDoesNotRunTheGroup_ShouldRefuseWithTheOwnersAddressWhereTheCallSucceeds()
    {
        // Arrange: the first instance initializes the store; the second finds it. A consumer
        // connects to the first, so the first runs the group and its address is on the lease.
        var (first, firstAddress) = await StartInstanceAsync();
        var (second, _) = await StartInstanceAsync();
        var streams = new Streams.StreamsClient(ChannelTo(firstAddress));
        var onFirst = new PersistentSubscriptions.PersistentSubscriptionsClient(ChannelTo(firstAddress));
        var onSecond = new PersistentSubscriptions.PersistentSubscriptionsClient(ChannelTo(second));
        await AppendAsync(streams, "orders-1", "order_placed", "order_paid");
        await onFirst.CreateAsync(new CreateRequest { Stream = "orders-1", Group = "billing", Settings = new Protocol.V1.GroupSettings { FromStart = new() } }, cancellationToken: TestContext.Current.CancellationToken);
        using var consumer = onFirst.Read(cancellationToken: TestContext.Current.CancellationToken);
        await consumer.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 5 } }, TestContext.Current.CancellationToken);
        (await NextAsync(consumer)).ContentCase.ShouldBe(PersistentReadResponse.ContentOneofCase.Confirmed);
        var placed = (await NextAsync(consumer)).Event.Event;
        var paid = (await NextAsync(consumer)).Event.Event;
        await consumer.RequestStream.WriteAsync(new PersistentReadRequest { Nack = new Nack { Ids = { placed.Id }, Action = Protocol.V1.NackAction.Park, Reason = "poison" } }, TestContext.Current.CancellationToken);
        await consumer.RequestStream.WriteAsync(new PersistentReadRequest { Ack = new Ack { Ids = { paid.Id } } }, TestContext.Current.CancellationToken);

        // Act: a second consumer and a replay, both asked of the instance that does not run the
        // group, are refused with the first instance's address; the replay repeated there wakes
        // the consumer, which receives the parked event again.
        var refusedRead = await Should.ThrowAsync<RpcException>(async () =>
        {
            using var call = onSecond.Read(cancellationToken: TestContext.Current.CancellationToken);
            await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing" } }, TestContext.Current.CancellationToken);
            await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken);
        });
        var refusedReplay = await Should.ThrowAsync<RpcException>(async () =>
            await onSecond.ReplayParkedAsync(new ReplayParkedRequest { Stream = "orders-1", Group = "billing", All = new() }, cancellationToken: TestContext.Current.CancellationToken));
        var address = new Uri(Detail(refusedReplay).Metadata["address"]);
        var replayed = await ReplayWhenParkedAsync(new PersistentSubscriptions.PersistentSubscriptionsClient(ChannelTo(address)));
        var again = (await NextAsync(consumer)).Event;

        // Assert
        Detail(refusedRead).Reason.ShouldBe("GROUP_OWNED_ELSEWHERE");
        Detail(refusedRead).Metadata["address"].ShouldBe(firstAddress.ToString());
        address.ShouldBe(firstAddress);
        replayed.ShouldBe(1);
        again.Event.Id.ShouldBe(placed.Id);
        (await TestDatabases.ScalarAsync<string>(_name, "SELECT owner_address FROM dbo.nightingale_leases WHERE name = 'group:orders-1:billing'")).ShouldBe(firstAddress.ToString());
        _ = first;
    }

    private static ErrorInfo Detail(RpcException exception) =>
        exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();

    private static async Task<PersistentReadResponse> NextAsync(AsyncDuplexStreamingCall<PersistentReadRequest, PersistentReadResponse> call)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        (await call.ResponseStream.MoveNext(timeout.Token)).ShouldBeTrue("the call ended before the expected message");
        return call.ResponseStream.Current;
    }

    private static async Task AppendAsync(Streams.StreamsClient streams, string stream, params string[] types)
    {
        using var call = streams.Append(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new AppendRequest { Options = new AppendOptions { Stream = stream, ExpectedRevision = -1 } }, TestContext.Current.CancellationToken);
        foreach (var type in types)
        {
            await call.RequestStream.WriteAsync(new AppendRequest { Event = new ProposedEvent { Id = Guid.NewGuid().ToString("D"), EventType = type, Data = Google.Protobuf.ByteString.CopyFromUtf8("{}") } }, TestContext.Current.CancellationToken);
        }

        await call.RequestStream.CompleteAsync();
        await call.ResponseAsync;
    }

    /// <summary>Replays the parked message once the park, asked for one-way, has landed.</summary>
    private static async Task<int> ReplayWhenParkedAsync(PersistentSubscriptions.PersistentSubscriptionsClient owner)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await owner.ReplayParkedAsync(new ReplayParkedRequest { Stream = "orders-1", Group = "billing", All = new() }, cancellationToken: TestContext.Current.CancellationToken);
            if (response.Replayed > 0 || attempt >= 100)
            {
                return response.Replayed;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private async Task<(WebApplication Instance, Uri Address)> StartInstanceAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddNightingaleServer();
        builder.Services.AddNightingalePolecat(TestDatabases.ConnectionStringFor(_name));
        var instance = builder.Build();
        instance.MapNightingaleServer();
        await instance.StartAsync(TestContext.Current.CancellationToken);
        _instances.Add(instance);
        var address = instance.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (instance, new Uri(address));
    }

    private GrpcChannel ChannelTo(Uri address)
    {
        var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler { UseProxy = false } });
        _channels.Add(channel);
        return channel;
    }

    private GrpcChannel ChannelTo(WebApplication instance) =>
        ChannelTo(new Uri(instance.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()));
}
