using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A stored style that draws a layer the service no longer has gives way to the generated one — ADR-028 condition 3.
/// </summary>
/// <remarks>
/// <b>The style passed its check once, when it was written.</b> A layer unpublished, taken out of the service or
/// renamed afterwards leaves it drawing a source that no longer exists; this is the check the style is served
/// through, so whichever door the layer left by, the served style is one that fits.
/// </remarks>
public sealed class AStyleThatOutlivesItsLayersIsNotServedTests
{
    private const string TwoLayers = """
        {
          "version": 8,
          "sources": { "esri": { "type": "vector", "url": "../../" } },
          "layers": [
            { "id": "parcels", "type": "fill", "source": "esri", "source-layer": "parcels" },
            { "id": "roads", "type": "line", "source": "esri", "source-layer": "roads" }
          ]
        }
        """;

    [Fact]
    public void A_style_that_draws_only_the_services_layers_is_served()
    {
        Assert.True(VectorTileEndpoints.StoredStyleFits(TwoLayers, ["parcels", "roads"], icons: null, out string? stale));
        Assert.Null(stale);
    }

    [Fact]
    public void A_style_that_draws_a_layer_the_service_no_longer_has_is_not_and_says_which()
    {
        Assert.False(VectorTileEndpoints.StoredStyleFits(TwoLayers, ["parcels"], icons: null, out string? stale));
        Assert.Contains("roads", stale, System.StringComparison.Ordinal);
    }

    private const string Pins = """
        {
          "version": 8,
          "layers": [
            { "id": "pins", "type": "symbol", "source-layer": "parcels", "layout": { "icon-image": "marker" } }
          ]
        }
        """;

    private static readonly string[] Parcels = ["parcels"];

    private static readonly string[] Marker = ["marker"];

    /// <summary>A style drawing an icon the service's sprite sheet has is served — ADR-092.</summary>
    [Fact]
    public void A_style_drawing_an_icon_the_sheet_has_is_served()
    {
        Assert.True(VectorTileEndpoints.StoredStyleFits(Pins, Parcels, Marker, out string? stale), stale);
    }

    /// <summary>
    /// A style drawing an icon the service no longer has a sheet for is not, and says so — the sheet's routes refuse
    /// to take a literal icon away, and this is the check at the door they cannot see.
    /// </summary>
    [Fact]
    public void A_style_drawing_an_icon_with_no_sheet_behind_it_is_not_served()
    {
        Assert.False(VectorTileEndpoints.StoredStyleFits(Pins, Parcels, icons: null, out string? stale));
        Assert.Contains("sprite", stale, System.StringComparison.Ordinal);
    }
}
