using SebastianCi.Web;

namespace SebastianCi.Tests;

public sealed class ServeAuthenticatorTests
{
    private readonly ServeAuthenticator _authenticator = new("correct-token");

    [Fact]
    public void IsAuthorized_AcceptsBearerHeader()
        => Assert.True(_authenticator.IsAuthorized("Bearer correct-token", null, null, null));

    [Fact]
    public void IsAuthorized_AcceptsTokenHeader()
        => Assert.True(_authenticator.IsAuthorized(null, "correct-token", null, null));

    [Fact]
    public void IsAuthorized_AcceptsQueryToken()
        => Assert.True(_authenticator.IsAuthorized(null, null, "correct-token", null));

    [Fact]
    public void IsAuthorized_AcceptsCookieToken()
        => Assert.True(_authenticator.IsAuthorized(null, null, null, "correct-token"));

    [Fact]
    public void IsAuthorized_RejectsWhenNothingProvided()
        => Assert.False(_authenticator.IsAuthorized(null, null, null, null));

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("")]
    [InlineData("correct-token-with-suffix")]
    public void IsAuthorized_RejectsWrongToken(string candidate)
        => Assert.False(_authenticator.IsAuthorized(null, null, candidate, null));

    [Fact]
    public void IsAuthorized_RejectsBearerWithWrongToken()
        => Assert.False(_authenticator.IsAuthorized("Bearer wrong", null, null, null));

    [Fact]
    public void IsAuthorized_RejectsRawTokenInAuthorizationHeader()
        => Assert.False(_authenticator.IsAuthorized("correct-token", null, null, null));
}
