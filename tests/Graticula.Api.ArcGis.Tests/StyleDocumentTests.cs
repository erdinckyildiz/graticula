using System;
using System.Collections.Generic;
using Graticula.Api.ArcGis;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// What a stored style has to satisfy before this server will serve it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two kinds of check, and they fail differently.</b> The <c>source-layer</c>
/// check catches the mistake that produces a blank map with no error anywhere —
/// correct tiles, a correct client, and nothing to search for. The URL checks
/// are security: a style is a document this server hands to every viewer's
/// browser, and that browser fetches whatever is in it.
/// </para>
/// <para>
/// The refusals are tested more heavily than the acceptances, because a
/// validator that accepts too much is indistinguishable from no validator until
/// somebody exploits it.
/// </para>
/// </remarks>
public sealed class StyleDocumentTests
{
    private static readonly string[] Layers = ["parcels", "buildings"];

    private static bool Valid(string json, out string? error) =>
        StyleDocument.TryValidate(json, Layers, icons: null, out error);

    private const string Good = """
        {
          "version": 8,
          "sources": { "esri": { "type": "vector", "url": "../../" } },
          "glyphs": "../fonts/{fontstack}/{range}.pbf",
          "sprite": "../sprites/sprite",
          "layers": [
            { "id": "parcel-fill", "type": "fill", "source": "esri",
              "source-layer": "parcels", "paint": { "fill-color": "#eee" } },
            { "id": "parcel-label", "type": "symbol", "source": "esri",
              "source-layer": "parcels",
              "layout": { "text-field": "{ada}/{parsel}", "text-font": ["DejaVu Sans Regular"] } }
          ]
        }
        """;

    // ---------- what a real style looks like ----------

    [Fact]
    public void A_style_a_cartographer_would_write_is_accepted()
    {
        Assert.True(Valid(Good, out string? error), error);
        Assert.Null(error);
    }

    /// <summary>
    /// A background layer legitimately draws no source.
    /// </summary>
    [Fact]
    public void A_background_layer_needs_no_source_layer()
    {
        Assert.True(Valid("""
            {"version":8,"layers":[
              {"id":"bg","type":"background","paint":{"background-color":"#fff"}}]}
            """, out string? error), error);
    }

    /// <summary>An empty layer list is a style that draws nothing, which is allowed.</summary>
    /// <remarks>
    /// Different from having no <c>layers</c> key at all: one is a deliberate
    /// blank map, the other is a malformed document.
    /// </remarks>
    [Fact]
    public void An_empty_layer_list_is_allowed()
    {
        Assert.True(Valid("""{"version":8,"layers":[]}""", out _));
        Assert.False(Valid("""{"version":8}""", out _));
    }

    /// <summary>
    /// Properties this server has never heard of survive.
    /// </summary>
    /// <remarks>
    /// <b>The reason the document is stored as text and not bound to a
    /// model.</b> The style specification grows, and clients understand more of
    /// it than we do. A validator that dropped what it did not recognise would
    /// silently delete a cartographer's work.
    /// </remarks>
    [Fact]
    public void Properties_this_server_does_not_know_are_not_a_problem()
    {
        Assert.True(Valid("""
            {"version":8,"metadata":{"mapbox:autocomposite":true},
             "terrain":{"source":"dem"},"someFutureKey":[1,2,3],
             "layers":[{"id":"a","type":"fill","source-layer":"parcels",
                        "paint":{"fill-antialias-mode":"future"}}]}
            """, out string? error), error);
    }

    // ---------- the check that pays for the type ----------

    /// <summary>
    /// A style naming a layer the service does not have is refused, and the
    /// refusal says what it does have.
    /// </summary>
    [Fact]
    public void A_source_layer_that_does_not_exist_is_refused()
    {
        Assert.False(Valid("""
            {"version":8,"layers":[
              {"id":"a","type":"fill","source-layer":"parcel"}]}
            """, out string? error));

        Assert.Contains("parcel", error!, StringComparison.Ordinal);
        Assert.Contains("parcels, buildings", error!, StringComparison.Ordinal);
    }

    /// <summary>Case matters, because it matters in the tile.</summary>
    /// <remarks>
    /// The source layer name in a tile is the layer's name exactly. Accepting
    /// <c>Parcels</c> here would store a style that renders nothing, which is
    /// the failure this check exists to prevent.
    /// </remarks>
    [Fact]
    public void A_source_layer_differing_only_in_case_is_refused()
    {
        Assert.False(Valid("""
            {"version":8,"layers":[{"id":"a","type":"fill","source-layer":"Parcels"}]}
            """, out _));
    }

    [Fact]
    public void A_layer_with_no_id_is_refused()
    {
        Assert.False(Valid("""
            {"version":8,"layers":[{"type":"fill","source-layer":"parcels"}]}
            """, out _));
    }

    [Fact]
    public void A_non_background_layer_with_no_source_layer_is_refused()
    {
        Assert.False(Valid("""
            {"version":8,"layers":[{"id":"a","type":"fill"}]}
            """, out _));
    }

    // ---------- nothing points off this server ----------

    /// <summary>
    /// An absolute URL anywhere in the document is refused.
    /// </summary>
    /// <remarks>
    /// <b>This is the security check, and the threat is not to the server.</b>
    /// A style is fetched by every viewer's browser. A publisher who can store
    /// one containing an external URL can make everybody else's browser reach an
    /// address of their choosing, from inside the network, and learn who opened
    /// the map and when. It also breaks air-gapped operation outright (Q-15).
    /// </remarks>
    [Theory]
    [InlineData("""{"version":8,"glyphs":"https://evil.example/{fontstack}/{range}.pbf","layers":[]}""")]
    [InlineData("""{"version":8,"sprite":"//evil.example/sprite","layers":[]}""")]
    [InlineData("""{"version":8,"sources":{"x":{"url":"https://evil.example/t.json"}},"layers":[]}""")]
    [InlineData("""{"version":8,"sources":{"x":{"tiles":["https://evil.example/{z}/{x}/{y}.pbf"]}},"layers":[]}""")]
    [InlineData("""{"version":8,"sources":{"x":{"data":"http://169.254.169.254/latest/meta-data/"}},"layers":[]}""")]
    [InlineData("""{"version":8,"sources":{"x":{"url":"javascript:alert(1)"}},"layers":[]}""")]
    public void A_url_that_leaves_this_server_is_refused(string json)
    {
        Assert.False(Valid(json, out string? error));
        Assert.NotNull(error);
    }

    /// <summary>
    /// A root-relative URL stays on the host and still leaves the service.
    /// </summary>
    /// <remarks>
    /// <b>The case that looks harmless.</b> <c>/rest/services/other/...</c> is
    /// on this server, so it passes any check aimed at exfiltration — but it
    /// addresses a different service, whose sharing this style's viewer may not
    /// satisfy. A sharing boundary expressed as a URL is still a sharing
    /// boundary.
    /// </remarks>
    [Fact]
    public void A_root_relative_url_is_refused()
    {
        Assert.False(Valid("""
            {"version":8,"sources":{"x":{"url":"/rest/services/private/VectorTileServer"}},
             "layers":[]}
            """, out _));
    }

    [Fact]
    public void Relative_urls_are_what_is_wanted()
    {
        Assert.True(Valid("""
            {"version":8,
             "sources":{"esri":{"type":"vector","url":"../../"}},
             "glyphs":"../fonts/{fontstack}/{range}.pbf",
             "layers":[]}
            """, out string? error), error);
    }

    // ---------- the shape of the document ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("""{"layers":[]}""")]
    [InlineData("""{"version":7,"layers":[]}""")]
    [InlineData("""{"version":"8","layers":[]}""")]
    [InlineData("""{"version":8,"layers":{}}""")]
    [InlineData("""{"version":8,"layers":["not an object"]}""")]
    public void A_document_that_is_not_a_style_is_refused(string? json)
    {
        Assert.False(StyleDocument.TryValidate(json, Layers, icons: null, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>A style larger than the cap is refused before it is parsed.</summary>
    [Fact]
    public void A_style_over_the_cap_is_refused()
    {
        string huge = """{"version":8,"layers":[],"pad":"""
                      + "\"" + new string('x', StyleDocument.MaximumBytes) + "\"}";

        Assert.False(StyleDocument.TryValidate(huge, Layers, icons: null, out string? error));
        Assert.Contains("KB", error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deep nesting is refused by the reader rather than by exhausting the stack.
    /// </summary>
    /// <remarks>
    /// A parser is a decompressor too: a few kilobytes of brackets is a stack
    /// overflow in a naive reader, and a stack overflow is not catchable.
    /// </remarks>
    [Fact]
    public void A_deeply_nested_document_is_refused_rather_than_fatal()
    {
        string deep = """{"version":8,"layers":[],"x":"""
                      + new string('[', 200) + new string(']', 200) + "}";

        Assert.False(StyleDocument.TryValidate(deep, Layers, icons: null, out string? error));
        Assert.NotNull(error);
    }

    /// <summary>The service's own layer list is what a style is checked against.</summary>
    [Fact]
    public void A_service_with_no_layers_accepts_only_a_style_that_draws_nothing()
    {
        Assert.True(StyleDocument.TryValidate(
            """{"version":8,"layers":[]}""", Array.Empty<string>(), icons: null, out _));

        Assert.False(StyleDocument.TryValidate(
            """{"version":8,"layers":[{"id":"a","type":"fill","source-layer":"parcels"}]}""",
            Array.Empty<string>(), icons: null, out _));
    }

    // ---------- icons, against the service's sprite sheet (ADR-092) ----------

    private static readonly string[] Icons = ["marker", "school", "hospital"];

    private static readonly string[] MarkerAndSchool = ["marker", "school"];

    private static string Pins(string iconImage) => $$$"""
        {"version":8,"layers":[
          {"id":"pins","type":"symbol","source-layer":"parcels",
           "layout":{"icon-image":{{{iconImage}}}}}]}
        """;

    /// <summary>
    /// A style that draws an icon on a service with no sprite sheet is refused, and says what to do.
    /// </summary>
    /// <remarks>
    /// <b>ADR-027 condition 5 and ADR-028 condition 4, discharged by ADR-092.</b> Until sheets could
    /// be uploaded every <c>icon-image</c> was refused; that blanket refusal is deleted, and what is
    /// left is the case where there is still nothing to draw from.
    /// </remarks>
    [Fact]
    public void An_icon_on_a_service_with_no_sprite_sheet_is_refused_and_says_to_upload_one()
    {
        Assert.False(Valid(Pins("\"marker\""), out string? error));

        Assert.Contains("pins", error!, StringComparison.Ordinal);
        Assert.Contains("no sprite sheet", error!, StringComparison.Ordinal);
        Assert.Contains("/sprite", error!, StringComparison.Ordinal);
    }

    /// <summary>Even an expression is refused when there is no sheet, since it can name nothing.</summary>
    [Fact]
    public void An_icon_expression_on_a_service_with_no_sprite_sheet_is_refused()
    {
        Assert.False(Valid(Pins("""["get","kind"]"""), out string? error));
        Assert.Contains("no sprite sheet", error!, StringComparison.Ordinal);
    }

    /// <summary>A literal icon the sheet has is accepted — the check the blanket refusal became.</summary>
    [Theory]
    [InlineData("\"marker\"")]
    [InlineData("""["literal","school"]""")]
    public void An_icon_the_sprite_sheet_has_is_accepted(string iconImage)
    {
        Assert.True(
            StyleDocument.TryValidate(Pins(iconImage), Layers, Icons, out string? error), error);
    }

    /// <summary>
    /// A literal icon the sheet does not have is refused, naming it and what the sheet has.
    /// </summary>
    /// <remarks>
    /// The mistyped <c>source-layer</c> check applied to icons: a missing name draws nothing and
    /// reports nothing.
    /// </remarks>
    [Theory]
    [InlineData("\"markr\"", "markr")]
    [InlineData("""["literal","hospitl"]""", "hospitl")]
    public void A_literal_icon_the_sprite_sheet_lacks_is_refused_and_named(string iconImage, string named)
    {
        Assert.False(StyleDocument.TryValidate(Pins(iconImage), Layers, Icons, out string? error));

        Assert.Contains($"'{named}'", error!, StringComparison.Ordinal);
        Assert.Contains("marker", error!, StringComparison.Ordinal);
    }

    /// <summary>Case matters, because a client looks the name up exactly.</summary>
    [Fact]
    public void An_icon_name_differing_only_in_case_is_refused()
    {
        Assert.False(StyleDocument.TryValidate(Pins("\"Marker\""), Layers, Icons, out _));
    }

    /// <summary>
    /// An expression is accepted when a sheet exists, and not resolved.
    /// </summary>
    /// <remarks>
    /// Which names an expression produces depends on the features, so checking them would mean
    /// reading the data at write time. A legacy <c>{token}</c> string is an expression too.
    /// </remarks>
    [Theory]
    [InlineData("""["get","kind"]""")]
    [InlineData("""["match",["get","kind"],"a","marker","school"]""")]
    [InlineData("\"{kind}-15\"")]
    public void An_icon_expression_is_accepted_when_there_is_a_sheet(string iconImage)
    {
        Assert.True(
            StyleDocument.TryValidate(Pins(iconImage), Layers, Icons, out string? error), error);
    }

    /// <summary>An empty sheet has a name for nothing, so every literal is missing from it.</summary>
    [Fact]
    public void A_literal_icon_against_an_empty_sheet_is_refused()
    {
        Assert.False(StyleDocument.TryValidate(Pins("\"marker\""), Layers, [], out string? error));
        Assert.Contains("names no icons", error!, StringComparison.Ordinal);
    }

    /// <summary>A null <c>icon-image</c> draws no icon and needs no sheet.</summary>
    [Fact]
    public void A_null_icon_needs_no_sheet()
    {
        Assert.True(Valid(Pins("null"), out string? error), error);
    }

    /// <summary>The literal names a stored style uses, which is what a sheet may not take away.</summary>
    [Fact]
    public void The_literal_icons_of_a_style_are_listed_once_and_expressions_are_skipped()
    {
        IReadOnlyList<string> names = StyleDocument.LiteralIcons("""
            {"version":8,"layers":[
              {"id":"a","type":"symbol","source-layer":"parcels","layout":{"icon-image":"marker"}},
              {"id":"b","type":"symbol","source-layer":"parcels","layout":{"icon-image":["literal","school"]}},
              {"id":"c","type":"symbol","source-layer":"parcels","layout":{"icon-image":["get","kind"]}},
              {"id":"d","type":"symbol","source-layer":"parcels","layout":{"icon-image":"{kind}"}},
              {"id":"e","type":"symbol","source-layer":"parcels","layout":{"icon-image":"marker"}}]}
            """);

        Assert.Equal(MarkerAndSchool, names);
        Assert.Empty(StyleDocument.LiteralIcons(null));
        Assert.Empty(StyleDocument.LiteralIcons("not json"));
    }

    /// <summary>A text symbol is fine, because glyphs exist.</summary>
    [Fact]
    public void A_text_symbol_is_allowed()
    {
        Assert.True(Valid("""
            {"version":8,"layers":[
              {"id":"labels","type":"symbol","source-layer":"parcels",
               "layout":{"text-field":"{name}","text-font":["DejaVu Sans Regular"]}}]}
            """, out string? error), error);
    }
}
