using System;
using System.Collections.Generic;
using Graticula.Api.ArcGis;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// A style may name another server only on an origin an administrator allowed — ADR-094, amending ADR-028 §5.
/// </summary>
/// <remarks>
/// <para>
/// <b>With the list empty nothing changed</b>, which is asserted first: the default is ADR-028's rule.
/// </para>
/// <para>
/// <b>Every place a style names a URL.</b> The first version of the rule read <c>glyphs</c>, a string
/// <c>sprite</c>, and a source's <c>url</c>, <c>tiles</c> and <c>data</c>. An array <c>sprite</c> and a video
/// source's <c>urls</c> were never read, so a style could name any host through either and pass; those are
/// pinned here with the list empty, which is the case they escaped.
/// </para>
/// </remarks>
public sealed class StyleDocumentOriginTests
{
    private static readonly string[] Layers = ["parcels"];

    private static readonly StyleOrigin[] Vendor = [Origin("https://tiles.example.com"), Origin("https://*.cdn.example.net")];

    private static StyleOrigin Origin(string text)
    {
        Assert.True(StyleOrigins.TryParse(text, out StyleOrigin? origin, out string? error), error);
        return origin!;
    }

    private static string Style(string topLevel = "", string sources = "") => $$"""
        {
          "version": 8,
          {{topLevel}}
          "sources": { "esri": { "type": "vector", "url": "../../" }{{sources}} },
          "layers": [ { "id": "p", "type": "fill", "source": "esri", "source-layer": "parcels" } ]
        }
        """;

    private static bool Valid(string json, IReadOnlyCollection<StyleOrigin>? allowed, out string? error) =>
        StyleDocument.TryValidate(json, Layers, icons: null, allowed, out error);

    // ---------- the default is unchanged ----------

    [Theory]
    [InlineData("\"glyphs\": \"https://tiles.example.com/fonts/{fontstack}/{range}.pbf\",", "")]
    [InlineData("\"sprite\": \"https://tiles.example.com/sprite\",", "")]
    [InlineData("", ", \"b\": { \"type\": \"vector\", \"url\": \"https://tiles.example.com/v1.json\" }")]
    [InlineData("", ", \"b\": { \"type\": \"vector\", \"tiles\": [\"https://tiles.example.com/{z}/{x}/{y}.pbf\"] }")]
    public void With_no_origin_allowed_an_absolute_url_is_refused_as_before(string top, string sources)
    {
        Assert.False(Valid(Style(top, sources), allowed: null, out string? error));
        Assert.Contains("https://tiles.example.com", error!, StringComparison.Ordinal);
        Assert.Contains("allows none", error!, StringComparison.Ordinal);

        Assert.False(Valid(Style(top, sources), allowed: [], out _));
    }

    // ---------- an allowed origin ----------

    [Theory]
    [InlineData("\"glyphs\": \"https://tiles.example.com/fonts/{fontstack}/{range}.pbf\",", "")]
    [InlineData("\"sprite\": \"https://tiles.example.com/sprite\",", "")]
    [InlineData("\"sprite\": [{ \"id\": \"v\", \"url\": \"https://a.cdn.example.net/sprite\" }],", "")]
    [InlineData("", ", \"b\": { \"type\": \"vector\", \"url\": \"https://tiles.example.com/v1.json\" }")]
    [InlineData("", ", \"b\": { \"type\": \"vector\", \"tiles\": [\"https://tiles.example.com/{z}/{x}/{y}.pbf\"] }")]
    [InlineData("", ", \"b\": { \"type\": \"raster\", \"tiles\": [\"https://a.cdn.example.net/{z}/{x}/{y}.png\", \"https://b.cdn.example.net/{z}/{x}/{y}.png\"] }")]
    [InlineData("", ", \"b\": { \"type\": \"geojson\", \"data\": \"https://tiles.example.com/points.geojson\" }")]
    [InlineData("", ", \"b\": { \"type\": \"video\", \"urls\": [\"https://tiles.example.com/v.mp4\"], \"coordinates\": [[0,0],[1,0],[1,1],[0,1]] }")]
    public void A_url_on_an_allowed_origin_is_accepted(string top, string sources) =>
        Assert.True(Valid(Style(top, sources), Vendor, out string? error), error);

    [Fact]
    public void Our_own_relative_resources_keep_working_beside_an_allowed_one()
    {
        string json = Style(
            "\"glyphs\": \"../fonts/{fontstack}/{range}.pbf\", \"sprite\": \"../sprites/sprite\",",
            ", \"b\": { \"type\": \"vector\", \"url\": \"https://tiles.example.com/v1.json\" }");

        Assert.True(Valid(json, Vendor, out string? error), error);
    }

    [Theory]
    [InlineData("https://evil.com/{z}/{x}/{y}.pbf", "https://evil.com")]
    [InlineData("https://tiles.example.com.evil.com/{z}/{x}/{y}.pbf", "https://tiles.example.com.evil.com")]
    [InlineData("https://tiles.example.com:8443/{z}/{x}/{y}.pbf", "https://tiles.example.com:8443")]
    [InlineData("https://cdn.example.net/{z}/{x}/{y}.pbf", "https://cdn.example.net")]
    public void A_url_on_another_origin_is_refused_and_its_origin_named(string url, string origin)
    {
        string json = Style(sources: $", \"b\": {{ \"type\": \"vector\", \"tiles\": [\"{url}\"] }}");

        Assert.False(Valid(json, Vendor, out string? error));
        Assert.Contains($"on {origin},", error!, StringComparison.Ordinal);
        Assert.Contains("Style sources", error!, StringComparison.Ordinal);
        Assert.Contains("https://tiles.example.com", error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://tiles.example.com/{z}/{x}/{y}.pbf")]
    [InlineData("//tiles.example.com/{z}/{x}/{y}.pbf")]
    [InlineData("https://tiles.example.com@evil.com/{z}/{x}/{y}.pbf")]
    [InlineData("https://tiles.example.com\\\\@evil.com/{z}/{x}/{y}.pbf")]
    [InlineData("/rest/services/other/VectorTileServer")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mapbox://styles/x")]
    public void What_cannot_be_an_allowed_origin_is_refused_even_with_one_allowed(string url)
    {
        string json = Style(sources: $", \"b\": {{ \"type\": \"vector\", \"url\": \"{url}\" }}");

        Assert.False(Valid(json, Vendor, out string? error), $"'{url}' was accepted.");
        Assert.Contains("Source 'b'", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void One_refused_tile_url_among_allowed_ones_refuses_the_style()
    {
        string json = Style(sources:
            ", \"b\": { \"type\": \"vector\", \"tiles\": [\"https://tiles.example.com/{z}/{x}/{y}.pbf\", \"https://evil.com/{z}/{x}/{y}.pbf\"] }");

        Assert.False(Valid(json, Vendor, out string? error));
        Assert.Contains("evil.com", error!, StringComparison.Ordinal);
    }

    // ---------- the spellings the first rule did not read ----------

    [Fact]
    public void An_array_sprite_is_read_and_refused_with_no_origin_allowed()
    {
        string json = Style("\"sprite\": [{ \"id\": \"x\", \"url\": \"https://evil.com/sprite\" }],");

        Assert.False(Valid(json, allowed: null, out string? error), "An array sprite escaped the URL check.");
        Assert.Contains("\"sprite\"", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_array_sprite_of_relative_urls_is_accepted() =>
        Assert.True(Valid(Style("\"sprite\": [{ \"id\": \"default\", \"url\": \"../sprites/sprite\" }],"), null, out string? error), error);

    [Fact]
    public void A_video_sources_urls_are_read_and_refused_with_no_origin_allowed()
    {
        string json = Style(sources:
            ", \"v\": { \"type\": \"video\", \"urls\": [\"https://evil.com/v.mp4\"], \"coordinates\": [[0,0],[1,0],[1,1],[0,1]] }");

        Assert.False(Valid(json, allowed: null, out string? error), "A video source's urls escaped the URL check.");
        Assert.Contains("Source 'v'", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inline_geojson_source_is_accepted_rather_than_thrown_on()
    {
        string json = Style(sources:
            ", \"g\": { \"type\": \"geojson\", \"data\": { \"type\": \"FeatureCollection\", \"features\": [] } }");

        Assert.True(Valid(json, allowed: null, out string? error), error);
    }

    [Fact]
    public void Whether_a_style_could_name_another_host_is_a_cheap_question()
    {
        Assert.True(StyleDocument.MayNameAnotherHost("{\"sprite\":\"https://x.example/s\"}"));
        Assert.False(StyleDocument.MayNameAnotherHost("{\"sprite\":\"../sprites/sprite\"}"));
        Assert.False(StyleDocument.MayNameAnotherHost(null));
    }
}
