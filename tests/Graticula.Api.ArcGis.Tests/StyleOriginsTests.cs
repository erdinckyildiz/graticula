using System;
using System.Collections.Generic;
using Graticula.Api.ArcGis;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// Which external origins an administrator may allow, and which URLs in a style an allowed origin covers —
/// ADR-094.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refusals are the point.</b> Every entry widens where every viewer's browser may be sent, so a
/// parser that accepts too much is a hole that looks like a feature. The URL half is tested against the
/// tricks that make one host look like another — user information, a suffix that is a prefix, a trailing
/// dot, case, an international spelling — because those are the ways a match goes wrong.
/// </para>
/// </remarks>
public sealed class StyleOriginsTests
{
    private static StyleOrigin Parse(string text)
    {
        Assert.True(StyleOrigins.TryParse(text, out StyleOrigin? origin, out string? error), error);
        return origin!;
    }

    private static string Refused(string text)
    {
        Assert.False(StyleOrigins.TryParse(text, out _, out string? error), $"'{text}' was accepted.");
        return error!;
    }

    private static bool Admits(string url, params string[] allowed)
    {
        List<StyleOrigin> list = [];

        foreach (string entry in allowed)
        {
            list.Add(Parse(entry));
        }

        return StyleOrigins.Admits(url, list);
    }

    // ---------- the list's entries ----------

    [Theory]
    [InlineData("https://tiles.example.com", "https://tiles.example.com")]
    [InlineData("HTTPS://Tiles.Example.COM", "https://tiles.example.com")]
    [InlineData("https://tiles.example.com/", "https://tiles.example.com")]
    [InlineData("https://tiles.example.com.", "https://tiles.example.com")]
    [InlineData("https://tiles.example.com:443", "https://tiles.example.com")]
    [InlineData("https://tiles.example.com:8443", "https://tiles.example.com:8443")]
    [InlineData("https://*.example.com", "https://*.example.com")]
    [InlineData("  https://tiles.example.com  ", "https://tiles.example.com")]
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example")]
    [InlineData("https://8.8.8.8", "https://8.8.8.8")]
    public void An_entry_is_read_and_written_back_in_one_spelling(string entry, string expected) =>
        Assert.Equal(expected, Parse(entry).Text);

    [Theory]
    [InlineData("http://tiles.example.com")]
    [InlineData("tiles.example.com")]
    [InlineData("//tiles.example.com")]
    [InlineData("https://")]
    [InlineData("https://tiles.example.com/path")]
    [InlineData("https://tiles.example.com?x=1")]
    [InlineData("https://tiles.example.com#x")]
    [InlineData("https://user@tiles.example.com")]
    [InlineData("https://allowed.com@evil.com")]
    [InlineData("https://tiles.example.com:0")]
    [InlineData("https://tiles.example.com:65536")]
    [InlineData("https://tiles.example.com:")]
    [InlineData("https://tiles.example.com:80:80")]
    [InlineData("https://*")]
    [InlineData("https://*.com")]
    [InlineData("https://a.*.example.com")]
    [InlineData("https://*example.com")]
    [InlineData("https://tiles..example.com")]
    [InlineData("https://tiles.example.com..")]
    [InlineData("https://ti_les.example.com")]
    [InlineData("https://tiles%2eexample.com")]
    [InlineData("https://[::1]")]
    [InlineData("https://localhost")]
    [InlineData("https://maps.localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.1.2.3")]
    [InlineData("https://172.16.0.1")]
    [InlineData("https://172.31.255.255")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://100.64.0.1")]
    [InlineData("https://0.0.0.0")]
    [InlineData("https://2130706433")]
    [InlineData("https://0x7f.1")]
    [InlineData("https://127.1")]
    [InlineData("https://010.0.0.1")]
    [InlineData("https://*.10.0.0.1")]
    public void An_entry_that_is_not_one_https_origin_is_refused(string entry) => Refused(entry);

    [Fact]
    public void A_refusal_names_the_entry_and_why()
    {
        string error = Refused("https://allowed.com@evil.com");

        Assert.Contains("allowed.com@evil.com", error, StringComparison.Ordinal);
        Assert.Contains("user information", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_public_address_is_an_origin_and_a_private_one_is_not()
    {
        Assert.Equal("https://203.0.114.1", Parse("https://203.0.114.1").Text);
        Assert.Contains("private", Refused("https://192.168.0.10"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_is_normalised_deduplicated_and_bounded()
    {
        Assert.True(StyleOrigins.TryParseList(
            ["https://A.example.com", "https://a.example.com/", "", null, "https://b.example.com"],
            out IReadOnlyList<StyleOrigin> origins, out string? error), error);

        Assert.Equal(["https://a.example.com", "https://b.example.com"], [.. System.Linq.Enumerable.Select(origins, o => o.Text)]);

        List<string> many = [];

        for (int i = 0; i <= StyleOrigins.MostOrigins; i++)
        {
            many.Add($"https://t{i}.example.com");
        }

        Assert.False(StyleOrigins.TryParseList(many, out _, out error));
        Assert.Contains($"{StyleOrigins.MostOrigins}", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void One_bad_entry_refuses_the_whole_list()
    {
        Assert.False(StyleOrigins.TryParseList(
            ["https://a.example.com", "http://b.example.com"], out IReadOnlyList<StyleOrigin> origins, out _));

        Assert.Empty(origins);
    }

    // ---------- URLs in a style ----------

    [Theory]
    [InlineData("https://tiles.example.com/{z}/{x}/{y}.pbf")]
    [InlineData("https://TILES.example.com/v1/tiles.json")]
    [InlineData("https://tiles.example.com./x")]
    [InlineData("https://tiles.example.com:443/x")]
    [InlineData("https://tiles.example.com")]
    [InlineData("https://tiles.example.com?key=1")]
    public void A_url_on_the_allowed_origin_is_admitted(string url) =>
        Assert.True(Admits(url, "https://tiles.example.com"));

    [Theory]
    [InlineData("https://allowed.com@evil.com/x")]
    [InlineData("https://allowed.com:443@evil.com/x")]
    [InlineData("https://allowed.com.evil.com/x")]
    [InlineData("https://evilallowed.com/x")]
    [InlineData("https://evil.com/allowed.com")]
    [InlineData("https://evil.com?allowed.com")]
    [InlineData("https://evil.com#@allowed.com")]
    [InlineData("http://allowed.com/x")]
    [InlineData("//allowed.com/x")]
    [InlineData("https://allowed.com:8443/x")]
    [InlineData("https://allowed.com\\@evil.com/x")]
    [InlineData("https://allowed.com\t.evil.com/x")]
    [InlineData("https:/allowed.com/x")]
    [InlineData("https://sub.allowed.com/x")]
    [InlineData("https://allowed.com%2f@evil.com/x")]
    [InlineData("https://allowed.com..evil.com/x")]
    public void A_url_that_only_resembles_the_allowed_origin_is_not_admitted(string url) =>
        Assert.False(Admits(url, "https://allowed.com"), $"'{url}' was admitted by https://allowed.com.");

    [Fact]
    public void A_wildcard_covers_every_name_under_the_host_and_not_the_host_itself()
    {
        Assert.True(Admits("https://a.tiles.example.com/x", "https://*.tiles.example.com"));
        Assert.True(Admits("https://a.b.tiles.example.com/x", "https://*.tiles.example.com"));
        Assert.False(Admits("https://tiles.example.com/x", "https://*.tiles.example.com"));
        Assert.False(Admits("https://atiles.example.com/x", "https://*.tiles.example.com"));
        Assert.False(Admits("https://a.tiles.example.com.evil.com/x", "https://*.tiles.example.com"));
    }

    [Fact]
    public void An_international_name_is_one_host_in_either_spelling()
    {
        Assert.True(Admits("https://bücher.example/x", "https://xn--bcher-kva.example"));
        Assert.True(Admits("https://xn--bcher-kva.example/x", "https://bücher.example"));
        Assert.False(Admits("https://bucher.example/x", "https://bücher.example"));
    }

    [Fact]
    public void A_port_is_part_of_the_origin()
    {
        Assert.True(Admits("https://tiles.example.com:8443/x", "https://tiles.example.com:8443"));
        Assert.False(Admits("https://tiles.example.com/x", "https://tiles.example.com:8443"));
    }

    [Fact]
    public void An_empty_list_admits_nothing() =>
        Assert.False(StyleOrigins.Admits("https://tiles.example.com/x", []));

    [Fact]
    public void The_origin_of_a_url_is_named_for_a_refusal()
    {
        Assert.Equal("https://tiles.example.com", StyleOrigins.OriginOf("https://Tiles.Example.com/a/b"));
        Assert.Equal("https://tiles.example.com:8443", StyleOrigins.OriginOf("https://tiles.example.com:8443/a"));
        Assert.Null(StyleOrigins.OriginOf("http://tiles.example.com/a"));
        Assert.Null(StyleOrigins.OriginOf("https://a@b.com/"));
    }
}
