using System.Threading;
using System.Threading.Tasks;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Whether the reference a publisher declares is the one the table holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>D-156.</b> <c>POST /admin/layers</c> took <c>srid</c> from the request body, checked
/// that it was a positive integer, and never compared it with the column — while the probe
/// that listed the table had reported its real SRID a hundred lines earlier.
/// <c>geometry-crs-policy</c> §2 asks for exactly that override and, in the same breath, for
/// the mismatch to be detected and never published over silently. Only the override had
/// shipped.
/// </para>
/// <para>
/// <b>Why the failure is worth a test rather than a comment.</b> A layer published under the
/// wrong reference answers every request successfully: the extent is computed from it, the
/// tile envelope is transformed into it, <c>outSR</c> reprojects from it, and every one of
/// those is arithmetically correct and geographically wrong. There is no error to see
/// afterwards — which is §2's own sentence, <em>everything works and everything is in the
/// wrong place</em>, written before it was possible to make it happen.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DeclaredReferenceTests : PostgresFixture
{
    /// <summary>A declaration that matches the column is not complained about.</summary>
    /// <remarks>
    /// <b>The control, and it earns its place.</b> A check that refuses a correct publish is
    /// worse than no check: it would be found immediately by the person it obstructs, and the
    /// obvious repair is to delete it.
    /// </remarks>
    [Fact]
    public async Task A_declaration_that_matches_the_column_is_accepted()
    {
        await MigrateAsync();

        await ExecuteAsync(
            $"create table {SchemaName}.agreeing (id int, geom geometry(Point, 4326))");

        await ExecuteAsync(
            $"insert into {SchemaName}.agreeing values (1, ST_SetSRID(ST_Point(29.0, 41.0), 4326))");

        DeclaredReference? found = await DeclaredReference.MeasureAsync(
            DataSource, SchemaName, "agreeing", "geom", 4326, CancellationToken.None);

        Assert.NotNull(found);
        Assert.True(found!.Agrees);
        Assert.Null(found.Complaint);
        Assert.Equal(4326, found.Stored);
    }

    /// <summary>A declaration the column contradicts is named exactly.</summary>
    /// <remarks>
    /// <b>The certain half of the check.</b> The table says what it holds, so this needs no
    /// heuristic and admits no argument — and it is the case D-156 was opened on: 3857 asked
    /// for, 4326 stored, published without a word.
    /// </remarks>
    [Fact]
    public async Task A_declaration_the_column_contradicts_is_refused()
    {
        await MigrateAsync();

        await ExecuteAsync(
            $"create table {SchemaName}.disagreeing (id int, geom geometry(Point, 4326))");

        await ExecuteAsync(
            $"insert into {SchemaName}.disagreeing values (1, ST_SetSRID(ST_Point(29.0, 41.0), 4326))");

        DeclaredReference? found = await DeclaredReference.MeasureAsync(
            DataSource, SchemaName, "disagreeing", "geom", 3857, CancellationToken.None);

        Assert.NotNull(found);
        Assert.False(found!.Agrees);
        Assert.Contains("holds EPSG:4326", found.Complaint);
        Assert.Contains("asked for EPSG:3857", found.Complaint);
    }

    /// <summary>
    /// A table declared in degrees whose coordinates are metres is caught by the heuristic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case the exact check cannot see, and §2 names it first:</b> <em>a table
    /// declared 4326 holding projected metres</em>. The column's own SRID says 4326 and the
    /// publisher says 4326, so they agree — and the coordinates are Web Mercator metres, six
    /// orders of magnitude outside the domain of degrees.
    /// </para>
    /// <para>
    /// <b>Constructed with <c>ST_SetSRID</c> rather than <c>ST_Transform</c></b>, because
    /// transforming would produce degrees and there would be nothing to find. Mislabelling is
    /// the fault being reproduced, so the test has to mislabel.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Degrees_declared_over_metres_are_caught()
    {
        await MigrateAsync();

        await ExecuteAsync(
            $"create table {SchemaName}.mislabelled (id int, geom geometry(Point, 4326))");

        // Istanbul in Web Mercator metres, stamped 4326.
        await ExecuteAsync(
            $"insert into {SchemaName}.mislabelled values "
            + "(1, ST_SetSRID(ST_Point(3228000, 5010000), 4326))");

        DeclaredReference? found = await DeclaredReference.MeasureAsync(
            DataSource, SchemaName, "mislabelled", "geom", 4326, CancellationToken.None);

        Assert.NotNull(found);
        Assert.False(found!.Agrees);
        Assert.Contains("measured in degrees", found.Complaint);
        Assert.Contains("not degrees", found.Complaint);
    }

    /// <summary>
    /// A table declared projected whose coordinates could be degrees is reported.
    /// </summary>
    /// <remarks>
    /// <b>The weaker direction, and the test says so.</b> A projected extent inside ±180/±90
    /// is what degrees under a projected code look like — and it is also what a small survey
    /// near its own grid origin looks like. The check reports it and the publisher decides,
    /// which is why the message describes the shape rather than asserting the fault.
    /// </remarks>
    [Fact]
    public async Task Metres_declared_over_something_that_could_be_degrees_are_reported()
    {
        await MigrateAsync();

        await ExecuteAsync(
            $"create table {SchemaName}.suspicious (id int, geom geometry(Point, 3857))");

        await ExecuteAsync(
            $"insert into {SchemaName}.suspicious values "
            + "(1, ST_SetSRID(ST_Point(29.0, 41.0), 3857))");

        DeclaredReference? found = await DeclaredReference.MeasureAsync(
            DataSource, SchemaName, "suspicious", "geom", 3857, CancellationToken.None);

        Assert.NotNull(found);
        Assert.False(found!.Agrees);
        Assert.Contains("projected system", found.Complaint);
        Assert.Contains("degrees stored under a projected code", found.Complaint);
    }

    /// <summary>An empty table is not evidence of anything.</summary>
    /// <remarks>
    /// <b>Silence rather than a finding, because publishing an empty table is ordinary.</b> A
    /// hosted layer starts empty and is published before anything is written to it, so a check
    /// that refused one would refuse the normal first step of the product it is protecting.
    /// </remarks>
    [Fact]
    public async Task An_empty_table_is_not_a_finding()
    {
        await MigrateAsync();

        await ExecuteAsync(
            $"create table {SchemaName}.empty_one (id int, geom geometry(Point, 4326))");

        DeclaredReference? found = await DeclaredReference.MeasureAsync(
            DataSource, SchemaName, "empty_one", "geom", 3857, CancellationToken.None);

        Assert.NotNull(found);
        Assert.True(found!.Agrees);
        Assert.Null(found.Stored);
    }

    /// <summary>
    /// A reference the deployment's own register does not know is not called safe.
    /// </summary>
    /// <remarks>
    /// <b>Unknown is a third answer.</b> The heuristic needs to know whether the declared code
    /// is geographic, and it asks <c>spatial_ref_sys</c> rather than deciding from the number —
    /// a range of EPSG codes written into our source is a copy of somebody else's register that
    /// goes stale silently, which is the argument Q-123 makes against exactly that shortcut. A
    /// code the database does not carry cannot be checked either way, and the exact half still
    /// runs.
    /// </remarks>
    [Fact]
    public async Task An_unknown_reference_still_gets_the_exact_check()
    {
        await MigrateAsync();

        await ExecuteAsync(
            $"create table {SchemaName}.unknown_ref (id int, geom geometry(Point, 4326))");

        await ExecuteAsync(
            $"insert into {SchemaName}.unknown_ref values "
            + "(1, ST_SetSRID(ST_Point(29.0, 41.0), 4326))");

        // 999999 is in no spatial_ref_sys this deployment carries.
        DeclaredReference? found = await DeclaredReference.MeasureAsync(
            DataSource, SchemaName, "unknown_ref", "geom", 999999, CancellationToken.None);

        Assert.NotNull(found);
        Assert.False(found!.Agrees);
        Assert.Contains("holds EPSG:4326", found.Complaint);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
