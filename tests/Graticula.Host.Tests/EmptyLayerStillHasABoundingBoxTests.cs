using System.Collections.Generic;
using System.Xml.Linq;
using Graticula.Api.Wms;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A named layer with nothing in it still carries the two elements WMS requires.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by the WMS 1.3 CITE suite on 2026-08-26, not by reading the specification.</b>
/// Two of this deployment's fourteen layers are empty, and neither carried
/// <c>EX_GeographicBoundingBox</c> or <c>BoundingBox</c> — the writer returned early on an
/// empty extent and the root layer states no <c>BoundingBox</c> to inherit. WMS 1.3.0
/// §7.2.4.6.6 and §7.2.4.6.8 require both on every named layer.
/// </para>
/// <para>
/// <b>The whole world is the honest value here.</b> It does not claim the data spans the
/// earth; an empty layer has no data to span anything. It says *this is not constrained*,
/// which is what is known. The alternative that suggests itself — a zero-area box at the
/// origin — is a false pinpoint in the Gulf of Guinea.
/// </para>
/// <para>
/// <b>Measured before and after against the suite</b>: 181 passed, 7 failed → 184 passed,
/// 6 failed, same scope. The remaining six are older and are not this.
/// </para>
/// </remarks>
public sealed class EmptyLayerStillHasABoundingBoxTests
{
    private static readonly XNamespace Wms = "http://www.opengis.net/wms";

    [Theory]
    [InlineData(WmsVersion.V130)]
    [InlineData(WmsVersion.V111)]
    public void An_empty_layer_carries_both_elements(WmsVersion version)
    {
        XElement layer = Only(version, Empty());

        Assert.NotNull(Geographic(layer, version));
        Assert.NotNull(layer.Element(Box(version)));
    }

    [Fact]
    public void The_box_is_in_a_reference_whose_axis_order_cannot_be_got_wrong()
    {
        // <b>CRS:84 rather than the layer's own reference.</b> The world in EPSG:3857 would
        // need that reference's own domain, which this server cannot look up reliably —
        // Q-123 measured why. CRS:84 is longitude-first by definition and is inherited by
        // every layer from the root, so it needs no lookup and no axis decision.
        XElement box = Only(WmsVersion.V130, Empty()).Element(Wms + "BoundingBox")!;

        Assert.Equal("CRS:84", box.Attribute("CRS")!.Value);
        Assert.Equal("-180", box.Attribute("minx")!.Value);
        Assert.Equal("-90", box.Attribute("miny")!.Value);
        Assert.Equal("180", box.Attribute("maxx")!.Value);
        Assert.Equal("90", box.Attribute("maxy")!.Value);
    }

    [Fact]
    public void A_layer_that_has_an_extent_still_states_its_own()
    {
        // <b>The half that would be lost by a blanket repair.</b> A layer with data must go
        // on publishing where its data is, in its own reference — replacing that with the
        // world would make every extent useless while making every document conformant.
        XElement box = Only(
            WmsVersion.V130,
            Empty() with
            {
                Extent = new Envelope(1_000, 2_000, 3_000, 4_000),
                Geographic = new Envelope(28, 36, 31, 41),
            })
            .Element(Wms + "BoundingBox")!;

        Assert.Equal("EPSG:3857", box.Attribute("CRS")!.Value);
        Assert.Equal("1000", box.Attribute("minx")!.Value);
    }

    [Fact]
    public void The_unnamed_root_is_left_alone()
    {
        // <b>The requirement is about *named* layers.</b> The root here has a title and no
        // name — a named root would be a layer a client could ask for, with nothing behind
        // it to draw — so it owes no BoundingBox, and inventing one for it would put a
        // world-wide box on a document that already says what it means.
        XElement root = Root(WmsVersion.V130, Empty());

        Assert.Null(root.Element(Wms + "Name"));
        Assert.Empty(root.Elements(Wms + "BoundingBox"));
    }

    private static WmsLayer Empty() =>
        new(
            "nothing_in_here",
            "Nothing in here",
            Abstract: null,
            Srid: 3857,
            GeometryKind.Point,
            Extent: null,
            Geographic: null,
            Queryable: true,
            Time: null);

    private static XElement Root(WmsVersion version, WmsLayer layer)
    {
        string xml = CapabilitiesDocument.Write(
            version,
            "https://example.test/wms",
            "Test",
            new List<WmsLayer> { layer },
            WmsLimits.Default);

        XElement document = XDocument.Parse(xml).Root!;
        XNamespace ns = document.Name.Namespace;

        return document.Element(ns + "Capability")!.Element(ns + "Layer")!;
    }

    private static XElement Only(WmsVersion version, WmsLayer layer)
    {
        XElement root = Root(version, layer);
        XNamespace ns = root.Name.Namespace;

        return Assert.Single(root.Elements(ns + "Layer"));
    }

    private static XElement? Geographic(XElement layer, WmsVersion version)
    {
        XNamespace ns = layer.Name.Namespace;

        return version == WmsVersion.V130
            ? layer.Element(ns + "EX_GeographicBoundingBox")
            : layer.Element(ns + "LatLonBoundingBox");
    }

    private static XName Box(WmsVersion version) =>
        version == WmsVersion.V130 ? Wms + "BoundingBox" : (XName)"BoundingBox";
}
