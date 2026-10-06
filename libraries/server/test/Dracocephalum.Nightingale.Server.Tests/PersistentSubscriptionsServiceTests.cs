using System.Text;
using System.Text.Json.Nodes;

using Dracocephalum.Nightingale.Protocol;
using Dracocephalum.Nightingale.Protocol.V1;
using FakeItEasy;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests;

/// <summary>
/// The service over fakes of both stores: the unary calls and their errors, and one consumer's
/// conversation from options to acknowledgement.
/// </summary>
public sealed class PersistentSubscriptionsServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Created = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private readonly IStreamStore _store = A.Fake<IStreamStore>(options => options.Strict());
    private readonly ISubscriptionGroupStore _groups = A.Fake<ISubscriptionGroupStore>();
    private readonly FakeTail _tail = new();
    private WebApplication? _app;
    private GrpcChannel? _channel;

    public async ValueTask InitializeAsync()
    {
        A.CallTo(() => _groups.AcquireLeaseAsync(A<string>._, A<string>._, A<Uri?>._, A<TimeSpan>._, A<CancellationToken>._)).Returns((LeaseHolder?)null);
        A.CallTo(() => _groups.LeaseHolderAsync(A<string>._, A<CancellationToken>._)).Returns((LeaseHolder?)null);
        A.CallTo(() => _groups.DueAsync(A<Guid>._, A<DateTimeOffset>._, A<CancellationToken>._)).Returns(new List<SubscriptionOutboxMessage>());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNightingaleServer();
        builder.Services.AddNightingaleOptions(new TestOptions());
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(_groups);
        builder.Services.AddSingleton<IStoreTail>(_tail);
        _app = builder.Build();
        _app.MapNightingaleServer();
        await _app.StartAsync();
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = _app.GetTestServer().CreateHandler() });
    }

    public async ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Create_ShouldStoreTheGroupWithItsSettingsAndNoCheckpoint()
    {
        // Arrange
        SubscriptionGroupDefinition? captured = null;
        A.CallTo(() => _groups.CreateAsync(A<SubscriptionGroupDefinition>._, A<CancellationToken>._)).Invokes(call => captured = call.GetArgument<SubscriptionGroupDefinition>(0));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        await client.CreateAsync(new CreateRequest { Stream = "orders-1", Group = "billing", Settings = new Protocol.V1.GroupSettings { FromStart = new(), MaxRetryCount = 3 } }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var group = captured.ShouldNotBeNull();
        group.Stream.ShouldBe("orders-1");
        group.Group.ShouldBe("billing");
        group.Checkpoint.ShouldBe(-1);
        group.Settings.Start.ShouldBe(StreamPosition.Start);
        group.Settings.MaxRetryCount.ShouldBe(3);
        group.Settings.MessageTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Create_UnderOrdinalNumbering_ShouldStoreTheNumberingWithTheGroup()
    {
        // Arrange
        SubscriptionGroupDefinition? captured = null;
        A.CallTo(() => _store.OrdinalsEnabled).Returns(true);
        A.CallTo(() => _groups.CreateAsync(A<SubscriptionGroupDefinition>._, A<CancellationToken>._)).Invokes(call => captured = call.GetArgument<SubscriptionGroupDefinition>(0));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        await client.CreateAsync(new CreateRequest { Stream = "$ce-orders", Group = "billing", Settings = new Protocol.V1.GroupSettings { FromStart = new(), Numbering = Protocol.V1.Numbering.Ordinal } }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        captured.ShouldNotBeNull().Settings.Numbering.ShouldBe(Numbering.Ordinal);
    }

    [Fact]
    public async Task Create_UnderOrdinalNumberingOnAPlainStream_ShouldRejectBeforeTouchingTheStore()
    {
        // Arrange
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.CreateAsync(new CreateRequest { Stream = "orders-1", Group = "billing", Settings = new Protocol.V1.GroupSettings { Numbering = Protocol.V1.Numbering.Ordinal } }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        A.CallTo(() => _groups.CreateAsync(A<SubscriptionGroupDefinition>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Create_UnderOrdinalNumberingWithoutOrdinals_ShouldFailAsNotEnabled()
    {
        // Arrange
        A.CallTo(() => _store.OrdinalsEnabled).Returns(false);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.CreateAsync(new CreateRequest { Stream = "$et-order_placed", Group = "billing", Settings = new Protocol.V1.GroupSettings { Numbering = Protocol.V1.Numbering.Ordinal } }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("ORDINALS_NOT_ENABLED");
        A.CallTo(() => _groups.CreateAsync(A<SubscriptionGroupDefinition>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Create_WhenTheGroupExists_ShouldFailAsAlreadyExists()
    {
        // Arrange
        A.CallTo(() => _groups.CreateAsync(A<SubscriptionGroupDefinition>._, A<CancellationToken>._)).ThrowsAsync(new GroupExistsException("orders-1", "billing"));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.CreateAsync(new CreateRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.AlreadyExists);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("GROUP_EXISTS");
    }

    [Fact]
    public async Task Delete_WhenTheGroupIsMissing_ShouldFailAsNotFound()
    {
        // Arrange
        A.CallTo(() => _groups.DeleteAsync("orders-1", "billing", A<CancellationToken>._)).Returns(false);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.DeleteAsync(new DeleteGroupRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("GROUP_NOT_FOUND");
    }

    [Fact]
    public async Task ReplayParked_ShouldMoveAllOrOneToTheOutboxAndFailWhenTheOneIsMissing()
    {
        // Arrange
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1));
        A.CallTo(() => _groups.ReplayAsync(A<Guid>._, null, SubscriptionParkedNumber.Revision, A<DateTimeOffset>._, A<CancellationToken>._)).Returns(3);
        A.CallTo(() => _groups.ReplayAsync(A<Guid>._, 7, SubscriptionParkedNumber.Revision, A<DateTimeOffset>._, A<CancellationToken>._)).Returns(0);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var all = await client.ReplayParkedAsync(new ReplayParkedRequest { Stream = "orders-1", Group = "billing", All = new() }, cancellationToken: TestContext.Current.CancellationToken);
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.ReplayParkedAsync(new ReplayParkedRequest { Stream = "orders-1", Group = "billing", Position = 7 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        all.Replayed.ShouldBe(3);
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("PARKED_MESSAGE_NOT_FOUND");
    }

    [Fact]
    public async Task ReplayParked_BeforeANumber_ShouldMoveThoseBelowItInTheGroupsNumbering()
    {
        // Arrange: a group over a category by position.
        var definition = new SubscriptionGroupDefinition("$ce-orders", "billing", GroupSettings.Default, -1);
        A.CallTo(() => _groups.GetAsync("$ce-orders", "billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _groups.ReplayBeforeAsync(definition.Id, 500, SubscriptionParkedNumber.Position, A<DateTimeOffset>._, A<CancellationToken>._)).Returns(4);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var response = await client.ReplayParkedAsync(new ReplayParkedRequest { Stream = "$ce-orders", Group = "billing", Before = 500 }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        response.Replayed.ShouldBe(4);
        A.CallTo(() => _groups.ReplayAsync(A<Guid>._, A<long?>._, A<SubscriptionParkedNumber>._, A<DateTimeOffset>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ListParked_ShouldGiveEachMessageItsNumberInTheGroupsNumberingAPageAtATime()
    {
        // Arrange: a group over a category under ordinal numbering; a parked row keeps all three numbers.
        var definition = new SubscriptionGroupDefinition("$ce-orders", "billing", GroupSettings.Default with { Numbering = Numbering.Ordinal }, -1);
        var eventId = Guid.NewGuid();
        A.CallTo(() => _groups.GetAsync("$ce-orders", "billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _groups.ListParkedAsync(definition.Id, SubscriptionParkedNumber.Ordinal, 7, 2, A<CancellationToken>._))
            .Returns(new List<SubscriptionParkedMessage> { new(definition.Id, 900, 3, 8, eventId, "poison", 2, Created) });
        A.CallTo(() => _groups.ListParkedAsync(definition.Id, SubscriptionParkedNumber.Ordinal, null, PersistentSubscriptionsService.DefaultPageSize, A<CancellationToken>._))
            .Returns(new List<SubscriptionParkedMessage>());
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var page = (await client.ListParkedAsync(new ListMessagesRequest { Stream = "$ce-orders", Group = "billing", After = 7, Limit = 2 }, cancellationToken: TestContext.Current.CancellationToken)).Messages;
        var byDefault = (await client.ListParkedAsync(new ListMessagesRequest { Stream = "$ce-orders", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken)).Messages;
        var tooMany = await Should.ThrowAsync<RpcException>(async () => await client.ListParkedAsync(new ListMessagesRequest { Stream = "$ce-orders", Group = "billing", Limit = PersistentSubscriptionsService.MaxPageSize + 1 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var message = page.ShouldHaveSingleItem();
        message.Number.ShouldBe(8);
        message.Position.ShouldBe(900);
        message.Revision.ShouldBe(3);
        message.EventId.ShouldBe(eventId.ToString("D"));
        message.Reason.ShouldBe("poison");
        message.RetryCount.ShouldBe(2);
        message.ParkedAt.ToDateTimeOffset().ShouldBe(Created);
        byDefault.ShouldBeEmpty();
        tooMany.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ListOutbox_ShouldGiveEachMessageItsNumberAndWhenItIsDue()
    {
        // Arrange: a group over a plain stream counts by revision.
        var definition = new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1);
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _groups.ListOutboxAsync(definition.Id, SubscriptionParkedNumber.Revision, null, 50, A<CancellationToken>._))
            .Returns(new List<SubscriptionOutboxMessage> { new(definition.Id, 900, 3, null, Guid.NewGuid(), "poison", 2, Created) });
        A.CallTo(() => _groups.GetAsync("orders-1", "nobody", A<CancellationToken>._)).Returns((SubscriptionGroupDefinition?)null);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var messages = (await client.ListOutboxAsync(new ListMessagesRequest { Stream = "orders-1", Group = "billing", Limit = 50 }, cancellationToken: TestContext.Current.CancellationToken)).Messages;
        var missing = await Should.ThrowAsync<RpcException>(async () => await client.ListOutboxAsync(new ListMessagesRequest { Stream = "orders-1", Group = "nobody" }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var message = messages.ShouldHaveSingleItem();
        message.Number.ShouldBe(3);
        message.Position.ShouldBe(900);
        message.DueAt.ToDateTimeOffset().ShouldBe(Created);
        missing.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("GROUP_NOT_FOUND");
    }

    [Fact]
    public async Task SkipParked_ShouldRemoveAllOneOrThoseBelowANumberAndFailWhenTheOneIsMissing()
    {
        // Arrange: skipping needs no running group, so it never asks who holds the lease.
        var definition = new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1);
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _groups.SkipAsync(definition.Id, null, null, SubscriptionParkedNumber.Revision, A<CancellationToken>._)).Returns(5);
        A.CallTo(() => _groups.SkipAsync(definition.Id, 7, null, SubscriptionParkedNumber.Revision, A<CancellationToken>._)).Returns(1);
        A.CallTo(() => _groups.SkipAsync(definition.Id, 9, null, SubscriptionParkedNumber.Revision, A<CancellationToken>._)).Returns(0);
        A.CallTo(() => _groups.SkipAsync(definition.Id, null, 4, SubscriptionParkedNumber.Revision, A<CancellationToken>._)).Returns(0);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var all = await client.SkipParkedAsync(new SkipParkedRequest { Stream = "orders-1", Group = "billing", All = new() }, cancellationToken: TestContext.Current.CancellationToken);
        var one = await client.SkipParkedAsync(new SkipParkedRequest { Stream = "orders-1", Group = "billing", Position = 7 }, cancellationToken: TestContext.Current.CancellationToken);
        var before = await client.SkipParkedAsync(new SkipParkedRequest { Stream = "orders-1", Group = "billing", Before = 4 }, cancellationToken: TestContext.Current.CancellationToken);
        var missing = await Should.ThrowAsync<RpcException>(async () => await client.SkipParkedAsync(new SkipParkedRequest { Stream = "orders-1", Group = "billing", Position = 9 }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert: none below a number is an answer, zero; one that is not there is an error.
        all.Skipped.ShouldBe(5);
        one.Skipped.ShouldBe(1);
        before.Skipped.ShouldBe(0);
        missing.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("PARKED_MESSAGE_NOT_FOUND");
        A.CallTo(() => _groups.LeaseHolderAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Update_ShouldChangeWhatIsNamedKeepTheRestAndAnswerWithAllOfIt()
    {
        // Arrange: a group from the start of a category, by ordinal, with a retry limit of its own.
        var settings = GroupSettings.Default with { Start = StreamPosition.Start, Numbering = Numbering.Ordinal, MaxRetryCount = 3, BufferSize = 40 };
        var definition = new SubscriptionGroupDefinition("$ce-orders", "billing", settings, 9);
        GroupSettings? written = null;
        A.CallTo(() => _groups.GetAsync("$ce-orders", "billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _groups.UpdateSettingsAsync(definition.Id, A<GroupSettings>._, A<CancellationToken>._))
            .Invokes(call => written = call.GetArgument<GroupSettings>(1))
            .Returns(true);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act: only the message timeout and the retry limit are named.
        var response = await client.UpdateAsync(
            new UpdateRequest { Stream = "$ce-orders", Group = "billing", Settings = new Protocol.V1.GroupSettings { MessageTimeout = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromSeconds(5)), MaxRetryCount = 7 } },
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var expected = settings with { MessageTimeout = TimeSpan.FromSeconds(5), MaxRetryCount = 7 };
        written.ShouldBe(expected);
        response.Settings.ToGroupSettings().ShouldBe(expected);
    }

    [Fact]
    public async Task Update_WhenItNamesAnotherStartOrNumbering_ShouldRefuseAndChangeNothing()
    {
        // Arrange
        var definition = new SubscriptionGroupDefinition("$ce-orders", "billing", GroupSettings.Default with { Start = StreamPosition.Start }, -1);
        A.CallTo(() => _groups.GetAsync("$ce-orders", "billing", A<CancellationToken>._)).Returns(definition);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act: the same start named again is no change; another one is, and so is another numbering.
        A.CallTo(() => _groups.UpdateSettingsAsync(definition.Id, A<GroupSettings>._, A<CancellationToken>._)).Returns(true);
        await client.UpdateAsync(new UpdateRequest { Stream = "$ce-orders", Group = "billing", Settings = new Protocol.V1.GroupSettings { FromStart = new(), Numbering = Protocol.V1.Numbering.Global } }, cancellationToken: TestContext.Current.CancellationToken);
        Fake.ClearRecordedCalls(_groups);
        var otherStart = await Should.ThrowAsync<RpcException>(async () => await client.UpdateAsync(new UpdateRequest { Stream = "$ce-orders", Group = "billing", Settings = new Protocol.V1.GroupSettings { FromPosition = 40 } }, cancellationToken: TestContext.Current.CancellationToken));
        var otherNumbering = await Should.ThrowAsync<RpcException>(async () => await client.UpdateAsync(new UpdateRequest { Stream = "$ce-orders", Group = "billing", Settings = new Protocol.V1.GroupSettings { Numbering = Protocol.V1.Numbering.Ordinal } }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        otherStart.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        otherStart.Status.Detail.ShouldContain("fixed when it is created");
        otherNumbering.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        A.CallTo(() => _groups.UpdateSettingsAsync(A<Guid>._, A<GroupSettings>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Update_WhenAnotherInstanceRunsTheGroupOrTheGroupIsGone_ShouldRefuse()
    {
        // Arrange
        var running = new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1);
        var gone = new SubscriptionGroupDefinition("orders-2", "billing", GroupSettings.Default, -1);
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(running);
        A.CallTo(() => _groups.GetAsync("orders-2", "billing", A<CancellationToken>._)).Returns(gone);
        A.CallTo(() => _groups.GetAsync("orders-3", "billing", A<CancellationToken>._)).Returns((SubscriptionGroupDefinition?)null);
        A.CallTo(() => _groups.LeaseHolderAsync(SubscriptionGroupRegistry.LeaseName(running.Id), A<CancellationToken>._)).Returns(new LeaseHolder("other-instance", new Uri("http://other-instance:5000")));
        A.CallTo(() => _groups.UpdateSettingsAsync(gone.Id, A<GroupSettings>._, A<CancellationToken>._)).Returns(false);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        var change = new Protocol.V1.GroupSettings { MaxRetryCount = 7 };

        // Act
        var elsewhere = await Should.ThrowAsync<RpcException>(async () => await client.UpdateAsync(new UpdateRequest { Stream = "orders-1", Group = "billing", Settings = change }, cancellationToken: TestContext.Current.CancellationToken));
        var deletedMeanwhile = await Should.ThrowAsync<RpcException>(async () => await client.UpdateAsync(new UpdateRequest { Stream = "orders-2", Group = "billing", Settings = change }, cancellationToken: TestContext.Current.CancellationToken));
        var missing = await Should.ThrowAsync<RpcException>(async () => await client.UpdateAsync(new UpdateRequest { Stream = "orders-3", Group = "billing", Settings = change }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var info = elsewhere.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("GROUP_OWNED_ELSEWHERE");
        info.Metadata["address"].ShouldBe("http://other-instance:5000/");
        A.CallTo(() => _groups.UpdateSettingsAsync(running.Id, A<GroupSettings>._, A<CancellationToken>._)).MustNotHaveHappened();
        deletedMeanwhile.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("GROUP_NOT_FOUND");
        missing.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("GROUP_NOT_FOUND");
    }

    [Fact]
    public async Task Update_WhenConsumersAreConnectedHere_ShouldEndTheirCallsSoTheyReconnectUnderTheNewSettings()
    {
        // Arrange: two consumers connected to this instance, two events delivered between them.
        var settings = GroupSettings.Default with { Start = StreamPosition.Start };
        var definition = new SubscriptionGroupDefinition("orders-1", "billing", settings, -1);
        var me = _app!.Services.GetRequiredService<SubscriptionGroupRegistry>().InstanceId;
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _groups.LeaseHolderAsync(SubscriptionGroupRegistry.LeaseName(definition.Id), A<CancellationToken>._)).Returns(new LeaseHolder(me, new Uri("http://localhost")));
        A.CallTo(() => _groups.UpdateSettingsAsync(definition.Id, A<GroupSettings>._, A<CancellationToken>._)).Returns(true);
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, A<long?>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<StreamSlice?>(new StreamSlice(new StreamHead(0, 1), call.GetArgument<long?>(2) == 0 ? [Record("orders-1", 0, 10), Record("orders-1", 1, 11)] : [])));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 1 } }, TestContext.Current.CancellationToken);
        await Next(call, 2);
        using var other = client.Read(cancellationToken: TestContext.Current.CancellationToken);
        await other.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 1 } }, TestContext.Current.CancellationToken);
        await Next(other, 2);

        // Act
        var response = await client.UpdateAsync(new UpdateRequest { Stream = "orders-1", Group = "billing", Settings = new Protocol.V1.GroupSettings { MaxRetryCount = 7 } }, cancellationToken: TestContext.Current.CancellationToken);
        var ended = await Should.ThrowAsync<RpcException>(async () => await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
        var otherEnded = await Should.ThrowAsync<RpcException>(async () => await other.ResponseStream.MoveNext(TestContext.Current.CancellationToken));

        // Assert: the change is made, and each consumer is told why its call ended.
        response.Settings.MaxRetryCount.ShouldBe(7);
        foreach (var exception in new[] { ended, otherEnded })
        {
            exception.StatusCode.ShouldBe(StatusCode.Aborted);
            var info = exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
            info.Reason.ShouldBe("GROUP_UPDATED");
            info.Metadata["group"].ShouldBe("billing");
        }
    }

    [Fact]
    public async Task Read_WithTwoConsumers_ShouldShareOneRunningGroupAndHandOverWhatALeaverHeld()
    {
        // Arrange: a three-event stream, two consumers with a buffer of one each.
        var settings = GroupSettings.Default with { Start = StreamPosition.Start };
        var definition = new SubscriptionGroupDefinition("orders-1", "billing", settings, -1);
        var me = _app!.Services.GetRequiredService<SubscriptionGroupRegistry>().InstanceId;
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _groups.DescribeAsync("orders-1", "billing", A<CancellationToken>._))
            .Returns(new SubscriptionGroupSummary(definition, Created, 0, 0, new LeaseHolder(me, new Uri("http://localhost"))));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, A<long?>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<StreamSlice?>(new StreamSlice(new StreamHead(0, 2), call.GetArgument<long?>(2) == 0 ? [Record("orders-1", 0, 10), Record("orders-1", 1, 11), Record("orders-1", 2, 12)] : [])));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Backwards, null, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 2), [Record("orders-1", 2, 12)]));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var first = client.Read(cancellationToken: TestContext.Current.CancellationToken);
        await first.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 1 } }, TestContext.Current.CancellationToken);
        var toFirst = await Next(first, 2);
        using var second = client.Read(cancellationToken: TestContext.Current.CancellationToken);
        await second.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 1 } }, TestContext.Current.CancellationToken);
        var toSecond = await Next(second, 2);

        // Act: the owner describes both consumers; then the first leaves cleanly, and what it held goes to the second.
        var info = (await client.GetInfoAsync(new GetInfoRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken)).Info;
        await first.RequestStream.CompleteAsync();
        var firstEnded = await first.ResponseStream.MoveNext(TestContext.Current.CancellationToken);
        await second.RequestStream.WriteAsync(new PersistentReadRequest { Ack = new Ack { Ids = { toSecond[1].Event.Event.Id } } }, TestContext.Current.CancellationToken);
        var handedOver = await Next(second, 1);
        await second.RequestStream.CompleteAsync();
        await second.ResponseStream.MoveNext(TestContext.Current.CancellationToken);

        // Assert
        toFirst[1].Event.Event.Revision.ShouldBe(0);
        toSecond[1].Event.Event.Revision.ShouldBe(1);
        toFirst[0].Confirmed.SubscriptionId.ShouldNotBe(toSecond[0].Confirmed.SubscriptionId);
        var live = info.Live.ShouldNotBeNull();
        live.FromOwner.ShouldBeTrue();
        live.ConsumerCount.ShouldBe(2);
        live.InFlightCount.ShouldBe(2);
        live.ConsumerBufferSize.ShouldBe(2);
        live.Consumers.Count.ShouldBe(2);
        live.Consumers.ShouldAllBe(consumer => consumer.BufferSize == 1 && consumer.InFlightCount == 1 && consumer.ConnectedAt != null);
        firstEnded.ShouldBeFalse("a clean leave ends the call without an error");
        handedOver[0].Event.Event.Revision.ShouldBe(0, "what the leaver held goes out first, ahead of the stream");
        handedOver[0].Event.RetryCount.ShouldBe(0, "a consumer leaving is not the event's fault");

        // One lease for the two, taken when the first arrived and given back when the last left.
        A.CallTo(() => _groups.AcquireLeaseAsync(SubscriptionGroupRegistry.LeaseName(definition.Id), me, A<Uri?>._, PersistentSubscriptionsService.LeaseDuration, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        await Until(() => A.CallTo(() => _groups.ReleaseLeaseAsync(SubscriptionGroupRegistry.LeaseName(definition.Id), me, A<CancellationToken>._)).MustHaveHappenedOnceExactly());
        A.CallTo(() => _groups.SaveLiveAsync(definition.Id, null, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Read_ShouldConfirmDeliverAndTurnAcknowledgementsIntoTheCheckpoint()
    {
        // Arrange: the group starts at the beginning of a two-event stream, with the checkpoint written on every acknowledgement.
        var settings = GroupSettings.Default with { Start = StreamPosition.Start, CheckpointUpperBound = 1, CheckpointLowerBound = 1 };
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(new SubscriptionGroupDefinition("orders-1", "billing", settings, -1));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, settings.BufferSize, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 1), [Record("orders-1", 0, 10), Record("orders-1", 1, 11)]));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 5 } }, TestContext.Current.CancellationToken);
        var messages = await Next(call, 3);
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Ack = new Ack { Ids = { messages[1].Event.Event.Id } } }, TestContext.Current.CancellationToken);
        await Until(() => A.CallTo(() => _groups.SaveCheckpointAsync(A<Guid>._, 0, A<CancellationToken>._)).MustHaveHappened());
        await call.RequestStream.CompleteAsync();

        // Assert
        messages[0].Confirmed.Checkpoint.ShouldBe(-1);
        messages[0].Confirmed.SubscriptionId.ShouldNotBeNullOrEmpty();
        messages[1].Event.Event.Revision.ShouldBe(0);
        messages[1].Event.RetryCount.ShouldBe(0);
        messages[2].Event.Event.Revision.ShouldBe(1);
        A.CallTo(() => _groups.AcquireLeaseAsync(A<string>.That.StartsWith("group:"), A<string>._, A<Uri?>._, PersistentSubscriptionsService.LeaseDuration, A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task Read_WhenTheStoreAnswersAnotherSpellingWithTheSameGroup_ShouldTreatItAsThatGroup()
    {
        // Arrange: a case-insensitive store answers "Orders-1" and "Billing" with the group created
        // as "orders-1" and "billing". One consumer is connected under the names it was created with.
        var definition = new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default with { Start = StreamPosition.Start, MaxSubscriberCount = 1 }, -1);
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _groups.GetAsync("Orders-1", "Billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, 0, definition.Settings.BufferSize, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 0), [Record("orders-1", 0, 10)]));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var first = client.Read(cancellationToken: TestContext.Current.CancellationToken);
        await first.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 5 } }, TestContext.Current.CancellationToken);
        await Next(first, 1);

        // Act: a second consumer and a replay, both under the other spelling.
        using var second = client.Read(cancellationToken: TestContext.Current.CancellationToken);
        await second.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "Orders-1", Group = "Billing" } }, TestContext.Current.CancellationToken);
        var refused = await Should.ThrowAsync<RpcException>(async () => await second.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
        await client.ReplayParkedAsync(new ReplayParkedRequest { Stream = "Orders-1", Group = "Billing", All = new() }, cancellationToken: TestContext.Current.CancellationToken);
        await first.RequestStream.CompleteAsync();

        // Assert: the one consumer the group allows is taken, and everything was asked of the
        // store under the group's id, whatever the spelling of the request.
        var lease = $"group:{definition.Id:N}";
        refused.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull().Reason.ShouldBe("CONSUMER_LIMIT_REACHED");
        A.CallTo(() => _groups.AcquireLeaseAsync(lease, A<string>._, A<Uri?>._, A<TimeSpan>._, A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _groups.AcquireLeaseAsync(A<string>.That.Not.IsEqualTo(lease), A<string>._, A<Uri?>._, A<TimeSpan>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _groups.LeaseHolderAsync(lease, A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _groups.ReplayAsync(definition.Id, null, SubscriptionParkedNumber.Revision, A<DateTimeOffset>._, A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task ReplayParked_WhenAnotherInstanceRunsTheGroup_ShouldRefuseWithItsAddressAndMoveNothing()
    {
        // Arrange: the consumer is connected elsewhere, so only that instance can wake it.
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1));
        A.CallTo(() => _groups.LeaseHolderAsync(A<string>.That.StartsWith("group:"), A<CancellationToken>._)).Returns(new LeaseHolder("other-instance", new Uri("http://other-instance:5000")));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.ReplayParkedAsync(new ReplayParkedRequest { Stream = "orders-1", Group = "billing", All = new() }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var info = exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("GROUP_OWNED_ELSEWHERE");
        info.Metadata["address"].ShouldBe("http://other-instance:5000/");
        A.CallTo(() => _groups.ReplayAsync(A<Guid>._, A<long?>._, A<SubscriptionParkedNumber>._, A<DateTimeOffset>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Read_WhenAnotherInstanceOwnsTheGroup_ShouldFailNamingTheOwner()
    {
        // Arrange
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1));
        A.CallTo(() => _groups.AcquireLeaseAsync(A<string>.That.StartsWith("group:"), A<string>._, A<Uri?>._, A<TimeSpan>._, A<CancellationToken>._)).Returns(new LeaseHolder("other-instance", new Uri("http://other-instance:5000")));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing" } }, TestContext.Current.CancellationToken);
        var exception = await Should.ThrowAsync<RpcException>(async () => await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));

        // Assert
        exception.StatusCode.ShouldBe(StatusCode.Unavailable);
        var info = exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("GROUP_OWNED_ELSEWHERE");
        info.Metadata["owner"].ShouldBe("other-instance");
        info.Metadata["address"].ShouldBe("http://other-instance:5000/");
    }

    [Fact]
    public async Task Read_WhenTheGroupIsMissing_ShouldFailAsNotFound()
    {
        // Arrange
        A.CallTo(() => _groups.GetAsync("orders-1", "nobody", A<CancellationToken>._)).Returns((SubscriptionGroupDefinition?)null);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "nobody" } }, TestContext.Current.CancellationToken);
        var exception = await Should.ThrowAsync<RpcException>(async () => await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("GROUP_NOT_FOUND");
    }

    [Fact]
    public async Task GetInfo_WhenNobodyRunsTheGroup_ShouldDescribeItFromTheStoreWithTheLastNumberOfItsStream()
    {
        // Arrange
        var settings = GroupSettings.Default with { Start = StreamPosition.Start, MaxRetryCount = 3 };
        A.CallTo(() => _groups.DescribeAsync("orders-1", "billing", A<CancellationToken>._))
            .Returns(new SubscriptionGroupSummary(new SubscriptionGroupDefinition("orders-1", "billing", settings, 4), Created, 2, 1, null));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Backwards, null, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 7), [Record("orders-1", 7, 70)]));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var info = (await client.GetInfoAsync(new GetInfoRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken)).Info;

        // Assert
        info.Stream.ShouldBe("orders-1");
        info.Group.ShouldBe("billing");
        info.Settings.MaxRetryCount.ShouldBe(3);
        info.CreatedAt.ToDateTimeOffset().ShouldBe(Created);
        info.Checkpoint.ShouldBe(4);
        info.ParkedCount.ShouldBe(2);
        info.OutboxCount.ShouldBe(1);
        info.Running.ShouldBeFalse();
        info.OwnerAddress.ShouldBeEmpty();
        info.LastKnownPosition.ShouldBe(7);
        info.Live.ShouldBeNull();
    }

    [Fact]
    public async Task GetInfo_WhenTheGroupHasNoCheckpointAndItsStreamHoldsNothing_ShouldLeaveBothNumbersOut()
    {
        // Arrange: a group over a category by ordinal, created and never read.
        var settings = GroupSettings.Default with { Numbering = Numbering.Ordinal };
        A.CallTo(() => _groups.DescribeAsync("$ce-orders", "billing", A<CancellationToken>._))
            .Returns(new SubscriptionGroupSummary(new SubscriptionGroupDefinition("$ce-orders", "billing", settings, -1), Created, 0, 0, null));
        A.CallTo(() => _store.OrdinalHeadAsync(new VirtualStreamName(VirtualStreamKind.Category, "orders"), A<CancellationToken>._)).Returns((StreamHead?)null);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var info = (await client.GetInfoAsync(new GetInfoRequest { Stream = "$ce-orders", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken)).Info;

        // Assert
        info.HasCheckpoint.ShouldBeFalse();
        info.HasLastKnownPosition.ShouldBeFalse();
        info.Settings.Numbering.ShouldBe(Protocol.V1.Numbering.Ordinal);
    }

    [Fact]
    public async Task GetInfo_WhenAnotherInstanceRunsTheGroup_ShouldRefuseWithItsAddress()
    {
        // Arrange
        A.CallTo(() => _groups.DescribeAsync("orders-1", "billing", A<CancellationToken>._))
            .Returns(new SubscriptionGroupSummary(new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, -1), Created, 0, 0, new LeaseHolder("other-instance", new Uri("http://other-instance:5000"))));
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.GetInfoAsync(new GetInfoRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var info = exception.GetRpcStatus().ShouldNotBeNull().GetDetail<ErrorInfo>().ShouldNotBeNull();
        info.Reason.ShouldBe("GROUP_OWNED_ELSEWHERE");
        info.Metadata["address"].ShouldBe("http://other-instance:5000/");
    }

    [Fact]
    public async Task GetInfo_WhenTheOwnerAdvertisesNoAddress_ShouldAnswerFromTheStoreAndSayItRuns()
    {
        // Arrange: there is nowhere to send the client, so what the store holds is the answer.
        A.CallTo(() => _groups.DescribeAsync("$all", "audit", A<CancellationToken>._))
            .Returns(new SubscriptionGroupSummary(
                new SubscriptionGroupDefinition("$all", "audit", GroupSettings.Default, 12),
                Created,
                0,
                0,
                new LeaseHolder("other-instance", null),
                new SubscriptionGroupLive(Created.AddMinutes(1), 3, 1, 10, 14, Created.AddMinutes(2), "ipv4:10.0.0.7:51234", Created.AddMinutes(3))));
        _tail.Advance(15);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var info = (await client.GetInfoAsync(new GetInfoRequest { Stream = "$all", Group = "audit" }, cancellationToken: TestContext.Current.CancellationToken)).Info;

        // Assert
        info.Running.ShouldBeTrue();
        info.OwnerAddress.ShouldBeEmpty();
        info.LastKnownPosition.ShouldBe(15);

        // What the running group last wrote to its row, with when it wrote it.
        var live = info.Live.ShouldNotBeNull();
        live.InFlightCount.ShouldBe(3);
        live.AwaitingRetryCount.ShouldBe(1);
        live.ConsumerBufferSize.ShouldBe(10);
        live.Checkpoint.ShouldBe(14);
        live.OldestInFlightAt.ToDateTimeOffset().ShouldBe(Created.AddMinutes(2));
        live.ConsumerAddress.ShouldBe("ipv4:10.0.0.7:51234");
        live.AsOf.ToDateTimeOffset().ShouldBe(Created.AddMinutes(3));
        live.FromOwner.ShouldBeFalse();
    }

    [Fact]
    public async Task GetInfo_WhenTheGroupIsMissing_ShouldFailAsNotFound()
    {
        // Arrange
        A.CallTo(() => _groups.DescribeAsync("orders-1", "nobody", A<CancellationToken>._)).Returns((SubscriptionGroupSummary?)null);
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var exception = await Should.ThrowAsync<RpcException>(async () => await client.GetInfoAsync(new GetInfoRequest { Stream = "orders-1", Group = "nobody" }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("GROUP_NOT_FOUND");
    }

    [Fact]
    public async Task GetInfo_WhenThisInstanceRunsTheGroup_ShouldAddWhatTheRunningGroupKnows()
    {
        // Arrange: a consumer connected here with a buffer of five has two events delivered and none acknowledged.
        var settings = GroupSettings.Default with { Start = StreamPosition.Start };
        var definition = new SubscriptionGroupDefinition("orders-1", "billing", settings, -1);
        var me = _app!.Services.GetRequiredService<SubscriptionGroupRegistry>().InstanceId;
        A.CallTo(() => _groups.GetAsync("orders-1", "billing", A<CancellationToken>._)).Returns(definition);
        A.CallTo(() => _groups.DescribeAsync("orders-1", "billing", A<CancellationToken>._))
            .Returns(new SubscriptionGroupSummary(definition, Created, 0, 0, new LeaseHolder(me, new Uri("http://localhost"))));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Forwards, A<long?>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<StreamSlice?>(new StreamSlice(new StreamHead(0, 1), call.GetArgument<long?>(2) == 0 ? [Record("orders-1", 0, 10), Record("orders-1", 1, 11)] : [])));
        A.CallTo(() => _store.ReadAsync("orders-1", Direction.Backwards, null, 1, A<CancellationToken>._))
            .Returns(new StreamSlice(new StreamHead(0, 1), [Record("orders-1", 1, 11)]));

        // The store's listing has the same group with numbers written a while ago, and another
        // that an instance elsewhere runs.
        var stale = new SubscriptionGroupLive(Created, 99, 9, 5, null, null, null, Created);
        A.CallTo(() => _groups.ListAsync(null, A<CancellationToken>._)).Returns(new List<SubscriptionGroupSummary>
        {
            new(definition, Created, 0, 0, new LeaseHolder(me, new Uri("http://localhost")), stale),
            new(new SubscriptionGroupDefinition("orders-2", "billing", GroupSettings.Default, -1), Created, 0, 0, new LeaseHolder("other-instance", null), stale),
        });
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);
        using var call = client.Read(cancellationToken: TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new PersistentReadRequest { Options = new PersistentReadOptions { Stream = "orders-1", Group = "billing", BufferSize = 5 } }, TestContext.Current.CancellationToken);
        await Next(call, 3);

        // Act
        var info = (await client.GetInfoAsync(new GetInfoRequest { Stream = "orders-1", Group = "billing" }, cancellationToken: TestContext.Current.CancellationToken)).Info;
        var listed = (await client.ListAsync(new ListRequest(), cancellationToken: TestContext.Current.CancellationToken)).Groups;
        await call.RequestStream.CompleteAsync();

        // Assert
        info.Running.ShouldBeTrue();
        info.LastKnownPosition.ShouldBe(1);
        var live = info.Live.ShouldNotBeNull();
        live.InFlightCount.ShouldBe(2);
        live.AwaitingRetryCount.ShouldBe(0);
        live.ConsumerBufferSize.ShouldBe(5);
        live.ConnectedAt.ShouldNotBeNull();
        live.HasCheckpoint.ShouldBeFalse();
        live.OldestInFlightAt.ShouldNotBeNull();
        live.AsOf.ShouldNotBeNull();
        live.FromOwner.ShouldBeTrue();

        // The listing asks the group this instance runs and takes the other from its row, saying which.
        listed[0].Live.FromOwner.ShouldBeTrue();
        listed[0].Live.InFlightCount.ShouldBe(2);
        listed[1].Live.FromOwner.ShouldBeFalse();
        listed[1].Live.InFlightCount.ShouldBe(99);

        // The group wrote how it stands when its consumer connected, and cleared it when the consumer left.
        A.CallTo(() => _groups.SaveLiveAsync(definition.Id, A<SubscriptionGroupLive>.That.Matches(written => written != null && written.ConsumerBufferSize == 5), A<CancellationToken>._)).MustHaveHappened();
        await Until(() => A.CallTo(() => _groups.SaveLiveAsync(definition.Id, null, A<CancellationToken>._)).MustHaveHappened());
    }

    [Fact]
    public async Task List_ShouldGiveWhatTheStoreHoldsForEachGroupWithWhatARunningGroupLastWrote()
    {
        // Arrange
        A.CallTo(() => _groups.ListAsync(null, A<CancellationToken>._)).Returns(new List<SubscriptionGroupSummary>
        {
            new(
                new SubscriptionGroupDefinition("orders-1", "billing", GroupSettings.Default, 9),
                Created,
                3,
                0,
                new LeaseHolder("other-instance", new Uri("http://other-instance:5000")),
                new SubscriptionGroupLive(Created.AddMinutes(1), 2, 0, 10, 11, null, null, Created.AddMinutes(3))),
            new(new SubscriptionGroupDefinition("orders-2", "billing", GroupSettings.Default, -1), Created, 0, 0, null),
        });
        A.CallTo(() => _groups.ListAsync("orders-2", A<CancellationToken>._)).Returns(new List<SubscriptionGroupSummary>
        {
            new(new SubscriptionGroupDefinition("orders-2", "billing", GroupSettings.Default, -1), Created, 0, 0, null),
        });
        var client = new PersistentSubscriptions.PersistentSubscriptionsClient(_channel);

        // Act
        var all = (await client.ListAsync(new ListRequest(), cancellationToken: TestContext.Current.CancellationToken)).Groups;
        var ofOne = (await client.ListAsync(new ListRequest { Stream = "orders-2" }, cancellationToken: TestContext.Current.CancellationToken)).Groups;
        var badName = await Should.ThrowAsync<RpcException>(async () => await client.ListAsync(new ListRequest { Stream = "$nonsense" }, cancellationToken: TestContext.Current.CancellationToken));

        // Assert: another instance runs the first group and the listing says where, without sending anyone there.
        all.Count.ShouldBe(2);
        all[0].Running.ShouldBeTrue();
        all[0].OwnerAddress.ShouldBe("http://other-instance:5000/");
        all[0].Checkpoint.ShouldBe(9);
        all[0].ParkedCount.ShouldBe(3);
        all[0].HasLastKnownPosition.ShouldBeFalse();
        all[0].Live.ShouldNotBeNull().InFlightCount.ShouldBe(2);
        all[0].Live.Checkpoint.ShouldBe(11);
        all[0].Live.OldestInFlightAt.ShouldBeNull();
        all[0].Live.ConsumerAddress.ShouldBeEmpty();
        all[0].Live.AsOf.ToDateTimeOffset().ShouldBe(Created.AddMinutes(3));
        all[0].Live.FromOwner.ShouldBeFalse();
        all[1].Running.ShouldBeFalse();
        all[1].Live.ShouldBeNull();
        all[1].HasCheckpoint.ShouldBeFalse();
        ofOne.ShouldHaveSingleItem().Stream.ShouldBe("orders-2");
        badName.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason.ShouldBe("INVALID_STREAM_NAME");
    }

    private static EventRecord Record(string stream, long revision, long position) =>
        new(Guid.NewGuid(), stream, revision, position, "order_placed", Created, Encoding.UTF8.GetBytes("{}"), new JsonObject());

    private static async Task<List<PersistentReadResponse>> Next(AsyncDuplexStreamingCall<PersistentReadRequest, PersistentReadResponse> call, int count)
    {
        var messages = new List<PersistentReadResponse>();
        while (messages.Count < count && await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken))
        {
            messages.Add(call.ResponseStream.Current);
        }

        return messages;
    }

    /// <summary>Retries an assertion for a moment: the acknowledgement is applied on the server's own task.</summary>
    private static async Task Until(Action assertion)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                assertion();
                return;
            }
            catch (Exception) when (attempt < 50)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            }
        }
    }

    private sealed class TestOptions : NightingaleOptionsBase
    {
    }
}
