using System.Collections.Generic;
using System.Linq;
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
/// A query that asks for Z and M gets them back from a real column, through the SQL the ArcGIS
/// <c>query</c> sends — ADR-077, step 3.
/// </summary>
/// <remarks>
/// <b>Against PostGIS rather than a WKB fixture</b>, because the reader was already tested on bytes; what
/// this step had to prove is that nothing between the column and the reader — the transform to an output
/// reference, the simplification, the precision — flattens the shape before the reader sees it, and those
/// are database functions whose behaviour was measured, not assumed.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AQueryReturnsTheOrdinatesItAskedForTests : PostgresFixture
{
    private async Task<PostGisFeatureSource> LayerAsync(string name, string type, string wkt)
    {
        await using (NpgsqlCommand create = DataSource.CreateCommand(
            $"create table \"{SchemaName}\".\"{name}\" (objectid serial primary key, geom geometry({type}, 3857));"
            + $"insert into \"{SchemaName}\".\"{name}\" (geom) values (st_geomfromtext('{wkt}', 3857))"))
        {
            await create.ExecuteNonQueryAsync();
        }

        return new PostGisFeatureSource(DataSource, new LayerDefinition(
            name: name, schemaName: SchemaName, tableName: name, geometryColumn: "geom", srid: 3857,
            identityColumn: "objectid", integerIdentityColumn: "objectid", isHosted: true));
    }

    private static async Task<Geometry> OneAsync(PostGisFeatureSource source, FeatureQuery query)
    {
        List<Feature> features = [];

        await foreach (Feature feature in source.ReadAsync(query, CancellationToken.None))
        {
            features.Add(feature);
        }

        return Assert.Single(features).Geometry!;
    }

    [Fact]
    public async Task A_point_keeps_its_elevation_and_measure_when_asked()
    {
        PostGisFeatureSource source = await LayerAsync("q_point_zm", "PointZM", "POINT ZM (10 20 300 7)");

        Point asked = (Point)await OneAsync(source, new FeatureQuery(10)
        {
            KeepOrdinates = GeometryOrdinates.Z | GeometryOrdinates.M,
        });

        Assert.Equal(300, asked.Z);
        Assert.Equal(7, asked.M);

        // Not asked is exactly what it was before this step.
        Point flat = (Point)await OneAsync(source, new FeatureQuery(10));
        Assert.Null(flat.Z);
        Assert.Null(flat.M);
    }

    [Fact]
    public async Task A_line_keeps_only_the_ordinate_it_asked_for()
    {
        PostGisFeatureSource source =
            await LayerAsync("q_line_zm", "LineStringZM", "LINESTRING ZM (0 0 5 0, 100 0 6 50, 200 0 7 100)");

        LineString line = (LineString)await OneAsync(source, new FeatureQuery(10)
        {
            KeepOrdinates = GeometryOrdinates.M,
        });

        Assert.Equal(GeometryOrdinates.M, line.Coordinates.Ordinates);
        Assert.Equal([0.0, 50.0, 100.0], line.Coordinates.MSpan().ToArray());
    }

    /// <summary>
    /// An output reference and a generalization keep Z — the functions the query wraps the column in were
    /// measured to, and this is the measurement kept.
    /// </summary>
    [Fact]
    public async Task Z_survives_the_transform_the_simplification_and_the_precision()
    {
        PostGisFeatureSource source = await LayerAsync(
            "q_line_z", "LineStringZ", "LINESTRING Z (0 0 5, 100 1 6, 200 0 7, 300 0 8)");

        LineString line = (LineString)await OneAsync(source, new FeatureQuery(
            10, precision: 3, maxAllowableOffset: 0.0001, outSrid: 4326)
        {
            KeepOrdinates = GeometryOrdinates.Z,
        });

        Assert.True(line.Coordinates.HasZ);
        Assert.Equal(5, line.Coordinates.Z(0));
        Assert.Equal(8, line.Coordinates.Z(line.Coordinates.Count - 1));
        Assert.InRange(line.Coordinates.X(line.Coordinates.Count - 1), 0.002, 0.003);
    }
}
