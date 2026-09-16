using System;
using System.Security.Cryptography;
using System.Text;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// The parts of ADR-076 that need no database: which redirects may be registered, and PKCE.
/// </summary>
public sealed class OAuthRulesTests
{
    [Theory]
    [InlineData("https://app.example/callback")]
    [InlineData("http://localhost:3000/cb")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("arcgis-fieldmaps://auth/")]
    [InlineData("urn:ietf:wg:oauth:2.0:oob")]
    public void These_redirects_may_be_registered(string uri) =>
        Assert.True(OAuthRules.IsAcceptableRedirect(uri, out string? why), why);

    [Theory]
    [InlineData("http://app.example/callback", "plain HTTP")]
    [InlineData("javascript:alert(1)", "run or opened")]
    [InlineData("data:text/html,x", "run or opened")]
    [InlineData("https://app.example/cb#frag", "fragment")]
    [InlineData("/relative/path", "absolute")]
    [InlineData(" https://app.example/cb", "spaces")]
    [InlineData("", "required")]
    public void These_are_refused_and_say_why(string uri, string expected)
    {
        Assert.False(OAuthRules.IsAcceptableRedirect(uri, out string? why));
        Assert.Contains(expected, why!, StringComparison.Ordinal);
    }

    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    [Fact]
    public void An_S256_challenge_is_answered_by_its_verifier_and_by_nothing_else()
    {
        // RFC 7636 Appendix B's own example pair.
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);

        Assert.True(OAuthRules.Verifies(challenge, "S256", Verifier));
        Assert.False(OAuthRules.Verifies(challenge, "S256", Verifier[..^1] + "Y"));
        Assert.False(OAuthRules.Verifies(challenge, "plain", Verifier));
    }

    [Fact]
    public void A_challenge_that_was_sent_makes_the_verifier_required()
    {
        // <b>Not treated as a client that skipped PKCE.</b> The code was issued under a promise that
        // only the holder of the verifier could redeem it.
        Assert.False(OAuthRules.Verifies(Verifier, "plain", null));
        Assert.False(OAuthRules.Verifies(Verifier, null, string.Empty));
        Assert.True(OAuthRules.Verifies(Verifier, null, Verifier));
    }

    [Fact]
    public void A_verifier_outside_the_RFC_s_length_is_refused_even_when_it_matches()
    {
        string tooShort = new('a', 42);
        Assert.False(OAuthRules.Verifies(tooShort, "plain", tooShort));
    }

    [Fact]
    public void Without_a_challenge_there_is_nothing_to_verify() =>
        Assert.True(OAuthRules.Verifies(null, null, null));

    [Theory]
    [InlineData(null, true)]
    [InlineData("S256", true)]
    [InlineData("plain", true)]
    [InlineData("s256", false)]
    [InlineData("SHA256", false)]
    public void Only_the_documented_methods_are_read(string? method, bool known) =>
        Assert.Equal(known, OAuthRules.IsKnownMethod(method));
}
