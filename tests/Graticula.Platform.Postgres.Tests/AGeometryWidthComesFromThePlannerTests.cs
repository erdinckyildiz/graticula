using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// How wide a layer's geometries are is asked of the planner, and the answer is often absent.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-148](../../docs/open-questions.md): a preview's cost is in vertices and its bound
/// counted rows.</b> A row is not a unit of cost — one 500-vertex polygon outweighs sixty
/// 5-vertex ones — so the composition preview now divides a byte budget by what the geometry
/// column actually costs. This is the reading that makes that possible, and the reading is the
/// half that can be wrong without anything looking wrong.
/// </para>
/// <para>
/// <b>The width is exact on uniform data, and these tests assert the exact number.</b>
/// PostgreSQL's <c>avg_width</c> for a PostGIS polygon is <c>16 × points + 40</c> — checked
/// here against a 5-point square (120 bytes) and a 129-point circle (2,104), and against the
/// benchmark corpus where 5-, 50- and 500-vertex polygons read 136, 856 and 8,056. Asserting the
/// value rather than an inequality is what would catch the statistic quietly becoming the
/// *compressed* width, which is a different number and the wrong one: PostgreSQL analyses with
/// <c>toast_raw_datum_size</c>, and the raw size is what the database must read before it can
/// simplify anything.
/// </para>
/// <para>
/// <b>Most of this file is about the absences</b>, because they are what a freshly imported
/// layer has and a freshly imported layer is the one somebody is most likely to be previewing.
/// A table with neither an index nor an analysis reports no width at all and a row count of
/// <b>−1</b> — not a poor estimate, no estimate; an index sets the count and still no width. A
/// caller that read either of those as a number would bound the picture by nonsense.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AGeometryWidthComesFromThePlannerTests : PostgresFixture
{
    private const int Srid = 3857;

    /// <summary>
    /// The width separates a dense layer from a simple one, exactly.
    /// </summary>
    /// <remarks>
    /// <b>Two orders of magnitude between them on the same row count, which is the whole
    /// case.</b> Both tables hold 200 features and the rows say nothing about the difference; the
    /// widths say all of it.
    /// </remarks>
    [Fact]
    public async Task An_analysed_table_reports_the_width_of_its_geometries()
    {
        await MakeAsync("narrow", segments: 1);
        await MakeAsync("wide", segments: 32);
        await ExecuteAsync(
            $"analyze \"{SchemaName}\".\"narrow\"; analyze \"{SchemaName}\".\"wide\"");

        GeometryWidth narrow = await WidthAsync("narrow");
        GeometryWidth wide = await WidthAsync("wide");

        // 16 bytes a point plus a 40-byte header: a square is 5 points, and
        // st_buffer with 32 quadrant segments is 129.
        Assert.Equal(120, narrow.AverageBytes);
        Assert.Equal(2104, wide.AverageBytes);

        Assert.Equal(200, narrow.Rows);
        Assert.Equal(200, wide.Rows);

        Assert.Equal(120, narrow.PerFeatureBytes);
        Assert.Equal(2104, wide.PerFeatureBytes);
    }

    /// <summary>
    /// A table with neither an index nor an analysis says so, rather than guessing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>reltuples</c> is −1 and it is a sentinel, not an estimate.</b> A caller that divided
    /// a byte budget by <c>size ÷ −1</c> would get a negative width; one that took −1 as *almost
    /// no rows* would bound a large table by nothing at all. Both are the same defect — reading a
    /// sentinel as a measurement — and this test is what stands between them and the layer an
    /// operator has just imported.
    /// </para>
    /// <para>
    /// <b>So the honest answer is null, and the caller falls back to its row ceiling.</b> That is
    /// what this server did before the budget existed, which makes it the one fallback that
    /// cannot be a regression.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_table_nothing_has_looked_at_reports_no_width_at_all()
    {
        await MakeAsync("fresh", segments: 1, index: false);

        GeometryWidth fresh = await WidthAsync("fresh");

        Assert.Null(fresh.AverageBytes);
        Assert.Equal(-1, fresh.Rows);
        Assert.Null(fresh.PerFeatureBytes);
    }

    /// <summary>
    /// An index sets the row count and no column statistics, so the relation's size stands in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the state a layer is in after an import that builds a spatial index</b>, and it
    /// is why the fallback exists at all rather than being a curiosity: <c>CREATE INDEX</c>
    /// updates <c>pg_class.reltuples</c> and writes nothing to <c>pg_statistic</c>.
    /// </para>
    /// <para>
    /// <b>The fallback is inflated, deliberately unrepaired, and bounded in the safe
    /// direction.</b> It counts the indexes and every other column, so it reads high — which
    /// draws fewer features than the truth would allow rather than more. It is asserted here as a
    /// range rather than a number because the exact figure moves with the page fill and the index
    /// build; what must hold is that it is above the real width and still the right order of
    /// magnitude.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_index_sets_the_row_count_and_the_relation_size_stands_in_for_the_width()
    {
        await MakeAsync("indexed", segments: 1);

        GeometryWidth indexed = await WidthAsync("indexed");

        Assert.Null(indexed.AverageBytes);
        Assert.Equal(200, indexed.Rows);

        int? fallback = indexed.PerFeatureBytes;

        Assert.True(
            fallback is > 120 and < 1200,
            $"An indexed but unanalysed table fell back to {fallback} bytes a feature where the "
            + "true width is 120. Below it, the budget would draw more than it promised; an "
            + "order of magnitude above it, and every simple layer would be sampled for nothing.");
    }

    /// <summary>
    /// A view answers nothing rather than raising, which is a view telling the truth.
    /// </summary>
    /// <remarks>
    /// <b>Checked rather than assumed, because the alternative was an exception on a real layer
    /// kind.</b> A view is publishable here — <c>PostgresDataSourceProbe</c> lists it — and
    /// <c>pg_total_relation_size</c> on one returns 0 rather than refusing. A view has no
    /// statistics of its own, so the caller reads *no statistic* and draws to its row ceiling,
    /// which is exactly what it did for views before any of this existed.
    /// </remarks>
    [Fact]
    public async Task A_view_reports_nothing_and_does_not_refuse()
    {
        await MakeAsync("under", segments: 1);
        await ExecuteAsync(
            $"create view \"{SchemaName}\".\"over\" as select * from \"{SchemaName}\".\"under\"");

        GeometryWidth over = await WidthAsync("over");

        Assert.Null(over.AverageBytes);
        Assert.Null(over.PerFeatureBytes);
    }

    /// <summary>
    /// A relation that is not there is null rather than a zero-width one.
    /// </summary>
    /// <remarks>
    /// <b>Zero would be a width, and a width of zero divides a budget into infinity.</b> A table
    /// dropped between publishing and previewing is an ordinary race, and the answer to it here
    /// is *I know nothing*, which the caller already handles.
    /// </remarks>
    [Fact]
    public async Task A_relation_that_is_not_there_answers_nothing()
    {
        Assert.Null(
            await new PostGisFeatureSource(DataSource, Layer("absent"))
                .GeometryWidthAsync(CancellationToken.None));
    }

    // ---------- fixtures ----------

    private LayerDefinition Layer(string name) => new(
        name: name,
        schemaName: SchemaName,
        tableName: name,
        geometryColumn: "geom",
        srid: Srid,
        identityColumn: "objectid",
        integerIdentityColumn: "objectid",
        isHosted: true);

    private async Task<GeometryWidth> WidthAsync(string name) =>
        Assert.NotNull(
            await new PostGisFeatureSource(DataSource, Layer(name))
                .GeometryWidthAsync(CancellationToken.None));

    /// <summary>
    /// A table of 200 circles, as round as the caller asks for.
    /// </summary>
    /// <param name="name">The table.</param>
    /// <param name="segments">Quadrant segments: 1 is a square, 32 is 129 points.</param>
    /// <param name="index">
    /// Whether to build the spatial index. False leaves the table in the state a bare
    /// <c>insert</c> leaves it, which is the state with no row count at all.
    /// </param>
    /// <returns>The work.</returns>
    private async Task MakeAsync(string name, int segments, bool index = true)
    {
        // <b>No primary key, because a primary key is an index.</b> `create table (… primary
        // key)` builds one and sets `reltuples` with it, which would make the no-statistics case
        // unreachable — the case this file is mostly about.
        await ExecuteAsync(
            $"""
            create table "{SchemaName}"."{name}" (
                objectid integer not null,
                geom     geometry(Polygon, {Srid})
            );

            insert into "{SchemaName}"."{name}" (objectid, geom)
            select g, st_buffer(st_point(g * 100, g * 100, {Srid}), 50, {segments})
              from generate_series(1, 200) g;
            """);

        if (index)
        {
            await ExecuteAsync(
                $"create index \"{name}_geom_idx\" on \"{SchemaName}\".\"{name}\" using gist (geom)");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
