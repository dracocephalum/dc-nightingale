using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>Creating and deleting a persistent-subscription group over scripted unary calls: what the client sends, and what a refusal becomes.</summary>
public sealed class PersistentSubscriptionCallsTests
{
    [Fact]
    public async Task CreatePersistentSubscriptionAsync_WhenNoSettingsAreGiven_ShouldSendTheDefaults()
    {
        // Arrange
        var asked = new List<CreateRequest>();
        await using var sut = new NightingaleClient(FakeUnaryCall.Answering(asked, new CreateResponse()));

        // Act
        await sut.CreatePersistentSubscriptionAsync("orders-1", "billing", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().ShouldBe(new CreateRequest { Stream = "orders-1", Group = "billing", Settings = GroupSettings.Default.ToWire() });
    }

    [Fact]
    public async Task CreatePersistentSubscriptionAsync_ShouldSendTheSettingsGiven()
    {
        // Arrange
        var asked = new List<CreateRequest>();
        await using var sut = new NightingaleClient(FakeUnaryCall.Answering(asked, new CreateResponse()));
        var settings = GroupSettings.Default with { MaxRetryCount = 0, MaxSubscriberCount = 3 };

        // Act
        await sut.CreatePersistentSubscriptionAsync("$ce-orders", "billing", settings, TestContext.Current.CancellationToken);

        // Assert: a zero is a setting, and is sent as one.
        var sent = asked.ShouldHaveSingleItem().Settings;
        sent.HasMaxRetryCount.ShouldBeTrue();
        (sent.MaxRetryCount, sent.MaxSubscriberCount).ShouldBe((0, 3));
    }

    [Fact]
    public async Task CreatePersistentSubscriptionAsync_WhenTheGroupExists_ShouldThrowNamingIt()
    {
        // Arrange
        await using var sut = new NightingaleClient(FakeUnaryCall.Failing<CreateRequest, CreateResponse>(FakeUnaryCall.Failure(StatusCode.AlreadyExists, "GROUP_EXISTS", ("stream", "orders-1"), ("group", "billing"))));

        // Act
        var exists = await Should.ThrowAsync<GroupExistsException>(() => sut.CreatePersistentSubscriptionAsync("orders-1", "billing", cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        (exists.Stream, exists.Group).ShouldBe(("orders-1", "billing"));
    }

    [Fact]
    public async Task DeletePersistentSubscriptionAsync_ShouldNameTheStreamAndTheGroup()
    {
        // Arrange
        var asked = new List<DeleteGroupRequest>();
        await using var sut = new NightingaleClient(FakeUnaryCall.Answering(asked, new DeleteGroupResponse()));

        // Act
        await sut.DeletePersistentSubscriptionAsync("orders-1", "billing", TestContext.Current.CancellationToken);

        // Assert
        asked.ShouldHaveSingleItem().ShouldBe(new DeleteGroupRequest { Stream = "orders-1", Group = "billing" });
    }

    [Fact]
    public async Task DeletePersistentSubscriptionAsync_WhenThereIsNoSuchGroup_ShouldThrowNamingIt()
    {
        // Arrange
        await using var sut = new NightingaleClient(FakeUnaryCall.Failing<DeleteGroupRequest, DeleteGroupResponse>(FakeUnaryCall.Failure(StatusCode.NotFound, "GROUP_NOT_FOUND", ("stream", "orders-1"), ("group", "billing"))));

        // Act
        var missing = await Should.ThrowAsync<GroupNotFoundException>(() => sut.DeletePersistentSubscriptionAsync("orders-1", "billing", TestContext.Current.CancellationToken));

        // Assert
        (missing.Stream, missing.Group).ShouldBe(("orders-1", "billing"));
    }
}
