using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Formats;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A hosted layer updated from a file — ADR-103, condition 1 — against real PostGIS.
/// </summary>
/// <remarks>
/// <b>Each refusal is checked by the table afterwards, not by the exception.</b> The promise is that a file which
/// does not fit leaves the layer as it was; an overwrite that threw after its truncate had committed would throw
/// just the same and leave an empty layer.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostGisAppendTests : PostgresFixture
{
    private static ImportedDataset Parse(string json)
    {
        Assert.True(
            GeoJsonFeatures.TryRead(
                JsonDocument.Parse(json).RootElement, ImportLimits.Default, out ImportedDataset? dataset, out string? error),
            error);

        return dataset!;
    }

    private static string Square(double x, double y) =>
        $$"""{"type":"Polygon","coordinates":[[[{{x}},{{y}}],[{{x}},{{y + 0.0002}}],[{{x + 0.0003}},{{y + 0.0002}}],[{{x + 0.0003}},{{y}}],[{{x}},{{y}}]]]}""";

    private static readonly string Two = $$$"""
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{{{Square(28.978, 41.008)}}},"properties":{"name":"one","floors":3}},
          {"type":"Feature","geometry":{{{Square(28.979, 41.009)}}},"properties":{"name":"two","floors":5}}
        ]}
        """;

    private static readonly string OneMore = $$$"""
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{{{Square(28.980, 41.010)}}},"properties":{"name":"three","floors":7,"colour":"red"}}
        ]}
        """;

    private const string APoint = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[28.98,41.01]},"properties":{"name":"p"}}
        ]}
        """;

    private static readonly string NotANumber = $$$"""
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{{{Square(28.981, 41.011)}}},"properties":{"name":"four","floors":"many"}}
        ]}
        """;

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(PostGisImporter Importer, ImportResult Made, string Table)> MakeAsync()
    {
        PostGisImporter importer = new(DataSource);
        ImportResult made = await importer.ImportAsync(
            Parse(Two), "zzz_append_" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);

        return (importer, made, $"{made.SchemaName}.\"{made.TableName}\"");
    }

    [Fact]
    public async Task Append_adds_the_files_features_and_names_the_columns_it_left_out()
    {
        (PostGisImporter importer, ImportResult made, string table) = await MakeAsync();

        try
        {
            AppendResult result = await importer.AppendAsync(
                made.SchemaName, made.TableName, Parse(OneMore), replace: false, CancellationToken.None);

            Assert.Equal(1, result.Rows);
            Assert.Contains("colour", result.Ignored);
            Assert.Contains("floors", result.Matched);
            Assert.Equal(3L, await ScalarAsync<long>($"select count(*) from {table}"));
            Assert.Equal(7, await ScalarAsync<int>($"select floors from {table} where name = 'three'"));
            Assert.Equal(1L, await ScalarAsync<long>($"select count(*) from {table} where name = 'one'"));
        }
        finally
        {
            await importer.DropAsync(made.SchemaName, made.TableName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overwrite_replaces_every_feature_and_ids_keep_counting()
    {
        (PostGisImporter importer, ImportResult made, string table) = await MakeAsync();

        try
        {
            int highest = await ScalarAsync<int>($"select max(objectid) from {table}");

            AppendResult result = await importer.AppendAsync(
                made.SchemaName, made.TableName, Parse(OneMore), replace: true, CancellationToken.None);

            Assert.Equal(1, result.Rows);
            Assert.Equal(1L, await ScalarAsync<long>($"select count(*) from {table}"));
            Assert.True(await ScalarAsync<int>($"select objectid from {table}") > highest,
                "An overwrite gave a new feature an id a client may already have seen.");
        }
        finally
        {
            await importer.DropAsync(made.SchemaName, made.TableName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Points_into_a_polygon_layer_are_refused_and_nothing_changes()
    {
        (PostGisImporter importer, ImportResult made, string table) = await MakeAsync();

        try
        {
            await Assert.ThrowsAsync<AppendRefusedException>(() => importer.AppendAsync(
                made.SchemaName, made.TableName, Parse(APoint), replace: true, CancellationToken.None));

            Assert.Equal(2L, await ScalarAsync<long>($"select count(*) from {table}"));
        }
        finally
        {
            await importer.DropAsync(made.SchemaName, made.TableName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_overwrite_whose_value_will_not_cast_leaves_the_layer_as_it_was()
    {
        (PostGisImporter importer, ImportResult made, string table) = await MakeAsync();

        try
        {
            PostgresException refused = await Assert.ThrowsAsync<PostgresException>(() => importer.AppendAsync(
                made.SchemaName, made.TableName, Parse(NotANumber), replace: true, CancellationToken.None));

            Assert.StartsWith("22", refused.SqlState, StringComparison.Ordinal);

            // The truncate ran before the insert failed; it must have gone with the transaction.
            Assert.Equal(2L, await ScalarAsync<long>($"select count(*) from {table}"));
        }
        finally
        {
            await importer.DropAsync(made.SchemaName, made.TableName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_file_in_another_reference_is_transformed_into_the_layers()
    {
        PostGisImporter importer = new(DataSource);

        ImportResult made = await importer.DefineAsync(
            [new Graticula.Features.FieldDescription("name", Graticula.Features.FieldType.Text, true, null)],
            Graticula.Geometries.GeometryKind.Polygon,
            3857,
            "zzz_append_" + Guid.NewGuid().ToString("N")[..8],
            CancellationToken.None);

        string table = $"{made.SchemaName}.\"{made.TableName}\"";

        try
        {
            AppendResult result = await importer.AppendAsync(
                made.SchemaName, made.TableName, Parse(OneMore), replace: false, CancellationToken.None);

            Assert.True(result.Transformed);
            Assert.Equal(3857, await ScalarAsync<int>($"select ST_SRID(geom) from {table}"));

            // 28.98° east is about 3.2 million metres in Web Mercator; left in degrees it would be 28.98.
            Assert.True(await ScalarAsync<double>($"select ST_XMin(geom) from {table}") > 3_000_000);
        }
        finally
        {
            await importer.DropAsync(made.SchemaName, made.TableName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_table_outside_the_hosted_schema_is_refused()
    {
        PostGisImporter importer = new(DataSource);

        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.AppendAsync(
            "public", "anything", Parse(OneMore), replace: true, CancellationToken.None));
    }
}
