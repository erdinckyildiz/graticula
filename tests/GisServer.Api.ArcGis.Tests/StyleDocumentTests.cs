using System;
using System.Collections.Generic;
using GisServer.Api.ArcGis;
using Xunit;

namespace GisServer.Api.ArcGis.Tests;

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
        StyleDocument.TryValidate(json, Layers, out error);

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
        Assert.False(StyleDocument.TryValidate(json, Layers, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>A style larger than the cap is refused before it is parsed.</summary>
    [Fact]
    public void A_style_over_the_cap_is_refused()
    {
        string huge = """{"version":8,"layers":[],"pad":"""
                      + "\"" + new string('x', StyleDocument.MaximumBytes) + "\"}";

        Assert.False(StyleDocument.TryValidate(huge, Layers, out string? error));
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

        Assert.False(StyleDocument.TryValidate(deep, Layers, out string? error));
        Assert.NotNull(error);
    }

    /// <summary>The service's own layer list is what a style is checked against.</summary>
    [Fact]
    public void A_service_with_no_layers_accepts_only_a_style_that_draws_nothing()
    {
        Assert.True(StyleDocument.TryValidate(
            """{"version":8,"layers":[]}""", Array.Empty<string>(), out _));

        Assert.False(StyleDocument.TryValidate(
            """{"version":8,"layers":[{"id":"a","type":"fill","source-layer":"parcels"}]}""",
            Array.Empty<string>(), out _));
    }

    /// <summary>
    /// A style that draws an icon is refused while the sprite sheet is empty.
    /// </summary>
    /// <remarks>
    /// <b>ADR-027 condition 5, answered a third way.</b> The condition offered
    /// two options — fill the sheet, or stop advertising it. Neither is right
    /// yet: there is no icon library to ship and clients probe the sheet
    /// regardless. Refusing the style that would silently draw nothing is the
    /// remaining honest option, and it removes the harm without pretending the
    /// feature exists. The check deletes itself the day sprites can be uploaded.
    /// </remarks>
    [Fact]
    public void An_icon_nobody_can_supply_is_refused_rather_than_drawn_as_nothing()
    {
        Assert.False(Valid("""
            {"version":8,"layers":[
              {"id":"pins","type":"symbol","source-layer":"parcels",
               "layout":{"icon-image":"marker"}}]}
            """, out string? error));

        Assert.Contains("sprite", error!, StringComparison.Ordinal);
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
