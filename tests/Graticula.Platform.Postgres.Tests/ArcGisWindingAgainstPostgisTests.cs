using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Geometries;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Checks the ArcGIS writer's ring winding against PostGIS's own answer.
/// </summary>
/// <remarks>
/// <para>
/// ArcGIS requires exterior rings clockwise and interior rings
/// counter-clockwise. PostGIS guarantees neither, and the OSM corpus contains
/// both orientations — so the writer normalises, and a sign error there produces
/// polygons that render as holes with no error raised anywhere.
/// </para>
/// <para>
/// <b><c>ST_ForcePolygonCW</c> is the oracle.</b> It is PostGIS's own definition
/// of the orientation ArcGIS wants, so if our output disagrees with it on real
/// data, we are wrong. This is the strongest check available for a conversion
/// that has no reference implementation to compare against.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ArcGisWindingAgainstPostgisTests : PostgresFixture
{
    private readonly ITestOutputHelper _output;

    public ArcGisWindingAgainstPostgisTests(ITestOutputHelper output) => _output = output;

    private async Task RequireCorpusAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select to_regclass('public.planet_osm_polygon') is not null");

        Assert.True(
            (bool)(await command.ExecuteScalarAsync())!,
            "public.planet_osm_polygon is not loaded. This verifies ArcGIS ring winding against "
            + "PostGIS on real data and fails rather than skips.");
    }

    private static JsonElement WriteRings(Geometry geometry)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            ArcGisGeometryWriter.Write(writer, geometry, 3857);
        }

        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement.GetProperty("rings").Clone();
    }

    [Fact]
    public async Task Our_shell_winding_matches_ST_ForcePolygonCW_on_real_data()
    {
        await RequireCorpusAsync();

        // Both orientations appear in the corpus, so this exercises the reverse
        // branch and the pass-through branch without contriving either.
        await using NpgsqlCommand command = DataSource.CreateCommand(
            """
            select st_asbinary(way), st_asbinary(st_forcepolygoncw(way)), st_ispolygonccw(way)
            from public.planet_osm_polygon
            where way is not null and st_nrings(way) = 1
            order by osm_id
            limit 500
            """);

        int checkedRows = 0;
        int reversedByUs = 0;

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Polygon asStored = Assert.IsType<Polygon>(WkbReader.Read((byte[])reader[0]));
            Polygon forcedClockwise = Assert.IsType<Polygon>(WkbReader.Read((byte[])reader[1]));

            if (reader.GetBoolean(2))
            {
                reversedByUs++;
            }

            JsonElement rings = WriteRings(asStored);
            XySequence expected = forcedClockwise.Shell.Coordinates;

            Assert.Equal(expected.Count, rings[0].GetArrayLength());

            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected.X(i), rings[0][i][0].GetDouble());
                Assert.Equal(expected.Y(i), rings[0][i][1].GetDouble());
            }

            checkedRows++;
        }

        _output.WriteLine(
            $"{checkedRows} polygons checked; {reversedByUs} arrived counter-clockwise and were "
            + "reversed to meet ArcGIS's convention.");

        Assert.Equal(500, checkedRows);

        // If the corpus were uniformly wound, the reverse branch would never run
        // and this test would verify only half of what it claims to.
        Assert.True(
            reversedByUs > 0,
            "No polygon in the sample needed reversing, so the normalisation path was never "
            + "exercised and this proves only that pass-through works.");
    }

    [Fact]
    public async Task Our_hole_winding_is_the_opposite_of_our_shell_winding()
    {
        // ArcGIS distinguishes shell from hole by orientation alone, so the two
        // must genuinely differ — otherwise a client reading our output cannot
        // recover the structure our domain kept explicit.
        await RequireCorpusAsync();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            """
            select st_asbinary(way)
            from public.planet_osm_polygon
            where st_nrings(way) > 1
            order by osm_id
            limit 100
            """);

        int checkedRows = 0;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Polygon polygon = Assert.IsType<Polygon>(WkbReader.Read((byte[])reader[0]));
            JsonElement rings = WriteRings(polygon);

            double shellArea = SignedArea2(rings[0]);
            Assert.True(shellArea < 0, $"Shell should be clockwise; signed area was {shellArea}.");

            for (int ring = 1; ring < rings.GetArrayLength(); ring++)
            {
                double holeArea = SignedArea2(rings[ring]);
                Assert.True(
                    holeArea > 0,
                    $"Hole {ring} should be counter-clockwise; signed area was {holeArea}.");
            }

            checkedRows++;
        }

        Assert.True(checkedRows > 0, "No multi-ring polygons found, so this verified nothing.");
    }

    /// <summary>
    /// Twice the signed area of a written ring, computed from the JSON rather
    /// than from our own types — so a sign error in the domain cannot hide
    /// behind the same sign error here.
    /// </summary>
    private static double SignedArea2(JsonElement ring)
    {
        double sum = 0;
        int count = ring.GetArrayLength();

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            double xi = ring[i][0].GetDouble(), yi = ring[i][1].GetDouble();
            double xj = ring[j][0].GetDouble(), yj = ring[j][1].GetDouble();
            sum += (xj - xi) * (yj + yi);
        }

        return sum;
    }
}
