using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Formats;
using GisServer.Providers.PostGis;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// Turning an uploaded file into a table, against real PostGIS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not against a fake.</b> Everything that can go wrong here is PostgreSQL
/// behaviour — a binary COPY that disagrees with the column types, a transaction
/// that leaves a table behind, a geometry column that rejects a mixed type, a
/// reprojection that silently does nothing. None of it appears against an
/// in-memory double.
/// </para>
/// <para>
/// Each test drops what it made. A failing test that leaks a table makes the
/// next run fail for a different reason.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostGisImporterTests : PostgresFixture
{
    private static ImportedDataset Parse(string json)
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

    /// <summary>Two Istanbul buildings with three properties of three types.</summary>
    private const string Sample = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature",
           "geometry":{"type":"Polygon","coordinates":[
             [[28.9780,41.0080],[28.9780,41.0082],[28.9783,41.0082],[28.9783,41.0080],[28.9780,41.0080]]]},
           "properties":{"name":"one","floors":3,"height":12.5}},
          {"type":"Feature",
           "geometry":{"type":"Polygon","coordinates":[
             [[28.9790,41.0090],[28.9790,41.0092],[28.9793,41.0092],[28.9793,41.0090],[28.9790,41.0090]]]},
           "properties":{"name":"two","floors":5,"height":20.0}}
        ]}
        """;

    private async Task DropAsync(ImportResult result) =>
        await new PostGisImporter(DataSource)
            .DropAsync(result.SchemaName, result.TableName, CancellationToken.None);

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    // ---------- the happy path ----------

    [Fact]
    public async Task An_imported_file_becomes_a_table_with_its_rows_in_it()
    {
        ImportResult result = await new PostGisImporter(DataSource)
            .ImportAsync(Parse(Sample), "test import", CancellationToken.None);

        try
        {
            Assert.Equal(2, result.Rows);
            Assert.Equal(PostGisImporter.HostedSchema, result.SchemaName);

            Assert.Equal(2L, await ScalarAsync<long>(
                $"select count(*) from {result.SchemaName}.\"{result.TableName}\""));
        }
        finally
        {
            await DropAsync(result);
        }
    }

    [Fact]
    public async Task The_column_types_are_the_ones_inference_decided()
    {
        ImportResult result = await new PostGisImporter(DataSource)
            .ImportAsync(Parse(Sample), "types", CancellationToken.None);

        try
        {
            string types = await ScalarAsync<string>(
                $"""
                 select string_agg(column_name || ':' || data_type, ', ' order by ordinal_position)
                 from information_schema.columns
                 where table_schema = '{result.SchemaName}' and table_name = '{result.TableName}'
                 """);

            Assert.Contains("name:text", types, StringComparison.Ordinal);
            Assert.Contains("floors:integer", types, StringComparison.Ordinal);
            Assert.Contains("height:double precision", types, StringComparison.Ordinal);

            // The import column exists only during the load. Left behind, it
            // would double the table's size with a copy of every geometry.
            Assert.DoesNotContain("import_wkb", types, StringComparison.Ordinal);
        }
        finally
        {
            await DropAsync(result);
        }
    }

    [Fact]
    public async Task The_geometry_is_stored_in_Web_Mercator_and_lands_where_it_should()
    {
        // <b>The check that the reprojection happened and was right.</b> A
        // transform that silently did nothing leaves 4326 degrees in a column
        // declared 3857, which every later query treats as metres near the
        // origin — off the coast of Africa.
        ImportResult result = await new PostGisImporter(DataSource)
            .ImportAsync(Parse(Sample), "projected", CancellationToken.None);

        try
        {
            Assert.Equal(4326, result.SourceSrid);
            Assert.Equal(PostGisImporter.StoredSrid, result.StoredSrid);
            Assert.False(string.IsNullOrWhiteSpace(result.ProjEngine));

            // Istanbul in Web Mercator is about 3,225,000 / 5,013,000. Degrees
            // left untransformed would be about 29 / 41.
            double x = await ScalarAsync<double>(
                $"select ST_X(ST_Centroid(geom)) from {result.SchemaName}.\"{result.TableName}\" limit 1");

            Assert.InRange(x, 3_200_000, 3_250_000);
        }
        finally
        {
            await DropAsync(result);
        }
    }

    [Fact]
    public async Task The_table_declares_the_geometry_type_the_file_had()
    {
        ImportResult result = await new PostGisImporter(DataSource)
            .ImportAsync(Parse(Sample), "typed geometry", CancellationToken.None);

        try
        {
            Assert.Equal("POLYGON", await ScalarAsync<string>(
                $"""
                 select type from geometry_columns
                 where f_table_schema = '{result.SchemaName}'
                   and f_table_name = '{result.TableName}'
                 """));
        }
        finally
        {
            await DropAsync(result);
        }
    }

    [Fact]
    public async Task A_spatial_index_exists_so_the_first_tile_is_not_a_sequential_scan()
    {
        ImportResult result = await new PostGisImporter(DataSource)
            .ImportAsync(Parse(Sample), "indexed", CancellationToken.None);

        try
        {
            Assert.Equal(1L, await ScalarAsync<long>(
                $"""
                 select count(*) from pg_indexes
                 where schemaname = '{result.SchemaName}'
                   and tablename = '{result.TableName}'
                   and indexdef like '%gist%'
                 """));
        }
        finally
        {
            await DropAsync(result);
        }
    }

    [Fact]
    public async Task Statistics_exist_so_the_layer_publishes_with_a_real_extent()
    {
        // Without ANALYZE, ST_EstimatedExtent returns nothing and the service
        // document carries a whole-world extent — which looks like a broken
        // layer rather than a missing statistic.
        ImportResult result = await new PostGisImporter(DataSource)
            .ImportAsync(Parse(Sample), "analyzed", CancellationToken.None);

        try
        {
            Assert.False(await ScalarAsync<bool>(
                $"""
                 select ST_EstimatedExtent(
                   '{result.SchemaName}', '{result.TableName}', 'geom') is null
                 """));
        }
        finally
        {
            await DropAsync(result);
        }
    }

    // ---------- names ----------

    [Fact]
    public async Task Two_imports_of_the_same_name_do_not_collide()
    {
        // The suffix is not decoration: without it the second import either
        // fails or, far worse, is asked whether to replace the first.
        PostGisImporter importer = new(DataSource);

        ImportResult first = await importer.ImportAsync(Parse(Sample), "roads", CancellationToken.None);
        ImportResult second = await importer.ImportAsync(Parse(Sample), "roads", CancellationToken.None);

        try
        {
            Assert.NotEqual(first.TableName, second.TableName);
            Assert.StartsWith("roads_", first.TableName, StringComparison.Ordinal);
        }
        finally
        {
            await DropAsync(first);
            await DropAsync(second);
        }
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("drop table students; --")]
    [InlineData("\"; delete from layer; --")]
    [InlineData("Ünïcödé Ǹámé")]
    [InlineData("...")]
    [InlineData("9lives")]
    public async Task A_hostile_or_awkward_name_produces_a_legal_identifier(string name)
    {
        // security.md: filenames are data, never paths — and a table name is the
        // same problem in a different dialect. The name the caller chose is the
        // service's; the table it lives in is derived here.
        ImportResult result = await new PostGisImporter(DataSource)
            .ImportAsync(Parse(Sample), name, CancellationToken.None);

        try
        {
            Assert.Matches("^[a-z][a-z0-9_]*$", result.TableName);
            Assert.True(result.TableName.Length <= 63, "PostgreSQL truncates at 63 bytes");

            Assert.Equal(2L, await ScalarAsync<long>(
                $"select count(*) from {result.SchemaName}.\"{result.TableName}\""));
        }
        finally
        {
            await DropAsync(result);
        }
    }

    // ---------- what it refuses to do ----------

    [Fact]
    public async Task Dropping_a_table_outside_the_hosted_schema_is_refused()
    {
        // The one thing this must never do is drop a table in a registered
        // customer database because a catalogue row said so.
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PostGisImporter(DataSource)
                .DropAsync("public", "planet_osm_polygon", CancellationToken.None));

        Assert.Contains("hosted", refused.Message, StringComparison.Ordinal);

        // And the table is still there.
        Assert.True(await ScalarAsync<bool>(
            "select to_regclass('public.planet_osm_polygon') is not null"));
    }

    [Fact]
    public async Task A_feature_with_no_geometry_imports_as_a_null_row()
    {
        ImportResult result = await new PostGisImporter(DataSource).ImportAsync(
            Parse("""
                {"type":"FeatureCollection","features":[
                  {"type":"Feature",
                   "geometry":{"type":"Point","coordinates":[28.97,41.00]},
                   "properties":{"n":1}},
                  {"type":"Feature","geometry":null,"properties":{"n":2}}
                ]}
                """),
            "with a gap",
            CancellationToken.None);

        try
        {
            Assert.Equal(2, result.Rows);

            Assert.Equal(1L, await ScalarAsync<long>(
                $"select count(*) from {result.SchemaName}.\"{result.TableName}\" where geom is null"));
        }
        finally
        {
            await DropAsync(result);
        }
    }
}
