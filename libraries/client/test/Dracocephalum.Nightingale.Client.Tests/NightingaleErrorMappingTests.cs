using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

public sealed class NightingaleErrorMappingTests
{
    [Fact]
    public void ToException_WhenRevisionConflict_ShouldCarryStreamExpectedAndActual()
    {
        // Arrange
        var failure = Failure(StatusCode.FailedPrecondition, "REVISION_CONFLICT", ("stream", "orders-1"), ("expected", "3"), ("actual", "5"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        var conflict = mapped.ShouldBeOfType<RevisionConflictException>();
        conflict.Stream.ShouldBe("orders-1");
        conflict.Expected.ShouldBe(StreamState.StreamRevision(3));
        conflict.ActualRevision.ShouldBe(5);
    }

    [Fact]
    public void ToException_WhenRevisionConflictOnNoStream_ShouldMapTheNamedState()
    {
        // Arrange
        var failure = Failure(StatusCode.FailedPrecondition, "REVISION_CONFLICT", ("stream", "orders-1"), ("expected", "-1"), ("actual", "0"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeOfType<RevisionConflictException>().Expected.ShouldBe(StreamState.NoStream);
    }

    [Theory]
    [InlineData("STREAM_DELETED", typeof(StreamDeletedException))]
    [InlineData("STREAM_NOT_FOUND", typeof(StreamNotFoundException))]
    [InlineData("INVALID_STREAM_NAME", typeof(ArgumentException))]
    [InlineData("FILTER_NOT_ALLOWED", typeof(ArgumentException))]
    [InlineData("INVALID_ARGUMENT", typeof(ArgumentException))]
    public void ToException_WhenReasonIsKnown_ShouldMapToItsException(string reason, System.Type expected)
    {
        // Arrange
        var failure = Failure(StatusCode.InvalidArgument, reason, ("stream", "orders-1"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeOfType(expected);
    }

    [Fact]
    public void ToException_WhenTheAppendCarriesTooManyEvents_ShouldSayHowManyOneMay()
    {
        // Arrange
        var failure = Failure(StatusCode.InvalidArgument, "APPEND_SIZE_EXCEEDED", ("stream", "orders-1"), ("limit", "150"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert: still an argument error, as it was before it had a type of its own.
        var exceeded = mapped.ShouldBeOfType<AppendSizeExceededException>();
        exceeded.Stream.ShouldBe("orders-1");
        exceeded.Limit.ShouldBe(150);
        exceeded.ShouldBeAssignableTo<ArgumentException>();
    }

    [Fact]
    public void ToException_WhenOrdinalsAreNotEnabled_ShouldNameTheStream()
    {
        // Arrange
        var failure = Failure(StatusCode.FailedPrecondition, "ORDINALS_NOT_ENABLED", ("stream", "$ce-orders"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeOfType<OrdinalsNotEnabledException>().Stream.ShouldBe("$ce-orders");
    }

    [Fact]
    public void ToException_WhenTheSequencerIsDisabledOrFaulted_ShouldSayWhichAndWhy()
    {
        // Arrange
        var disabled = Failure(StatusCode.FailedPrecondition, "SEQUENCER_DISABLED");
        var faulted = Failure(StatusCode.Unavailable, "SEQUENCER_FAULTED", ("cause", "the batch timed out"));

        // Act
        var whenDisabled = NightingaleErrorMapping.ToException(disabled);
        var whenFaulted = NightingaleErrorMapping.ToException(faulted);

        // Assert: one base type, so a caller that only cares that numbering stopped catches one.
        whenDisabled.ShouldBeOfType<OrdinalSequencerDisabledException>().ShouldBeAssignableTo<OrdinalSequencerException>();
        whenFaulted.ShouldBeOfType<OrdinalSequencerFaultedException>().Cause.ShouldBe("the batch timed out");
    }

    [Fact]
    public void ToException_WhenReasonIsUnknown_ShouldReturnTheOriginal()
    {
        // Arrange
        var failure = Failure(StatusCode.Internal, "SOMETHING_NEW");

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeSameAs(failure);
    }

    [Fact]
    public void ToException_WhenThereIsNoDetail_ShouldReturnTheOriginal()
    {
        // Arrange
        var failure = new RpcException(new Grpc.Core.Status(StatusCode.Unavailable, "gone"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeSameAs(failure);
    }

    [Fact]
    public void ToException_WhenDeletionIsDisabled_ShouldBeTheDeletionDisabledException()
    {
        // Arrange
        var failure = Failure(StatusCode.PermissionDenied, "DELETION_DISABLED", ("stream", "orders-1"), ("operation", "Tombstone"));

        // Act
        var exception = NightingaleErrorMapping.ToException(failure);

        // Assert
        exception.ShouldBeOfType<DeletionDisabledException>().Message.ShouldBe(failure.Status.Detail);
    }

    [Fact]
    public void ToException_WhenStreamsConflict_ShouldCarryEveryConflictFromTheTypedDetail()
    {
        // Arrange: the reason, and the conflicts beside it.
        var info = new ErrorInfo { Domain = "nightingale", Reason = "STREAM_CONFLICTS", Metadata = { ["count"] = "2" } };
        var conflicts = new Protocols.Grpc.V1.AppendConflicts
        {
            Conflicts =
            {
                new Protocols.Grpc.V1.AppendConflict { Index = 0, Stream = "orders-1", ExpectedRevision = 3, ActualRevision = 5 },
                new Protocols.Grpc.V1.AppendConflict { Index = 2, Stream = "ledger-1", ExpectedRevision = StreamState.NoStream.ToInt64(), ActualRevision = 0 },
            },
        };
        var failure = new Google.Rpc.Status { Code = (int)StatusCode.FailedPrecondition, Message = "failed", Details = { Any.Pack(info), Any.Pack(conflicts) } }.ToRpcException();

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeOfType<StreamConflictsException>().Conflicts.ShouldBe(
        [
            new StreamConflict(0, "orders-1", StreamState.StreamRevision(3), 5),
            new StreamConflict(2, "ledger-1", StreamState.NoStream, 0),
        ]);
    }

    [Theory]
    [InlineData("AUTHENTICATION_FAILED", typeof(AuthenticationFailedException))]
    [InlineData("CREDENTIAL_EXISTS", typeof(CredentialExistsException))]
    [InlineData("CREDENTIAL_NOT_FOUND", typeof(CredentialNotFoundException))]
    [InlineData("TENANT_EXISTS", typeof(TenantExistsException))]
    public void ToException_WhenAManagementCallIsRefused_ShouldMapToItsExceptionWithTheServersMessage(string reason, System.Type expected)
    {
        // Arrange
        var failure = Failure(StatusCode.FailedPrecondition, reason);

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeOfType(expected);
        mapped.Message.ShouldBe(failure.Status.Detail);
    }

    [Theory]
    [InlineData("GROUP_EXISTS", typeof(GroupExistsException))]
    [InlineData("GROUP_NOT_FOUND", typeof(GroupNotFoundException))]
    [InlineData("CONSUMER_LIMIT_REACHED", typeof(ConsumerLimitReachedException))]
    [InlineData("PARKED_MESSAGE_NOT_FOUND", typeof(ParkedMessageNotFoundException))]
    public void ToException_WhenAGroupCallIsRefused_ShouldMapToItsExceptionNamingTheStreamAndTheGroup(string reason, System.Type expected)
    {
        // Arrange
        var failure = Failure(StatusCode.FailedPrecondition, reason, ("stream", "orders-1"), ("group", "billing"));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeOfType(expected);
        mapped.Message.ShouldContain("orders-1");
        mapped.Message.ShouldContain("billing");
    }

    [Theory]
    [InlineData("ops", CredentialRole.Ops)]
    [InlineData("ADMIN", CredentialRole.Admin)]
    [InlineData("", null)]
    [InlineData("auditor", null)]
    public void ToException_WhenAccessIsDenied_ShouldSayWhichRoleWasNeededWhenTheServerNamedOne(string named, CredentialRole? required)
    {
        // Arrange
        var failure = Failure(StatusCode.PermissionDenied, "ACCESS_DENIED", ("required", named));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        var denied = mapped.ShouldBeOfType<AccessDeniedException>();
        denied.Required.ShouldBe(required);
        denied.Message.ShouldBe(failure.Status.Detail);
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", "11111111-1111-1111-1111-111111111111")]
    [InlineData("not-an-id", null)]
    public void ToException_WhenTheTenantIsNotFound_ShouldCarryItsIdWhenTheServerGaveOne(string named, string? expected)
    {
        // Arrange
        var failure = Failure(StatusCode.NotFound, "TENANT_NOT_FOUND", ("tenant", named));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeOfType<TenantNotFoundException>().TenantId.ShouldBe(expected is null ? null : Guid.Parse(expected));
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", "11111111-1111-1111-1111-111111111111")]
    [InlineData("", "00000000-0000-0000-0000-000000000000")]
    public void ToException_WhenTheTenantIsDisabled_ShouldCarryItsId(string named, string expected)
    {
        // Arrange
        var failure = Failure(StatusCode.PermissionDenied, "TENANT_DISABLED", ("tenant", named));

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeOfType<TenantDisabledException>().TenantId.ShouldBe(Guid.Parse(expected));
    }

    [Fact]
    public void ToException_WhenTheReasonIsAnotherProductsOwn_ShouldReturnTheOriginal()
    {
        // Arrange: a reason this build knows, under a domain that is not the server's.
        var info = new ErrorInfo { Domain = "storage.example", Reason = "STREAM_DELETED" };
        var failure = new Google.Rpc.Status { Code = (int)StatusCode.FailedPrecondition, Message = "failed", Details = { Any.Pack(info) } }.ToRpcException();

        // Act
        var mapped = NightingaleErrorMapping.ToException(failure);

        // Assert
        mapped.ShouldBeSameAs(failure);
    }

    private static RpcException Failure(StatusCode code, string reason, params (string Key, string Value)[] metadata)
    {
        var info = new ErrorInfo { Domain = "nightingale", Reason = reason };
        foreach (var (key, value) in metadata)
        {
            info.Metadata[key] = value;
        }

        return new Google.Rpc.Status { Code = (int)code, Message = "failed", Details = { Any.Pack(info) } }.ToRpcException();
    }
}
