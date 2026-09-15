using System.Net;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// ArcGIS's <c>client</c> binds a token to an address or a referer, and a request from elsewhere is
/// refused — D-268.
/// </summary>
/// <remarks>Written 2026-09-15: <c>client=requestip</c> and <c>client=referer</c> bound nothing, so a
/// token leaked into a log was usable from anywhere for its whole life.</remarks>
public sealed class TokenBindingTests
{
    private static readonly IPAddress Here = IPAddress.Parse("198.51.100.7");

    [Fact]
    public void No_client_is_no_binding_and_admits_anything()
    {
        Assert.True(TokenBinding.TryRead(null, null, null, Here, out string? bound, out _));
        Assert.Null(bound);
        Assert.True(TokenBinding.Admits(bound, null, null, null));
    }

    [Fact]
    public void Requestip_binds_the_asking_address_and_refuses_another()
    {
        Assert.True(TokenBinding.TryRead("requestip", null, null, Here, out string? bound, out _));

        Assert.Equal("ip:198.51.100.7", bound);
        Assert.True(TokenBinding.Admits(bound, IPAddress.Parse("::ffff:198.51.100.7"), null, null));
        Assert.False(TokenBinding.Admits(bound, IPAddress.Parse("198.51.100.8"), null, null));
        Assert.False(TokenBinding.Admits(bound, null, null, null));
    }

    [Fact]
    public void Ip_binds_the_named_address()
    {
        Assert.True(TokenBinding.TryRead("IP", null, "203.0.113.9", Here, out string? bound, out _));

        Assert.False(TokenBinding.Admits(bound, Here, null, null));
        Assert.True(TokenBinding.Admits(bound, IPAddress.Parse("203.0.113.9"), null, null));
    }

    [Fact]
    public void A_url_referer_is_compared_by_origin_because_browsers_send_only_the_origin()
    {
        Assert.True(TokenBinding.TryRead("referer", "https://maps.example.com/app/index.html", null, Here, out string? bound, out _));

        Assert.True(TokenBinding.Admits(bound, null, "https://maps.example.com/", null));
        Assert.True(TokenBinding.Admits(bound, null, null, "https://MAPS.example.com"));
        Assert.False(TokenBinding.Admits(bound, null, "https://evil.example.com/", null));
        Assert.False(TokenBinding.Admits(bound, null, "http://maps.example.com/", null));
        Assert.False(TokenBinding.Admits(bound, Here, null, null));
    }

    [Fact]
    public void A_referer_that_is_not_a_url_must_match_exactly()
    {
        // What the ArcGIS API for Python sends.
        Assert.True(TokenBinding.TryRead("referer", "http", null, Here, out string? bound, out _));

        Assert.True(TokenBinding.Admits(bound, null, "http", null));
        Assert.False(TokenBinding.Admits(bound, null, "https://anything.example/", null));
    }

    [Theory]
    [InlineData("referer", null, null, "'referer'")]
    [InlineData("ip", null, "not-an-address", "'ip'")]
    [InlineData("header", null, null, "not one of")]
    public void A_binding_that_cannot_be_made_is_refused_rather_than_issued_unbound(
        string client, string? referer, string? ip, string reason)
    {
        Assert.False(TokenBinding.TryRead(client, referer, ip, Here, out string? bound, out string? error));
        Assert.Null(bound);
        Assert.Contains(reason, error, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Requestip_with_no_known_address_is_refused()
    {
        Assert.False(TokenBinding.TryRead("requestip", null, null, null, out _, out _));
    }

    [Fact]
    public void A_binding_this_build_does_not_know_is_refused_rather_than_ignored()
    {
        Assert.False(TokenBinding.Admits("device:abc", Here, "x", "y"));
    }
}
