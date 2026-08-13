using System;
using System.Threading.Tasks;
using GisServer.Geometries;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// Checks <see cref="WkbReader"/> against PostGIS's own answers, on real data.
/// </summary>
/// <remarks>
/// <para>
/// <b>PostGIS is the oracle here, and that is the point.</b> Hand-built WKB
/// tests verify the reader against my understanding of the format; these verify
/// it against an independent implementation that has been reading this format
/// for twenty years. If the two disagree, the reader is wrong.
/// </para>
/// <para>
/// The corpus is the Turkish OpenStreetMap extract already loaded for
/// <c>benchmarks/mvt-generation</c> — 6,499,215 polygons, up to 215,488 vertices
/// each. Real data has the shapes a fixture never does: holes, near-duplicate
/// vertices, and one polygon large enough to have shaped three benchmark rounds.
/// </para>
/// <para>
/// These need the OSM table and are skipped by name if it is absent, because a
/// developer with a bare PostgreSQL should still be able to run the rest of the
/// integration suite.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class WkbAgainstPostgisTests : PostgresFixture
{
    private const string Table = "public.planet_osm_polygon";

    /// <summary>
    /// Asserts the corpus is present rather than returning quietly if it is not.
    /// </summary>
    /// <remarks>
    /// The first draft of this class returned early when the table was missing,
    /// which would have turned every test below into a green tick that verified
    /// nothing. That is the fourth instrument this project has caught in that
    /// state, and the pattern is always the same: the absence of the subject
    /// reads as success. If you do not have the corpus, exclude this class by
    /// name; do not let it pass.
    /// </remarks>
    private async Task RequireCorpusAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"select to_regclass('{Table}') is not null");

        Assert.True(
            (bool)(await command.ExecuteScalarAsync())!,
            $"{Table} is not loaded. These tests verify the WKB reader against PostGIS on real "
            + "data and are the strongest validation of it we have, so they fail rather than "
            + "skip. Load the corpus with experiments/_env, or exclude this class by name.");
    }

    [Fact]
    public async Task Our_vertex_count_matches_PostGIS_across_a_sample()
    {
        await RequireCorpusAsync();

        // Ordered by osm_id so the sample is deterministic: a failure is
        // reproducible rather than a story about a random row.
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"""
             select st_asbinary(way), st_npoints(way),
                    st_xmin(way), st_ymin(way), st_xmax(way), st_ymax(way)
             from {Table}
             where way is not null
             order by osm_id
             limit 2000
             """);

        int checkedRows = 0;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            byte[] wkb = (byte[])reader[0];
            int expectedPoints = reader.GetInt32(1);
            Envelope expected = new(
                reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5));

            Geometry geometry = WkbReader.Read(wkb, out bool dropped);

            Assert.False(dropped);
            Assert.Equal(expectedPoints, geometry.CoordinateCount);
            Assert.Equal(expected, geometry.Envelope);

            checkedRows++;
        }

        Assert.Equal(2000, checkedRows);
    }

    [Fact]
    public async Task The_largest_polygon_in_the_corpus_reads_correctly()
    {
        // 215,488 vertices. The shape that made finding 11 — a tile's cost floor
        // is set by the largest geometry overlapping it, not by its content.
        await RequireCorpusAsync();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"""
             select st_asbinary(way), st_npoints(way), st_nrings(way)
             from {Table}
             where way is not null
             order by st_npoints(way) desc
             limit 1
             """);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        byte[] wkb = (byte[])reader[0];
        int expectedPoints = reader.GetInt32(1);
        int expectedRings = reader.GetInt32(2);

        Polygon polygon = Assert.IsType<Polygon>(WkbReader.Read(wkb));

        Assert.Equal(expectedPoints, polygon.CoordinateCount);
        Assert.Equal(expectedRings, 1 + polygon.Holes.Count);
        Assert.True(expectedPoints > 200_000, $"Expected the corpus's largest, got {expectedPoints}.");
    }

    [Fact]
    public async Task Polygons_with_holes_read_shell_and_holes_the_way_PostGIS_counts_them()
    {
        await RequireCorpusAsync();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"""
             select st_asbinary(way), st_nrings(way), st_npoints(st_exteriorring(way))
             from {Table}
             where st_nrings(way) > 2
             order by osm_id
             limit 200
             """);

        int checkedRows = 0;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Polygon polygon = Assert.IsType<Polygon>(WkbReader.Read((byte[])reader[0]));

            Assert.Equal(reader.GetInt32(1), 1 + polygon.Holes.Count);
            Assert.Equal(reader.GetInt32(2), polygon.Shell.CoordinateCount);

            checkedRows++;
        }

        Assert.True(checkedRows > 0, "No multi-ring polygons found, so this verified nothing.");
    }

    [Fact]
    public async Task Ring_winding_matches_PostGIS()
    {
        // Winding drives shell-versus-hole classification when converting to
        // ArcGIS (ADR-005 §3.3c), and a sign error there produces inside-out
        // polygons that render as holes. PostGIS's ST_IsPolygonCCW is the oracle.
        await RequireCorpusAsync();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"""
             select st_asbinary(way), st_ispolygonccw(way)
             from {Table}
             where way is not null
             order by osm_id
             limit 500
             """);

        int checkedRows = 0;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Polygon polygon = Assert.IsType<Polygon>(WkbReader.Read((byte[])reader[0]));

            Assert.Equal(reader.GetBoolean(1), polygon.Shell.IsCounterClockwise);
            checkedRows++;
        }

        Assert.Equal(500, checkedRows);
    }

    [Fact]
    public async Task An_EWKB_buffer_from_PostGIS_reads_the_same_as_its_WKB()
    {
        // ST_AsEWKB carries the SRID in the type's high bits; ST_AsBinary does
        // not. Both appear depending on which function a query used, so both
        // must land on the same geometry.
        await RequireCorpusAsync();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"select st_asbinary(way), st_asewkb(way) from {Table} order by osm_id limit 50");

        int checkedRows = 0;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Geometry fromWkb = WkbReader.Read((byte[])reader[0]);
            Geometry fromEwkb = WkbReader.Read((byte[])reader[1]);

            Assert.Equal(fromWkb.CoordinateCount, fromEwkb.CoordinateCount);
            Assert.Equal(fromWkb.Envelope, fromEwkb.Envelope);
            checkedRows++;
        }

        Assert.Equal(50, checkedRows);
    }
}
