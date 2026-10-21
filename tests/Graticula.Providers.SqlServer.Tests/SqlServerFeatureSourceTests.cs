using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Xunit;
using SortKey = Graticula.Features.SortKey;

namespace Graticula.Providers.SqlServer.Tests;

/// <summary>
/// ADR-210's reader against a real SQL Server: a table of points as <c>geometry</c> in EPSG:4326 with a spatial index,
/// the same points as <c>geography</c>, and the tables the probe must skip — each answer compared with the same rule
/// applied here to the rows that were written.
/// </summary>
[Trait("Needs", "SqlServer")]
public sealed class SqlServerFeatureSourceTests : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The places written: object id, name, kind, population, longitude, latitude.</summary>
    private static readonly (int Id, string? Name, string Kind, int Population, double X, double Y)[] Rows =
    [
        (1, "Ankara", "city", 5_700_000, 32.85, 39.93),
        (2, "İstanbul", "city", 15_600_000, 28.97, 41.01),
        (3, "İzmir", "city", 4_400_000, 27.14, 38.42),
        (4, "Eğirdir", "town", 33_000, 30.87, 37.87),
        (5, "Abana", "town", 4_000, 34.01, 41.98),
        (6, "Kemer", "town", 47_000, 30.57, 36.60),
        (7, "Sinop", "town", 60_000, 35.15, 42.03),
        (8, "Konya", "city", 2_300_000, 32.49, 37.87),

        // On a box's western edge, and nameless; on its north-eastern corner, with a bracket T-SQL's LIKE would read.
        (9, null, "town", 1_000, 31.00, 38.00),
        (10, "Kar[ş]ı", "town", 2_000, 33.00, 40.00),
    ];

    public async Task InitializeAsync()
    {
        string[] tables = ["zz_places", "zz_geo", "zz_mixed", "zz_empty", "zz_nokey"];

        await SqlServerFixture.ExecuteAsync([.. tables.Select(t => $"drop table if exists dbo.{t}")]);
        await SqlServerFixture.ExecuteAsync(
            "create table dbo.zz_places (objectid int primary key, ad nvarchar(50), tur nvarchar(10), nufus int, shape geometry not null)",
            "create table dbo.zz_geo (objectid int primary key, ad nvarchar(50), shape geography not null)",
            "create table dbo.zz_mixed (objectid int primary key, shape geometry)",
            "create table dbo.zz_empty (objectid int primary key, shape geometry)",
            "create table dbo.zz_nokey (code nvarchar(5), shape geometry)");

        foreach ((int id, string? name, string kind, int population, double x, double y) in Rows)
        {
            string at = string.Create(CultureInfo.InvariantCulture, $"POINT({x} {y})");
            string named = name is null ? "null" : $"N'{name}'";

            await SqlServerFixture.ExecuteAsync(
                $"insert into dbo.zz_places values ({id}, {named}, N'{kind}', {population}, geometry::STGeomFromText('{at}', 4326))",
                $"insert into dbo.zz_geo values ({id}, {named}, geography::STGeomFromText('{at}', 4326))");
        }

        await SqlServerFixture.ExecuteAsync(
            "create spatial index zz_places_sx on dbo.zz_places (shape) with (bounding_box = (-180, -90, 180, 90))",
            "insert into dbo.zz_mixed values (1, geometry::STGeomFromText('POINT(30 40)', 4326)), (2, geometry::STGeomFromText('POINT(3656000 4855000)', 3857))",
            "insert into dbo.zz_nokey values (N'a', geometry::STGeomFromText('POINT(30 40)', 4326))");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static SqlServerFeatureSource Places() => new(
        SqlServerFixture.Locator,
        new LayerDefinition("Places", "dbo", "zz_places", "shape", 4326, "objectid", "objectid", isHosted: false),
        new StillProjector());

    private static SqlServerFeatureSource Geo() => new(
        SqlServerFixture.Locator,
        new LayerDefinition("Geo", "dbo", "zz_geo", "shape", 4326, "objectid", "objectid", isHosted: false),
        new StillProjector());

    private static async Task<List<Feature>> AllAsync(SqlServerFeatureSource source, FeatureQuery query)
    {
        List<Feature> features = [];

        await foreach (Feature feature in source.ReadAsync(query, None))
        {
            features.Add(feature);
        }

        return features;
    }

    private static ParsedWhere Where(string clause)
    {
        Assert.True(
            WhereClause.TryParse(
                clause, ["objectid", "ad", "tur", "nufus"], LayerDefinition.Quote, out ParsedWhere parsed, out string? error,
                new Dictionary<string, FieldType>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectid"] = FieldType.Integer, ["ad"] = FieldType.Text, ["tur"] = FieldType.Text, ["nufus"] = FieldType.Integer,
                }),
            error);

        return parsed;
    }

    private static long[] Inside(double x0, double y0, double x1, double y1) =>
        [.. Rows.Where(r => r.X >= x0 && r.X <= x1 && r.Y >= y0 && r.Y <= y1).Select(r => (long)r.Id)];

    [Fact]
    public async Task The_probe_offers_the_spatial_tables_and_names_why_it_skips_the_others()
    {
        ProbeResult probed = await new SqlServerDataSourceProbe().ProbeAsync(SqlServerFixture.Locator, None);

        Assert.Equal(ProbeOutcome.Usable, probed.Outcome);
        Assert.Contains(probed.Tables, t => t.TableName == "zz_places" && t.SchemaName == "dbo" && t.Srid == 4326
            && t.GeometryType == "Point" && t.CandidateObjectIdColumn == "objectid" && !t.Writable);
        Assert.Contains(probed.Tables, t => t.TableName == "zz_geo" && t.Srid == 4326);
        Assert.Contains(probed.Skipped!, s => s.Name == "dbo.zz_mixed" && s.Reason.Contains("more than one reference", StringComparison.Ordinal));
        Assert.Contains(probed.Skipped!, s => s.Name == "dbo.zz_empty" && s.Reason.Contains("no shape yet", StringComparison.Ordinal));
        Assert.Contains(probed.Skipped!, s => s.Name == "dbo.zz_nokey" && s.Reason.Contains("primary key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_row_is_read_in_identity_order_longitude_first_with_its_extent()
    {
        List<Feature> read = await AllAsync(Places(), new FeatureQuery(100, fields: ["ad", "nufus"]));

        Assert.Equal(Rows.Select(r => r.Id.ToString(CultureInfo.InvariantCulture)), read.Select(f => f.Id));
        Assert.Equal(Rows.Select(r => r.Name), read.Select(f => (string?)f[0]));
        Assert.Equal(Rows.Select(r => (object)r.Population), read.Select(f => f[1]));

        Point ankara = Assert.IsType<Point>(read[0].Geometry);
        Assert.Equal((32.85, 39.93), (ankara.X, ankara.Y));

        LayerDescription described = await Places().DescribeAsync(None);
        Envelope extent = Assert.NotNull(described.Extent);
        Assert.Equal((27.14, 36.60, 35.15, 42.03), (extent.MinX, extent.MinY, extent.MaxX, extent.MaxY));
        Assert.False(described.Writable);
        Assert.Contains(described.Fields, f => f.Name == "ad" && f.Type == FieldType.Text && f.MaxLength == 50);
    }

    [Fact]
    public async Task A_box_keeps_its_edges_and_corners_and_a_shape_is_decided_exactly()
    {
        Assert.Equal(Inside(31, 37, 33, 40), await Places().ObjectIdsAsync(new FeatureQuery(1, boundingBox: new Envelope(31, 37, 33, 40)), None));
        Assert.Equal(Inside(31, 37, 33, 40).Length, await Places().CountAsync(new FeatureQuery(1, boundingBox: new Envelope(31, 37, 33, 40)), None));

        // A triangle is not a box: candidates from the box, decided here.
        Polygon triangle = new(new LinearRing(XySequence.Wrap([30, 37, 35, 37, 30, 42, 30, 37])));
        long[] under = [.. Rows.Where(r => r.X >= 30 && r.Y >= 37 && r.X + r.Y <= 72.0000001).Select(r => (long)r.Id)];

        Assert.Equal(under, await Places().ObjectIdsAsync(
            new FeatureQuery(1, spatial: new SpatialFilter(triangle, SpatialRelation.Intersects)), None));
    }

    [Fact]
    public async Task A_geography_column_is_read_longitude_first_and_boxed_as_drawn_not_along_great_circles()
    {
        Point ankara = Assert.IsType<Point>((await AllAsync(Geo(), new FeatureQuery(1, identities: [1]))).Single().Geometry);
        Assert.Equal((32.85, 39.93), (ankara.X, ankara.Y));

        // A box nineteen degrees wide: SQL Server's geography would bend its southern edge north past Kemer at 36.60.
        Assert.Equal(Inside(26, 36, 45, 40), await Geo().ObjectIdsAsync(new FeatureQuery(1, boundingBox: new Envelope(26, 36, 45, 40)), None));

        Envelope extent = Assert.NotNull((await Geo().DescribeAsync(None)).Extent);
        Assert.Equal((27.14, 36.60, 35.15, 42.03), (extent.MinX, extent.MinY, extent.MaxX, extent.MaxY));
    }

    [Fact]
    public async Task A_where_clause_is_written_in_t_sql()
    {
        // Case folded, and ArcGIS's integer division.
        long[] expected = [.. Rows.Where(r => r.Name is { } n && n.StartsWith('K') && r.Population / 1000 > 40).Select(r => (long)r.Id)];

        Assert.Equal(expected, await Places().ObjectIdsAsync(new FeatureQuery(1, where: Where("UPPER(ad) LIKE 'K%' AND nufus / 1000 > 40")), None));

        // A bracket is a character, as in PostgreSQL, not the start of a T-SQL character class.
        Assert.Equal([10L], await Places().ObjectIdsAsync(new FeatureQuery(1, where: Where("ad LIKE 'Kar[%'")), None));
        Assert.Equal([9L], await Places().ObjectIdsAsync(new FeatureQuery(1, where: Where("ad IS NULL")), None));
    }

    [Fact]
    public async Task A_page_is_its_slice_and_a_null_sorts_last_ascending_and_first_descending()
    {
        List<Feature> page = await AllAsync(Places(), new FeatureQuery(3, offset: 2));
        Assert.Equal(["3", "4", "5"], page.Select(f => f.Id));

        List<Feature> ascending = await AllAsync(Places(), new FeatureQuery(100, fields: ["ad"], orderBy: [new SortKey("ad", false)]));
        List<Feature> descending = await AllAsync(Places(), new FeatureQuery(100, fields: ["ad"], orderBy: [new SortKey("ad", true)]));

        Assert.Equal("9", ascending[^1].Id);
        Assert.Equal("9", descending[0].Id);
    }

    [Fact]
    public async Task Statistics_are_grouped_and_a_percentile_is_postgresqls()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> grouped = await Places().StatisticsAsync(
            new FeatureQuery(10,
                statistics:
                [
                    new StatisticRequest(StatisticKind.Count, "objectid", "n"),
                    new StatisticRequest(StatisticKind.Avg, "nufus", "mean"),
                    new StatisticRequest(StatisticKind.PercentileContinuous, "nufus", "median", Fraction: 0.5),
                    new StatisticRequest(StatisticKind.PercentileDiscrete, "nufus", "p90", Fraction: 0.9),
                ],
                groupBy: ["tur"]),
            None);

        foreach (var kind in Rows.GroupBy(r => r.Kind))
        {
            IReadOnlyDictionary<string, object?> row = Assert.Single(grouped, r => (string?)r["tur"] == kind.Key);
            double[] sorted = [.. kind.Select(r => (double)r.Population).Order()];

            Assert.Equal((long)kind.Count(), row["n"]);
            Assert.Equal(sorted.Average(), (double)row["mean"]!, 6);
            Assert.Equal(Continuous(sorted, 0.5), (double)row["median"]!, 6);
            Assert.Equal(sorted[(int)Math.Ceiling(0.9 * sorted.Length) - 1], Convert.ToDouble(row["p90"], CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task Top_features_rank_within_their_group()
    {
        FeatureQuery query = new(100, fields: ["tur"]) { Top = new TopFilter(["tur"], 2, [new SortKey("nufus", Descending: true)]) };

        long[] expected = [.. Rows.GroupBy(r => r.Kind).SelectMany(g => g.OrderByDescending(r => r.Population).Take(2)).Select(r => (long)r.Id).Order()];

        Assert.Equal(expected, (await Places().ObjectIdsAsync(query, None)).Order());
        Assert.Equal(expected.Length, await Places().CountAsync(query, None));
    }

    [Fact]
    public async Task An_edit_is_refused_with_the_reason()
    {
        SqlServerReadOnly refused = new(new LayerDefinition("Places", "dbo", "zz_places", "shape", 4326, "objectid", "objectid", isHosted: false));

        QueryNotSupportedException no = await Assert.ThrowsAsync<QueryNotSupportedException>(() => refused.ApplyAsync(null!, None));
        Assert.Contains("reads and does not edit", no.Message, StringComparison.Ordinal);
        Assert.False(await refused.IsOnAsync(None));
    }

    private static double Continuous(double[] sorted, double p)
    {
        double position = 1 + (p * (sorted.Length - 1));
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return sorted[lower - 1] + ((position - lower) * (sorted[upper - 1] - sorted[lower - 1]));
    }

    /// <summary>A projector for a test that never asks for another reference.</summary>
    private sealed class StillProjector : IProjector
    {
        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Asked to move EPSG:{fromSrid} to EPSG:{toSrid}.");

        public Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked for a written reference.");

        public Task<IReadOnlyList<Geometry>> GeneralizeAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to generalize.");

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) =>
            Task.FromResult<Envelope?>(null);

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to describe a projection.");
    }
}
