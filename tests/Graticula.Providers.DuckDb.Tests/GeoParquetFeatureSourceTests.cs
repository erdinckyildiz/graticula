using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Testing;
using Xunit;

namespace Graticula.Providers.DuckDb.Tests;

/// <summary>
/// A layer served from a GeoParquet file — ADR-066.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grid is a hundred unit squares two units apart</b>, so which ids a box or a shape meets
/// can be worked out on paper: square <c>id</c> sits at column <c>(id-1) % 10</c> and row
/// <c>(id-1) / 10</c>, from <c>(2c, 2r)</c> to <c>(2c+1, 2r+1)</c>.
/// </para>
/// <para>
/// <b>PostGIS is the oracle for real data</b> in <c>GeoParquetAgainstPostgisTests</c>; this class
/// is the mechanism, including the parts the oracle cannot reach — refusals, the sandbox and a
/// file that changes under a published layer.
/// </para>
/// </remarks>
public sealed class GeoParquetFeatureSourceTests : IDisposable
{
    private readonly TemporaryFolder _temporary = new();
    private readonly GeoParquetFolder _folder;
    private readonly ShiftingProjector _projector = new();

    public GeoParquetFeatureSourceTests()
    {
        GeoParquetFixture.Write(_temporary.File("grid.parquet"), Shapes.GridColumns, Shapes.Grid(10), srid: 3857);
        _folder = new GeoParquetFolder(_temporary.Path, new GeoParquetOptions { MemoryLimit = "256MB", Threads = 2 });
    }

    public void Dispose()
    {
        _folder.Dispose();
        _temporary.Dispose();
    }

    private static readonly string[] Columns = ["objectid", "name", "kind", "area", "day", "score"];

    private GeoParquetFeatureSource Source(string identity = "objectid", string table = "grid") =>
        new(_folder, new LayerDefinition("grid", "main", table, "geom", 3857, identity, identity, isHosted: false), _projector);

    private static ParsedWhere Where(string clause)
    {
        Dictionary<string, FieldType> types = new() { ["day"] = FieldType.Date };

        Assert.True(
            WhereClause.TryParse(clause, Columns, n => $"\"{n}\"", out ParsedWhere parsed, out string? error, types),
            error);

        return parsed;
    }

    private static async Task<List<Feature>> ReadAsync(IFeatureSource source, FeatureQuery query)
    {
        List<Feature> features = [];

        await foreach (Feature feature in source.ReadAsync(query, CancellationToken.None))
        {
            features.Add(feature);
        }

        return features;
    }

    private static async Task<long[]> IdsAsync(IFeatureSource source, FeatureQuery query) =>
        [.. (await ReadAsync(source, query)).Select(f => long.Parse(f.Id, System.Globalization.CultureInfo.InvariantCulture))];

    // ---------- describing ----------

    [Fact]
    public async Task The_layer_describes_its_columns_its_extent_and_that_it_cannot_be_written()
    {
        LayerDescription described = await Source().DescribeAsync(CancellationToken.None);

        Assert.Equal(Columns, described.Fields.Select(f => f.Name));
        Assert.Equal(
            [FieldType.BigInteger, FieldType.Text, FieldType.Text, FieldType.Double, FieldType.Date, FieldType.Integer],
            described.Fields.Select(f => f.Type));
        Assert.Equal(new Envelope(0, 0, 19, 19), described.Extent);
        Assert.False(described.Writable);
    }

    [Fact]
    public void The_folder_lists_the_file_with_its_reference_kind_and_measured_identity()
    {
        GeoParquetTable table = Assert.Single(_folder.List());

        Assert.Null(table.Problem);
        Assert.Equal("grid", table.Name);
        Assert.Equal(100, table.Rows);
        Assert.Equal(3857, table.Geometry.Srid);
        Assert.Equal(GeometryKind.Polygon, table.Kind);
        Assert.Equal("geom_bbox", table.Geometry.Covering);
        Assert.Equal("objectid", table.CandidateObjectIdColumn);

        // `score` is an integer and repeats, so it is not offered; the row number always is.
        Assert.Equal(["objectid", GeoParquetFolder.RowNumberColumn], table.IdentityCandidates);
    }

    // ---------- reading ----------

    [Fact]
    public async Task A_read_returns_rows_in_identity_order_with_their_values_and_shapes()
    {
        List<Feature> features = await ReadAsync(Source(), new FeatureQuery(3, fields: Columns));

        Assert.Equal(["1", "2", "3"], features.Select(f => f.Id));
        Assert.Equal("p2", features[1]["name"]);
        Assert.Equal(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), features[1]["day"]);
        Assert.Equal(new Envelope(2, 0, 3, 1), features[1].Geometry!.Envelope);
    }

    [Theory]
    [InlineData("name = 'p42'", new long[] { 42 })]
    [InlineData("kind = 'field' and objectid < 10", new long[] { 3, 6, 9 })]
    [InlineData("name like 'p1_'", new long[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 })]
    [InlineData("objectid in (5, 50, 500)", new long[] { 5, 50 })]
    [InlineData("score between 5 and 6 and objectid <= 13", new long[] { 5, 6, 12, 13 })]
    [InlineData("day = '2026-01-08'", new long[] { 7 })]
    [InlineData("not (objectid > 2)", new long[] { 1, 2 })]
    [InlineData("name is null", new long[0])]
    public async Task A_where_clause_is_emitted_again_in_DuckDB_s_dialect(string clause, long[] expected)
    {
        Assert.Equal(expected, await IdsAsync(Source(), new FeatureQuery(1000, where: Where(clause))));
    }

    [Fact]
    public async Task A_where_clause_without_its_tree_is_refused_rather_than_pasted()
    {
        FeatureQuery query = new(10, where: new ParsedWhere("\"objectid\" = @w0", [1L]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => IdsAsync(Source(), query));
    }

    [Fact]
    public async Task A_bounding_box_meets_the_squares_it_touches_edges_included()
    {
        // From x 1 to 2 and y 0 to 0.5: the right edge of square 1 and the left edge of square 2.
        FeatureQuery query = new(1000, boundingBox: new Envelope(1, 0, 2, 0.5));

        long[] touched = await IdsAsync(Source(), query);
        Assert.Equal([1L, 2L], touched);
    }

    [Fact]
    public async Task Intersects_is_decided_by_the_shape_and_not_by_the_box()
    {
        // An L whose box covers squares 1, 2, 11 and 12 but whose shape misses square 12: it runs
        // along the bottom row and up the left column, leaving the notch at (2..3, 2..3) empty.
        Polygon ell = new(new LinearRing(XySequence.Wrap(
            [0.5, 0.5, 2.5, 0.5, 2.5, 1.5, 1.5, 1.5, 1.5, 2.5, 0.5, 2.5, 0.5, 0.5])));

        GeoParquetFeatureSource source = Source();

        long[] shaped = await IdsAsync(source, new FeatureQuery(1000, spatial: new SpatialFilter(ell)));
        Assert.Equal([1L, 2L, 11L], shaped);
        long[] boxed = await IdsAsync(source, new FeatureQuery(1000, spatial: new SpatialFilter(ell, SpatialRelation.EnvelopeIntersects)));
        Assert.Equal([1L, 2L, 11L, 12L], boxed);
        Assert.Equal(3, await source.CountAsync(new FeatureQuery(1, spatial: new SpatialFilter(ell)), CancellationToken.None));
    }

    [Fact]
    public async Task A_shape_that_meets_nothing_its_box_meets_answers_nothing_everywhere()
    {
        // Inside the gap between four squares.
        Polygon gap = Shapes.Square(1.2, 1.2, 1.8, 1.8);
        GeoParquetFeatureSource source = Source();
        FeatureQuery query = new(1000, spatial: new SpatialFilter(gap), statistics: [new StatisticRequest(StatisticKind.Count, "objectid", "n")]);

        Assert.Empty(await IdsAsync(source, query));
        Assert.Equal(0, await source.CountAsync(query, CancellationToken.None));
        Assert.Equal((null, 0L), await source.ExtentAsync(query, CancellationToken.None));
        Assert.Empty(await source.ObjectIdsAsync(query, CancellationToken.None));
        Assert.Equal(0L, (await source.StatisticsAsync(query, CancellationToken.None))[0]["n"]);
    }

    [Theory]
    [InlineData(SpatialRelation.Within)]
    [InlineData(SpatialRelation.Contains)]
    [InlineData(SpatialRelation.Touches)]
    [InlineData(SpatialRelation.Crosses)]
    [InlineData(SpatialRelation.Overlaps)]
    public async Task A_relation_this_layer_cannot_answer_is_refused_before_any_work(SpatialRelation relation)
    {
        FeatureQuery query = new(10, spatial: new SpatialFilter(Shapes.Square(0, 0, 1, 1), relation));
        GeoParquetFeatureSource source = Source();

        QueryNotSupportedException refused = Assert.Throws<QueryNotSupportedException>(() => source.SchemaFor(query));
        Assert.Contains(relation.ToString(), refused.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<QueryNotSupportedException>(() => source.CountAsync(query, CancellationToken.None));
        await Assert.ThrowsAsync<QueryNotSupportedException>(() => source.StatisticsAsync(query, CancellationToken.None));
    }

    [Fact]
    public void A_distance_is_refused_and_the_refusal_says_what_to_do_instead()
    {
        FeatureQuery query = new(10, spatial: new SpatialFilter(new Point(0, 0), Distance: 5));

        QueryNotSupportedException refused = Assert.Throws<QueryNotSupportedException>(() => Source().SchemaFor(query));
        Assert.Contains("buffer", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pages_ordered_by_a_repeating_column_cover_every_row_once()
    {
        // D-21's defect, on this provider: order by a column that ties, walk the pages.
        GeoParquetFeatureSource source = Source();
        List<long> seen = [];

        for (int offset = 0; offset < 100; offset += 7)
        {
            seen.AddRange(await IdsAsync(source, new FeatureQuery(7, fields: ["kind"], offset: offset, orderBy: [new SortKey("kind", false)])));
        }

        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), seen.Order());
    }

    [Fact]
    public async Task Distinct_returns_one_row_per_combination_and_counts_combinations()
    {
        GeoParquetFeatureSource source = Source();
        FeatureQuery query = new(100, fields: ["kind"], includeGeometry: false, distinct: true);

        List<Feature> kinds = await ReadAsync(source, query);

        Assert.Equal(["field", "forest", "water"], kinds.Select(f => (string)f["kind"]!));
        Assert.Equal(3, await source.CountAsync(query, CancellationToken.None));
    }

    // ---------- summaries ----------

    [Fact]
    public async Task Extent_ids_counts_and_statistics_describe_the_same_filtered_set()
    {
        GeoParquetFeatureSource source = Source();
        FeatureQuery query = new(1000, boundingBox: new Envelope(0, 0, 3.5, 3.5), where: Where("kind <> 'water'"));

        // The box meets squares 1, 2, 11 and 12; 2 and 11 are water.
        IReadOnlyList<long> ids = await source.ObjectIdsAsync(query, CancellationToken.None);
        Assert.Equal([1L, 12L], ids);
        Assert.Equal(2, await source.CountAsync(query, CancellationToken.None));
        Assert.Equal(1, await source.CountUpToAsync(query, 1, CancellationToken.None));
        Assert.Equal((new Envelope(0, 0, 3, 3), 2L), await source.ExtentAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task Statistics_group_order_and_compute_percentiles()
    {
        FeatureQuery query = new(
            10,
            groupBy: ["kind"],
            orderBy: [new SortKey("n", true)],
            statistics:
            [
                new StatisticRequest(StatisticKind.Count, "objectid", "n"),
                new StatisticRequest(StatisticKind.Sum, "area", "total"),
                new StatisticRequest(StatisticKind.PercentileContinuous, "area", "median", 0.5),
            ]);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
            await Source().StatisticsAsync(query, CancellationToken.None);

        // 34 forests (1, 4, … 100) come first; fields and water have 33 each, and their order is
        // the tie it is on PostgreSQL's path too, so only the first row is asserted.
        Assert.Equal("forest", rows[0]["kind"]);
        Assert.Equal(34L, rows[0]["n"]);
        Assert.Equal(1.5 * Enumerable.Range(0, 34).Select(i => 1 + (3 * i)).Sum(), Convert.ToDouble(rows[0]["total"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1.5 * 50.5, Convert.ToDouble(rows[0]["median"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(3, rows.Count);
    }

    // ---------- references and precision ----------

    [Fact]
    public async Task Output_is_projected_through_the_projector_and_a_filter_in_another_reference_is_brought_in()
    {
        GeoParquetFeatureSource source = Source();

        // The filter is stated in the fake 4326, a million to the right of the layer's 3857.
        FeatureQuery query = new(
            10,
            boundingBox: new Envelope(1_000_004, 0, 1_000_004.5, 0.5),
            outSrid: 4326,
            filterSrid: 4326);

        Feature only = Assert.Single(await ReadAsync(source, query));

        Assert.Equal("3", only.Id);
        Assert.Equal(new Envelope(1_000_004, 0, 1_000_005, 1), only.Geometry!.Envelope);
        Assert.Contains((4326, 3857, 1), _projector.Calls);
        Assert.Contains((3857, 4326, 1), _projector.Calls);

        Assert.Equal(
            (new Envelope(1_000_004, 0, 1_000_005, 1), 1L),
            await source.ExtentAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task A_tolerance_is_applied_in_the_output_reference_in_the_same_round_trip_as_the_projection()
    {
        // What the ArcGIS SDK sends for every tile of a polygon layer. Until 2026-09-13 this was
        // ignored and a tile of real areas carried 200,159 vertices where 1,808 would do.
        FeatureQuery projected = new(10, boundingBox: new Envelope(4, 0, 4.5, 0.5), outSrid: 4326, maxAllowableOffset: 5);

        Feature moved = Assert.Single(await ReadAsync(Source(), projected));

        Assert.Equal(new Envelope(1_000_004, 0, 1_000_005, 1), moved.Geometry!.Envelope);
        Assert.Contains((3857, 4326, 1, 5.0), _projector.Generalized);
        Assert.DoesNotContain(_projector.Calls, call => call.From == 3857 && call.To == 4326);

        // In the layer's own reference there is nothing to move, and the tolerance still applies.
        FeatureQuery local = new(10, boundingBox: new Envelope(4, 0, 4.5, 0.5), maxAllowableOffset: 2.5);

        Assert.Single(await ReadAsync(Source(), local));
        Assert.Contains((3857, 3857, 1, 2.5), _projector.Generalized);
    }

    [Fact]
    public void Precision_rounds_every_coordinate_and_changes_nothing_else()
    {
        Polygon shape = new(
            new LinearRing(XySequence.Wrap([0.123456, 0.5, 1.987654, 0.5, 1.987654, 2.25, 0.123456, 0.5])),
            [new LinearRing(XySequence.Wrap([0.51, 0.61, 0.71, 0.61, 0.71, 0.81, 0.51, 0.61]))]);

        Polygon rounded = (Polygon)GeoParquetFeatureSource.Round(shape, 1);

        Assert.Equal([0.1, 0.5, 2.0, 0.5, 2.0, 2.3, 0.1, 0.5], rounded.Shell.Coordinates.ToInterleavedArray());
        Assert.Equal([0.5, 0.6, 0.7, 0.6, 0.7, 0.8, 0.5, 0.6], rounded.Holes[0].Coordinates.ToInterleavedArray());
    }

    // ---------- files without an identity, and files that change ----------

    [Fact]
    public async Task A_file_with_no_unique_integer_column_is_served_by_row_number()
    {
        GeoParquetFixture.Write(
            _temporary.File("unnumbered.parquet"),
            [new("label", "VARCHAR"), new("rank", "INTEGER")],
            Enumerable.Range(0, 5).Select(i => (new object?[] { $"n{i}", i % 2 }, (Geometry?)new Point(i, i))));

        GeoParquetTable table = _folder.Find("unnumbered")!;
        Assert.Equal(GeoParquetFolder.RowNumberColumn, table.CandidateObjectIdColumn);

        GeoParquetFeatureSource source = new(
            _folder,
            new LayerDefinition("unnumbered", "main", "unnumbered", "geom", 4326, GeoParquetFolder.RowNumberColumn, GeoParquetFolder.RowNumberColumn, false),
            _projector);

        LayerDescription described = await source.DescribeAsync(CancellationToken.None);
        Assert.Equal([GeoParquetFolder.RowNumberColumn, "label", "rank"], described.Fields.Select(f => f.Name));

        IReadOnlyList<long> numbered = await source.ObjectIdsAsync(new FeatureQuery(10), CancellationToken.None);
        Assert.Equal([0L, 1L, 2L, 3L, 4L], numbered);
        IReadOnlyList<long> hit = await source.ObjectIdsAsync(
                new FeatureQuery(10, spatial: new SpatialFilter(Shapes.Square(2.5, 2.5, 3.5, 3.5))),
                CancellationToken.None);
        Assert.Equal([3L], hit);
    }

    [Fact]
    public async Task A_file_replaced_by_a_different_one_is_refused_with_what_changed()
    {
        GeoParquetFixture.Write(_temporary.File("moving.parquet"), Shapes.GridColumns, Shapes.Grid(2), srid: 3857);

        GeoParquetFeatureSource source = new(
            _folder, new LayerDefinition("moving", "main", "moving", "geom", 3857, "objectid", "objectid", false), _projector);

        string before = (await source.VersionOfAsync(1, CancellationToken.None))!;

        // Same name, another reference — the kind of replacement that would put every feature in
        // the wrong place if the layer kept its published SRID.
        File.SetLastWriteTimeUtc(_temporary.File("moving.parquet"), DateTime.UtcNow.AddMinutes(-5));
        GeoParquetFixture.Write(_temporary.File("moving.parquet"), Shapes.GridColumns, Shapes.Grid(2), srid: 2320);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.CountAsync(new FeatureQuery(1), CancellationToken.None));

        Assert.Contains("EPSG:2320", refused.Message, StringComparison.Ordinal);
        Assert.NotEqual(before, _folder.Find("moving")!.Version);
    }

    [Fact]
    public async Task CacheValidatorAsync_agrees_with_VersionOfAsync_and_changes_when_the_file_is_replaced()
    {
        // <b>ADR-069.</b> `CacheValidatorAsync` exists so a query response's ETag can be computed
        // without running the query; this asserts it answers the same thing `VersionOfAsync` does
        // — the file's own version — rather than drifting into a second, independently-wrong idea
        // of what "changed" means.
        GeoParquetFixture.Write(_temporary.File("cacheable.parquet"), Shapes.GridColumns, Shapes.Grid(2), srid: 3857);

        GeoParquetFeatureSource source = new(
            _folder, new LayerDefinition("cacheable", "main", "cacheable", "geom", 3857, "objectid", "objectid", false), _projector);

        string beforeVersion = (await source.VersionOfAsync(1, CancellationToken.None))!;
        string beforeValidator = (await source.CacheValidatorAsync(CancellationToken.None))!;
        Assert.Equal(beforeVersion, beforeValidator);

        File.SetLastWriteTimeUtc(_temporary.File("cacheable.parquet"), DateTime.UtcNow.AddMinutes(-5));
        GeoParquetFixture.Write(_temporary.File("cacheable.parquet"), Shapes.GridColumns, Shapes.Grid(3), srid: 3857);

        string afterValidator = (await source.CacheValidatorAsync(CancellationToken.None))!;

        // <b>Falsified: making `CacheValidatorAsync` return a constant instead of `Table().Version`
        // makes this fail</b> — confirmed by hand.
        Assert.NotEqual(beforeValidator, afterValidator);
    }

    [Fact]
    public async Task A_file_replaced_by_one_whose_identity_repeats_is_refused()
    {
        GeoParquetFixture.Write(_temporary.File("repeating.parquet"), Shapes.GridColumns, Shapes.Grid(2), srid: 3857);

        GeoParquetFeatureSource source = new(
            _folder, new LayerDefinition("repeating", "main", "repeating", "geom", 3857, "objectid", "objectid", false), _projector);

        Assert.Equal(4, await source.CountAsync(new FeatureQuery(1), CancellationToken.None));

        // The same columns and reference, and every row claiming objectid 1 — the replacement a
        // spatial filter would answer wrongly, because the identities it carries back match four rows.
        File.SetLastWriteTimeUtc(_temporary.File("repeating.parquet"), DateTime.UtcNow.AddMinutes(-5));
        GeoParquetFixture.Write(
            _temporary.File("repeating.parquet"),
            Shapes.GridColumns,
            Shapes.Grid(2).Select(row => (row.Values.Skip(1).Prepend(1L).ToArray(), row.Shape)),
            srid: 3857);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.CountAsync(new FeatureQuery(1), CancellationToken.None));

        Assert.Contains("no longer unique", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_is_named()
    {
        GeoParquetFeatureSource source = Source(table: "absent");

        FileNotFoundException missing = await Assert.ThrowsAsync<FileNotFoundException>(
            () => source.CountAsync(new FeatureQuery(1), CancellationToken.None));

        Assert.Contains("absent.parquet", missing.Message, StringComparison.Ordinal);
    }

    // ---------- the covering column's precision ----------

    [Fact]
    public async Task A_covering_box_rounded_inwards_does_not_lose_a_feature_at_its_edge()
    {
        // At 10,000,000 a float step is 1, so the covering corners of a square ending at
        // 10,000,000.4 round to 10,000,000 — inside the true edge. A query box starting at
        // 10,000,000.2 meets the square and misses its covering box.
        GeoParquetFixture.Write(
            _temporary.File("far.parquet"),
            [new("objectid", "BIGINT")],
            [(new object?[] { 1L }, (Geometry?)Shapes.Square(9_999_990, 0, 10_000_000.4, 10))],
            srid: 3857);

        GeoParquetFeatureSource source = new(
            _folder, new LayerDefinition("far", "main", "far", "geom", 3857, "objectid", "objectid", false), _projector);

        IReadOnlyList<long> atTheEdge = await source.ObjectIdsAsync(new FeatureQuery(10, boundingBox: new Envelope(10_000_000.2, 1, 10_000_010, 2)), CancellationToken.None);
        Assert.Equal([1L], atTheEdge);

        // And the other side: a box starting a tenth past the true edge passes the widened covering
        // test and misses the shape's own box, which is the exact test this process makes.
        IReadOnlyList<long> pastTheEdge = await source.ObjectIdsAsync(new FeatureQuery(10, boundingBox: new Envelope(10_000_000.5, 1, 10_000_010, 2)), CancellationToken.None);
        Assert.Empty(pastTheEdge);
        Assert.Equal(0, await source.CountAsync(new FeatureQuery(10, boundingBox: new Envelope(10_000_000.5, 1, 10_000_010, 2)), CancellationToken.None));
    }

    [Fact]
    public async Task A_file_without_a_covering_column_is_filtered_exactly_by_DuckDB()
    {
        GeoParquetFixture.Write(_temporary.File("uncovered.parquet"), Shapes.GridColumns, Shapes.Grid(10), srid: 3857, covering: false);

        Assert.Null(_folder.Find("uncovered")!.Geometry.Covering);

        GeoParquetFeatureSource source = Source(table: "uncovered");

        long[] touched = await IdsAsync(source, new FeatureQuery(1000, boundingBox: new Envelope(1, 0, 2, 0.5)));
        Assert.Equal([1L, 2L], touched);

        Polygon ell = new(new LinearRing(XySequence.Wrap(
            [0.5, 0.5, 2.5, 0.5, 2.5, 1.5, 1.5, 1.5, 1.5, 2.5, 0.5, 2.5, 0.5, 0.5])));

        long[] shaped = await IdsAsync(source, new FeatureQuery(1000, spatial: new SpatialFilter(ell)));
        Assert.Equal([1L, 2L, 11L], shaped);
    }

    [Fact]
    public async Task A_box_that_matches_more_than_the_bound_is_answered_by_DuckDB_rather_than_refused()
    {
        GeoParquetFeatureSource bounded = new(
            _folder,
            new LayerDefinition("grid", "main", "grid", "geom", 3857, "objectid", "objectid", false),
            _projector,
            null,
            mostMatched: 5);

        FeatureQuery everything = new(1000, boundingBox: new Envelope(-1, -1, 30, 30));

        Assert.Equal(100, await bounded.CountAsync(everything, CancellationToken.None));
        Assert.Equal(100, (await IdsAsync(bounded, everything)).Length);
    }

    // ---------- bounds (a security review's findings) ----------

    [Fact]
    public async Task A_query_past_its_statement_timeout_is_stopped_and_says_so()
    {
        // Enough rows that reading their shapes takes far longer than the bound.
        GeoParquetFixture.Write(
            _temporary.File("many.parquet"),
            [new("objectid", "BIGINT")],
            Enumerable.Range(1, 200_000).Select(i => (new object?[] { (long)i }, (Geometry?)new Point(i % 1000, i / 1000))),
            srid: 3857);

        GeoParquetFeatureSource source = new(
            _folder,
            new LayerDefinition("many", "main", "many", "geom", 3857, "objectid", "objectid", false),
            _projector,
            TimeSpan.FromMilliseconds(1));

        TimeoutException extent = await Assert.ThrowsAsync<TimeoutException>(
            () => source.ExtentAsync(new FeatureQuery(1), CancellationToken.None));
        Assert.Contains("'many'", extent.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<TimeoutException>(
            () => ReadAsync(source, new FeatureQuery(FeatureQuery.MaximumLimit)));

        // The caller leaving is not a timeout, and is not reported as one.
        using CancellationTokenSource gone = new();
        await gone.CancelAsync();

        GeoParquetFeatureSource patient = new(
            _folder, new LayerDefinition("many", "main", "many", "geom", 3857, "objectid", "objectid", false), _projector);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => patient.ExtentAsync(new FeatureQuery(1), gone.Token));
    }

    [Fact]
    public async Task A_spatial_filter_that_matches_more_than_the_bound_is_refused()
    {
        GeoParquetFeatureSource bounded = new(
            _folder,
            new LayerDefinition("grid", "main", "grid", "geom", 3857, "objectid", "objectid", false),
            _projector,
            null,
            mostMatched: 5);

        Polygon wide = Shapes.Square(-1, -1, 30, 30);

        QueryNotSupportedException refused = await Assert.ThrowsAsync<QueryNotSupportedException>(
            () => bounded.CountAsync(new FeatureQuery(1, spatial: new SpatialFilter(wide)), CancellationToken.None));

        Assert.Contains("more than 5", refused.Message, StringComparison.Ordinal);

        // Under the bound it answers.
        Assert.Equal(1, await bounded.CountAsync(
            new FeatureQuery(1, spatial: new SpatialFilter(Shapes.Square(0.2, 0.2, 0.8, 0.8))), CancellationToken.None));
    }

    [Fact]
    public async Task A_filter_geometry_with_more_vertices_than_the_bound_is_refused_before_any_work()
    {
        double[] coordinates = new double[(GeoParquetFeatureSource.MostFilterVertices + 1) * 2];

        for (int i = 0; i < coordinates.Length; i += 2)
        {
            coordinates[i] = i;
            coordinates[i + 1] = i % 7;
        }

        FeatureQuery query = new(1, spatial: new SpatialFilter(new LineString(XySequence.Wrap(coordinates))));

        Assert.Throws<QueryNotSupportedException>(() => Source().SchemaFor(query));
        await Assert.ThrowsAsync<QueryNotSupportedException>(() => Source().CountAsync(query, CancellationToken.None));
    }

    [Fact]
    public void A_linked_file_is_refused_rather_than_followed()
    {
        string outside = Path.Combine(Path.GetTempPath(), "graticula-linked-" + Guid.NewGuid().ToString("n")[..8] + ".parquet");
        File.Copy(_temporary.File("grid.parquet"), outside);

        try
        {
            try
            {
                File.CreateSymbolicLink(_temporary.File("linked.parquet"), outside);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Creating a link needs a privilege Windows does not give every account; the Linux
                // runners and the VPS exercise this.
                return;
            }

            GeoParquetTable table = _folder.Find("linked")!;

            Assert.Contains("symbolic link", table.Problem, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // ---------- the sandbox ----------

    [Fact]
    public void The_folder_s_DuckDB_cannot_read_outside_the_folder_or_unlock_itself()
    {
        string outside = Path.Combine(Path.GetTempPath(), "graticula-outside-" + Guid.NewGuid().ToString("n")[..8] + ".csv");
        File.WriteAllText(outside, "a\n1\n");

        try
        {
            using DuckDBConnection connection = _folder.Open();

            foreach (string attempt in new[]
            {
                $"select * from read_csv('{outside.Replace('\\', '/')}')",
                "set enable_external_access = true",
                "set allowed_directories = ['/']",
                "load spatial",
                "install spatial",
            })
            {
                using DuckDBCommand command = connection.CreateCommand();
                command.CommandText = attempt;

                Assert.ThrowsAny<DuckDBException>(() => command.ExecuteNonQuery());
            }
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Files_that_cannot_be_layers_are_listed_with_the_reason()
    {
        GeoParquetFixture.Write(_temporary.File("unknown_crs.parquet"), [new("objectid", "BIGINT")],
            [(new object?[] { 1L }, (Geometry?)new Point(1, 1))],
            geo: "{\"version\":\"1.1.0\",\"primary_column\":\"geom\",\"columns\":{\"geom\":{\"encoding\":\"WKB\",\"geometry_types\":[],\"crs\":null}}}");

        using (DuckDBConnection plain = new("DataSource=:memory:"))
        {
            plain.Open();
            using DuckDBCommand command = plain.CreateCommand();
            command.CommandText = $"copy (select 1 as a) to '{_temporary.File("plain.parquet").Replace('\\', '/')}' (format parquet)";
            command.ExecuteNonQuery();
        }

        File.Copy(_temporary.File("grid.parquet"), _temporary.File("has-dash.parquet"));

        Dictionary<string, string?> problems = _folder.List().ToDictionary(t => t.Name, t => t.Problem);

        Assert.Null(problems["grid"]);
        Assert.True(problems["unknown_crs"]!.Contains("unknown", StringComparison.Ordinal), problems["unknown_crs"]);
        Assert.Contains("not GeoParquet", problems["plain"], StringComparison.Ordinal);
        Assert.Contains("plain identifier", problems["has-dash"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../grid")]
    [InlineData("a/b")]
    [InlineData("x.parquet")]
    public void A_table_name_is_never_a_path(string name)
    {
        Assert.Throws<ArgumentException>(() => _folder.PathOf(name));
    }

    [Fact]
    public void Values_arrive_in_the_shapes_the_writers_expect()
    {
        Assert.Equal(new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc), GeoParquetFeatureSource.Normalise(new DateOnly(2026, 2, 3)));
        Assert.Equal((short)5, GeoParquetFeatureSource.Normalise((sbyte)5));
        Assert.Equal(7.0, GeoParquetFeatureSource.Normalise(new System.Numerics.BigInteger(7)));
        Assert.Equal(new byte[] { 1, 2 }, GeoParquetFeatureSource.Normalise(new MemoryStream([1, 2])));
        Assert.Equal("[1,2]", GeoParquetFeatureSource.Normalise(new List<int> { 1, 2 }));
        Assert.Null(GeoParquetFeatureSource.Normalise(DBNull.Value));
    }
}
