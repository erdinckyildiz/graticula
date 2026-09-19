using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A hosted table keeps the Z and M every imported feature carries, and a defined one declares what it is
/// asked to — ADR-080, closing D-107.
/// </summary>
/// <remarks>
/// Against real PostGIS, because what matters is the column's declared type and what the rows hold after the
/// binary COPY and the <c>ST_GeomFromWKB</c> that follows it — both of which a fake would take on trust.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AnImportKeepsWhatEveryFeatureCarriesTests : PostgresFixture
{
    private static ImportedDataset Parse(string json)
    {
        Assert.True(
            GeoJsonFeatures.TryRead(JsonDocument.Parse(json).RootElement, ImportLimits.Default, out ImportedDataset? dataset, out string? error),
            error);

        return dataset!;
    }

    private async Task<string?> TextAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task<string?> DeclaredAsync(ImportResult result) => await TextAsync(
        $"select postgis_typmod_type(a.atttypmod) from pg_attribute a where a.attrelid = '\"{result.SchemaName}\".\"{result.TableName}\"'::regclass and a.attname = 'geom'");

    [Fact]
    public async Task A_file_whose_features_all_carry_an_elevation_makes_a_z_table()
    {
        PostGisImporter importer = new(DataSource);
        ImportResult result = await importer.ImportAsync(Parse("""
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[28.97,41.00,120.5]},"properties":{"name":"a"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[28.98,41.01,95.25]},"properties":{"name":"b"}}
            ]}
            """), "zimport_all", CancellationToken.None);

        try
        {
            Assert.Equal(GeometryOrdinates.Z, result.Stored);
            Assert.Equal(0, result.Flattened);
            Assert.Equal("PointZ", await DeclaredAsync(result));
            Assert.Equal(
                "POINT Z (28.97 41 120.5)",
                await TextAsync($"select st_astext(geom) from \"{result.SchemaName}\".\"{result.TableName}\" where name = 'a'"));
        }
        finally
        {
            await importer.DropAsync(result.SchemaName, result.TableName, CancellationToken.None);
        }
    }

    /// <summary>
    /// Some features with Z and some without: the table keeps what they share and counts the rest, rather than
    /// inventing a height for the features that had none.
    /// </summary>
    [Fact]
    public async Task A_file_where_only_some_carry_it_keeps_what_they_share_and_counts_the_rest()
    {
        PostGisImporter importer = new(DataSource);
        ImportResult result = await importer.ImportAsync(Parse("""
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[28.97,41.00,120.5]},"properties":{"name":"a"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[28.98,41.01]},"properties":{"name":"b"}}
            ]}
            """), "zimport_some", CancellationToken.None);

        try
        {
            Assert.Equal(GeometryOrdinates.None, result.Stored);
            Assert.Equal(1, result.Flattened);
            Assert.Equal("Point", await DeclaredAsync(result));
        }
        finally
        {
            await importer.DropAsync(result.SchemaName, result.TableName, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(GeometryOrdinates.Z, "MultiLineStringZ")]
    [InlineData(GeometryOrdinates.M, "MultiLineStringM")]
    [InlineData(GeometryOrdinates.Z | GeometryOrdinates.M, "MultiLineStringZM")]
    public async Task A_defined_layer_declares_the_ordinates_it_was_asked_for(GeometryOrdinates ordinates, string expected)
    {
        PostGisImporter importer = new(DataSource);
        ImportResult result = await importer.DefineAsync(
            new List<FieldDescription> { new("name", FieldType.Text, true, null) },
            GeometryKind.MultiLineString,
            3857,
            $"zdefine_{(int)ordinates}",
            ordinates,
            CancellationToken.None);

        try
        {
            Assert.Equal(expected, await DeclaredAsync(result));
        }
        finally
        {
            await importer.DropAsync(result.SchemaName, result.TableName, CancellationToken.None);
        }
    }
}
