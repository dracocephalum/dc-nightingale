using Dracocephalum.Nightingale.Protocol.V1;
using FakeItEasy;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>
/// Describing and listing persistent-subscription groups over scripted unary calls: what the
/// client asks, what it makes of the answer, and that it follows the owner's address once.
/// </summary>
public sealed class PersistentSubscriptionInfoTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Connected = new(2026, 9, 21, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetInfo_ShouldAskForTheGroupAndReturnEverythingTheServerSaid()
    {
        // Arrange
        var asked = new List<GetInfoRequest>();
        var invoker = Describing(asked, new GroupInfo
        {
            Stream = "$ce-orders",
            Group = "billing",
            Settings = new Protocol.V1.GroupSettings { FromStart = new(), MaxRetryCount = 3, Numbering = Protocol.V1.Numbering.Ordinal },
            CreatedAt = Timestamp.FromDateTimeOffset(Created),
            Checkpoint = 41,
            ParkedCount = 2,
            OutboxCount = 1,
            Running = true,
            OwnerAddress = "http://node-1:5000/",
            LastKnownPosition = 57,
            Live = new GroupLiveInfo
            {
                ConnectedAt = Timestamp.FromDateTimeOffset(Connected),
                InFlightCount = 4,
                AwaitingRetryCount = 1,
                ConsumerBufferSize = 10,
                Checkpoint = 43,
                OldestInFlightAt = Timestamp.FromDateTimeOffset(Connected.AddMinutes(1)),
                ConsumerAddress = "ipv4:10.0.0.7:51234",
                AsOf = Timestamp.FromDateTimeOffset(Connected.AddMinutes(2)),
                FromOwner = true,
                ConsumerCount = 2,
                Consumers =
                {
                    new ConsumerInfo { ConnectedAt = Timestamp.FromDateTimeOffset(Connected), Address = "ipv4:10.0.0.7:51234", BufferSize = 6, InFlightCount = 3 },
                    new ConsumerInfo { ConnectedAt = Timestamp.FromDateTimeOffset(Connected.AddSeconds(30)), Address = "ipv4:10.0.0.8:51234", BufferSize = 4, InFlightCount = 1 },
                },
            },
        });
        await using var sut = new NightingaleClient(invoker);

        // Act
        var info = await sut.GetPersistentSubscriptionInfoAsync("$ce-orders", "billing", TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().ShouldBe(new GetInfoRequest { Stream = "$ce-orders", Group = "billing" });
        info.Stream.ShouldBe("$ce-orders");
        info.Group.ShouldBe("billing");
        info.Settings.Start.ShouldBe(StreamPosition.Start);
        info.Settings.MaxRetryCount.ShouldBe(3);
        info.Settings.Numbering.ShouldBe(Numbering.Ordinal);
        info.CreatedAt.ShouldBe(Created);
        info.Checkpoint.ShouldBe(41);
        info.ParkedCount.ShouldBe(2);
        info.OutboxCount.ShouldBe(1);
        info.Running.ShouldBeTrue();
        info.OwnerAddress.ShouldBe(new Uri("http://node-1:5000/"));
        info.LastKnownPosition.ShouldBe(57);
        info.Live.ShouldBeEquivalentTo(new PersistentSubscriptionLiveInfo(
            Connected,
            4,
            1,
            10,
            43,
            Connected.AddMinutes(1),
            "ipv4:10.0.0.7:51234",
            Connected.AddMinutes(2),
            true,
            2,
            new List<PersistentSubscriptionConsumerInfo> { new(Connected, "ipv4:10.0.0.7:51234", 6, 3), new(Connected.AddSeconds(30), "ipv4:10.0.0.8:51234", 4, 1) }));
    }

    [Fact]
    public async Task GetInfo_WhenTheServerLeavesNumbersOut_ShouldLeaveThemNull()
    {
        // Arrange: a group created and never read, over a stream that holds nothing.
        var invoker = Describing([], new GroupInfo { Stream = "orders-1", Group = "billing", Settings = new Protocol.V1.GroupSettings(), CreatedAt = Timestamp.FromDateTimeOffset(Created) });
        await using var sut = new NightingaleClient(invoker);

        // Act
        var info = await sut.GetPersistentSubscriptionInfoAsync("orders-1", "billing", TestContext.Current.CancellationToken);

        // Assert
        info.Checkpoint.ShouldBeNull();
        info.LastKnownPosition.ShouldBeNull();
        info.Running.ShouldBeFalse();
        info.OwnerAddress.ShouldBeNull();
        info.Live.ShouldBeNull();
        info.Settings.ShouldBe(GroupSettings.Default);
    }

    [Fact]
    public async Task GetInfo_WhenTheGroupRunsElsewhereWithAnAddress_ShouldAskThereOnce()
    {
        // Arrange: the first instance refuses and names the owner's address; the owner answers.
        var front = Failing<GetInfoRequest, GetInfoResponse>(Failure(StatusCode.Unavailable, "GROUP_OWNED_ELSEWHERE", ("stream", "orders-1"), ("group", "billing"), ("owner", "node-2"), ("address", "http://node-2:5000/")));
        var askedOfOwner = new List<GetInfoRequest>();
        var owner = Describing(askedOfOwner, new GroupInfo { Stream = "orders-1", Group = "billing", Running = true, OwnerAddress = "http://node-2:5000/", Live = new GroupLiveInfo { InFlightCount = 2 } });
        var redirectedTo = new List<Uri>();
        await using var sut = new NightingaleClient(front, address =>
        {
            redirectedTo.Add(address);
            return owner;
        });

        // Act
        var info = await sut.GetPersistentSubscriptionInfoAsync("orders-1", "billing", TestContext.Current.CancellationToken);

        // Assert
        redirectedTo.ShouldBe([new Uri("http://node-2:5000/")]);
        askedOfOwner.ShouldHaveSingleItem().Group.ShouldBe("billing");
        info.Live.ShouldNotBeNull().InFlightCount.ShouldBe(2);
        info.Live.Checkpoint.ShouldBeNull();
        info.Live.OldestInFlightAt.ShouldBeNull();
        info.Live.ConsumerAddress.ShouldBeNull();
        info.Live.FromOwner.ShouldBeFalse();
    }

    [Fact]
    public async Task GetInfo_WhenTheGroupIsMissing_ShouldFailAsNotFound()
    {
        // Arrange
        var invoker = Failing<GetInfoRequest, GetInfoResponse>(Failure(StatusCode.NotFound, "GROUP_NOT_FOUND", ("stream", "orders-1"), ("group", "nobody")));
        await using var sut = new NightingaleClient(invoker);

        // Act
        var exception = await Should.ThrowAsync<GroupNotFoundException>(() => sut.GetPersistentSubscriptionInfoAsync("orders-1", "nobody", TestContext.Current.CancellationToken));

        // Assert
        exception.Group.ShouldBe("nobody");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("orders-1", "orders-1")]
    public async Task List_ShouldAskForEveryGroupOrOneStreamsAndReturnThemInTheServersOrder(string? stream, string asked)
    {
        // Arrange
        var requests = new List<ListRequest>();
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<ListRequest, ListResponse>>._, A<string?>._, A<CallOptions>._, A<ListRequest>._))
            .ReturnsLazily(call =>
            {
                requests.Add(call.GetArgument<ListRequest>(3)!);
                return Unary(Task.FromResult(new ListResponse
                {
                    Groups =
                    {
                        new GroupInfo { Stream = "orders-1", Group = "billing", Checkpoint = 9, ParkedCount = 3, Running = true, OwnerAddress = "http://node-2:5000/" },
                        new GroupInfo { Stream = "orders-1", Group = "shipping" },
                    },
                }));
            });
        await using var sut = new NightingaleClient(invoker);

        // Act
        var groups = await sut.ListPersistentSubscriptionsAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        requests.ShouldHaveSingleItem().Stream.ShouldBe(asked);
        groups.Select(group => group.Group).ShouldBe(["billing", "shipping"]);
        groups[0].Checkpoint.ShouldBe(9);
        groups[0].ParkedCount.ShouldBe(3);
        groups[0].OwnerAddress.ShouldBe(new Uri("http://node-2:5000/"));
        groups[0].Live.ShouldBeNull();
        groups[1].Checkpoint.ShouldBeNull();
        groups[1].Running.ShouldBeFalse();
    }

    private static CallInvoker Describing(List<GetInfoRequest> asked, GroupInfo info)
    {
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<GetInfoRequest, GetInfoResponse>>._, A<string?>._, A<CallOptions>._, A<GetInfoRequest>._))
            .ReturnsLazily(call =>
            {
                asked.Add(call.GetArgument<GetInfoRequest>(3)!);
                return Unary(Task.FromResult(new GetInfoResponse { Info = info }));
            });
        return invoker;
    }

    private static CallInvoker Failing<TRequest, TResponse>(RpcException failure)
        where TRequest : class
        where TResponse : class
    {
        var invoker = A.Fake<CallInvoker>();
        A.CallTo(() => invoker.AsyncUnaryCall(A<Method<TRequest, TResponse>>._, A<string?>._, A<CallOptions>._, A<TRequest>._))
            .ReturnsLazily(_ => Unary(Task.FromException<TResponse>(failure)));
        return invoker;
    }

    private static AsyncUnaryCall<TResponse> Unary<TResponse>(Task<TResponse> response) =>
        new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

    private static RpcException Failure(StatusCode code, string reason, params (string Key, string Value)[] metadata)
    {
        var info = new Google.Rpc.ErrorInfo { Domain = "nightingale", Reason = reason };
        foreach (var (key, value) in metadata)
        {
            info.Metadata[key] = value;
        }

        var status = new Google.Rpc.Status { Code = (int)code, Message = reason, Details = { Any.Pack(info) } };
        return status.ToRpcException();
    }
}
