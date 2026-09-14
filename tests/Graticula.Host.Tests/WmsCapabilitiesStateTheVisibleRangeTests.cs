using Graticula.Api.Wms;
using Graticula.Cartography;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>ADR-070 condition 2: a WMS 1.3.0 capabilities document states each layer's visible range.</summary>
public sealed class WmsCapabilitiesStateTheVisibleRangeTests
{
    private static string Capabilities(VisibleScaleRange range, WmsVersion version = WmsVersion.V130) =>
        CapabilitiesDocument.Write(
            version,
            "https://example.test/wms",
            "Graticula",
            [new WmsLayer("buildings", "buildings", null, 4326, GeometryKind.Polygon,
                new Envelope(28, 40, 30, 42), new Envelope(28, 40, 30, 42), true, null) { VisibleRange = range }],
            WmsLimits.Default);

    [Fact]
    public void The_denominators_are_on_a_028_mm_pixel_and_named_the_wms_way_round()
    {
        // ArcGIS's zoomed-out limit (minScale) is WMS's MaxScaleDenominator, and the reverse.
        string document = Capabilities(new VisibleScaleRange(VisibleScaleRange.DotsPerMetre * 10, VisibleScaleRange.DotsPerMetre));

        Assert.Contains("<MaxScaleDenominator>35714.285714</MaxScaleDenominator>", document);
        Assert.Contains("<MinScaleDenominator>3571.428571</MinScaleDenominator>", document);
        Assert.True(document.IndexOf("<MinScaleDenominator>", System.StringComparison.Ordinal)
            > document.IndexOf("<Style>", System.StringComparison.Ordinal));
    }

    [Fact]
    public void A_layer_with_no_range_and_the_1_1_1_document_state_none()
    {
        Assert.DoesNotContain("ScaleDenominator", Capabilities(VisibleScaleRange.Unlimited));
        Assert.DoesNotContain("ScaleDenominator", Capabilities(new VisibleScaleRange(50_000, 0), WmsVersion.V111));
    }
}
