using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Geometries;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Domain → ArcGIS JSON → domain, judged by PostGIS.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is Q-90's hard requirement, made testable.</b> The question was
/// recorded as: <em>a tool that reads a MultiPolygon, nudges one vertex and
/// writes it back must not silently merge two parts.</em> ArcGIS carries a
/// polygon as a flat bag of rings with the part structure encoded in winding
/// order, so the reconstruction on the way back is where that merge would
/// happen — and it would happen quietly, producing a valid geometry that is not
/// the one the client sent.
/// </para>
/// <para>
/// <b>One thing the round trip legitimately changes, and it is not loss.</b>
/// ArcGIS requires shells clockwise and holes counter-clockwise; PostGIS stores
/// whatever it was given. So a polygon whose shell was counter-clockwise comes
/// back with its vertices reversed. The region is identical — orientation is not
/// meaningful in Simple Features — so the comparison is against
/// <c>ST_ForcePolygonCW</c> of the original rather than the original itself, and
/// that is stated here rather than hidden behind a looser assertion.
/// </para>
/// </remarks>
/// <remarks>
/// <b>Excluded from CI, deliberately and out loud — [ADR-048](../../docs/adr/ADR-048-ci-does-not-run-the-real-data-suites.md).</b>
/// This class reads <c>public.planet_osm_polygon</c>, a real OpenStreetMap extract on
/// a developer machine and nothing at all in CI. It fails rather than skips when the
/// table is absent, which is the right behaviour and is why CI cannot simply run it.
/// The trait is what CI filters on, and the workflow prints what it excluded so a
/// green run never claims more than it proved.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Corpus", "RealData")]
public sealed class ArcGisRoundTripAgainstPostgisTests : PostgresFixture
{
    private const int Srid = 3857;

    private async Task RequireCorpusAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select to_regclass('public.planet_osm_polygon') is not null");

        Assert.True(
            (bool)(await command.ExecuteScalarAsync())!,
            "public.planet_osm_polygon is not loaded. These tests fail rather than skip.");
    }

    /// <summary>Our geometry, out through the writer and back through the reader.</summary>
    private static Geometry RoundTrip(Geometry original)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            ArcGisGeometryWriter.Write(writer, original, Srid);
        }

        JsonElement json = JsonDocument.Parse(buffer.ToArray()).RootElement;

        Assert.True(
            ArcGisGeometryReader.TryRead(json, Srid, out Geometry? read, out string? error),
            $"the reader refused what the writer produced: {error}\n{Encoding.UTF8.GetString(buffer.ToArray())[..Math.Min(300, (int)buffer.Length)]}");

        return read!;
    }

    private async Task<List<Geometry>> ReadAsync(string sql)
    {
        List<Geometry> geometries = [];

        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            geometries.Add(WkbReader.Read(reader.GetFieldValue<byte[]>(0), out _));
        }

        return geometries;
    }

    /// <summary>Asks PostGIS whether the round trip changed the geometry.</summary>
    private async Task<(bool Same, string Detail)> CompareAsync(Geometry original, Geometry returned)
    {
        // ST_ForcePolygonCW on the original, because ArcGIS normalises winding
        // and that is a representation change rather than a loss. Everything
        // else — vertex order within a ring, ring order, part order, part count
        // — must be identical, which is what ST_OrderingEquals checks.
        const string Sql = """
            select
              st_orderingequals(st_geomfromwkb(@returned), st_forcepolygoncw(st_geomfromwkb(@original))),
              st_numgeometries(st_geomfromwkb(@original)),
              st_numgeometries(st_geomfromwkb(@returned)),
              st_nrings(st_geomfromwkb(@original)),
              st_nrings(st_geomfromwkb(@returned)),
              st_astext(st_geomfromwkb(@returned))
            """;

        await using NpgsqlCommand command = DataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("original", NpgsqlDbType.Bytea, WkbWriter.ToArray(original));
        command.Parameters.AddWithValue("returned", NpgsqlDbType.Bytea, WkbWriter.ToArray(returned));

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        bool same = reader.GetBoolean(0);
        int partsBefore = reader.GetInt32(1);
        int partsAfter = reader.GetInt32(2);
        int ringsBefore = reader.GetInt32(3);
        int ringsAfter = reader.GetInt32(4);

        return (
            same && partsBefore == partsAfter && ringsBefore == ringsAfter,
            $"parts {partsBefore}->{partsAfter}, rings {ringsBefore}->{ringsAfter}, "
            + $"ordering-equal {same}");
    }

    [Fact]
    public async Task Real_polygons_survive_the_round_trip_unchanged()
    {
        await RequireCorpusAsync();

        List<Geometry> originals = await ReadAsync(
            """
            select st_asbinary(way) from public.planet_osm_polygon
            where way is not null limit 300
            """);

        Assert.True(originals.Count >= 100);

        List<string> failures = [];

        foreach (Geometry original in originals)
        {
            (bool same, string detail) = await CompareAsync(original, RoundTrip(original));

            if (!same)
            {
                failures.Add(detail);
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.GetRange(0, Math.Min(5, failures.Count))));
    }

    [Fact]
    public async Task Polygons_with_holes_keep_every_hole_and_keep_it_in_its_own_shell()
    {
        // The reconstruction that would go wrong: a hole reattached to the
        // wrong shell, or promoted to a shell of its own. Ring counts catch the
        // second; ordering equality catches the first.
        await RequireCorpusAsync();

        List<Geometry> originals = await ReadAsync(
            """
            select st_asbinary(way) from public.planet_osm_polygon
            where way is not null and st_nrings(way) > 2 limit 150
            """);

        Assert.True(originals.Count >= 10, $"only {originals.Count} multi-ring polygons found.");

        foreach (Geometry original in originals)
        {
            (bool same, string detail) = await CompareAsync(original, RoundTrip(original));
            Assert.True(same, detail);
        }
    }

    [Fact]
    public async Task A_multipolygon_does_not_lose_its_part_boundaries()
    {
        // Q-90 in one test. Two polygons in, two polygons out — not one polygon
        // with four rings, which is what a reconstruction that ignored winding
        // would produce, and which is a perfectly valid geometry that is not the
        // one anybody sent.
        await RequireCorpusAsync();

        List<Geometry> parts = await ReadAsync(
            """
            select st_asbinary(way) from public.planet_osm_polygon
            where way is not null and st_nrings(way) > 1 limit 3
            """);

        Assert.Equal(3, parts.Count);

        MultiPolygon original = new([.. parts.ConvertAll(p => (Polygon)p)]);
        Geometry returned = RoundTrip(original);

        MultiPolygon typed = Assert.IsType<MultiPolygon>(returned);
        Assert.Equal(3, typed.Parts.Count);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(
                ((Polygon)parts[i]).Holes.Count,
                typed.Parts[i].Holes.Count);
        }

        (bool same, string detail) = await CompareAsync(original, returned);
        Assert.True(same, detail);
    }

    [Fact]
    public async Task Lines_and_points_survive_the_round_trip()
    {
        await RequireCorpusAsync();

        foreach (string table in (string[])["public.planet_osm_line", "public.planet_osm_point"])
        {
            List<Geometry> originals = await ReadAsync(
                $"select st_asbinary(way) from {table} where way is not null limit 150");

            Assert.NotEmpty(originals);

            foreach (Geometry original in originals)
            {
                (bool same, string detail) = await CompareAsync(original, RoundTrip(original));
                Assert.True(same, detail);
            }
        }
    }

    [Fact]
    public async Task A_single_path_returns_as_a_LineString_and_several_as_a_MultiLineString()
    {
        // ArcGIS calls both a Polyline. Collapsing a MultiLineString to a
        // LineString on the way back would be the linear equivalent of merging
        // polygon parts.
        await RequireCorpusAsync();

        List<Geometry> lines = await ReadAsync(
            "select st_asbinary(way) from public.planet_osm_line where way is not null limit 2");

        Assert.Equal(2, lines.Count);

        Assert.IsType<LineString>(RoundTrip(lines[0]));
        Assert.IsType<MultiLineString>(
            RoundTrip(new MultiLineString([(LineString)lines[0], (LineString)lines[1]])));
    }
}
