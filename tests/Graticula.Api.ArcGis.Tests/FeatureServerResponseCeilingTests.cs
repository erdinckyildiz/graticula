using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// A response body has a ceiling in bytes, and crossing it is reported as
/// <c>exceededTransferLimit</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Q-113.</b> <see cref="FeatureQuery.MaximumLimit"/> caps a page at 50,000 rows
/// and nothing capped their width. Measured against 25,280 real districts: one
/// request for 5,000 features with geometry and every field returns **4.2 MB**, so
/// the same request at the row cap is around forty — inside every limit this server
/// had. A row is not a unit of cost.
/// </para>
/// <para>
/// <b>The ceiling reuses the protocol's own signal rather than inventing one.</b>
/// ArcGIS clients already page on <c>exceededTransferLimit</c>; a body truncated by
/// size sets the same flag a body truncated by row count sets, so a client that
/// pages today needs no change. An error would have been a new contract and a silent
/// truncation would be worse than either.
/// </para>
/// </remarks>
public sealed class FeatureServerResponseCeilingTests
{
    private static readonly string[] Fields = ["objectid", "name"];

    private static LayerDefinition Layer() => new(
        name: "wide",
        schemaName: "public",
        tableName: "wide",
        geometryColumn: "geom",
        srid: 4326,
        identityColumn: "objectid",
        integerIdentityColumn: "objectid",
        isHosted: true);

    /// <summary>Writes the response and returns the parsed body.</summary>
    private static async Task<(JsonElement Body, int Bytes, int Written)> WriteAsync(
        long ceiling, int available)
    {
        FeatureServerQueryWriter writer = new(Layer(), ceiling);
        FakeSource source = new(available);

        using MemoryStream stream = new();
        int written;

        await using (Utf8JsonWriter json = new(stream))
        {
            written = await writer.WriteAsync(
                json,
                source,
                new FeatureQuery(available, fields: Fields),
                GeometryKind.Point,
                CancellationToken.None);
        }

        byte[] bytes = stream.ToArray();

        return (JsonDocument.Parse(bytes).RootElement, bytes.Length, written);
    }

    [Fact]
    public async Task A_ceiling_of_zero_is_no_ceiling()
    {
        // The behaviour of every build before this one, which is what makes the
        // change additive: an unset ceiling must not truncate anything.
        (JsonElement body, _, int written) = await WriteAsync(ceiling: 0, available: 200);

        Assert.Equal(200, written);
        Assert.Equal(200, body.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task Crossing_the_ceiling_truncates_and_says_so()
    {
        (JsonElement body, int bytes, int written) = await WriteAsync(ceiling: 2_000, available: 500);

        Assert.True(written < 500, $"Expected truncation, wrote all {written} features.");
        Assert.True(body.GetProperty("exceededTransferLimit").GetBoolean());

        // The overshoot is one feature at most: the check runs after writing, so the
        // body may pass the ceiling by the size of the feature that crossed it and
        // no more.
        Assert.True(bytes >= 2_000, $"Expected to reach the ceiling, got {bytes} bytes.");
    }

    [Fact]
    public async Task A_truncated_body_is_still_valid_json_with_its_header_intact()
    {
        // The property that makes truncation safe: the arrays and objects are closed
        // by breaking out of the loop rather than by abandoning the writer, so a
        // client parses the short answer exactly as it parses a full one.
        (JsonElement body, _, _) = await WriteAsync(ceiling: 1_500, available: 500);

        Assert.Equal("objectid", body.GetProperty("objectIdFieldName").GetString());
        Assert.Equal(4326, body.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("features").ValueKind);
    }

    [Fact]
    public async Task One_feature_is_always_returned_so_paging_can_advance()
    {
        // <b>A ceiling too small for the first feature must not produce an empty
        // page.</b> An empty page with exceededTransferLimit true is a paging loop
        // that never advances: the client asks for the next offset, gets nothing
        // again, and either spins or gives up. So the check runs after the first
        // write, unconditionally.
        (JsonElement body, _, int written) = await WriteAsync(ceiling: 1, available: 500);

        Assert.Equal(1, written);
        Assert.Equal(1, body.GetProperty("features").GetArrayLength());
        Assert.True(body.GetProperty("exceededTransferLimit").GetBoolean());
    }

    [Fact]
    public async Task A_body_under_the_ceiling_reports_the_row_limit_as_before()
    {
        // The flag keeps its original meaning when size was not the reason: a full
        // page still says exceededTransferLimit, and a short page still does not.
        (JsonElement full, _, _) = await WriteAsync(ceiling: 10_000_000, available: 10);
        Assert.True(full.GetProperty("exceededTransferLimit").GetBoolean());

        FeatureServerQueryWriter writer = new(Layer(), 10_000_000);
        FakeSource source = new(3);

        using MemoryStream stream = new();

        await using (Utf8JsonWriter json = new(stream))
        {
            await writer.WriteAsync(
                json, source, new FeatureQuery(10, fields: Fields),
                GeometryKind.Point, CancellationToken.None);
        }

        JsonElement partial = JsonDocument.Parse(stream.ToArray()).RootElement;

        Assert.Equal(3, partial.GetProperty("features").GetArrayLength());
        Assert.False(partial.GetProperty("exceededTransferLimit").GetBoolean());
    }

    /// <summary>
    /// Each geometry names the reference its coordinates are in, which under <c>outSR</c> is not
    /// the layer's.
    /// </summary>
    /// <remarks>
    /// Written 2026-09-15: a layer stored in 4326 queried with <c>outSR=3857</c> on the showcase
    /// answered a 3857 header and, inside every geometry, <c>{"wkid":4326}</c> beside coordinates
    /// in metres.
    /// </remarks>
    [Fact]
    public async Task Under_outSR_every_geometry_names_the_output_reference()
    {
        JsonElement body = await WriteQueryAsync(new FeatureQuery(3, fields: Fields, outSrid: 3857));

        Assert.Equal(3857, body.GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        foreach (JsonElement feature in body.GetProperty("features").EnumerateArray())
        {
            Assert.Equal(
                3857,
                feature.GetProperty("geometry").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        }
    }

    [Fact]
    public async Task Under_a_written_outSR_every_geometry_carries_the_definition()
    {
        const string Definition = "PROJCS[\"a written reference\"]";

        JsonElement body = await WriteQueryAsync(
            new FeatureQuery(2, fields: Fields, outSrid: 0) { OutWkt = Definition });

        foreach (JsonElement feature in body.GetProperty("features").EnumerateArray())
        {
            JsonElement reference = feature.GetProperty("geometry").GetProperty("spatialReference");

            Assert.Equal(Definition, reference.GetProperty("wkt").GetString());
            Assert.False(reference.TryGetProperty("wkid", out _), $"A written reference also carried a code: {reference}");
        }
    }

    [Fact]
    public async Task Without_outSR_every_geometry_names_the_layer_reference()
    {
        JsonElement body = await WriteQueryAsync(new FeatureQuery(2, fields: Fields));

        foreach (JsonElement feature in body.GetProperty("features").EnumerateArray())
        {
            Assert.Equal(
                4326,
                feature.GetProperty("geometry").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        }
    }

    /// <summary>
    /// A query response's <c>fields</c> carry the type, alias and length the layer document gives
    /// each column.
    /// </summary>
    /// <remarks>
    /// Written 2026-09-15: every column went out as <c>esriFieldTypeString</c> labelled with its
    /// own name, beside a layer document that said Integer and gave the operator's label.
    /// </remarks>
    [Fact]
    public async Task The_fields_header_says_what_the_layer_document_says()
    {
        FieldDescription[] described =
        [
            new("objectid", FieldType.Integer, false, null),
            new("name", FieldType.Text, true, 80, Alias: "Ad"),
        ];

        JsonElement body = await WriteQueryAsync(new FeatureQuery(1, fields: Fields), described);

        JsonElement[] fields = [.. body.GetProperty("fields").EnumerateArray()];

        Assert.Equal("esriFieldTypeOID", fields[0].GetProperty("type").GetString());
        Assert.Equal("esriFieldTypeString", fields[1].GetProperty("type").GetString());
        Assert.Equal("Ad", fields[1].GetProperty("alias").GetString());
        Assert.Equal(80, fields[1].GetProperty("length").GetInt32());
    }

    [Fact]
    public async Task A_numeric_column_is_declared_numeric()
    {
        string[] names = ["objectid", "name"];
        FieldDescription[] described =
        [
            new("objectid", FieldType.Integer, false, null),
            new("name", FieldType.Double, true, null),
        ];

        JsonElement body = await WriteQueryAsync(new FeatureQuery(1, fields: names), described);

        Assert.Equal(
            "esriFieldTypeDouble",
            body.GetProperty("fields")[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_column_the_description_does_not_carry_is_a_string_under_its_own_name()
    {
        JsonElement body = await WriteQueryAsync(
            new FeatureQuery(1, fields: Fields), [new("objectid", FieldType.Integer, false, null)]);

        JsonElement name = body.GetProperty("fields")[1];

        Assert.Equal("esriFieldTypeString", name.GetProperty("type").GetString());
        Assert.Equal("name", name.GetProperty("alias").GetString());
    }

    private static async Task<JsonElement> WriteQueryAsync(
        FeatureQuery query, IReadOnlyList<FieldDescription>? described = null)
    {
        FeatureServerQueryWriter writer = new(Layer(), 0, described);
        using MemoryStream stream = new();

        await using (Utf8JsonWriter json = new(stream))
        {
            await writer.WriteAsync(json, new FakeSource(query.Limit), query, GeometryKind.Point, CancellationToken.None);
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement;
    }

    [Fact]
    public void A_negative_ceiling_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeatureServerQueryWriter(Layer(), -1));
    }

    /// <summary>A source of identical points, as many as asked for.</summary>
    private sealed class FakeSource : IFeatureSource
    {
        private readonly int _count;

        public FakeSource(int count) => _count = count;

        public FeatureSchema SchemaFor(FeatureQuery query) => new(Fields);

        // Neither is on the path this test exercises: the writer streams from
        // ReadAsync and takes its header from SchemaFor. Throwing rather than
        // returning a plausible value means a future change that starts calling
        // one of them fails loudly here instead of asserting against a fixture
        // nobody wrote on purpose.
        public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The response writer does not describe.");

        public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The response writer does not count.");

        public Task<long> CountUpToAsync(
            FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The response writer does not count.");

        public async IAsyncEnumerable<Feature> ReadAsync(
            FeatureQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < _count && i < query.Limit; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                yield return new Feature(
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new Point(i, i),
                    new FeatureSchema(Fields),
                    [i, "a name long enough that a few hundred of them are measurable"]);

                await Task.Yield();
            }
        }
    }
}
