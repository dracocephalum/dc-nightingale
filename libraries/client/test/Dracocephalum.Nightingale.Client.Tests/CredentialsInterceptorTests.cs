using Dracocephalum.Nightingale.Protocol.V1;
using FakeItEasy;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Shouldly;

namespace Dracocephalum.Nightingale.Client.Tests;

/// <summary>The headers the client sends with every call: the credentials under Basic, the tenant when one is named, and never over what the call carries itself.</summary>
public sealed class CredentialsInterceptorTests
{
    private static readonly Method<DeleteRequest, DeleteResponse> Unary = new(MethodType.Unary, "nightingale.v1.Streams", "Delete", Marshallers.Create(_ => [], _ => new DeleteRequest()), Marshallers.Create(_ => [], _ => new DeleteResponse()));
    private readonly CallInvoker _inner = A.Fake<CallInvoker>();

    [Fact]
    public void AsyncUnaryCall_ShouldSendTheCredentialsAndTheTenant()
    {
        // Arrange
        var sut = _inner.Intercept(new CredentialsInterceptor(new UserCredentials("reader", "p@ss:word"), "*"));

        // Act
        sut.AsyncUnaryCall(Unary, null, default, new DeleteRequest());

        // Assert: "reader:p@ss:word" in base64, and the tenant as given.
        A.CallTo(() => _inner.AsyncUnaryCall(Unary, null, A<CallOptions>.That.Matches(options => Has(options, "authorization", "Basic cmVhZGVyOnBAc3M6d29yZA==") && Has(options, "nightingale-tenant", "*")), A<DeleteRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void AsyncUnaryCall_WhenTheCallCarriesTheHeadersAlready_ShouldKeepThem()
    {
        // Arrange
        var sut = _inner.Intercept(new CredentialsInterceptor(new UserCredentials("reader", "secret"), "*"));
        var own = new Metadata { { "authorization", "Basic b3duOm93bg==" }, { "nightingale-tenant", "11111111-1111-1111-1111-111111111111" } };

        // Act
        sut.AsyncUnaryCall(Unary, null, new CallOptions(headers: own), new DeleteRequest());

        // Assert
        A.CallTo(() => _inner.AsyncUnaryCall(Unary, null, A<CallOptions>.That.Matches(options => options.Headers!.Count == 2 && Has(options, "authorization", "Basic b3duOm93bg==") && Has(options, "nightingale-tenant", "11111111-1111-1111-1111-111111111111")), A<DeleteRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void AsyncUnaryCall_WithATenantOnly_ShouldSendJustThat()
    {
        // Arrange
        var sut = _inner.Intercept(new CredentialsInterceptor(null, "*"));

        // Act
        sut.AsyncUnaryCall(Unary, null, default, new DeleteRequest());

        // Assert
        A.CallTo(() => _inner.AsyncUnaryCall(Unary, null, A<CallOptions>.That.Matches(options => options.Headers!.Count == 1 && Has(options, "nightingale-tenant", "*")), A<DeleteRequest>._))
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("nightingale://admin:pw@events.example?tls=false&tenant=*", "*")]
    [InlineData("nightingale://admin:pw@events.example?tenant=11111111-1111-1111-1111-111111111111", "11111111-1111-1111-1111-111111111111")]
    public void Parse_WithATenant_ShouldHoldIt(string connectionString, string tenant)
    {
        // Act & Assert
        NightingaleClientSettings.Parse(connectionString).Tenant.ShouldBe(tenant);
        Should.Throw<FormatException>(() => NightingaleClientSettings.Parse("nightingale://events.example?tenant=billing")).Message.ShouldContain("UUID");
    }

    private static bool Has(CallOptions options, string key, string value) =>
        options.Headers is { } headers && headers.Any(entry => entry.Key == key && entry.Value == value);
}
