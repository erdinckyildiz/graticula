using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A geometry sent in another reference is projected into the layer's, and a polygon that is not valid is
/// stored as the valid polygon made of it — Q-153, owner decision 2026-09-15.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EditGeometryIsProjectedAndRepairedTests : PostgresFixture
{
    private async Task<PostGisFeatureWriter> WriterAsync(string name, string type, GeometryKind kind)
    {
        await using (NpgsqlCommand create = DataSource.CreateCommand(
            $"create table \"{SchemaName}\".\"{name}\" (objectid serial primary key, label text, geom geometry({type}, 3857))"))
        {
            await create.ExecuteNonQueryAsync();
        }

        LayerDefinition layer = new(
            name: name, schemaName: SchemaName, tableName: name, geometryColumn: "geom", srid: 3857,
            identityColumn: "objectid", integerIdentityColumn: "objectid", isHosted: true);

        LayerDescription described = await new PostGisFeatureSource(DataSource, layer).DescribeAsync(CancellationToken.None);

        return new PostGisFeatureWriter(DataSource, layer, described.Fields, geometryKind: kind);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static Dictionary<string, object?> Label(string value) => new() { ["label"] = value };

    [Fact]
    public async Task A_point_sent_in_4326_is_stored_in_the_layer_s_3857()
    {
        PostGisFeatureWriter writer = await WriterAsync("geo_projected", "Point", GeometryKind.Point);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([new FeatureAdd(Label("istanbul"), new Point(29, 41), GeometrySrid: 4326)], [], []),
            CancellationToken.None);

        Assert.True(outcome.Adds[0].Succeeded, outcome.Adds[0].Error);

        double x = await ScalarAsync<double>($"select st_x(geom) from \"{SchemaName}\".geo_projected");
        Assert.Equal(3228251.4, x, 1);
    }

    [Fact]
    public async Task A_bow_tie_is_stored_valid_and_the_result_says_it_was_repaired()
    {
        PostGisFeatureWriter writer = await WriterAsync("geo_repaired", "MultiPolygon", GeometryKind.MultiPolygon);

        Polygon bowTie = new(new LinearRing(XySequence.Wrap([0, 0, 10, 10, 10, 0, 0, 10, 0, 0])));
        Polygon square = new(new LinearRing(XySequence.Wrap([20, 20, 20, 30, 30, 30, 30, 20, 20, 20])));

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([new FeatureAdd(Label("bow"), bowTie), new FeatureAdd(Label("square"), square)], [], []),
            CancellationToken.None);

        Assert.True(outcome.Adds[0].Succeeded, outcome.Adds[0].Error);
        Assert.True(outcome.Adds[0].GeometryRepaired);
        Assert.True(outcome.Adds[1].Succeeded, outcome.Adds[1].Error);
        Assert.False(outcome.Adds[1].GeometryRepaired);

        Assert.True(await ScalarAsync<bool>($"select bool_and(st_isvalid(geom)) from \"{SchemaName}\".geo_repaired"));
        Assert.Equal(100.0, await ScalarAsync<double>($"select st_area(geom) from \"{SchemaName}\".geo_repaired where label = 'square'"), 6);
    }
}
