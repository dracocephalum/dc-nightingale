using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class ExceptionTests
{
    private const string Stream = "orders-1";

    private const string Group = "billing";

    private static readonly Guid Tenant = Guid.Parse("7d2b6c1e-0000-4000-8000-000000000001");

    /// <summary>Gets the exceptions that carry the three constructors every exception is expected to have.</summary>
    public static TheoryData<Type> Conventional => new()
    {
        typeof(AccessDeniedException),
        typeof(AppendSizeExceededException),
        typeof(AuthenticationFailedException),
        typeof(ConsumerLimitReachedException),
        typeof(CredentialExistsException),
        typeof(CredentialNotFoundException),
        typeof(DeletionDisabledException),
        typeof(GroupExistsException),
        typeof(GroupNotFoundException),
        typeof(GroupUpdatedException),
        typeof(ParkedMessageNotFoundException),
        typeof(TenantDisabledException),
        typeof(TenantExistsException),
        typeof(TenantNotFoundException),
    };

    /// <summary>Gets the exceptions about one persistent-subscription group, which name its stream and the group.</summary>
    public static TheoryData<Type> OfAGroup => new()
    {
        typeof(ConsumerLimitReachedException),
        typeof(GroupExistsException),
        typeof(GroupNotFoundException),
        typeof(GroupUpdatedException),
        typeof(ParkedMessageNotFoundException),
    };

    /// <summary>Gets the exceptions about one stream, which take its name and nothing else.</summary>
    public static TheoryData<Type> OfAStream => new()
    {
        typeof(OrdinalsNotEnabledException),
        typeof(StreamDeletedException),
        typeof(StreamNotFoundException),
    };

    [Theory]
    [MemberData(nameof(Conventional))]
    public void Constructor_WhenGivenNothing_ShouldSayMoreThanItsBaseTypeDoes(Type type)
    {
        // Arrange
        var framework = Create(type.BaseType!);

        // Act
        var sut = Create(type);

        // Assert
        sut.Message.ShouldNotBeNullOrWhiteSpace();
        sut.Message.ShouldNotBe(framework.Message);
    }

    [Theory]
    [MemberData(nameof(Conventional))]
    public void Constructor_WhenGivenAMessage_ShouldCarryIt(Type type)
    {
        // Act
        var sut = Create(type, "The call was refused.");

        // Assert
        sut.Message.ShouldBe("The call was refused.");
        sut.InnerException.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Conventional))]
    public void Constructor_WhenGivenAMessageAndACause_ShouldCarryBoth(Type type)
    {
        // Arrange
        var cause = new TimeoutException();

        // Act
        var sut = Create(type, "The call was refused.", cause);

        // Assert
        sut.Message.ShouldBe("The call was refused.");
        sut.InnerException.ShouldBeSameAs(cause);
    }

    [Theory]
    [MemberData(nameof(OfAGroup))]
    public void Constructor_WhenGivenAStreamAndAGroup_ShouldNameBoth(Type type)
    {
        // Act
        var sut = Create(type, Stream, Group);

        // Assert
        ReadText(sut, "Stream").ShouldBe(Stream);
        ReadText(sut, "Group").ShouldBe(Group);
        sut.Message.ShouldContain("Stream 'orders-1', group 'billing'.");
    }

    [Theory]
    [MemberData(nameof(OfAGroup))]
    public void Constructor_WhenGivenOnlyAMessage_ShouldLeaveTheStreamAndTheGroupEmpty(Type type)
    {
        // Act
        var sut = Create(type, "The call was refused.");

        // Assert
        ReadText(sut, "Stream").ShouldBe(string.Empty);
        ReadText(sut, "Group").ShouldBe(string.Empty);
    }

    [Theory]
    [MemberData(nameof(OfAStream))]
    public void Constructor_WhenGivenAStream_ShouldNameIt(Type type)
    {
        // Act
        var sut = Create(type, Stream);

        // Assert
        ReadText(sut, "Stream").ShouldBe(Stream);
        sut.Message.ShouldContain("Stream 'orders-1'");
    }

    [Fact]
    public void AccessDenied_WhenGivenTheRoleRequired_ShouldCarryIt()
    {
        // Act
        var sut = new AccessDeniedException("Only an operator may do this.", CredentialRole.Ops);

        // Assert
        sut.Required.ShouldBe(CredentialRole.Ops);
        sut.Message.ShouldBe("Only an operator may do this.");
    }

    [Fact]
    public void AccessDenied_WhenGivenNoRole_ShouldRequireNone()
    {
        // Act
        var sut = new AccessDeniedException("The credential may not do this.");

        // Assert
        sut.Required.ShouldBeNull();
    }

    [Fact]
    public void AppendSizeExceeded_WhenTheLimitIsKnown_ShouldNameTheStreamAndTheLimit()
    {
        // Act
        var sut = new AppendSizeExceededException(Stream, 500);

        // Assert
        sut.Stream.ShouldBe(Stream);
        sut.Limit.ShouldBe(500);
        sut.Message.ShouldContain("stream 'orders-1'");
        sut.Message.ShouldContain("than the 500 one append may");
    }

    [Fact]
    public void AppendSizeExceeded_WhenTheLimitIsUnknown_ShouldLeaveItOutOfTheMessage()
    {
        // Act
        var sut = new AppendSizeExceededException(Stream, 0);

        // Assert
        sut.Limit.ShouldBe(0);
        sut.Message.ShouldContain("stream 'orders-1'");
        sut.Message.ShouldContain("than one append may");
    }

    [Fact]
    public void AppendSizeExceeded_WhenGivenACause_ShouldCarryIt()
    {
        // Arrange
        var cause = new TimeoutException();

        // Act
        var sut = new AppendSizeExceededException(Stream, 500, cause);

        // Assert
        sut.InnerException.ShouldBeSameAs(cause);
    }

    [Fact]
    public void AppendSizeExceeded_WhenGivenOnlyAMessage_ShouldLeaveTheStreamEmpty()
    {
        // Act
        var sut = new AppendSizeExceededException("The append is too large.");

        // Assert
        sut.Stream.ShouldBe(string.Empty);
        sut.Limit.ShouldBe(0);
    }

    [Fact]
    public void GroupOwnedElsewhere_WhenTheOwnerAdvertisesAnAddress_ShouldNameTheOwnerAndTheAddress()
    {
        // Arrange
        var address = new Uri("http://node-2.example:2113");

        // Act
        var sut = new GroupOwnedElsewhereException(Stream, Group, "node-2", address);

        // Assert
        sut.Stream.ShouldBe(Stream);
        sut.Group.ShouldBe(Group);
        sut.Owner.ShouldBe("node-2");
        sut.Address.ShouldBe(address);
        sut.Message.ShouldBe("Group 'billing' on stream 'orders-1' is owned by instance node-2 at http://node-2.example:2113/.");
    }

    [Fact]
    public void GroupOwnedElsewhere_WhenTheOwnerAdvertisesNoAddress_ShouldSaySo()
    {
        // Act
        var sut = new GroupOwnedElsewhereException(Stream, Group, "node-2");

        // Assert
        sut.Owner.ShouldBe("node-2");
        sut.Address.ShouldBeNull();
        sut.Message.ShouldBe("Group 'billing' on stream 'orders-1' is owned by instance node-2, which advertises no address.");
    }

    [Fact]
    public void GroupOwnedElsewhere_WhenNoInstanceHoldsTheGroup_ShouldNameNoOwner()
    {
        // Act
        var sut = new GroupOwnedElsewhereException(Stream, Group);

        // Assert
        sut.Stream.ShouldBe(Stream);
        sut.Group.ShouldBe(Group);
        sut.Owner.ShouldBe(string.Empty);
        sut.Address.ShouldBeNull();
        sut.Message.ShouldContain("Group 'billing' on stream 'orders-1' is no longer owned by this instance");
    }

    [Fact]
    public void OrdinalSequencerDisabled_WhenRaised_ShouldSayTheSequencerIsDisabled()
    {
        // Act
        OrdinalSequencerException sut = new OrdinalSequencerDisabledException();

        // Assert
        sut.Message.ShouldStartWith("The ordinal sequencer is disabled");
        sut.InnerException.ShouldBeNull();
    }

    [Fact]
    public void OrdinalSequencerFaulted_WhenGivenWhatWentWrong_ShouldCarryItInTheCauseAndTheMessage()
    {
        // Arrange
        var failure = new TimeoutException();

        // Act
        OrdinalSequencerException sut = new OrdinalSequencerFaultedException("the connection was lost", failure);

        // Assert
        sut.ShouldBeOfType<OrdinalSequencerFaultedException>().Cause.ShouldBe("the connection was lost");
        sut.Message.ShouldEndWith("trying again: the connection was lost");
        sut.InnerException.ShouldBeSameAs(failure);
    }

    [Fact]
    public void RevisionConflict_WhenRaised_ShouldCarryWhatWasExpectedAndWhatWasFound()
    {
        // Act
        var sut = new RevisionConflictException(Stream, StreamState.NoStream, 4);

        // Assert
        sut.Stream.ShouldBe(Stream);
        sut.Expected.ShouldBe(StreamState.NoStream);
        sut.ActualRevision.ShouldBe(4);
        sut.Message.ShouldBe("Stream 'orders-1' was expected at NoStream but is at revision 4.");
    }

    [Fact]
    public void TenantDisabled_WhenGivenTheTenant_ShouldNameIt()
    {
        // Act
        var sut = new TenantDisabledException(Tenant);

        // Assert
        sut.TenantId.ShouldBe(Tenant);
        sut.Message.ShouldBe("Tenant '7d2b6c1e-0000-4000-8000-000000000001' is disabled.");
    }

    [Fact]
    public void TenantNotFound_WhenGivenTheTenant_ShouldNameIt()
    {
        // Act
        var sut = new TenantNotFoundException(Tenant);

        // Assert
        sut.TenantId.ShouldBe(Tenant);
        sut.Message.ShouldBe("Tenant '7d2b6c1e-0000-4000-8000-000000000001' was not found.");
    }

    [Fact]
    public void TenantNotFound_WhenTheCallNamedNoTenant_ShouldSaySo()
    {
        // Act
        var sut = new TenantNotFoundException((Guid?)null);

        // Assert
        sut.TenantId.ShouldBeNull();
        sut.Message.ShouldStartWith("The call named no tenant");
    }

    [Fact]
    public void ValueTooLong_WhenGivenWhatWasTooLong_ShouldNameIt()
    {
        // Arrange
        var cause = new TimeoutException();

        // Act
        var sut = new ValueTooLongException(ValueTooLongException.StreamName, cause);

        // Assert
        sut.What.ShouldBe("stream name");
        sut.Message.ShouldBe("The stream name is longer than the store holds.");
        sut.InnerException.ShouldBeSameAs(cause);
    }

    private static Exception Create(Type type, params object[] arguments) =>
        (Exception)Activator.CreateInstance(type, arguments)!;

    private static string? ReadText(Exception sut, string property) =>
        (string?)sut.GetType().GetProperty(property)?.GetValue(sut);
}
