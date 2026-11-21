using Dracocephalum.Nightingale.Protocols.Grpc;
using Dracocephalum.Nightingale.Protocols.Grpc.V1;
using Shouldly;

namespace Dracocephalum.Nightingale.Tests;

public sealed class ErrorReasonsTests
{
    public static TheoryData<ErrorReason> Every => [.. Enum.GetValues<ErrorReason>()];

    [Theory]
    [InlineData(ErrorReason.Unspecified, "UNSPECIFIED")]
    [InlineData(ErrorReason.StreamNotFound, "STREAM_NOT_FOUND")]
    [InlineData(ErrorReason.RevisionConflict, "REVISION_CONFLICT")]
    public void GetName_ShouldBeTheContractsNameWithoutItsPrefix(ErrorReason reason, string expected)
    {
        // Act
        var name = ErrorReasons.GetName(reason);

        // Assert
        name.ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void Parse_WhenGivenTheNameOfAReason_ShouldBeThatReason(ErrorReason reason)
    {
        // Arrange
        var name = ErrorReasons.GetName(reason);

        // Act
        var parsed = ErrorReasons.Parse(name);

        // Assert
        parsed.ShouldBe(reason);
        name.ShouldNotStartWith("ERROR_REASON_");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOT_A_REASON")]
    [InlineData("revision_conflict")]
    [InlineData("ERROR_REASON_REVISION_CONFLICT")]
    public void Parse_WhenTheNameIsNotOneThisBuildKnows_ShouldBeUnspecified(string? name)
    {
        // Act
        var parsed = ErrorReasons.Parse(name);

        // Assert
        parsed.ShouldBe(ErrorReason.Unspecified);
    }
}
