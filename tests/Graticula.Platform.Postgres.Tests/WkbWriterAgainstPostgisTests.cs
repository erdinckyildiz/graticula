using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// The WKB writer, judged by PostGIS.
/// </summary>
/// <remarks>
/// <para>
/// <b>A round trip through our own reader proves almost nothing.</b> A writer
/// and a reader that share a misunderstanding agree with each other perfectly —
/// the same trap this project already avoided for the reader by testing it
/// against PostGIS on 6.5 million polygons rather than against a fixture we
/// wrote.
/// </para>
/// <para>
/// So the oracle is: read a real geometry from PostGIS, write it back out with
/// <see cref="WkbWriter"/>, hand the bytes to PostGIS, and ask PostGIS whether
/// the result is the same geometry. If our bytes are wrong in any way it
/// notices, they are wrong.
/// </para>
/// </remarks>
/// <remarks>
/// <b>Excluded from CI, deliberately and out loud — [ADR-048](../../docs/adr/ADR-048-ci-does-not-run-the-real-data-suites.md).</b>
/// This class reads a real OpenStreetMap extract, which a developer machine has and
/// CI does not. It fails rather than skips when the table is absent, which is the
/// right behaviour and is why CI cannot simply run it. The trait is what CI filters
/// on, and the workflow prints what it excluded so a green run never claims more
/// than it proved.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Corpus", "RealData")]
public sealed class WkbWriterAgainstPostgisTests : PostgresFixture
{
    private async Task RequireCorpusAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select to_regclass('public.planet_osm_polygon') is not null");

        Assert.True(
            (bool)(await command.ExecuteScalarAsync())!,
            "public.planet_osm_polygon is not loaded. These tests fail rather than skip.");
    }

    /// <summary>Reads geometries out, writes them back, asks PostGIS if they match.</summary>
    private async Task<(int Checked, List<string> Failures)> RoundTripAsync(string sourceSql)
    {
        List<(byte[] Original, Geometry Parsed)> read = [];

        await using (NpgsqlCommand source = DataSource.CreateCommand(sourceSql))
        await using (NpgsqlDataReader reader = await source.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                byte[] wkb = reader.GetFieldValue<byte[]>(0);
                read.Add((wkb, WkbReader.Read(wkb, out _)));
            }
        }

        Assert.NotEmpty(read);

        List<string> failures = [];

        foreach ((byte[] original, Geometry parsed) in read)
        {
            byte[] ours = WkbWriter.ToArray(parsed);

            // ST_OrderingEquals is the strict one: same geometry, same vertex
            // order, same structure. ST_Equals would call a polygon and its
            // reversal equal, which is exactly the mistake worth catching.
            const string Sql = """
                select
                  st_orderingequals(st_geomfromwkb(@ours), st_geomfromwkb(@original)),
                  st_astext(st_geomfromwkb(@ours)),
                  st_astext(st_geomfromwkb(@original))
                """;

            await using NpgsqlCommand check = DataSource.CreateCommand(Sql);
            check.Parameters.AddWithValue("ours", NpgsqlDbType.Bytea, ours);
            check.Parameters.AddWithValue("original", NpgsqlDbType.Bytea, original);

            await using NpgsqlDataReader verdict = await check.ExecuteReaderAsync();
            await verdict.ReadAsync();

            if (!verdict.GetBoolean(0))
            {
                failures.Add(
                    $"ours={Truncate(verdict.GetString(1))} original={Truncate(verdict.GetString(2))}");
            }
        }

        return (read.Count, failures);
    }

    [Fact]
    public async Task Polygons_survive_a_round_trip_through_our_reader_and_writer()
    {
        await RequireCorpusAsync();

        (int checked_, List<string> failures) = await RoundTripAsync(
            """
            select st_asbinary(way) from public.planet_osm_polygon
            where way is not null and st_geometrytype(way) = 'ST_Polygon'
            limit 400
            """);

        Assert.True(checked_ >= 100, $"only {checked_} polygons were available.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {checked_} differ:\n" + string.Join("\n", failures.GetRange(0, Math.Min(5, failures.Count))));
    }

    [Fact]
    public async Task Polygons_with_holes_survive_including_their_ring_order()
    {
        // The interesting case: ring count, ring order and per-ring vertex order
        // all have to come back the same, and ST_OrderingEquals checks all three.
        await RequireCorpusAsync();

        (int checked_, List<string> failures) = await RoundTripAsync(
            """
            select st_asbinary(way) from public.planet_osm_polygon
            where way is not null and st_nrings(way) > 1
            limit 200
            """);

        Assert.True(checked_ >= 20, $"only {checked_} polygons with holes were available.");
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Multi_geometries_we_assemble_from_real_parts_survive()
    {
        // <b>Assembled rather than found, and the reason is recorded because it
        // weakens the test.</b> This corpus is 100% ST_Polygon — osm2pgsql
        // splits multipolygons on import — so there is no real multipolygon to
        // round-trip. Rather than let the test pass on an empty result, the
        // parts are real geometries read from the corpus and the container is
        // ours. What is being judged is therefore our collection encoding, not
        // our handling of multipolygons somebody else wrote.
        await RequireCorpusAsync();

        List<Polygon> parts = [];

        await using (NpgsqlCommand source = DataSource.CreateCommand(
            "select st_asbinary(way) from public.planet_osm_polygon where way is not null limit 6"))
        await using (NpgsqlDataReader reader = await source.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                parts.Add((Polygon)WkbReader.Read(reader.GetFieldValue<byte[]>(0), out _));
            }
        }

        Assert.Equal(6, parts.Count);

        MultiPolygon multi = new(parts);

        await using NpgsqlCommand check = DataSource.CreateCommand(
            "select st_geometrytype(g), st_numgeometries(g) from (select st_geomfromwkb(@wkb) as g) s");
        check.Parameters.AddWithValue("wkb", NpgsqlDbType.Bytea, WkbWriter.ToArray(multi));

        await using NpgsqlDataReader verdict = await check.ExecuteReaderAsync();
        await verdict.ReadAsync();

        Assert.Equal("ST_MultiPolygon", verdict.GetString(0));
        Assert.Equal(6, verdict.GetInt32(1));

        // Every part must still be individually intact — a collection whose
        // count is right and whose members are shifted by one part boundary is
        // the classic offset bug, and st_numgeometries alone would not see it.
        for (int i = 0; i < parts.Count; i++)
        {
            await using NpgsqlCommand part = DataSource.CreateCommand(
                "select st_orderingequals(st_geometryn(st_geomfromwkb(@multi), @n), "
                + "st_geomfromwkb(@part))");
            part.Parameters.AddWithValue("multi", NpgsqlDbType.Bytea, WkbWriter.ToArray(multi));
            part.Parameters.AddWithValue("part", NpgsqlDbType.Bytea, WkbWriter.ToArray(parts[i]));
            part.Parameters.AddWithValue("n", i + 1);

            Assert.True((bool)(await part.ExecuteScalarAsync())!, $"part {i} does not match.");
        }
    }

    [Fact]
    public async Task Lines_and_points_survive()
    {
        await RequireCorpusAsync();

        foreach (string table in (string[])["public.planet_osm_line", "public.planet_osm_point"])
        {
            (int checked_, List<string> failures) = await RoundTripAsync(
                $"select st_asbinary(way) from {table} where way is not null limit 200");

            Assert.True(checked_ > 0, $"{table} yielded nothing.");
            Assert.Empty(failures);
        }
    }

    [Fact]
    public async Task Our_bytes_are_byte_identical_to_what_PostGIS_itself_emits()
    {
        // Stronger than "the same geometry": the same encoding. PostGIS emits
        // little-endian ISO WKB for 2D geometry, which is precisely what our
        // writer claims to produce — so for a 2D geometry read and written back
        // unchanged, the bytes should match exactly. Where they do not, one of
        // us is making a choice the other is not, and that is worth knowing even
        // when both parse.
        await RequireCorpusAsync();

        const string Sql = """
            select st_asbinary(way) from public.planet_osm_polygon
            where way is not null and st_geometrytype(way) = 'ST_Polygon'
            limit 200
            """;

        int identical = 0;
        int total = 0;

        await using NpgsqlCommand command = DataSource.CreateCommand(Sql);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            byte[] original = reader.GetFieldValue<byte[]>(0);
            total++;

            if (WkbWriter.ToArray(WkbReader.Read(original, out _)).AsSpan().SequenceEqual(original))
            {
                identical++;
            }
        }

        Assert.Equal(total, identical);
    }

    [Fact]
    public async Task A_geometry_we_build_ourselves_is_read_by_PostGIS_as_what_we_meant()
    {
        // The other direction: nothing round-tripped, just our bytes handed to
        // PostGIS cold. This is what the write path will actually do.
        await RequireCorpusAsync();

        Polygon square = new(
            new LinearRing(XySequence.Wrap([0, 0, 10, 0, 10, 10, 0, 10, 0, 0])),
            [new LinearRing(XySequence.Wrap([2, 2, 2, 4, 4, 4, 4, 2, 2, 2]))]);

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select st_geometrytype(g), st_nrings(g), st_area(g), st_isvalid(g) "
            + "from (select st_geomfromwkb(@wkb) as g) s");
        command.Parameters.AddWithValue("wkb", NpgsqlDbType.Bytea, WkbWriter.ToArray(square));

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal("ST_Polygon", reader.GetString(0));
        Assert.Equal(2, reader.GetInt32(1));

        // 100 for the shell less 4 for the hole. If the hole were written as a
        // second shell, or dropped, this is the number that would say so.
        Assert.Equal(96d, reader.GetDouble(2), 6);
        Assert.True(reader.GetBoolean(3), "PostGIS considers the geometry we wrote invalid.");
    }

    private static string Truncate(string text) =>
        text.Length <= 120 ? text : text[..120] + "…";
}
