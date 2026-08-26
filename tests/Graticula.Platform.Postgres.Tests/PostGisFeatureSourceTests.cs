using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// The first read path, against 6.5 million real polygons.
/// </summary>
/// <remarks>
/// <b>Excluded from CI, deliberately and out loud — [ADR-048](../../docs/adr/ADR-048-ci-does-not-run-the-real-data-suites.md).</b>
/// This class reads <c>public.planet_osm_polygon</c>, a real OpenStreetMap extract on
/// a developer machine and nothing at all in CI. It fails rather than skips when the
/// table is absent, which is the right behaviour and is why CI cannot simply run it.
/// The trait is what CI filters on, and the workflow prints what it excluded so a
/// green run never claims more than it proved.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Needs", "RealCorpus")]
public sealed class PostGisFeatureSourceTests : PostgresFixture
{
    private readonly ITestOutputHelper _output;

    public PostGisFeatureSourceTests(ITestOutputHelper output) => _output = output;

    /// <summary>Istanbul, roughly — the area the benchmarks used.</summary>
    private static readonly Envelope Istanbul = new(3_200_000, 5_000_000, 3_260_000, 5_060_000);

    private static LayerDefinition OsmPolygons => new(
        name: "osm-polygons",
        schemaName: "public",
        tableName: "planet_osm_polygon",
        geometryColumn: "way",
        srid: 3857,
        identityColumn: "osm_id",
        integerIdentityColumn: null,          // osm_id is bigint, not a 32-bit OID
        isHosted: false);

    private async Task RequireCorpusAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select to_regclass('public.planet_osm_polygon') is not null");

        Assert.True(
            (bool)(await command.ExecuteScalarAsync())!,
            "public.planet_osm_polygon is not loaded. These tests exercise the read path against "
            + "real data and fail rather than skip; load the corpus with experiments/_env, or "
            + "exclude this class by name.");
    }

    private PostGisFeatureSource Source() => new(DataSource, OsmPolygons);

    /// <summary>The small materialised layer, which has an integer object id.</summary>
    private static LayerDefinition Buildings => new(
        name: "buildings",
        schemaName: "public",
        tableName: "osm_buildings",
        geometryColumn: "way",
        srid: 3857,
        identityColumn: "objectid",
        integerIdentityColumn: "objectid",
        isHosted: false);

    [Fact]
    public async Task Features_stream_with_geometry_and_identity()
    {
        await RequireCorpusAsync();

        List<Feature> features = [];
        await foreach (Feature feature in Source().ReadAsync(new FeatureQuery(10), CancellationToken.None))
        {
            features.Add(feature);
        }

        Assert.Equal(10, features.Count);
        Assert.All(features, f => Assert.NotEmpty(f.Id));
        Assert.Contains(features, f => f.Geometry is Polygon);
    }

    [Fact]
    public async Task Requested_fields_come_back_and_others_do_not()
    {
        await RequireCorpusAsync();

        FeatureQuery query = new(5, fields: ["name", "building"]);
        FeatureSchema schema = Source().SchemaFor(query);

        Assert.Equal(["name", "building"], schema.Names);

        await foreach (Feature feature in Source().ReadAsync(query, CancellationToken.None))
        {
            _ = feature["name"];
            Assert.Throws<ArgumentException>(() => feature["highway"]);
            break;
        }
    }

    [Fact]
    public async Task No_fields_requested_means_identity_and_geometry_only()
    {
        // Reading columns nobody asked for is the cheapest waste there is, so
        // the default is nothing rather than everything.
        await RequireCorpusAsync();

        await foreach (Feature feature in Source().ReadAsync(new FeatureQuery(1), CancellationToken.None))
        {
            Assert.Equal(0, feature.Schema.Count);
            Assert.NotNull(feature.Geometry);
            return;
        }

        Assert.Fail("No features returned.");
    }

    [Fact]
    public async Task Every_returned_geometry_intersects_the_requested_box()
    {
        await RequireCorpusAsync();

        int count = 0;
        await foreach (Feature feature in Source()
            .ReadAsync(new FeatureQuery(500, Istanbul), CancellationToken.None))
        {
            Assert.True(
                feature.Geometry!.Envelope.Intersects(Istanbul),
                $"Feature {feature.Id} has envelope {feature.Geometry.Envelope}, which does not "
                + $"intersect the requested {Istanbul}.");
            count++;
        }

        Assert.True(count > 0, "The Istanbul box matched nothing, so this verified nothing.");
    }

    [Fact]
    public async Task The_bounding_box_filter_is_pushed_down_rather_than_applied_here()
    {
        // The claim ADR-003 §6a rests on. If the filter were applied in our
        // process, an unfiltered query would read the same rows and take
        // comparable time — so the assertion is that it does not.
        await RequireCorpusAsync();

        await using NpgsqlCommand explain = DataSource.CreateCommand(
            """
            explain (format text)
            select osm_id, st_asbinary(way) from public.planet_osm_polygon
            where way && st_makeenvelope(3200000, 5000000, 3260000, 5060000, 3857)
            limit 500
            """);

        string plan = string.Empty;
        await using (NpgsqlDataReader reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan += reader.GetString(0) + "\n";
            }
        }

        _output.WriteLine(plan);

        Assert.Contains("Index", plan, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// <para>
    /// <b>This was flaky twice and the cause was in the test.</b> Its guard
    /// asserted that fewer than a quarter of the first 20,000 rows fall in the
    /// Istanbul box — which is a claim about <em>physical row order</em>, not
    /// about the data. A read with no <c>order by</c> returns whatever the heap
    /// offers, PostgreSQL is free to change that, and this corpus was loaded by
    /// osm2pgsql roughly in id order, which correlates with geography. Both
    /// failures came after a run that had done DDL against the same database.
    /// </para>
    /// <para>
    /// The guard is now a question about the corpus, asked of the corpus:
    /// <em>is a city box a small fraction of this table?</em> That is what the
    /// comparison needs to be true and it is deterministic. The unordered read
    /// is still what the test measures — it just no longer asserts anything
    /// about which rows the heap handed over.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Filtering_in_the_database_is_what_makes_a_bbox_query_possible_at_all()
    {
        // The first version of this test claimed a bounded read is faster than
        // an unbounded one. It is not, and the measurement said so: 63 ms
        // against 5 ms, because LIMIT 500 with no filter just takes the first
        // 500 rows off the heap. The claim was wrong, not the number.
        //
        // What pushdown actually buys is correctness of the cheap path. To get
        // 500 Istanbul features without it, we would read rows and discard the
        // ones that miss — and this measures how ruinous that is.
        await RequireCorpusAsync();

        const int sample = 20_000;
        int inIstanbul = 0;

        Stopwatch clientSide = Stopwatch.StartNew();
        await foreach (Feature feature in Source()
            .ReadAsync(new FeatureQuery(sample), CancellationToken.None))
        {
            if (feature.Geometry?.Envelope.Intersects(Istanbul) == true)
            {
                inIstanbul++;
            }
        }

        clientSide.Stop();

        Stopwatch pushedDown = Stopwatch.StartNew();
        int matched = await CountAsync(new FeatureQuery(500, Istanbul));
        pushedDown.Stop();

        _output.WriteLine(
            $"client-side: read {sample} features in {clientSide.ElapsedMilliseconds} ms to find "
            + $"{inIstanbul} in the box. Pushed down: {matched} in "
            + $"{pushedDown.ElapsedMilliseconds} ms.");

        Assert.Equal(500, matched);

        // <b>Asked of the corpus, not of the heap.</b> The comparison only means
        // something if a city box is a small part of a national table — that is
        // a fact about the data and stays true however the rows are physically
        // ordered. Asserting it about the first 20,000 rows instead is what made
        // this test flaky.
        await using NpgsqlCommand fraction = DataSource.CreateCommand(
            $"""
             select count(*) filter (
                      where way && ST_MakeEnvelope({Istanbul.MinX}, {Istanbul.MinY},
                                                   {Istanbul.MaxX}, {Istanbul.MaxY}, 3857))::float8
                    / count(*)::float8
             from public.planet_osm_polygon
             """);

        double share = (double)(await fraction.ExecuteScalarAsync())!;

        _output.WriteLine($"the Istanbul box holds {share:P2} of the corpus.");

        Assert.True(
            share < 0.25,
            $"the Istanbul box holds {share:P1} of this corpus, so it is not the national "
            + "dataset this comparison assumes and the result shows nothing.");
    }

    [Fact]
    public async Task The_limit_is_honoured()
    {
        await RequireCorpusAsync();

        Assert.Equal(3, await CountAsync(new FeatureQuery(3)));
    }

    [Fact]
    public async Task Cancellation_stops_the_read()
    {
        await RequireCorpusAsync();

        using CancellationTokenSource cancellation = new();
        int seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (Feature _ in Source().ReadAsync(new FeatureQuery(10_000), cancellation.Token))
            {
                if (++seen == 5)
                {
                    await cancellation.CancelAsync();
                }
            }
        });

        Assert.True(seen < 10_000, "Cancellation did not stop the read.");
    }

    [Fact]
    public async Task An_identity_column_that_is_null_is_a_registration_error_not_a_shrug()
    {
        // Q-57: identity is declared, never inferred. A null there means the
        // registration named the wrong column, and guessing would hide it.
        await using (NpgsqlCommand create = DataSource.CreateCommand(
            "create table nullable_id (id bigint, geom geometry(Point, 3857))"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand insert = DataSource.CreateCommand(
            "insert into nullable_id values (null, st_setsrid(st_makepoint(0, 0), 3857))"))
        {
            await insert.ExecuteNonQueryAsync();
        }

        LayerDefinition layer = new(
            "broken", SchemaName, "nullable_id", "geom", 3857, "id", null, isHosted: true);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await foreach (Feature _ in new PostGisFeatureSource(DataSource, layer)
                    .ReadAsync(new FeatureQuery(1), CancellationToken.None))
                {
                    // The throw happens while enumerating.
                }
            });

        Assert.Contains("declared rather than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_with_no_geometry_is_returned_rather_than_dropped()
    {
        // Dropping it would quietly change the answer to a count.
        await using (NpgsqlCommand create = DataSource.CreateCommand(
            "create table sparse (id bigint primary key, geom geometry(Point, 3857))"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand insert = DataSource.CreateCommand(
            "insert into sparse values (1, null), (2, st_setsrid(st_makepoint(1, 1), 3857))"))
        {
            await insert.ExecuteNonQueryAsync();
        }

        LayerDefinition layer = new(
            "sparse", SchemaName, "sparse", "geom", 3857, "id", null, isHosted: true);

        List<Feature> features = [];
        await foreach (Feature feature in new PostGisFeatureSource(DataSource, layer)
            .ReadAsync(new FeatureQuery(10), CancellationToken.None))
        {
            features.Add(feature);
        }

        Assert.Equal(2, features.Count);
        Assert.Contains(features, f => f.Geometry is null);
    }

    private async Task<int> CountAsync(FeatureQuery query)
    {
        int count = 0;
        await foreach (Feature _ in Source().ReadAsync(query, CancellationToken.None))
        {
            count++;
        }

        return count;
    }
    [Fact]
    public async Task Describe_reports_the_columns_a_client_needs_and_excludes_the_geometry()
    {
        // A geometry column in the field list makes an ArcGIS client offer to
        // label features with WKB.
        await RequireCorpusAsync();

        LayerDescription description = await new PostGisFeatureSource(DataSource, Buildings)
            .DescribeAsync(CancellationToken.None);

        Assert.DoesNotContain(description.Fields, f => f.Name == "way");
        Assert.Contains(description.Fields, f => f.Name == "objectid" && f.Type == FieldType.Integer);
        Assert.Contains(description.Fields, f => f.Name == "osm_id" && f.Type == FieldType.BigInteger);
    }

    [Fact]
    public async Task Describe_reports_an_extent_that_actually_contains_the_data()
    {
        // Null means unknown and a client zooms to the origin, so an extent that
        // is merely present is not enough — it has to be near the features.
        await RequireCorpusAsync();

        LayerDescription description = await new PostGisFeatureSource(DataSource, Buildings)
            .DescribeAsync(CancellationToken.None);

        Assert.NotNull(description.Extent);

        Envelope extent = description.Extent!.Value;
        Assert.True(extent.MinX > 3_000_000 && extent.MaxX < 3_500_000, $"x out of range: {extent}");
        Assert.True(extent.MinY > 4_900_000 && extent.MaxY < 5_200_000, $"y out of range: {extent}");
    }

    [Fact]
    public async Task Describe_on_a_table_that_does_not_exist_reports_no_fields_rather_than_throwing()
    {
        // information_schema simply has no rows for it. A metadata request for a
        // layer whose table was dropped should degrade to "nothing to see",
        // because the alternative is a 500 on the endpoint an administrator
        // would use to diagnose exactly that.
        await RequireCorpusAsync();

        LayerDefinition missing = new(
            name: "ghost", schemaName: "public", tableName: "no_such_table_here",
            geometryColumn: "way", srid: 3857, identityColumn: "id",
            integerIdentityColumn: "id", isHosted: false);

        LayerDescription description =
            await new PostGisFeatureSource(DataSource, missing).DescribeAsync(CancellationToken.None);

        Assert.Empty(description.Fields);
        Assert.Null(description.Extent);
    }

    // ---------- distinct ----------

    /// <summary>
    /// A distinct query returns one row per combination, and counting it counts combinations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found by the independent §66 Correctness gate on 2026-08-19, and it was the worst class of
    /// defect this server has had: a silent wrong answer on a capability every layer document advertises
    /// as supported.</b> `returnDistinctValues=true` returned ordinary rows up to the page limit and
    /// `returnCountOnly` beside it returned the layer's whole row count — measured against a 46,041-row
    /// layer with two distinct values in the column asked for.
    /// </para>
    /// <para>
    /// <b>Its own table, because the claim needs a column with repeated values and no corpus layer is
    /// guaranteed to have one.</b> The conformance suite asserts the same property over HTTP where it
    /// can — no two returned rows share a combination, and the count matches — but its control column is
    /// the object id, which is unique by definition, so a test that only used it would still pass with
    /// `DISTINCT ON` doing nothing. That is exactly the hole the defect lived in. Four rows and two
    /// categories here settle it in one statement.
    /// </para>
    /// <para>
    /// <b>The object id is deliberately absent from the query's fields.</b> Including it made every row
    /// distinct by construction, which was the whole bug: the field list forced it in, and `DISTINCT ON`
    /// is built from that list.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Distinct_returns_one_row_per_combination_and_counts_them()
    {
        const string Table = "zz_distinct_probe";

        await ExecuteAsync(
            $"""
             drop table if exists public.{Table};
             create table public.{Table} (
               objectid integer generated always as identity primary key,
               kind text not null,
               way geometry(Point, 3857) not null);
             insert into public.{Table} (kind, way) values
               ('road', st_setsrid(st_point(0, 0), 3857)),
               ('road', st_setsrid(st_point(1, 1), 3857)),
               ('path', st_setsrid(st_point(2, 2), 3857)),
               ('path', st_setsrid(st_point(3, 3), 3857));
             """);

        try
        {
            PostGisFeatureSource source = new(
                DataSource,
                new LayerDefinition(
                    name: Table,
                    schemaName: "public",
                    tableName: Table,
                    geometryColumn: "way",
                    srid: 3857,
                    identityColumn: "objectid",
                    integerIdentityColumn: "objectid",
                    isHosted: true));

            FeatureQuery distinct = new(
                limit: 100, fields: ["kind"], includeGeometry: false, distinct: true);

            List<string> kinds = [];

            await foreach (Feature feature in
                           source.ReadAsync(distinct, CancellationToken.None))
            {
                kinds.Add(feature[0]?.ToString() ?? "(null)");
            }

            Assert.Equal(2, kinds.Count);
            Assert.Equal(["path", "road"], kinds.Order(StringComparer.Ordinal));

            // <b>The count and the rows must describe the same set.</b> This pair read 46,041 and 1,000
            // before the fix, which is the shape that makes a wrong count worse than no count: the
            // client believes it.
            Assert.Equal(2, await source.CountAsync(distinct, CancellationToken.None));

            // And the same query without distinct still sees every row, so the filter is not the thing
            // doing the work.
            FeatureQuery all = new(limit: 100, fields: ["kind"], includeGeometry: false);

            Assert.Equal(4, await source.CountAsync(all, CancellationToken.None));
        }
        finally
        {
            await ExecuteAsync($"drop table if exists public.{Table}");
        }
    }

    /// <summary>Runs one statement against the fixture's database.</summary>
    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
