using Dracocephalum.Nightingale.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Data;

/// <summary>The credential store over an in-memory table: what a failed and a successful login record, and what creating, changing, deleting and listing do.</summary>
public sealed class CredentialStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Billing = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly InMemoryContexts _contexts = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly CredentialStore _sut;

    public CredentialStoreTests() => _sut = new CredentialStore(_contexts, _time);

    [Fact]
    public async Task FindAsync_ShouldGiveTheCredentialOfThatNameOrNothing()
    {
        // Arrange
        var reader = NewCredential("reader");
        await _contexts.SeedAsync(reader, NewCredential("operator"));

        // Act
        var found = await _sut.FindAsync("reader", TestContext.Current.CancellationToken);
        var missing = await _sut.FindAsync("auditor", TestContext.Current.CancellationToken);

        // Assert
        found.ShouldNotBeNull().Id.ShouldBe(reader.Id);
        missing.ShouldBeNull();
    }

    [Fact]
    public async Task RecordFailureAsync_WhenBelowTheThreshold_ShouldCountAndNotLock()
    {
        // Arrange
        var reader = NewCredential("reader");
        await _contexts.SeedAsync(reader);

        // Act
        await _sut.RecordFailureAsync(reader.Id, 3, TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        await _sut.RecordFailureAsync(reader.Id, 3, TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        // Assert
        var row = await ReadRowAsync(reader.Id);
        (row.FailedAttempts, row.LockedUntil, row.UpdatedAt).ShouldBe((2, null, Now));
    }

    [Fact]
    public async Task RecordFailureAsync_WhenTheThresholdIsReached_ShouldLockForTheDurationAndStartCountingAgain()
    {
        // Arrange
        var reader = NewCredential("reader");
        reader.FailedAttempts = 2;
        await _contexts.SeedAsync(reader);

        // Act
        await _sut.RecordFailureAsync(reader.Id, 3, TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        // Assert
        var row = await ReadRowAsync(reader.Id);
        (row.FailedAttempts, row.LockedUntil).ShouldBe((0, Now.AddMinutes(15)));
    }

    [Fact]
    public async Task RecordFailureAsync_WhenTheThresholdIsZero_ShouldNeverLock()
    {
        // Arrange
        var reader = NewCredential("reader");
        reader.FailedAttempts = 40;
        await _contexts.SeedAsync(reader);

        // Act
        await _sut.RecordFailureAsync(reader.Id, 0, TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        // Assert
        var row = await ReadRowAsync(reader.Id);
        (row.FailedAttempts, row.LockedUntil).ShouldBe((41, null));
    }

    [Fact]
    public async Task RecordSuccessAsync_ShouldClearTheFailuresAndTheLockAndKeepANewHash()
    {
        // Arrange
        var reader = NewCredential("reader");
        reader.FailedAttempts = 2;
        reader.LockedUntil = Now.AddMinutes(-1);
        await _contexts.SeedAsync(reader);

        // Act
        await _sut.RecordSuccessAsync(reader.Id, "hash-remade", TestContext.Current.CancellationToken);

        // Assert
        var row = await ReadRowAsync(reader.Id);
        (row.FailedAttempts, row.LockedUntil, row.PasswordHash, row.UpdatedAt).ShouldBe((0, null, "hash-remade", Now));
    }

    [Fact]
    public async Task RecordSuccessAsync_WhenThereIsNothingToClear_ShouldWriteNothing()
    {
        // Arrange
        var reader = NewCredential("reader");
        await _contexts.SeedAsync(reader);

        // Act
        await _sut.RecordSuccessAsync(reader.Id, null, TestContext.Current.CancellationToken);

        // Assert: the row is as it was seeded, its time of change included.
        var row = await ReadRowAsync(reader.Id);
        (row.PasswordHash, row.UpdatedAt).ShouldBe(("hash", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task RecordingALogin_WhenTheCredentialIsGone_ShouldDoNothing()
    {
        // Act & Assert: a credential deleted while its login was verified is not an error.
        await Should.NotThrowAsync(() => _sut.RecordFailureAsync(Guid.NewGuid(), 3, TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken));
        await Should.NotThrowAsync(() => _sut.RecordSuccessAsync(Guid.NewGuid(), "hash-remade", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAsync_ShouldKeepTheCredential()
    {
        // Arrange
        var reader = NewCredential("reader", Billing);

        // Act
        await _sut.CreateAsync(reader, TestContext.Current.CancellationToken);

        // Assert
        var row = await ReadRowAsync(reader.Id);
        (row.Name, row.TenantId).ShouldBe(("reader", Billing));
    }

    [Fact]
    public async Task CreateAsync_WhenTheNameIsTaken_ShouldThrowThatItExists()
    {
        // Arrange
        await _contexts.SeedAsync(NewCredential("reader"));

        // Act
        var exists = await Should.ThrowAsync<CredentialExistsException>(() => _sut.CreateAsync(NewCredential("reader"), TestContext.Current.CancellationToken));

        // Assert
        exists.Message.ShouldContain("reader");
    }

    [Fact]
    public async Task UpdateAsync_ShouldChangeOnlyWhatIsGivenAndStampTheChange()
    {
        // Arrange
        var reader = NewCredential("reader", Billing);
        var stamp = reader.SecurityStamp;
        await _contexts.SeedAsync(reader);

        // Act
        var updated = await _sut.UpdateAsync(reader.Id, CredentialRole.Ops, null, true, null, TestContext.Current.CancellationToken);

        // Assert: the stamp moves, so what was verified under the old one is verified again.
        var row = await ReadRowAsync(reader.Id);
        updated.ShouldNotBeNull().Role.ShouldBe(CredentialRole.Ops);
        (row.Role, row.TenantId, row.IsDisabled, row.PasswordHash, row.UpdatedAt).ShouldBe((CredentialRole.Ops, Billing, true, "hash", Now));
        row.SecurityStamp.ShouldNotBe(stamp);
        row.PasswordChangedAt.ShouldBe(DateTimeOffset.UnixEpoch);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("22222222-2222-2222-2222-222222222222", "22222222-2222-2222-2222-222222222222")]
    public async Task UpdateAsync_WhenATenantIsGiven_ShouldBindToItOrMakeTheCredentialGlobal(string given, string? kept)
    {
        // Arrange
        var reader = NewCredential("reader", Billing);
        await _contexts.SeedAsync(reader);

        // Act
        await _sut.UpdateAsync(reader.Id, null, Guid.Parse(given), null, null, TestContext.Current.CancellationToken);

        // Assert
        (await ReadRowAsync(reader.Id)).TenantId.ShouldBe(kept is null ? null : Guid.Parse(kept));
    }

    [Fact]
    public async Task UpdateAsync_WhenAPasswordHashIsGiven_ShouldKeepItAndClearTheLockout()
    {
        // Arrange
        var reader = NewCredential("reader");
        reader.FailedAttempts = 2;
        reader.LockedUntil = Now.AddMinutes(10);
        await _contexts.SeedAsync(reader);

        // Act
        await _sut.UpdateAsync(reader.Id, null, null, null, "hash-new", TestContext.Current.CancellationToken);

        // Assert
        var row = await ReadRowAsync(reader.Id);
        (row.PasswordHash, row.PasswordChangedAt, row.FailedAttempts, row.LockedUntil).ShouldBe(("hash-new", Now, 0, null));
    }

    [Fact]
    public async Task UpdateAsync_WhenThereIsNoSuchCredential_ShouldGiveNothing()
    {
        // Act
        var updated = await _sut.UpdateAsync(Guid.NewGuid(), CredentialRole.Admin, null, null, null, TestContext.Current.CancellationToken);

        // Assert
        updated.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheCredentialAndSayWhetherThereWasOne()
    {
        // Arrange
        var reader = NewCredential("reader");
        await _contexts.SeedAsync(reader);

        // Act
        var first = await _sut.DeleteAsync(reader.Id, TestContext.Current.CancellationToken);
        var second = await _sut.DeleteAsync(reader.Id, TestContext.Current.CancellationToken);

        // Assert
        (first, second).ShouldBe((true, false));
        (await _sut.FindAsync("reader", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldGiveEveryCredentialByNameOrThoseOfOneTenant()
    {
        // Arrange
        await _contexts.SeedAsync(NewCredential("reader", Billing), NewCredential("auditor"), NewCredential("operator", Billing));

        // Act
        var all = await _sut.ListAsync(null, TestContext.Current.CancellationToken);
        var ofBilling = await _sut.ListAsync(Billing, TestContext.Current.CancellationToken);

        // Assert
        all.Select(row => row.Name).ShouldBe(["auditor", "operator", "reader"]);
        ofBilling.Select(row => row.Name).ShouldBe(["operator", "reader"]);
    }

    private static Credential NewCredential(string name, Guid? tenantId = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PasswordHash = "hash",
        Role = CredentialRole.User,
        TenantId = tenantId,
        SecurityStamp = Guid.NewGuid(),
        PasswordChangedAt = DateTimeOffset.UnixEpoch,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private async Task<Credential> ReadRowAsync(Guid id)
    {
        await using var context = _contexts.CreateDbContext();
        return await context.Credentials.AsNoTracking().SingleAsync(row => row.Id == id, TestContext.Current.CancellationToken);
    }
}
