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
        objectIdColumn: "objectid",
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
