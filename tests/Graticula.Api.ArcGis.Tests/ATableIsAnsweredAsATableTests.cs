using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// A layer with no geometry is answered as ArcGIS answers a standalone table — ADR-213.
/// </summary>
/// <remarks>
/// <b>Each assertion is a shape read off Esri's own sample server</b> (<c>ServiceRequest/FeatureServer</c> and its table
/// <c>1</c>, 2026-10-21), not a shape this server chose: a client tells a table from a layer by these documents.
/// </remarks>
public sealed class ATableIsAnsweredAsATableTests
{
    private static LayerDefinition Table() => new(
        name: "inspections",
        schemaName: "hosted",
        tableName: "inspections",
        geometryColumn: null,
        srid: LayerDefinition.TableSrid,
        identityColumn: "objectid",
        integerIdentityColumn: "objectid",
        isHosted: true);

    private static LayerDescription Description() =>
        new(
            [
                new FieldDescription("objectid", FieldType.Integer, false, null),
                new FieldDescription("site_code", FieldType.Text, true, 10),
                new FieldDescription("score", FieldType.SmallInteger, true, null),
            ],
            null);

    private static JsonElement Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    [Fact]
    public void A_definition_with_no_geometry_column_is_a_table_and_says_which_reference_it_carries()
    {
        LayerDefinition table = Table();

        Assert.True(table.IsTable);
        Assert.Null(table.GeometryColumn);

        // Nothing reads it; it is there because the catalogue's srid is not null (ADR-213 §5c).
        Assert.Equal(4326, table.Srid);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => table.RequireGeometryColumn());
        Assert.Contains("inspections", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tables_document_says_Table_and_has_none_of_a_layers_geometry_keys()
    {
        JsonElement document = Json(FeatureServerMetadataWriter.Layer(
            Table(), geometryType: null, Description(), "Query,Create,Update,Delete,Editing", layerId: 1));

        Assert.Equal("Table", document.GetProperty("type").GetString());
        Assert.Equal(1, document.GetProperty("id").GetInt32());
        Assert.Equal("objectid", document.GetProperty("objectIdField").GetString());

        foreach (string key in (string[])[
            "geometryType", "extent", "drawingInfo", "minScale", "maxScale", "hasZ", "hasM", "allowGeometryUpdates",
            "supportedSpatialRelationships", "sourceSpatialReference"])
        {
            Assert.False(document.TryGetProperty(key, out _), $"A table's document carries '{key}', which ArcGIS's does not.");
        }

        // It is still queried and edited as a layer is, and its template draws nothing.
        Assert.True(document.GetProperty("supportsStatistics").GetBoolean());
        Assert.False(document.GetProperty("supportsReturningQueryExtent").GetBoolean());
        Assert.Equal(
            "esriFeatureEditToolNone",
            document.GetProperty("templates")[0].GetProperty("drawingTool").GetString());
    }

    [Fact]
    public void A_table_whose_relationships_are_unknown_is_still_a_table()
    {
        // The other dictionary path (ADR-026 condition 3) must not undo the narrowing.
        JsonElement document = Json(FeatureServerMetadataWriter.Layer(
            Table(), geometryType: null, Description(), "Query", relationshipsKnown: false));

        Assert.Equal("Table", document.GetProperty("type").GetString());
        Assert.False(document.GetProperty("relationshipsKnown").GetBoolean());
        Assert.False(document.TryGetProperty("geometryType", out _));
    }

    [Fact]
    public void A_service_lists_its_tables_apart_from_its_layers_in_one_numbering()
    {
        FeatureServerMetadataWriter.ServiceLayer sites =
            new(0, "sites", GeometryKind.Point, 4326, new Envelope(28, 36, 45, 42));

        JsonElement service = Json(FeatureServerMetadataWriter.Service(
            [sites], "Query", tables: [new FeatureServerMetadataWriter.ServiceTable(1, "inspections")]));

        Assert.Equal(0, service.GetProperty("layers")[0].GetProperty("id").GetInt32());

        JsonElement table = service.GetProperty("tables").EnumerateArray().Single();
        Assert.Equal(1, table.GetProperty("id").GetInt32());
        Assert.Equal("inspections", table.GetProperty("name").GetString());

        // The tables add nothing to the extent, which is the layers' alone.
        Assert.Equal(28, service.GetProperty("fullExtent").GetProperty("xmin").GetDouble());
    }

    [Fact]
    public async Task A_tables_query_answer_names_no_geometry_type_and_no_reference()
    {
        FeatureServerQueryWriter writer = new(Table(), 0);
        FeatureQuery query = new(10, fields: ["objectid", "site_code"]);

        using MemoryStream stream = new();

        await using (Utf8JsonWriter json = new(stream))
        {
            await writer.WriteAsync(json, new Rows(), query, geometryType: null, CancellationToken.None);
        }

        JsonElement body = JsonDocument.Parse(stream.ToArray()).RootElement;

        Assert.False(body.TryGetProperty("geometryType", out _));
        Assert.False(body.TryGetProperty("spatialReference", out _));
        Assert.Equal("objectid", body.GetProperty("objectIdFieldName").GetString());

        JsonElement feature = body.GetProperty("features")[0];
        Assert.Equal("A1", feature.GetProperty("attributes").GetProperty("site_code").GetString());
        Assert.False(feature.TryGetProperty("geometry", out _));
    }

    /// <summary>Two rows with no shape, as a table's source answers.</summary>
    private sealed class Rows : IFeatureSource
    {
        private static readonly string[] Fields = ["objectid", "site_code"];

        public FeatureSchema SchemaFor(FeatureQuery query) => new(Fields);

        public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The response writer does not describe.");

        public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The response writer does not count.");

        public Task<long> CountUpToAsync(FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The response writer does not count.");

        public async IAsyncEnumerable<Feature> ReadAsync(
            FeatureQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach ((int id, string code) in new[] { (1, "A1"), (2, "B2") })
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new Feature(id.ToString(System.Globalization.CultureInfo.InvariantCulture), null, new FeatureSchema(Fields), [id, code]);
            }

            await Task.CompletedTask;
        }
    }
}
