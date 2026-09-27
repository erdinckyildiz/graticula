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
        Assert.True(VectorTileEndpoints.StoredStyleFits(TwoLayers, ["parcels", "roads"], out string? stale));
        Assert.Null(stale);
    }

    [Fact]
    public void A_style_that_draws_a_layer_the_service_no_longer_has_is_not_and_says_which()
    {
        Assert.False(VectorTileEndpoints.StoredStyleFits(TwoLayers, ["parcels"], out string? stale));
        Assert.Contains("roads", stale, System.StringComparison.Ordinal);
    }
}
