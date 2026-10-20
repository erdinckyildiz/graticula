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

namespace Graticula.Providers.MySql.Tests;

/// <summary>
/// ADR-208 phase 1's reader against a real MySQL: a table of points in EPSG:4326 — stored latitude first, read here
/// longitude first — and one in a projected reference, each answer compared with the same rule applied here to the
/// rows that were written.
/// </summary>
[Trait("Needs", "MySql")]
public sealed class MySqlFeatureSourceTests : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The places written: object id, name, kind, population, longitude, latitude.</summary>
    private static readonly (int Id, string Name, string Kind, int Population, double X, double Y)[] Rows =
    [
        (1, "Ankara", "city", 5_700_000, 32.85, 39.93),
        (2, "İstanbul", "city", 15_600_000, 28.97, 41.01),
        (3, "İzmir", "city", 4_400_000, 27.14, 38.42),
        (4, "Eğirdir", "town", 33_000, 30.87, 37.87),
        (5, "Abana", "town", 4_000, 34.01, 41.98),
        (6, "Kemer", "town", 47_000, 30.57, 36.60),
        (7, "Sinop", "town", 60_000, 35.15, 42.03),
        (8, "Konya", "city", 2_300_000, 32.49, 37.87),
    ];

    private static string Database => MySqlFixture.Source.Database;

    public async Task InitializeAsync()
    {
        await MySqlFixture.ExecuteAsync(
            "drop table if exists zz_places",
            "drop table if exists zz_projected",
            "drop table if exists zz_unindexed",
            "drop table if exists zz_no_srid",
            "create table zz_places (objectid int primary key, ad varchar(50), tur varchar(10), nufus int, "
            + "shape point not null srid 4326, spatial index (shape))",
            "create table zz_projected (objectid int primary key, shape point not null srid 3857, spatial index (shape))",
            "create table zz_unindexed (objectid int primary key, shape point not null srid 4326)",
            "create table zz_no_srid (objectid int primary key, shape point)");

        foreach ((int id, string name, string kind, int population, double x, double y) in Rows)
        {
            string at = string.Create(CultureInfo.InvariantCulture, $"POINT({x} {y})");
            await MySqlFixture.ExecuteAsync(
                $"insert into zz_places values ({id}, '{name}', '{kind}', {population}, st_geomfromtext('{at}', 4326, 'axis-order=long-lat'))");
        }

        await MySqlFixture.ExecuteAsync("insert into zz_projected values (1, st_geomfromtext('POINT(3656000 4855000)', 3857))");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static MySqlFeatureSource Places() => new(
        MySqlFixture.Locator,
        new LayerDefinition("Places", Database, "zz_places", "shape", 4326, "objectid", "objectid", isHosted: false),
        new StillProjector());

    private static async Task<List<Feature>> AllAsync(MySqlFeatureSource source, FeatureQuery query)
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

    [Fact]
    public async Task The_probe_offers_the_spatial_tables_and_names_why_it_skips_the_others()
    {
        ProbeResult probed = await new MySqlDataSourceProbe().ProbeAsync(MySqlFixture.Locator, None);

        Assert.Equal(ProbeOutcome.Usable, probed.Outcome);
        Assert.Contains(probed.Tables, t => t.TableName == "zz_places" && t.Srid == 4326 && t.GeometryType == "Point" && t.CandidateObjectIdColumn == "objectid");
        Assert.Contains(probed.Tables, t => t.TableName == "zz_projected" && t.Srid == 3857);
        Assert.Contains(probed.Skipped!, s => s.Name.EndsWith("zz_unindexed", StringComparison.Ordinal) && s.Reason.Contains("spatial index", StringComparison.Ordinal));
        Assert.Contains(probed.Skipped!, s => s.Name.EndsWith("zz_no_srid", StringComparison.Ordinal) && s.Reason.Contains("SRID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_row_is_read_in_identity_order_longitude_first()
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
    }

    [Fact]
    public async Task A_box_and_a_where_clause_select_what_they_name()
    {
        Envelope box = new(30, 37, 34, 40);
        long[] inBox = [.. Rows.Where(r => r.X >= 30 && r.X <= 34 && r.Y >= 37 && r.Y <= 40).Select(r => (long)r.Id)];

        Assert.Equal(inBox, await Places().ObjectIdsAsync(new FeatureQuery(1, boundingBox: box), None));

        // Case folded, a cast, and ArcGIS's integer division — MySQL's forms of PostgreSQL's spellings.
        long[] expected = [.. Rows.Where(r => r.Name.StartsWith('K') && r.Population / 1000 > 40).Select(r => (long)r.Id)];

        Assert.Equal(
            expected,
            await Places().ObjectIdsAsync(new FeatureQuery(1, where: Where("UPPER(ad) LIKE 'K%' AND nufus / 1000 > 40")), None));
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

        foreach (IGrouping<string, (int Id, string Name, string Kind, int Population, double X, double Y)> kind in Rows.GroupBy(r => r.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
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
        FeatureQuery query = new(100, fields: ["tur"]) { Top = new TopFilter(["tur"], 2, [new Graticula.Features.SortKey("nufus", Descending: true)]) };

        long[] expected = [.. Rows.GroupBy(r => r.Kind).SelectMany(g => g.OrderByDescending(r => r.Population).Take(2)).Select(r => (long)r.Id).Order()];

        Assert.Equal(expected, (await Places().ObjectIdsAsync(query, None)).Order());
        Assert.Equal(expected.Length, await Places().CountAsync(query, None));
    }

    [Fact]
    public async Task A_rows_version_moves_when_the_row_does()
    {
        string? before = await Places().VersionOfAsync(5, None);
        await MySqlFixture.ExecuteAsync("update zz_places set nufus = nufus + 1 where objectid = 5");
        string? after = await Places().VersionOfAsync(5, None);

        Assert.NotNull(before);
        Assert.NotEqual(before, after);
        Assert.Equal(after, await Places().VersionOfAsync(5, None));
        Assert.Null(await Places().VersionOfAsync(999, None));
    }

    [Fact]
    public async Task A_projected_layer_is_read_as_it_is_stored()
    {
        MySqlFeatureSource projected = new(
            MySqlFixture.Locator,
            new LayerDefinition("Projected", Database, "zz_projected", "shape", 3857, "objectid", "objectid", isHosted: false),
            new StillProjector());

        Point point = Assert.IsType<Point>(Assert.Single(await AllAsync(projected, new FeatureQuery(10))).Geometry);
        Assert.Equal((3656000d, 4855000d), (point.X, point.Y));
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
