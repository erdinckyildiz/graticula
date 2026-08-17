using System;
using System.Linq;
using System.Text.Json;
using Graticula.Api.ArcGis;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// The documents a client reads before it asks for a tile.
/// </summary>
/// <remarks>
/// <para>
/// Asserted through the serialised JSON rather than against the anonymous
/// objects, because the field <em>names</em> are the contract. A property
/// renamed by a serialiser setting is invisible to a test that inspects the
/// object and fatal to a client that matches on the name — which is exactly how
/// the <c>source-layer</c> hyphen problem would have gone unnoticed.
/// </para>
/// <para>
/// The FeatureServer work established the cost of getting this wrong: the query
/// endpoint was correct for days while no ArcGIS client could find it.
/// </para>
/// </remarks>
public sealed class VectorTileServerMetadataWriterTests
{
    private static JsonElement Service(Envelope? extent = null, int maxZoom = 22, int srid = 3857) =>
        Parse(VectorTileServerMetadataWriter.Service("roads", ["roads"], extent, maxZoom, srid));

    private static JsonElement Parse(object document) =>
        JsonDocument.Parse(JsonSerializer.Serialize(document)).RootElement;

    // ---------- the tile template ----------

    [Fact]
    public void The_tile_template_is_z_slash_y_slash_x_which_is_not_the_usual_order()
    {
        // ArcGIS puts the row before the column, and almost every other tile
        // scheme in the world does the opposite. Emitting {z}/{x}/{y} produces a
        // service that loads, requests tiles, and shows the wrong part of the
        // world — mirrored about the diagonal, which looks like corrupt data.
        Assert.Equal(
            "tile/{z}/{y}/{x}.pbf",
            Service().GetProperty("tiles")[0].GetString());
    }

    [Fact]
    public void The_tile_template_is_relative_so_a_reverse_proxy_does_not_break_it()
    {
        string template = Service().GetProperty("tiles")[0].GetString()!;

        Assert.False(
            template.StartsWith('/') || template.Contains("://", StringComparison.Ordinal),
            "An absolute URL is built from the request host, works in development, and hands "
            + "out unreachable links the moment the server sits behind a proxy.");
    }

    // ---------- the tiling scheme ----------

    [Fact]
    public void The_origin_is_the_top_left_of_the_world()
    {
        // Bottom-left is TMS, and the difference is a map that is upside down
        // while looking entirely plausible at a glance.
        JsonElement origin = Service().GetProperty("tileInfo").GetProperty("origin");

        Assert.Equal(-VectorTileServerMetadataWriter.WorldExtent, origin.GetProperty("x").GetDouble());
        Assert.Equal(VectorTileServerMetadataWriter.WorldExtent, origin.GetProperty("y").GetDouble());
    }

    [Fact]
    public void There_is_one_level_of_detail_per_zoom_including_zero()
    {
        JsonElement lods = Service(maxZoom: 22).GetProperty("tileInfo").GetProperty("lods");

        Assert.Equal(23, lods.GetArrayLength());
        Assert.Equal(0, lods[0].GetProperty("level").GetInt32());
        Assert.Equal(22, lods[22].GetProperty("level").GetInt32());
    }

    [Fact]
    public void Each_level_halves_the_resolution_and_the_scale()
    {
        JsonElement lods = Service().GetProperty("tileInfo").GetProperty("lods");

        for (int level = 1; level < lods.GetArrayLength(); level++)
        {
            Assert.Equal(
                lods[level - 1].GetProperty("resolution").GetDouble() / 2,
                lods[level].GetProperty("resolution").GetDouble(),
                precision: 9);

            Assert.Equal(
                lods[level - 1].GetProperty("scale").GetDouble() / 2,
                lods[level].GetProperty("scale").GetDouble(),
                precision: 6);
        }
    }

    [Fact]
    public void The_level_zero_resolution_matches_the_declared_tile_size()
    {
        // The two numbers must agree or every label and line width comes out at
        // the wrong size: resolution is metres per pixel, so it is the world
        // span divided by the pixel count the tileInfo claims.
        JsonElement info = Service().GetProperty("tileInfo");
        double span = VectorTileServerMetadataWriter.WorldExtent * 2;

        Assert.Equal(
            span / info.GetProperty("cols").GetInt32(),
            info.GetProperty("lods")[0].GetProperty("resolution").GetDouble(),
            precision: 6);
    }

    [Fact]
    public void The_tile_grid_is_square()
    {
        JsonElement info = Service().GetProperty("tileInfo");

        Assert.Equal(info.GetProperty("cols").GetInt32(), info.GetProperty("rows").GetInt32());
    }

    [Fact]
    public void The_spatial_reference_is_declared_in_the_code_Esri_clients_send()
    {
        // 102100 is Esri's own identifier for Web Mercator Auxiliary Sphere.
        // Declaring 3857 alone is technically the same projection and is not
        // what an ArcGIS client matches on — the FeatureServer surface already
        // learned this by refusing every request an SDK made.
        JsonElement sr = Service().GetProperty("tileInfo").GetProperty("spatialReference");

        Assert.Equal(102100, sr.GetProperty("wkid").GetInt32());
        Assert.Equal(3857, sr.GetProperty("latestWkid").GetInt32());
    }

    // ---------- the extent ----------

    [Fact]
    public void A_known_extent_is_reported()
    {
        JsonElement extent = Service(new Envelope(1, 2, 3, 4)).GetProperty("fullExtent");

        Assert.Equal(1, extent.GetProperty("xmin").GetDouble());
        Assert.Equal(4, extent.GetProperty("ymax").GetDouble());
    }

    [Fact]
    public void An_unknown_extent_becomes_the_world_rather_than_being_omitted()
    {
        // ST_EstimatedExtent returns nothing for a table that has never been
        // analysed, which is the state a freshly loaded table is in. A client
        // with no extent either shows the whole world or refuses to add the
        // layer, and both read as a broken service.
        JsonElement extent = Service(extent: null).GetProperty("fullExtent");

        Assert.Equal(-VectorTileServerMetadataWriter.WorldExtent, extent.GetProperty("xmin").GetDouble());
        Assert.Equal(VectorTileServerMetadataWriter.WorldExtent, extent.GetProperty("xmax").GetDouble());
    }

    [Fact]
    public void The_initial_and_full_extents_agree()
    {
        JsonElement service = Service(new Envelope(1, 2, 3, 4));

        Assert.Equal(
            service.GetProperty("fullExtent").GetProperty("xmin").GetDouble(),
            service.GetProperty("initialExtent").GetProperty("xmin").GetDouble());
    }

    // ---------- capability claims ----------

    [Fact]
    public void Bulk_export_is_refused_explicitly_rather_than_left_unstated()
    {
        // Absent is read as "unknown" by some clients, and offering an export
        // that does not exist fails at the least convenient moment.
        Assert.False(Service().GetProperty("exportTilesAllowed").GetBoolean());
    }

    [Fact]
    public void The_service_declares_itself_a_vector_tile_service()
    {
        Assert.Equal("indexedVector", Service().GetProperty("type").GetString());
        Assert.Equal("TilesOnly", Service().GetProperty("capabilities").GetString());
    }

    [Fact]
    public void The_maximum_zoom_is_reported_and_matches_the_deepest_level()
    {
        JsonElement service = Service(maxZoom: 18);
        JsonElement lods = service.GetProperty("tileInfo").GetProperty("lods");

        Assert.Equal(18, service.GetProperty("maxzoom").GetInt32());
        Assert.Equal(18, lods[lods.GetArrayLength() - 1].GetProperty("level").GetInt32());
    }

    // ---------- the style ----------

    [Fact]
    public void The_style_names_the_layer_inside_the_tile_under_the_hyphenated_key()
    {
        // "source-layer" cannot be a C# member name, so it comes from a
        // dictionary. If it ever regresses to sourceLayer the style loads, the
        // map stays empty, and nothing appears in the browser console.
        JsonElement layer = Parse(
            VectorTileServerMetadataWriter.Style([("roads", GeometryKind.LineString, null)]))
            .GetProperty("layers")[0];

        Assert.Equal("roads", layer.GetProperty("source-layer").GetString());
    }

    [Fact]
    public void The_style_source_points_two_levels_up_at_the_service_root()
    {
        // The style is served from resources/styles/root.json, so ../../ is the
        // service root — which is what the client re-reads to find the tile
        // template. Wrong here means a style that loads and a map that never
        // requests a tile.
        Assert.Equal(
            "../../",
            Parse(VectorTileServerMetadataWriter.Style([("roads", GeometryKind.Polygon, null)]))
                .GetProperty("sources").GetProperty("esri").GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(GeometryKind.Point, "circle")]
    [InlineData(GeometryKind.MultiPoint, "circle")]
    [InlineData(GeometryKind.LineString, "line")]
    [InlineData(GeometryKind.MultiLineString, "line")]
    [InlineData(GeometryKind.Polygon, "fill")]
    [InlineData(GeometryKind.MultiPolygon, "fill")]
    public void The_style_draws_each_geometry_kind_as_the_right_thing(
        GeometryKind kind, string expected)
    {
        // A polygon layer styled as a line draws outlines and no fill, which
        // reads as a rendering bug rather than a default nobody set.
        Assert.Equal(
            expected,
            Parse(VectorTileServerMetadataWriter.Style([("x", kind, null)]))
                .GetProperty("layers")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void The_style_paint_matches_the_layer_type_it_declares()
    {
        // A fill layer carrying circle-radius is silently ignored by the
        // renderer, so the two have to move together.
        JsonElement layer = Parse(VectorTileServerMetadataWriter.Style([("x", GeometryKind.Polygon, null)]))
            .GetProperty("layers")[0];

        Assert.True(layer.GetProperty("paint").EnumerateObject()
            .All(p => p.Name.StartsWith("fill-", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_style_declares_version_eight()
    {
        Assert.Equal(
            8,
            Parse(VectorTileServerMetadataWriter.Style([("x", GeometryKind.Polygon, null)]))
                .GetProperty("version").GetInt32());
    }

    [Fact]
    public void A_service_needs_a_name()
    {
        Assert.Throws<ArgumentException>(
            () => VectorTileServerMetadataWriter.Service(" ", ["x"], null, 22, 3857));
    }

    // ---------- the extent's reference, which is not the grid's ----------

    /// <summary>
    /// An extent that is not in Web Mercator is refused, not declared.
    /// </summary>
    /// <remarks>
    /// <b>D-49, and its second attempt is the instructive one.</b> Both fields used
    /// to be stamped Web Mercator, so a layer stored in EPSG:4326 published a degree
    /// extent labelled as metres — for Turkey, a box off West Africa. The first fix
    /// declared the extent's real reference, which is honest and non-conformant: a
    /// tile service's tiling scheme *is* Web Mercator and clients read
    /// <c>fullExtent</c> in the scheme's reference. Given a document whose two
    /// references disagreed, the ArcGIS JS client read the metadata, the style and
    /// the sprites, and then **requested no tile at all** — measured in the server
    /// log, silent everywhere else.
    ///
    /// So the extent has to be projected before it gets here, and a caller that
    /// skipped that is told rather than accommodated.
    /// </remarks>
    [Fact]
    public void An_extent_that_is_not_web_mercator_is_refused()
    {
        ArgumentOutOfRangeException refusal = Assert.Throws<ArgumentOutOfRangeException>(
            () => Service(new Envelope(24.7, 34.8, 45.5, 42.8), srid: 4326));

        Assert.Contains("tiling scheme", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_web_mercator_layer_still_declares_the_ArcGIS_spelling()
    {
        JsonElement extent = Service(new Envelope(-1, -1, 1, 1), srid: 3857)
            .GetProperty("fullExtent");

        // 102100 rather than 3857 in `wkid`, because that is the spelling ArcGIS
        // clients match on.
        Assert.Equal(102100, extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.Equal(
            3857, extent.GetProperty("spatialReference").GetProperty("latestWkid").GetInt32());
    }

    /// <summary>An unknown extent is the whole world, in metres.</summary>
    /// <remarks>
    /// Which is also what a failed projection produces: the endpoint hands null
    /// rather than the unprojected numbers, because the world is a safe answer for a
    /// client and degrees labelled as metres are not.
    /// </remarks>
    [Fact]
    public void An_unknown_extent_is_the_whole_world_in_metres()
    {
        JsonElement extent = Service(null).GetProperty("fullExtent");

        Assert.Equal(-VectorTileServerMetadataWriter.WorldExtent, extent.GetProperty("xmin").GetDouble());
        Assert.Equal(VectorTileServerMetadataWriter.WorldExtent, extent.GetProperty("ymax").GetDouble());
    }
}
