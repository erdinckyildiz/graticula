using System;
using System.Linq;
using System.Text.Json;
using GisServer.Features;
using GisServer.Formats;
using GisServer.Geometries;
using Xunit;

namespace GisServer.Core.Tests.Formats;

/// <summary>
/// Reading a GeoJSON upload, and everything it refuses.
/// </summary>
/// <remarks>
/// <para>
/// This is the first surface that turns a stranger's file into a database table,
/// so most of what matters is the refusals. A file that parses into the wrong
/// table is worse than one that is rejected: the rejection is visible and the
/// wrong table is not.
/// </para>
/// </remarks>
public sealed class GeoJsonFeaturesTests
{
    private static ImportedDataset Read(string json)
    {
        Assert.True(
            GeoJsonFeatures.TryRead(
                JsonDocument.Parse(json).RootElement,
                ImportLimits.Default,
                out ImportedDataset? dataset,
                out string? error),
            error);

        return dataset!;
    }

    private static string Refuse(string json, ImportLimits? limits = null)
    {
        Assert.False(GeoJsonFeatures.TryRead(
            JsonDocument.Parse(json).RootElement,
            limits ?? ImportLimits.Default,
            out _,
            out string? error));

        Assert.False(string.IsNullOrWhiteSpace(error), "a refusal must say why");
        return error!;
    }

    private static string Collection(params string[] features) =>
        $$"""{"type":"FeatureCollection","features":[{{string.Join(",", features)}}]}""";

    private static string Feature(string geometry, string properties = "{}") =>
        $$"""{"type":"Feature","geometry":{{geometry}},"properties":{{properties}}}""";

    private const string Point = """{"type":"Point","coordinates":[28.9,41.0]}""";

    private const string Square =
        """{"type":"Polygon","coordinates":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}""";

    // ---------- the basics ----------

    [Fact]
    public void A_collection_of_points_reads_as_a_point_layer()
    {
        ImportedDataset dataset = Read(Collection(Feature(Point), Feature(Point)));

        Assert.Equal(2, dataset.Features.Count);
        Assert.Equal(GeometryKind.Point, dataset.GeometryType);
        Assert.Equal(GeoJsonFeatures.GeoJsonSrid, dataset.Srid);
    }

    [Fact]
    public void A_polygons_ring_is_closed_if_the_file_left_it_open()
    {
        // RFC 7946 requires it and exporters forget. Refusing would reject files
        // every other tool accepts, over something with one correct repair.
        ImportedDataset dataset = Read(Collection(Feature(
            """{"type":"Polygon","coordinates":[[[0,0],[0,1],[1,1],[1,0]]]}""")));

        Polygon polygon = Assert.IsType<Polygon>(dataset.Features[0].Geometry);
        XySequence ring = polygon.Shell.Coordinates;

        Assert.Equal(ring.X(0), ring.X(ring.Count - 1));
        Assert.Equal(ring.Y(0), ring.Y(ring.Count - 1));
    }

    [Fact]
    public void A_feature_with_no_geometry_is_kept_rather_than_refusing_the_file()
    {
        // Allowed by the specification. A feature with attributes and no
        // location is data somebody may want, and refusing the whole upload over
        // one is disproportionate.
        ImportedDataset dataset = Read(Collection(
            Feature(Point, """{"n":1}"""),
            """{"type":"Feature","geometry":null,"properties":{"n":2}}"""));

        Assert.Equal(2, dataset.Features.Count);
        Assert.Null(dataset.Features[1].Geometry);
    }

    // ---------- type inference ----------

    [Fact]
    public void A_column_takes_the_type_that_holds_every_value_in_the_file()
    {
        // <b>The test that decides whether a load fails partway through.</b>
        // Inferring from the first feature makes 'code' an integer, and the
        // ninth row then does not fit — after the table exists and most of the
        // data is in it.
        ImportedDataset dataset = Read(Collection(
            Feature(Point, """{"code":1}"""),
            Feature(Point, """{"code":2}"""),
            Feature(Point, """{"code":"n/a"}""")));

        Assert.Equal(FieldType.Text, dataset.Columns.Single(c => c.Name == "code").Type);
    }

    [Theory]
    [InlineData("1", FieldType.Integer)]
    [InlineData("2147483648", FieldType.BigInteger)]
    [InlineData("1.5", FieldType.Double)]
    [InlineData("\"x\"", FieldType.Text)]
    [InlineData("true", FieldType.Boolean)]
    public void A_single_kind_of_value_gets_the_narrowest_type_that_holds_it(
        string value, FieldType expected)
    {
        ImportedDataset dataset = Read(Collection(Feature(Point, $$"""{"v":{{value}}}""")));

        Assert.Equal(expected, dataset.Columns.Single().Type);
    }

    [Fact]
    public void An_integer_and_a_fraction_together_are_a_double()
    {
        ImportedDataset dataset = Read(Collection(
            Feature(Point, """{"v":1}"""),
            Feature(Point, """{"v":1.5}""")));

        Assert.Equal(FieldType.Double, dataset.Columns.Single().Type);
    }

    [Fact]
    public void A_boolean_mixed_with_a_number_is_text_rather_than_either()
    {
        // A column holding both true and 1 is not a boolean column. Collapsing
        // them invents a meaning the file did not have.
        ImportedDataset dataset = Read(Collection(
            Feature(Point, """{"v":true}"""),
            Feature(Point, """{"v":1}""")));

        Assert.Equal(FieldType.Text, dataset.Columns.Single().Type);
    }

    [Fact]
    public void A_nested_object_is_kept_as_text_rather_than_dropped()
    {
        // Losing it silently would be worse than storing the JSON verbatim: the
        // caller believes they uploaded it.
        ImportedDataset dataset = Read(Collection(
            Feature(Point, """{"v":{"nested":true}}""")));

        Assert.Equal(FieldType.Text, dataset.Columns.Single().Type);
    }

    [Fact]
    public void A_property_missing_from_any_feature_makes_the_column_nullable()
    {
        // Declaring NOT NULL from a file where most rows happen to carry a value
        // is how a load fails on row nine hundred.
        ImportedDataset dataset = Read(Collection(
            Feature(Point, """{"a":1,"b":2}"""),
            Feature(Point, """{"a":3}""")));

        Assert.True(dataset.Columns.Single(c => c.Name == "b").Nullable);
        Assert.False(dataset.Columns.Single(c => c.Name == "a").Nullable);
    }

    [Fact]
    public void An_explicit_null_makes_a_column_nullable_and_does_not_decide_its_type()
    {
        ImportedDataset dataset = Read(Collection(
            Feature(Point, """{"v":null}"""),
            Feature(Point, """{"v":7}""")));

        InferredColumn column = dataset.Columns.Single();

        Assert.True(column.Nullable);
        Assert.Equal(FieldType.Integer, column.Type);
    }

    [Fact]
    public void Columns_keep_the_order_they_first_appeared_in()
    {
        // So the table reads like the file. Alphabetical would be tidier and
        // would make a wide table unrecognisable to the person who uploaded it.
        ImportedDataset dataset = Read(Collection(
            Feature(Point, """{"zebra":1,"apple":2}""")));

        Assert.Equal(["zebra", "apple"], dataset.Columns.Select(c => c.Name));
    }

    // ---------- geometry types ----------

    [Fact]
    public void A_polygon_and_a_multipolygon_together_become_a_multipolygon_layer()
    {
        // Exporters produce mixed singular and plural constantly — one island is
        // a Polygon, two are a MultiPolygon, in the same file from the same
        // source. Refusing that would refuse most real data.
        ImportedDataset dataset = Read(Collection(
            Feature(Square),
            Feature("""{"type":"MultiPolygon","coordinates":[[[[0,0],[0,1],[1,1],[1,0],[0,0]]]]}""")));

        Assert.Equal(GeometryKind.MultiPolygon, dataset.GeometryType);
    }

    [Fact]
    public void Points_and_polygons_in_one_file_are_refused_with_the_feature_number()
    {
        string error = Refuse(Collection(Feature(Point), Feature(Square)));

        Assert.Contains("1", error, StringComparison.Ordinal);
        Assert.Contains("Split the file", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_geometry_collection_is_refused_because_no_client_draws_one()
    {
        Assert.Contains(
            "GeometryCollection",
            Refuse(Collection(Feature("""{"type":"GeometryCollection","geometries":[]}"""))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_whose_features_all_lack_geometry_is_refused()
    {
        Assert.Contains(
            "nothing to publish",
            Refuse(Collection("""{"type":"Feature","geometry":null,"properties":{}}""")),
            StringComparison.Ordinal);
    }

    // ---------- coordinates ----------

    [Fact]
    public void A_declared_crs_is_refused_rather_than_ignored()
    {
        // The 2008 draft's crs member was widely written and widely ignored, so
        // a file carrying one is a file whose author believes their coordinates
        // are in something other than 4326. Ignoring it publishes their data
        // somewhere it is not.
        string error = Refuse(
            """
            {"type":"FeatureCollection",
             "crs":{"type":"name","properties":{"name":"EPSG:27700"}},
             "features":[{"type":"Feature",
                          "geometry":{"type":"Point","coordinates":[0,0]},
                          "properties":{}}]}
            """);

        Assert.Contains("7946", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_coordinate_outside_WGS84_is_refused_and_names_the_axis_order()
    {
        // The only detectable case of a latitude-first file. Most swapped files
        // parse perfectly and land in the wrong hemisphere; this catches the
        // ones where the swap made the numbers impossible.
        string error = Refuse(Collection(Feature(
            """{"type":"Point","coordinates":[500000,4500000]}""")));

        Assert.Contains("longitude then latitude", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_third_ordinate_is_dropped_rather_than_refused()
    {
        // RFC 7946 allows elevation and our model is two-dimensional. Refusing
        // would reject a large fraction of real files over an ordinate nothing
        // in this product reads.
        ImportedDataset dataset = Read(Collection(Feature(
            """{"type":"Point","coordinates":[28.9,41.0,120.5]}""")));

        Point point = Assert.IsType<Point>(dataset.Features[0].Geometry);

        Assert.Equal(28.9, point.X);
        Assert.Equal(41.0, point.Y);
    }

    [Fact]
    public void A_non_finite_coordinate_is_refused()
    {
        // JSON has no NaN literal, so this arrives as a string and the parser
        // rejects it before we see it — but a number large enough to become
        // infinity on parse does not.
        Refuse(Collection(Feature("""{"type":"Point","coordinates":[1e400,0]}""")));
    }

    // ---------- shape of the document ----------

    [Fact]
    public void A_bare_feature_is_refused_because_a_layer_needs_a_collection()
    {
        Assert.Contains(
            "FeatureCollection",
            Refuse(Feature(Point)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_collection_is_refused()
    {
        Assert.Contains(
            "no features",
            Refuse("""{"type":"FeatureCollection","features":[]}"""),
            StringComparison.Ordinal);
    }

    // ---------- the caps ----------

    [Fact]
    public void Too_many_features_is_refused()
    {
        Assert.Contains(
            "more than 1 features",
            Refuse(
                Collection(Feature(Point), Feature(Point)),
                new ImportLimits(1, 1_000, 10)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_coordinates_is_refused()
    {
        Assert.Contains(
            "coordinates",
            Refuse(
                Collection(Feature(Square), Feature(Square)),
                new ImportLimits(100, 5, 10)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_distinct_property_names_is_refused()
    {
        // A file whose every feature invents new names would otherwise produce a
        // table with more columns than PostgreSQL accepts, and find that out
        // during the DDL.
        Assert.Contains(
            "property names",
            Refuse(
                Collection(
                    Feature(Point, """{"a":1}"""),
                    Feature(Point, """{"b":1}"""),
                    Feature(Point, """{"c":1}""")),
                new ImportLimits(100, 1_000, 2)),
            StringComparison.Ordinal);
    }
}
