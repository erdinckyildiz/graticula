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
/// A distance against a layer stored in degrees is measured in metres on the ellipsoid.
/// </summary>
/// <remarks>
/// Written 2026-09-15. It was refused on 4326 — on the showcase, most layers — and a layer in any
/// other geographic reference was sent the metres as degrees.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DistanceOnAGeographicLayerTests : PostgresFixture
{
    [Theory]
    [InlineData(4326)]
    [InlineData(4258)]
    public async Task Ten_kilometres_from_a_point_is_ten_kilometres_on_the_ground(int srid)
    {
        string table = "near_" + srid;

        // Ankara, a point about 5 km north of it and one about 50 km north. A degree of latitude
        // is about 111 km, so 0.045 and 0.45 degrees.
        await using (NpgsqlCommand create = DataSource.CreateCommand(
            $"""
             create table "{SchemaName}"."{table}" (
               objectid integer generated always as identity primary key,
               label text,
               geom geometry(Point, {srid}));
             insert into "{SchemaName}"."{table}" (label, geom) values
               ('five',  st_setsrid(st_makepoint(32.85, 39.975), {srid})),
               ('fifty', st_setsrid(st_makepoint(32.85, 40.38),  {srid}));
             """))
        {
            await create.ExecuteNonQueryAsync();
        }

        LayerDefinition layer = new(table, SchemaName, table, "geom", srid, "objectid", "objectid", isHosted: false);
        PostGisFeatureSource source = new(DataSource, layer);

        FeatureQuery query = new(
            10,
            fields: ["objectid", "label"],
            spatial: new SpatialFilter(new Point(32.85, 39.93), Distance: 10_000),
            filterSrid: srid);

        List<string> found = [];

        await foreach (Feature feature in source.ReadAsync(query, CancellationToken.None))
        {
            found.Add((string)feature[1]!);
        }

        Assert.Equal(["five"], found);
    }
}
