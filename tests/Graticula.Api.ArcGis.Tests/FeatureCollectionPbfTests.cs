using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Pbf;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Testing;
using Xunit;
using Field = Graticula.Testing.PbfReader.Field;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// The <c>f=pbf</c> answer, read back with a decoder written from the published proto — ADR-073.
/// </summary>
/// <remarks>
/// Written 2026-09-15, when <c>f=pbf</c> was first answered. Each property here was also checked once
/// against the independent npm decoder <c>arcgis-pbf-parser</c>.
/// </remarks>
public sealed class FeatureCollectionPbfTests
{
    private static readonly string[] Names = ["objectid", "name", "when", "big", "flag", "uid"];

    private static LayerDefinition Layer(int srid = 3857) => new(
        name: "pbf",
        schemaName: "public",
        tableName: "pbf",
        geometryColumn: "geom",
        srid: srid,
        identityColumn: "objectid",
        integerIdentityColumn: "objectid",
        isHosted: true);

    private static readonly FieldDescription[] Described =
    [
        new("objectid", FieldType.Integer, false, null),
        new("name", FieldType.Text, true, 40, Alias: "Ad"),
        new("when", FieldType.Date, true, null),
        new("big", FieldType.BigInteger, true, null),
        new("flag", FieldType.Boolean, true, null),
        new("uid", FieldType.Guid, true, null),
    ];

    private static async Task<(IReadOnlyList<Field> Result, int Written)> WriteAsync(
        IReadOnlyList<Geometry?> shapes,
        PbfQuantization? quantization = null,
        long ceiling = 0,
        int limit = 100,
        GeometryKind kind = GeometryKind.Polygon)
    {
        FeatureCollectionPbfWriter writer = new(Layer(), ceiling, Described);
        using MemoryStream stream = new();

        int written = await writer.WriteAsync(
            stream,
            new Source(shapes),
            new FeatureQuery(limit, fields: Names),
            kind,
            quantization ?? PbfQuantization.Default(3857),
            CancellationToken.None);

        return (PbfReader.QueryResult(stream.ToArray()).One(1).Message, written);
    }

    private static Polygon Square(double x, double y, double size, bool counterClockwise, LinearRing? hole = null)
    {
        double[] ring = counterClockwise
            ? [x, y, x + size, y, x + size, y + size, x, y + size, x, y]
            : [x, y, x, y + size, x + size, y + size, x + size, y, x, y];

        return new Polygon(new LinearRing(XySequence.Wrap(ring)), hole is null ? null : [hole]);
    }

    private static double SignedArea(List<(double X, double Y)> ring)
    {
        double sum = 0;

        for (int i = 0; i < ring.Count - 1; i++)
        {
            sum += (ring[i].X * ring[i + 1].Y) - (ring[i + 1].X * ring[i].Y);
        }

        return sum / 2;
    }

    [Fact]
    public async Task The_header_names_the_object_id_the_types_the_aliases_and_the_reference()
    {
        (IReadOnlyList<Field> result, _) = await WriteAsync([new Point(1, 2)], kind: GeometryKind.Point);

        Assert.Equal("objectid", result.One(1).Text);
        Assert.Equal(0UL, result.OptionalVarint(7));
        Assert.Equal(3857UL, result.One(8).Message.One(1).Varint);

        List<IReadOnlyList<Field>> fields = [.. result.All(13).Select(f => f.Message)];

        Assert.Equal(Names, fields.Select(f => f.One(1).Text));
        Assert.Equal([6UL, 4UL, 5UL, 4UL, 0UL, 10UL], fields.Select(f => f.OptionalVarint(2)));
        Assert.Equal("Ad", fields[1].One(3).Text);
    }

    [Fact]
    public async Task Values_follow_the_json_writer_rules()
    {
        (IReadOnlyList<Field> result, _) = await WriteAsync([new Point(1, 2)], kind: GeometryKind.Point);

        List<IReadOnlyList<Field>> values = [.. result.One(15).Message.All(1).Select(v => v.Message)];

        Assert.Equal(5, values[0].One(5).Number);
        Assert.Equal(0UL, values[0].One(5).Varint);
        Assert.Equal("feature 0", values[1].One(1).Text);
        Assert.Equal(Source.When.ToUnixTimeMilliseconds(), values[2].One(8).SInt);

        // A 64-bit integer is text, as in json: JavaScript cannot hold it.
        Assert.Equal("9007199254740993", values[3].One(1).Text);
        Assert.Equal(1, values[4].One(4).SInt);
        Assert.Equal("{" + Source.Uid.ToString("D").ToUpperInvariant() + "}", values[5].One(1).Text);
    }

    [Fact]
    public async Task A_point_decodes_to_where_it_was_on_the_default_grid()
    {
        (IReadOnlyList<Field> result, _) = await WriteAsync([new Point(3123456.789012, -1234567.890123)], kind: GeometryKind.Point);

        IReadOnlyList<Field> geometry = result.One(15).Message.One(2).Message;
        (double x, double y) = PbfReader.Parts(geometry, result.One(12).Message)[0][0];

        Assert.Null(geometry.Find(f => f.Number == 2));
        Assert.Equal(3123456.789012, x, 1e-4);
        Assert.Equal(-1234567.890123, y, 1e-4);
    }

    [Fact]
    public async Task Each_part_starts_absolute_and_rings_are_wound_the_arcgis_way()
    {
        // A counter-clockwise shell with a clockwise hole, beside a second polygon: every ring has the
        // wrong sense for ArcGIS, so every one must come out reversed.
        LinearRing hole = new(XySequence.Wrap([2, 2, 2, 4, 4, 4, 4, 2, 2, 2]));
        MultiPolygon shape = new([Square(0, 0, 10, counterClockwise: true, hole), Square(100, 100, 5, counterClockwise: true)]);

        (IReadOnlyList<Field> result, _) = await WriteAsync([shape]);
        IReadOnlyList<Field> geometry = result.One(15).Message.One(2).Message;
        List<List<(double X, double Y)>> rings = PbfReader.Parts(geometry, result.One(12).Message);

        Assert.Equal([5UL, 5UL, 5UL], PbfReader.Packed(geometry.One(2).Bytes));
        Assert.True(SignedArea(rings[0]) < 0, "The shell is not clockwise.");
        Assert.True(SignedArea(rings[1]) > 0, "The hole is not counter-clockwise.");
        Assert.True(SignedArea(rings[2]) < 0, "The second shell is not clockwise.");

        Assert.Equal(100, rings[2][0].X, 1e-4);
        Assert.Equal(100, rings[2][0].Y, 1e-4);

        // The second polygon's first vertex is written as its own position, not as the step from the
        // hole's last vertex: the specification's example starts its second ring at 56, 56.
        List<long> coords = PbfReader.PackedSInt(geometry.One(3).Bytes);
        Assert.Equal(PbfQuantization.Default(3857).X(100), coords[20]);
    }

    [Fact]
    public async Task View_mode_drops_vertices_on_one_cell_and_parts_that_collapse_and_edit_mode_keeps_them()
    {
        PbfQuantization view = new() { Tolerance = 10, OriginX = 0, OriginY = 1000, UpperLeft = true, View = true };
        PbfQuantization edit = view with { View = false };

        MultiLineString lines = new(
        [
            new LineString(XySequence.Wrap([0, 0, 1, 1, 2, 2, 50, 50])),
            new LineString(XySequence.Wrap([500, 500, 501, 501])),
        ]);

        (IReadOnlyList<Field> viewed, _) = await WriteAsync([lines], view, kind: GeometryKind.MultiLineString);
        (IReadOnlyList<Field> edited, _) = await WriteAsync([lines], edit, kind: GeometryKind.MultiLineString);

        Assert.Equal([2UL], PbfReader.Packed(viewed.One(15).Message.One(2).Message.One(2).Bytes));
        Assert.Equal([4UL, 2UL], PbfReader.Packed(edited.One(15).Message.One(2).Message.One(2).Bytes));

        List<List<(double X, double Y)>> decoded = PbfReader.Parts(viewed.One(15).Message.One(2).Message, viewed.One(12).Message);
        Assert.Equal((0, 0), decoded[0][0]);
        Assert.Equal((50, 50), decoded[0][1]);
    }

    [Fact]
    public async Task A_shape_view_mode_leaves_nothing_of_is_written_without_geometry_and_keeps_its_row()
    {
        PbfQuantization view = new() { Tolerance = 1000, OriginX = 0, OriginY = 0, UpperLeft = false, View = true };

        (IReadOnlyList<Field> result, int written) = await WriteAsync([Square(1, 1, 3, counterClockwise: false)], view);

        Assert.Equal(1, written);
        Assert.Null(result.One(15).Message.Find(f => f.Number == 2));
        Assert.Equal(1UL, result.One(12).Message.OptionalVarint(1));
    }

    [Fact]
    public async Task The_ceiling_truncates_after_a_feature_and_says_so()
    {
        Geometry?[] many = [.. Enumerable.Range(0, 400).Select(i => (Geometry?)new Point(i, i))];

        (IReadOnlyList<Field> truncated, int written) = await WriteAsync(many, ceiling: 2_000, limit: 1000, kind: GeometryKind.Point);
        Assert.InRange(written, 1, 399);
        Assert.Equal(1UL, truncated.OptionalVarint(9));

        (IReadOnlyList<Field> one, int first) = await WriteAsync(many, ceiling: 1, limit: 1000, kind: GeometryKind.Point);
        Assert.Equal(1, first);
        Assert.Single(one.All(15));

        (IReadOnlyList<Field> whole, _) = await WriteAsync(many[..3], limit: 10, kind: GeometryKind.Point);
        Assert.Equal(0UL, whole.OptionalVarint(9));
    }

    [Fact]
    public async Task Counts_ids_and_extents_have_their_own_messages()
    {
        using MemoryStream count = new();
        await FeatureCollectionPbfWriter.WriteCountAsync(count, 42, CancellationToken.None);
        Assert.Equal(42UL, PbfReader.QueryResult(count.ToArray()).One(2).Message.OptionalVarint(1));

        using MemoryStream ids = new();
        await FeatureCollectionPbfWriter.WriteIdsAsync(ids, "objectid", [3, 1, 4_000_000_000], CancellationToken.None);
        IReadOnlyList<Field> listed = PbfReader.QueryResult(ids.ToArray()).One(3).Message;
        Assert.Equal("objectid", listed.One(1).Text);
        Assert.Equal([3UL, 1UL, 4_000_000_000UL], PbfReader.Packed(listed.One(3).Bytes));

        using MemoryStream extent = new();
        await FeatureCollectionPbfWriter.WriteExtentAsync(extent, new Envelope(1, 2, 3, 4), 7, 4326, CancellationToken.None);
        IReadOnlyList<Field> boxed = PbfReader.QueryResult(extent.ToArray()).One(4).Message;
        IReadOnlyList<Field> envelope = boxed.One(1).Message;
        Assert.Equal([1d, 2d, 3d, 4d], Enumerable.Range(1, 4).Select(n => envelope.One(n).AsDouble));
        Assert.Equal(7UL, boxed.OptionalVarint(2));
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("{\"mode\":\"sketch\"}", "mode")]
    [InlineData("{\"tolerance\":0}", "tolerance")]
    [InlineData("{\"originPosition\":\"middle\"}", "originPosition")]
    [InlineData("{\"extent\":{\"xmin\":0,\"ymin\":0,\"xmax\":1,\"ymax\":1,\"spatialReference\":{\"wkid\":4326}}}", "wkid 4326")]
    public void A_grid_that_cannot_be_used_is_refused_with_its_reason(string json, string reason)
    {
        Assert.False(PbfQuantization.TryParse(json, 3857, out _, out string? error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grid_places_its_origin_at_the_named_corner()
    {
        const string Extent = "\"extent\":{\"xmin\":10,\"ymin\":20,\"xmax\":30,\"ymax\":40,\"spatialReference\":{\"wkid\":102100}}";

        Assert.True(PbfQuantization.TryParse("{\"tolerance\":2," + Extent + "}", 3857, out PbfQuantization? upper, out _));
        Assert.True(PbfQuantization.TryParse("{\"mode\":\"edit\",\"originPosition\":\"lowerLeft\",\"tolerance\":2," + Extent + "}", 3857, out PbfQuantization? lower, out _));

        Assert.Equal((10d, 40d, true, true), (upper!.OriginX, upper.OriginY, upper.UpperLeft, upper.View));
        Assert.Equal((10d, 20d, false, false), (lower!.OriginX, lower.OriginY, lower.UpperLeft, lower.View));
        Assert.Equal(5, upper.Y(30));
        Assert.Equal(5, lower.Y(30));
    }

    /// <summary>One feature per shape, with one value of every kind the header declares.</summary>
    private sealed class Source(IReadOnlyList<Geometry?> shapes) : IFeatureSource
    {
        public static readonly DateTimeOffset When = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        public static readonly Guid Uid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        public FeatureSchema SchemaFor(FeatureQuery query) => new(Names);

        public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> CountUpToAsync(FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<Feature> ReadAsync(
            FeatureQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (int i = 0; i < shapes.Count && i < query.Limit; i++)
            {
                yield return new Feature(
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    shapes[i],
                    new FeatureSchema(Names),
                    [i, $"feature {i}", When, 9007199254740993L, true, Uid]);

                await Task.Yield();
            }
        }
    }
}
