using Graticula.Geometries;
using Xunit;

namespace Graticula.Providers.DuckDb.Tests;

/// <summary>
/// Reading a GeoParquet file's <c>geo</c> key — ADR-066 §6.
/// </summary>
/// <remarks>
/// <b>Mostly the cases where the answer is <em>no</em>,</b> because a file this reads wrongly is a
/// layer published in the wrong place, and a layer in the wrong place draws — every coordinate
/// right and every one attached to the wrong part of the Earth.
/// </remarks>
public sealed class GeoParquetMetadataTests
{
    private static string Geo(string column) =>
        "{\"version\":\"1.1.0\",\"primary_column\":\"geom\",\"columns\":{\"geom\":" + column + "}}";

    [Fact]
    public void An_absent_crs_is_CRS84_and_is_published_as_4326()
    {
        GeoParquetMetadata read = GeoParquetMetadata.Parse(Geo("{\"encoding\":\"WKB\",\"geometry_types\":[\"Point\"]}"));

        Assert.Null(read.Problem);
        Assert.Equal("geom", read.Column);
        Assert.Equal(4326, read.Srid);
        Assert.Equal(GeometryKind.Point, read.Kind);
    }

    [Fact]
    public void A_null_crs_is_unknown_and_is_not_published_on_a_guess()
    {
        GeoParquetMetadata read = GeoParquetMetadata.Parse(Geo("{\"encoding\":\"WKB\",\"geometry_types\":[],\"crs\":null}"));

        Assert.Null(read.Srid);
        Assert.Contains("unknown", read.Problem, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"id\":{\"authority\":\"EPSG\",\"code\":3857}}", 3857)]
    [InlineData("{\"id\":{\"authority\":\"EPSG\",\"code\":\"2320\"}}", 2320)]
    [InlineData("{\"ids\":[{\"authority\":\"ESRI\",\"code\":102100},{\"authority\":\"EPSG\",\"code\":3857}]}", 3857)]
    [InlineData("{\"id\":{\"authority\":\"OGC\",\"code\":\"CRS84\"}}", 4326)]
    [InlineData("\"EPSG:5254\"", 5254)]
    [InlineData("\"OGC:CRS84\"", 4326)]
    public void A_crs_is_read_from_its_identifier(string crs, int srid)
    {
        GeoParquetMetadata read = GeoParquetMetadata.Parse(Geo("{\"encoding\":\"WKB\",\"geometry_types\":[],\"crs\":" + crs + "}"));

        Assert.Null(read.Problem);
        Assert.Equal(srid, read.Srid);
    }

    [Fact]
    public void A_crs_with_no_EPSG_identifier_is_refused_by_name()
    {
        GeoParquetMetadata read = GeoParquetMetadata.Parse(
            Geo("{\"encoding\":\"WKB\",\"geometry_types\":[],\"crs\":{\"type\":\"ProjectedCRS\",\"name\":\"Local grid\"}}"));

        Assert.Null(read.Srid);
        Assert.Contains("Local grid", read.Problem, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_native_GeoArrow_encoding_is_refused_rather_than_misread()
    {
        GeoParquetMetadata read = GeoParquetMetadata.Parse(Geo("{\"encoding\":\"polygon\"}"));

        Assert.Contains("WKB only", read.Problem, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[\"Polygon\",\"MultiPolygon\"]", GeometryKind.MultiPolygon)]
    [InlineData("[\"LineString\"]", GeometryKind.LineString)]
    [InlineData("[\"Point Z\"]", GeometryKind.Point)]
    [InlineData("[\"MultiPoint\"]", GeometryKind.MultiPoint)]
    public void Geometry_types_become_one_kind(string types, GeometryKind kind)
    {
        GeoParquetMetadata read = GeoParquetMetadata.Parse(Geo("{\"encoding\":\"WKB\",\"geometry_types\":" + types + "}"));

        Assert.Null(read.Problem);
        Assert.Equal(kind, read.Kind);
    }

    [Fact]
    public void A_missing_geometry_types_list_is_the_file_s_fault_and_says_so()
    {
        GeoParquetMetadata read = GeoParquetMetadata.Parse(Geo("{\"encoding\":\"WKB\"}"));

        Assert.Contains("geometry_types", read.Problem, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_families_are_refused_because_a_layer_has_one()
    {
        GeoParquetMetadata read = GeoParquetMetadata.Parse(
            Geo("{\"encoding\":\"WKB\",\"geometry_types\":[\"Point\",\"Polygon\"]}"));

        Assert.Contains("mixes geometry families", read.Problem, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_covering_is_found_only_when_all_four_corners_are_one_struct()
    {
        const string Good = "{\"encoding\":\"WKB\",\"covering\":{\"bbox\":{\"xmin\":[\"geom_bbox\",\"xmin\"],\"ymin\":[\"geom_bbox\",\"ymin\"],\"xmax\":[\"geom_bbox\",\"xmax\"],\"ymax\":[\"geom_bbox\",\"ymax\"]}}}";
        const string Split = "{\"encoding\":\"WKB\",\"covering\":{\"bbox\":{\"xmin\":[\"a\",\"xmin\"],\"ymin\":[\"b\",\"ymin\"],\"xmax\":[\"a\",\"xmax\"],\"ymax\":[\"a\",\"ymax\"]}}}";

        Assert.Equal("geom_bbox", GeoParquetMetadata.Parse(Geo(Good)).Covering);
        Assert.Null(GeoParquetMetadata.Parse(Geo(Split)).Covering);
    }

    [Fact]
    public void The_bbox_is_read_in_two_and_three_dimensions()
    {
        Assert.Equal(new Envelope(1, 2, 3, 4), GeoParquetMetadata.Parse(Geo("{\"encoding\":\"WKB\",\"bbox\":[1,2,3,4]}")).Bbox);
        Assert.Equal(new Envelope(1, 2, 4, 5), GeoParquetMetadata.Parse(Geo("{\"encoding\":\"WKB\",\"bbox\":[1,2,0,4,5,9]}")).Bbox);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\":\"1.1.0\",\"columns\":{}}")]
    [InlineData("{\"version\":\"1.1.0\",\"primary_column\":\"geom\",\"columns\":{}}")]
    [InlineData("[]")]
    [InlineData("\"geo\"")]
    [InlineData("{\"primary_column\":\"geom\",\"columns\":{\"geom\":[]}}")]
    [InlineData("{\"primary_column\":\"geom\",\"columns\":{\"geom\":{\"encoding\":\"WKB\",\"geometry_types\":[1]}}}")]
    [InlineData("{\"primary_column\":\"geom\",\"columns\":{\"geom\":{\"encoding\":\"WKB\",\"geometry_types\":[],\"crs\":{\"id\":[]}}}}")]
    public void Metadata_that_does_not_say_where_the_geometry_is_is_a_problem(string? json)
    {
        // A problem, and never an exception: one bad file must not fail the folder it is in.
        GeoParquetMetadata read = GeoParquetMetadata.Parse(json);

        Assert.True(read.Problem is not null || read.Srid is null, json);
    }

    [Fact]
    public void A_covering_path_of_the_wrong_kind_is_ignored_rather_than_thrown()
    {
        const string Numbers = "{\"encoding\":\"WKB\",\"geometry_types\":[],\"covering\":{\"bbox\":{\"xmin\":[1,2],\"ymin\":[1,2],\"xmax\":[1,2],\"ymax\":[1,2]}}}";

        Assert.Null(GeoParquetMetadata.Parse(Geo(Numbers)).Covering);
    }
}
