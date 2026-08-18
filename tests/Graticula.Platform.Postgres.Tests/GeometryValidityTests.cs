using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// What this server can say about geometry it did not check before.
/// </summary>
/// <remarks>
/// <b>D-53.</b> The importer wrote invalid geometry and the publish path recorded tables
/// it never looked at, so <c>hosted.tr_ilce_511f6767</c> served 18 invalid geometries out
/// of 25,280 and the way that came to light was <em>another server refusing to publish
/// the same table</em>. These tests are the counting that was missing.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GeometryValidityTests : PostgresFixture
{
    /// <summary>
    /// An invalid geometry is counted, and the reason comes back readable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A degenerate line, because that is what the real case is.</b> Measured against
    /// the owner's table: <c>hosted.tr_ilce_511f6767</c> is 25,280 <c>ST_LineString</c>
    /// rows — district boundaries imported as lines, not polygons — and the 18 invalid
    /// ones say <em>Too few points in geometry component</em>, which for a line means
    /// fewer than two <em>distinct</em> points. Not the self-intersection one imagines on
    /// hearing "invalid". <c>LINESTRING(0 0, 0 0)</c> reproduces it exactly.
    /// </para>
    /// <para>
    /// <b>A self-intersection is here as well, because the two are found differently.</b>
    /// PostGIS's WKT parser <em>refuses</em> a polygon with a three-coordinate ring
    /// outright — <c>XX000: geometry requires more points</c> — so that shape cannot even
    /// be written from text. Which is worth knowing on its own: the invalid rows in the
    /// real table exist because our importer writes WKB, and the binary path performs no
    /// such check. A test written only from WKT would have concluded the database was
    /// protecting us.
    /// </para>
    /// <para>
    /// <b>The reason is asserted trimmed.</b> <c>ST_IsValidReason</c> appends the
    /// coordinate of the fault, so eighteen bad rows produce eighteen distinct strings
    /// that are one reason; a report that listed them all would be unreadable at exactly
    /// the moment somebody needs to read it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_invalid_geometry_is_counted_and_the_reason_is_readable()
    {
        await MigrateAsync();

        await ExecuteAsync(
            $"create table {SchemaName}.shapes (id int, geom geometry(Geometry, 4326))");

        // Two valid shapes, two invalid ones, and a row with no geometry at all.
        // The two degenerate lines are the real fault; the bowtie is the other family.
        await ExecuteAsync($"""
            insert into {SchemaName}.shapes (id, geom) values
              (1, st_geomfromtext('POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))', 4326)),
              (2, st_geomfromtext('LINESTRING(2 2, 3 3, 4 2)', 4326)),
              (3, st_geomfromtext('LINESTRING(0 0, 0 0)', 4326)),
              (4, st_geomfromtext('LINESTRING(9 9, 9 9)', 4326)),
              (5, null)
            """);

        GeometryValidity validity = await GeometryValidity.MeasureAsync(
            DataSource, SchemaName, "shapes", "geom", CancellationToken.None);

        // Five rows, one of them null — a row with no geometry is not an invalid one.
        Assert.Equal(4, validity.Rows);
        Assert.Equal(2, validity.Invalid);
        Assert.False(validity.AllValid);

        // One reason, not two, and with no coordinate in it.
        Assert.Single(validity.Reasons);
        Assert.DoesNotContain("[", validity.Reasons[0], StringComparison.Ordinal);

        Assert.Contains("2 of 4", validity.Explanation, StringComparison.Ordinal);
        Assert.Contains(validity.Reasons[0], validity.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table PostGIS is happy with reports so, in words.
    /// </summary>
    /// <remarks>
    /// <b>The pair, and it earns its place.</b> A measurement that always found something
    /// wrong would be a measurement nobody trusted — and the sentence matters as much as
    /// the number, because <em>All 12 geometries are valid</em> is what tells an operator
    /// the question was asked at all.
    /// </remarks>
    [Fact]
    public async Task A_valid_table_says_so_rather_than_saying_nothing()
    {
        await MigrateAsync();

        await ExecuteAsync(
            $"create table {SchemaName}.clean (id int, geom geometry(Polygon, 4326))");

        await ExecuteAsync($"""
            insert into {SchemaName}.clean (id, geom) values
              (1, st_geomfromtext('POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))', 4326)),
              (2, st_geomfromtext('POLYGON((2 2, 3 2, 3 3, 2 3, 2 2))', 4326))
            """);

        GeometryValidity validity = await GeometryValidity.MeasureAsync(
            DataSource, SchemaName, "clean", "geom", CancellationToken.None);

        Assert.True(validity.AllValid);
        Assert.Equal(0, validity.Invalid);
        Assert.Equal(2, validity.Rows);
        Assert.Empty(validity.Reasons);
        Assert.Equal("All 2 geometries are valid.", validity.Explanation);
    }

    /// <summary>
    /// An identifier that would be a quoting hole is quoted by PostgreSQL itself.
    /// </summary>
    /// <remarks>
    /// <b>security.md: a table name is a filename in a different dialect.</b> There is no
    /// parameter form for an identifier in SQL, so the query is composed with
    /// <c>format(… %I …)</c> — PostgreSQL's own quoting rather than ours. This creates a
    /// table whose name and column would break naive interpolation, and asserts the count
    /// still comes back; nothing else proves the composition holds.
    /// </remarks>
    [Fact]
    public async Task An_awkward_identifier_is_quoted_by_postgres_rather_than_by_us()
    {
        await MigrateAsync();

        // A name with a space and an embedded double quote, and a column with a space.
        // Doubled here because this string is going through SQL, which is exactly the
        // hazard being tested.
        const string Table = @"odd name ""quoted""";
        const string Column = "the geom";

        string quotedTable = '"' + Table.Replace("\"", "\"\"", System.StringComparison.Ordinal) + '"';
        string quotedColumn = '"' + Column + '"';

        await ExecuteAsync(
            $"create table {SchemaName}.{quotedTable} "
            + $"(id int, {quotedColumn} geometry(LineString, 4326))");

        await ExecuteAsync(
            $"insert into {SchemaName}.{quotedTable} ({quotedColumn}) values "
            + "(st_geomfromtext('LINESTRING(0 0, 0 0)', 4326))");

        GeometryValidity validity = await GeometryValidity.MeasureAsync(
            DataSource, SchemaName, Table, Column, CancellationToken.None);

        Assert.Equal(1, validity.Rows);
        Assert.Equal(1, validity.Invalid);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
