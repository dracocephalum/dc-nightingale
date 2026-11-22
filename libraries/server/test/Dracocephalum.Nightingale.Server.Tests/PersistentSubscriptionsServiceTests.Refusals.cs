using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using FakeItEasy;
using Google.Rpc;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <content>What the persistent-subscriptions service refuses before the store is asked, what it makes of the store's own refusals, and what a consumer's refusal of an event does.</content>
public sealed partial class PersistentSubscriptionsServiceTests
{
    [Fact]
    public async Task Create_WhenASettingIsOutOfRange_ShouldRefuseNamingItBeforeTheStore()
    {
        // Arrange
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        var request = new CreateRequest { Stream = "orders-1", Group = "billing", Settings = new Protocols.Grpc.V1.GroupSettings { MaxRetryCount = -1 } };

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(async () => await client.CreateAsync(request, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("is out of range");
        A.CallTo(() => _groups.CreateAsync(A<SubscriptionGroupDefinition>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Create_WhenTheStoreSaysAValueIsTooLong_ShouldFailAsTheCallersMistake()
    {
        // Arrange
        A.CallTo(() => _groups.CreateAsync(A<SubscriptionGroupDefinition>._, A<CancellationToken>._)).ThrowsAsync(new ValueTooLongException("group name"));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(async () => await client.CreateAsync(new CreateRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("longer than the store holds");
    }

    [Theory]
    [InlineData(0, 7, "INVALID_STREAM_NAME")]
    [InlineData(251, 7, "INVALID_STREAM_NAME")]
    [InlineData(8, 0, "INVALID_ARGUMENT")]
    [InlineData(8, 251, "INVALID_ARGUMENT")]
    public async Task Create_WhenAStreamOrGroupNameIsEmptyOrTooLong_ShouldRefuseItBeforeTheStore(int streamLength, int groupLength, string reason)
    {
        // Arrange
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        var request = new CreateRequest { Stream = new string('s', streamLength), Group = new string('g', groupLength) };

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(async () => await client.CreateAsync(request, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        ReasonOf(refusal).ShouldBe(reason);
        A.CallTo(() => _groups.CreateAsync(A<SubscriptionGroupDefinition>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Update_WhenASettingIsOutOfRange_ShouldRefuseNamingItAndChangeNothing()
    {
        // Arrange
        var definition = new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, 9);
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(definition);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        var request = new UpdateRequest { Stream = "orders-1", Group = "billing", Settings = new Protocols.Grpc.V1.GroupSettings { MaxRetryCount = -1 } };

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(async () => await client.UpdateAsync(request, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("is out of range");
        A.CallTo(() => _groups.UpdateSettingsAsync(A<Guid>._, A<GroupSettings>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Update_WhenItNamesTheStartTheGroupAlreadyHas_ShouldAcceptIt()
    {
        // Arrange: a group that starts at the end, and an update that says so again.
        var settings = GroupSettings.Default with { Start = StreamPosition.End };
        var definition = new SubscriptionGroupDefinition("orders-1", "billing", settings, 9);
        GroupSettings? written = null;
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _groups.UpdateSettingsAsync(definition.Id, A<GroupSettings>._, A<CancellationToken>._))
            .Invokes(call => written = call.GetArgument<GroupSettings>(1))
            .Returns(true);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        var request = new UpdateRequest { Stream = "orders-1", Group = "billing", Settings = new Protocols.Grpc.V1.GroupSettings { FromEnd = new(), MaxRetryCount = 2 } };

        // Act
        await client.UpdateAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        written.ShouldBe(settings with { MaxRetryCount = 2 });
    }

    [Fact]
    public async Task Delete_ShouldRemoveTheGroupFromTheStore()
    {
        // Arrange
        A.CallTo(() => _groups.DeleteAsync("orders-1", "billing", A<CancellationToken>._)).Returns(true);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        await client.DeleteAsync(new DeleteGroupRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        A.CallTo(() => _groups.DeleteAsync("orders-1", "billing", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetInfo_WhenTheGroupsStreamIsDeleted_ShouldDescribeTheGroupWithoutALastNumber()
    {
        // Arrange: a group outlives its stream.
        A.CallTo(() => _groups.DescribeAsync("orders-1", "billing", A<CancellationToken>._))
            .Returns(new SubscriptionGroupSummary(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, 4), Created, 0, 0, null));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Backwards, null, 1, A<CancellationToken>._)).ThrowsAsync(new StreamDeletedException("orders-1"));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var info = (await client.GetInfoAsync(new GetInfoRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken)).Info;

        // Assert
        info.Checkpoint.ShouldBe(4);
        info.HasLastKnownPosition.ShouldBeFalse();
    }

    [Fact]
    public async Task Read_WhenTheFirstMessageIsNotTheOptions_ShouldRefuseItBeforeTheStore()
    {
        // Arrange
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var refusal = await Should.ThrowAsync<RpcException>(async () =>
        {
            await call.RequestStream.WriteAsync(new PersistentReadRequest { Ack = new Ack() }, TestContext.Current.CancellationToken);
            await call.RequestStream.CompleteAsync();
            await Next(call, 1);
        });

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain("carries the options");
        A.CallTo(() => _groups.GetAsync(A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Read_OfAGroupByOrdinal_WhileTheSequencerIsDisabled_ShouldRefuseWithItsReason()
    {
        // Arrange
        var settings = GroupSettings.Default with { Numbering = Numbering.Ordinal };
        A.CallTo(() => _groups.GetAsync("$ce-orders", "billing", A<CancellationToken>._)).Returns(new SubscriptionGroupDefinition("$ce-orders", "billing", settings, -1));
        _tail.NumberingStopped = new OrdinalSequencerDisabledException();
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "$ce-orders", Group = "billing", BufferSize = 5 } }, TestContext.Current.CancellationToken);
        var refusal = await Should.ThrowAsync<RpcException>(() => Next(call, 1));

        // Assert
        ReasonOf(refusal).ShouldBe("SEQUENCER_DISABLED");
    }

    [Theory]
    [InlineData(Protocols.Grpc.V1.NackAction.Park)]
    [InlineData(Protocols.Grpc.V1.NackAction.Skip)]
    public async Task Read_WhenAConsumerRefusesAnEventForGood_ShouldMoveTheCheckpointPastIt(Protocols.Grpc.V1.NackAction action)
    {
        // Arrange: a one-event stream, with the checkpoint written on every event that is done with.
        var settings = GroupSettings.Default with { Start = StreamPosition.Start, CheckpointUpperBound = 1, CheckpointLowerBound = 1 };
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(new SubscriptionGroupDefinition("orders-1", "billing", settings, -1));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, settings.BufferSize, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10)]));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 5 } }, TestContext.Current.CancellationToken);
        var messages = await Next(call, 2);

        var saved = Expect(A.CallTo(() => _groups.SaveCheckpointAsync(A<Guid>._, 0, A<string>._, A<CancellationToken>._)), Task.FromResult(true));

        // Act
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Nack = new Nack { Action = action, Reason = "poison", Ids = { messages[1].Event.Event.Id } } }, TestContext.Current.CancellationToken);
        await saved;
        await call.RequestStream.CompleteAsync();

        // Assert: parked, the event is kept with the reason; skipped, it is kept nowhere.
        var parked = A.CallTo(() => _groups.ParkAsync(A<SubscriptionParkedMessage>.That.Matches(message => message.Reason == "poison"), A<string>._, A<CancellationToken>._));
        if (action == Protocols.Grpc.V1.NackAction.Park)
        {
            parked.MustHaveHappenedOnceExactly();
        }
        else
        {
            parked.MustNotHaveHappened();
        }
    }

    [Theory]
    [InlineData("an id that is not a UUID", "a UUID in its canonical form")]
    [InlineData("too many ids", "ids per acknowledgement")]
    [InlineData("the options again", "acknowledges or refuses events")]
    public async Task Read_WhenAMessageAfterTheOptionsIsMalformed_ShouldEndTheCallSayingWhy(string what, string why)
    {
        // Arrange: a group that is running, over a stream with nothing in it yet.
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default with { Start = StreamPosition.Start }, -1));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, A<int>._, A<CancellationToken>._)).Returns((StreamSlice?)null);
        var options = new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 5 } };
        var malformed = what switch
        {
            "an id that is not a UUID" => new PersistentReadRequest { Ack = new Ack { Ids = { "not-an-id" } } },
            "too many ids" => new PersistentReadRequest { Ack = new Ack { Ids = { Enumerable.Range(0, 2001).Select(_ => Guid.NewGuid().ToString("D")) } } },
            _ => options,
        };
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(options, TestContext.Current.CancellationToken);
        await Next(call, 1);

        // Act: the requests are completed too, so a server that took the message ends the call
        // without a refusal and the test fails, where it would otherwise wait for good.
        var refusal = await Should.ThrowAsync<RpcException>(async () =>
        {
            await call.RequestStream.WriteAsync(malformed, TestContext.Current.CancellationToken);
            await call.RequestStream.CompleteAsync();
            await Next(call, 1);
        });

        // Assert
        ReasonOf(refusal).ShouldBe("INVALID_ARGUMENT");
        refusal.Status.Detail.ShouldContain(why);
    }

    private static string? ReasonOf(RpcException refusal) => refusal.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason;
}
