using Dracocephalum.Nightingale.Server.Auth;
using Shouldly;

namespace Dracocephalum.Nightingale.Server.Tests.Auth;

/// <summary>The hasher: what a hash carries, what verifies against it, and when it asks to be remade.</summary>
public sealed class PasswordHasherTests
{
    private static NightingaleOptionsBase.AuthSettings Settings(int iterations = 10_000, string? pepper = "pepper", int pepperId = 1) =>
        new() { AdminPassword = "x", Iterations = iterations, Pepper = pepper, PepperId = pepperId };

    [Fact]
    public void Hash_ShouldBeAPhcStringWithItsParametersAndARandomSalt()
    {
        // Arrange
        var sut = new PasswordHasher(Settings());

        // Act
        var first = sut.Hash("correct horse battery staple");
        var second = sut.Hash("correct horse battery staple");

        // Assert
        first.ShouldStartWith("$pbkdf2-sha256$i=10000,p=1$");
        first.ShouldNotBe(second, "each hash has its own salt");
        sut.Verify("correct horse battery staple", first).ShouldBe((true, false));
        sut.Verify("correct horse battery staple", second).ShouldBe((true, false));
        sut.Verify("Correct horse battery staple", first).Matches.ShouldBeFalse();
    }

    [Fact]
    public void Verify_WhenTheParametersMovedOn_ShouldMatchAndAskForARehash()
    {
        // Arrange: a hash made under fewer iterations, checked by a hasher that wants more.
        var old = new PasswordHasher(Settings(iterations: 10_000)).Hash("secret-enough-password");
        var sut = new PasswordHasher(Settings(iterations: 20_000));

        // Act & Assert
        sut.Verify("secret-enough-password", old).ShouldBe((true, true));
        sut.Verify("wrong", old).ShouldBe((false, false));
    }

    [Fact]
    public void Verify_UnderAnotherPepper_ShouldNotMatch()
    {
        // Arrange: the same password hashed under pepper 1, verified where the pepper is 2.
        var underFirst = new PasswordHasher(Settings(pepperId: 1)).Hash("secret-enough-password");
        var sut = new PasswordHasher(Settings(pepper: "other", pepperId: 2));

        // Act & Assert
        sut.Verify("secret-enough-password", underFirst).Matches.ShouldBeFalse();
    }

    [Fact]
    public void Verify_ShouldNormalizeThePasswordSoCanonicallyEqualTextMatches()
    {
        // Arrange: é as one code point and as e plus a combining accent.
        var sut = new PasswordHasher(Settings());
        var hash = sut.Hash("café-password-long-enough");

        // Act & Assert
        sut.Verify("café-password-long-enough", hash).Matches.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=4$c2FsdA$aGFzaA")]
    [InlineData("$pbkdf2-sha256$i=abc,p=1$c2FsdA$aGFzaA")]
    [InlineData("$pbkdf2-sha256$i=10000,p=1$not base64!$aGFzaA")]
    public void Verify_AgainstSomethingThatIsNotOneOfItsHashes_ShouldNotMatchNorThrow(string encoded)
    {
        // Arrange
        var sut = new PasswordHasher(Settings());

        // Act & Assert
        sut.Verify("anything", encoded).ShouldBe((false, false));
    }
}
