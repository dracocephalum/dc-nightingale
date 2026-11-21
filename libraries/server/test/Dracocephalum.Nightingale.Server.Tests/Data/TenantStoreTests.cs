using Dracocephalum.Nightingale.Server.Auth;
using Dracocephalum.Nightingale.Server.Data;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Data;

/// <summary>The tenant store over an in-memory table: the store id a new tenant gets, what a rename or a disable does and where it is applied at once, and the listing.</summary>
public sealed class TenantStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryContexts _contexts = new();
    private readonly ITenantProvisioner _provisioner = A.Fake<ITenantProvisioner>();
    private readonly TenantDirectory _directory;
    private readonly TenantStore _sut;

    public TenantStoreTests()
    {
        _directory = new TenantDirectory(_contexts, new SubscriptionGroupRegistry(), NullLogger<TenantDirectory>.Instance);
        _sut = new TenantStore(_contexts, _provisioner, _directory, new FakeTimeProvider(Now));
    }

    [Fact]
    public async Task CreateAsync_WhenItIsTheFirstTenant_ShouldGiveItStoreIdOneAndReadyTheStore()
    {
        // Act
        var created = await _sut.CreateAsync("billing", TestContext.Current.CancellationToken);

        // Assert
        (created.Name, created.StoreTenantId, created.IsDisabled, created.CreatedAt, created.UpdatedAt).ShouldBe(("billing", "1", false, Now, Now));
        A.CallTo(() => _provisioner.ProvisionAsync("1", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        (await _sut.ListAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Id.ShouldBe(created.Id);
    }

    [Fact]
    public async Task CreateAsync_ShouldTakeTheNumberAfterTheHighestInUsePassingOverIdsThatAreNotNumbers()
    {
        // Arrange: the default tenant's id is not a number, and counts as none.
        await _contexts.SeedAsync(
            new Tenant { Id = Tenant.DefaultId, Name = Tenant.DefaultName, StoreTenantId = Tenant.DefaultStoreTenantId },
            new Tenant { Id = Guid.NewGuid(), Name = "billing", StoreTenantId = "7" },
            new Tenant { Id = Guid.NewGuid(), Name = "shipping", StoreTenantId = "3" });

        // Act
        var created = await _sut.CreateAsync("invoicing", TestContext.Current.CancellationToken);

        // Assert
        created.StoreTenantId.ShouldBe("8");
    }

    [Fact]
    public async Task CreateAsync_WhenTheNameIsTaken_ShouldThrowThatItExistsAndReadyNothing()
    {
        // Arrange
        await _contexts.SeedAsync(new Tenant { Id = Guid.NewGuid(), Name = "billing", StoreTenantId = "1" });

        // Act
        var exists = await Should.ThrowAsync<TenantExistsException>(() => _sut.CreateAsync("billing", TestContext.Current.CancellationToken));

        // Assert
        exists.Message.ShouldContain("billing");
        A.CallTo(_provisioner).MustNotHaveHappened();
    }

    [Fact]
    public async Task CreateAsync_ShouldMakeTheTenantKnownHereAtOnce()
    {
        // Act
        var created = await _sut.CreateAsync("billing", TestContext.Current.CancellationToken);
        var known = await _directory.FindAsync(created.Id, TestContext.Current.CancellationToken);

        // Assert
        known.ShouldBe(new TenantDirectory.TenantEntry(created.Id, "1", false));
    }

    [Fact]
    public async Task UpdateAsync_WhenANameIsGiven_ShouldRenameAndKeepTheRest()
    {
        // Arrange
        var billing = new Tenant { Id = Guid.NewGuid(), Name = "billing", StoreTenantId = "1" };
        await _contexts.SeedAsync(billing);

        // Act
        var updated = await _sut.UpdateAsync(billing.Id, "invoicing", null, TestContext.Current.CancellationToken);

        // Assert
        var tenant = updated.ShouldNotBeNull();
        (tenant.Name, tenant.StoreTenantId, tenant.IsDisabled, tenant.UpdatedAt).ShouldBe(("invoicing", "1", false, Now));
    }

    [Fact]
    public async Task UpdateAsync_WhenTheNewNameIsAnotherTenants_ShouldThrowThatItExists()
    {
        // Arrange
        var billing = new Tenant { Id = Guid.NewGuid(), Name = "billing", StoreTenantId = "1" };
        await _contexts.SeedAsync(billing, new Tenant { Id = Guid.NewGuid(), Name = "shipping", StoreTenantId = "2" });

        // Act & Assert
        await Should.ThrowAsync<TenantExistsException>(() => _sut.UpdateAsync(billing.Id, "shipping", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_WhenTheNameGivenIsItsOwn_ShouldLeaveItBe()
    {
        // Arrange
        var billing = new Tenant { Id = Guid.NewGuid(), Name = "billing", StoreTenantId = "1" };
        await _contexts.SeedAsync(billing);

        // Act
        var updated = await _sut.UpdateAsync(billing.Id, "billing", null, TestContext.Current.CancellationToken);

        // Assert
        updated.ShouldNotBeNull().Name.ShouldBe("billing");
    }

    [Fact]
    public async Task UpdateAsync_WhenDisabled_ShouldSaySoHereAtOnce()
    {
        // Arrange: the directory has read the tenant, enabled.
        var billing = new Tenant { Id = Guid.NewGuid(), Name = "billing", StoreTenantId = "1" };
        await _contexts.SeedAsync(billing);
        var before = await _directory.FindAsync(billing.Id, TestContext.Current.CancellationToken);

        // Act
        var updated = await _sut.UpdateAsync(billing.Id, null, true, TestContext.Current.CancellationToken);
        var after = await _directory.FindAsync(billing.Id, TestContext.Current.CancellationToken);

        // Assert
        before.ShouldNotBeNull().IsDisabled.ShouldBeFalse();
        updated.ShouldNotBeNull().IsDisabled.ShouldBeTrue();
        after.ShouldNotBeNull().IsDisabled.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateAsync_WhenThereIsNoSuchTenant_ShouldGiveNothing()
    {
        // Act
        var updated = await _sut.UpdateAsync(Guid.NewGuid(), "invoicing", null, TestContext.Current.CancellationToken);

        // Assert
        updated.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldGiveTheTenantsByName()
    {
        // Arrange
        await _contexts.SeedAsync(
            new Tenant { Id = Guid.NewGuid(), Name = "shipping", StoreTenantId = "2" },
            new Tenant { Id = Guid.NewGuid(), Name = "billing", StoreTenantId = "1" });

        // Act
        var tenants = await _sut.ListAsync(TestContext.Current.CancellationToken);

        // Assert
        tenants.Select(tenant => tenant.Name).ShouldBe(["billing", "shipping"]);
    }
}
