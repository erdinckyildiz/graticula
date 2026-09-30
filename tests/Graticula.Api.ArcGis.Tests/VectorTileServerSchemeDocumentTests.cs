using System.Linq;
using System.Text.Json;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Geometries;
using Graticula.Tiles;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// The service document and style of a service cut on another grid — ADR-096.
/// </summary>
/// <remarks>
/// <b>D-49 is the failure this guards against</b>: a document whose <c>fullExtent</c> is in one reference and
/// whose <c>tileInfo</c> is in another makes the ArcGIS JS client read everything and request no tile. So
/// every reference in the document is asserted to be the scheme's, and the Web Mercator overload is asserted
/// to write the document it always wrote.
/// </remarks>
public sealed class VectorTileServerSchemeDocumentTests
{
    private static VectorTileScheme Tm30 => VectorTileSchemes.Find("turef-tm30")!.Scheme;

    private static JsonElement Parse(object document) =>
        JsonDocument.Parse(JsonSerializer.Serialize(document)).RootElement;

    [Fact]
    public void A_Web_Mercator_service_s_document_is_the_one_it_always_was()
    {
        Envelope extent = new(3_000_000, 4_500_000, 3_300_000, 5_000_000);
        VisibleScaleRange range = new(1_000_000, 0);

        string before = JsonSerializer.Serialize(
            VectorTileServerMetadataWriter.Service("roads", ["roads"], extent, 22, 3857, range));
        string now = JsonSerializer.Serialize(
            VectorTileServerMetadataWriter.Service("roads", ["roads"], extent, VectorTileScheme.WebMercator, range));

        Assert.Equal(before, now);
    }

    [Fact]
    public void A_TM30_service_states_TM30_in_every_place_a_client_reads_a_reference()
    {
        Envelope extent = new(400_000, 4_500_000, 450_000, 4_550_000);
        JsonElement document = Parse(
            VectorTileServerMetadataWriter.Service("roads", ["roads"], extent, Tm30));

        JsonElement info = document.GetProperty("tileInfo");

        Assert.Equal(5254, info.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.Equal(5254, document.GetProperty("fullExtent").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.Equal(5254, document.GetProperty("initialExtent").GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        Assert.Equal(104_000, info.GetProperty("origin").GetProperty("x").GetDouble());
        Assert.Equal(4_777_000, info.GetProperty("origin").GetProperty("y").GetDouble());
        Assert.Equal(512, info.GetProperty("rows").GetInt32());
        Assert.Equal(400_000, document.GetProperty("fullExtent").GetProperty("xmin").GetDouble());

        JsonElement[] lods = [.. info.GetProperty("lods").EnumerateArray()];

        Assert.Equal(19, lods.Length);
        Assert.Equal(3400.390625, lods[0].GetProperty("resolution").GetDouble());
        Assert.Equal(3400.390625 * 96 * 39.37, lods[0].GetProperty("scale").GetDouble(), 3);
        Assert.Equal(3400.390625 / 262144, lods[18].GetProperty("resolution").GetDouble());
        Assert.Equal(18, lods[18].GetProperty("level").GetInt32());

        Assert.Equal(0, document.GetProperty("minLOD").GetInt32());
        Assert.Equal(18, document.GetProperty("maxLOD").GetInt32());
        Assert.Equal(18, document.GetProperty("maxzoom").GetInt32());
        Assert.Equal("tile/{z}/{y}/{x}.pbf", document.GetProperty("tiles")[0].GetString());
    }

    [Fact]
    public void An_unknown_extent_on_TM30_is_the_scheme_s_frame_not_the_Mercator_world()
    {
        JsonElement extent = Parse(
            VectorTileServerMetadataWriter.Service("roads", ["roads"], null, Tm30)).GetProperty("fullExtent");

        Assert.Equal(104_000, extent.GetProperty("xmin").GetDouble());
        Assert.Equal(3_036_000, extent.GetProperty("ymin").GetDouble());
        Assert.Equal(1_845_000, extent.GetProperty("xmax").GetDouble());
        Assert.Equal(4_777_000, extent.GetProperty("ymax").GetDouble());
    }

    [Fact]
    public void A_style_s_zooms_are_narrowed_on_the_scheme_s_own_levels()
    {
        VisibleScaleRange range = new(Tm30.Scale(6), 0);

        JsonElement onTm30 = Parse(VectorTileServerMetadataWriter.Style(
            [("roads", GeometryKind.LineString, null)],
            null,
            new System.Collections.Generic.Dictionary<string, VisibleScaleRange> { ["roads"] = range },
            Tm30.Level0Scale));

        Assert.Equal(6, onTm30.GetProperty("layers")[0].GetProperty("minzoom").GetDouble(), 6);

        // The same range on Web Mercator is Web Mercator's zoom for that scale, as it was.
        JsonElement onMercator = Parse(VectorTileServerMetadataWriter.Style(
            [("roads", GeometryKind.LineString, null)],
            null,
            new System.Collections.Generic.Dictionary<string, VisibleScaleRange> { ["roads"] = range }));

        Assert.Equal(
            System.Math.Round(range.StyleMinZoom!.Value, 6),
            onMercator.GetProperty("layers")[0].GetProperty("minzoom").GetDouble(),
            6);
    }
}
