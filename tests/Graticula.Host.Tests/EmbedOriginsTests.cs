using System;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-275: the sites that may frame a scene's embed address are origins and nothing more, and only that address's
/// policy is widened to them.
/// </summary>
public sealed class EmbedOriginsTests
{
    [Fact]
    public void Origins_are_read_from_a_list_normalised_and_kept_once()
    {
        Assert.Equal(["https://maps.example.org", "http://intranet:8080"],
            HostSettings.ParseEmbedOrigins(" https://maps.example.org/, http://intranet:8080  https://MAPS.example.org"));
        Assert.Empty(HostSettings.ParseEmbedOrigins(null));
        Assert.Empty(HostSettings.ParseEmbedOrigins("  "));
    }

    [Theory]
    [InlineData("maps.example.org")]
    [InlineData("https://*.example.org")]
    [InlineData("https://maps.example.org/page")]
    [InlineData("https://maps.example.org/?x=1")]
    [InlineData("ftp://maps.example.org")]
    [InlineData("https://user@maps.example.org")]
    [InlineData("'self'")]
    public void Anything_but_an_origin_stops_the_server_naming_it(string named)
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => HostSettings.ParseEmbedOrigins(named));
        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_frame_ancestors_is_widened_and_nothing_when_no_site_is_named()
    {
        string policy = SecurityHeaders.ConsolePolicyFor("https://js.arcgis.com", []);

        Assert.Equal(policy, SecurityHeaders.Framable(policy, []));
        string framable = SecurityHeaders.Framable(policy, ["https://maps.example.org", "https://b.example"]);
        Assert.Contains("frame-ancestors https://maps.example.org https://b.example;", framable, StringComparison.Ordinal);
        Assert.DoesNotContain("frame-ancestors 'none'", framable, StringComparison.Ordinal);
        Assert.Equal(policy.Replace("frame-ancestors 'none'", string.Empty, StringComparison.Ordinal),
            framable.Replace("frame-ancestors https://maps.example.org https://b.example", string.Empty, StringComparison.Ordinal));
    }
}
