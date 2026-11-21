using Dracocephalum.Nightingale.Protocols.Grpc;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class ManagementConversionsTests
{
    private const string Tenant = "7d2b6c1e-0000-4000-8000-000000000001";

    private static readonly DateTimeOffset Created = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CredentialRole.User, Protocols.Grpc.V1.CredentialRole.User)]
    [InlineData(CredentialRole.Ops, Protocols.Grpc.V1.CredentialRole.Ops)]
    [InlineData(CredentialRole.Admin, Protocols.Grpc.V1.CredentialRole.Admin)]
    public void CredentialRole_ShouldRoundTrip(CredentialRole role, Protocols.Grpc.V1.CredentialRole wire)
    {
        // Act & Assert
        role.ToWire().ShouldBe(wire);
        wire.ToCredentialRole().ShouldBe(role);
    }

    [Fact]
    public void ToCredentialRole_WhenUnspecified_ShouldBeNullForTheCallerToDecide()
    {
        // Act
        var role = Protocols.Grpc.V1.CredentialRole.Unspecified.ToCredentialRole();

        // Assert
        role.ShouldBeNull();
    }

    [Fact]
    public void ToCredentialInfo_ShouldCarryEveryField()
    {
        // Arrange
        var wire = new Protocols.Grpc.V1.CredentialInfo
        {
            Name = "reporting",
            Role = Protocols.Grpc.V1.CredentialRole.Ops,
            Tenant = Tenant,
            Disabled = true,
            CreatedAt = Timestamp.FromDateTimeOffset(Created),
            PasswordChangedAt = Timestamp.FromDateTimeOffset(Created.AddDays(1)),
            LockedUntil = Timestamp.FromDateTimeOffset(Created.AddDays(2)),
        };

        // Act
        var info = wire.ToCredentialInfo();

        // Assert
        info.ShouldBe(new CredentialInfo("reporting", CredentialRole.Ops, Guid.Parse(Tenant), true, Created, Created.AddDays(1), Created.AddDays(2)));
    }

    [Fact]
    public void ToCredentialInfo_WhenOnlyTheNameIsSet_ShouldBeAUserOfNoTenantThatIsNotLocked()
    {
        // Arrange
        var wire = new Protocols.Grpc.V1.CredentialInfo { Name = "reporting" };

        // Act
        var info = wire.ToCredentialInfo();

        // Assert
        info.ShouldBe(new CredentialInfo("reporting", CredentialRole.User, null, false, default, default, null));
    }

    [Fact]
    public void ToTenantInfo_ShouldCarryEveryField()
    {
        // Arrange
        var wire = new Protocols.Grpc.V1.TenantInfo { Id = Tenant, Name = "north", Disabled = true, CreatedAt = Timestamp.FromDateTimeOffset(Created) };

        // Act
        var info = wire.ToTenantInfo();

        // Assert
        info.ShouldBe(new TenantInfo(Guid.Parse(Tenant), "north", true, Created));
    }

    [Fact]
    public void ToTenantInfo_WhenTheIdIsNotAGuid_ShouldThrow()
    {
        // Arrange
        var wire = new Protocols.Grpc.V1.TenantInfo { Id = "north", Name = "north" };

        // Act & Assert
        Should.Throw<FormatException>(() => wire.ToTenantInfo());
    }
}
