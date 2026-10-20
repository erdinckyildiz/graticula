using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>
/// ADR-205 phase 1's reader, against the test schema's TR_YER (1,421 places) and TR_KARA (the land Oracle calls
/// invalid). Every filtered answer is compared with the same filter applied here to the whole table, read unfiltered.
/// </summary>
[Trait("Needs", "Oracle")]
public sealed class OracleFeatureSourceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static OracleFeatureSource Places() => new(
        OracleFixture.Locator,
        new LayerDefinition("Places", OracleFixture.Source.User.ToUpperInvariant(), "TR_YER", "SHAPE", 4326, "OBJECTID", "OBJECTID", isHosted: false),
        new StillProjector());

    private static OracleFeatureSource Land() => new(
        OracleFixture.Locator,
        new LayerDefinition("Land", OracleFixture.Source.User.ToUpperInvariant(), "TR_KARA", "SHAPE", 4326, "OBJECTID", "OBJECTID", isHosted: false),
        new StillProjector());

    private static async Task<List<Feature>> AllAsync(OracleFeatureSource source, FeatureQuery query)
    {
        List<Feature> features = [];

        await foreach (Feature feature in source.ReadAsync(query, None))
        {
            features.Add(feature);
        }

        return features;
    }

    private static Task<List<Feature>> EveryPlaceAsync() =>
        AllAsync(Places(), new FeatureQuery(2000, fields: ["AD", "TUR"]));

    private static ParsedWhere Where(string clause)
    {
        Assert.True(
            WhereClause.TryParse(
                clause, ["OBJECTID", "AD", "TUR"], LayerDefinition.Quote, out ParsedWhere parsed, out string? error,
                new Dictionary<string, FieldType>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OBJECTID"] = FieldType.Integer, ["AD"] = FieldType.Text, ["TUR"] = FieldType.Text,
                }),
            error);

        return parsed;
    }

    [Fact]
    public async Task A_table_is_described_with_its_columns_and_its_index_extent()
    {
        LayerDescription description = await Places().DescribeAsync(None);

        Assert.Equal(["OBJECTID", "AD", "TUR", "NUFUS"], description.Fields.Select(f => f.Name));
        Assert.Equal(FieldType.Integer, description.Fields[0].Type);
        Assert.Equal(FieldType.Text, description.Fields[1].Type);
        Assert.True(description.Writable, "The test user owns TR_YER, so it may edit it — ADR-205 phase 2.");
        Assert.NotNull(description.Extent);
        Assert.InRange(description.Extent!.Value.MinX, 25, 27);
        Assert.InRange(description.Extent!.Value.MaxY, 41, 43);
    }

    [Fact]
    public async Task Every_row_is_read_once_in_identity_order_with_its_shape()
    {
        List<Feature> all = await EveryPlaceAsync();

        Assert.Equal(1421, all.Count);
        Assert.Equal(Enumerable.Range(1, 1421).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), all.Select(f => f.Id));
        Assert.All(all, f => Assert.IsType<Point>(f.Geometry));
        Assert.Equal(1421, await Places().CountAsync(new FeatureQuery(1), None));
    }

    [Fact]
    public async Task A_page_is_the_slice_of_the_order_it_names()
    {
        List<Feature> all = await EveryPlaceAsync();
        List<Feature> page = await AllAsync(
            Places(), new FeatureQuery(10, fields: ["AD"], offset: 20, orderBy: [new SortKey("AD", Descending: true)]));

        List<string> expected = [.. all
            .OrderByDescending(f => (string?)f[0], StringComparer.Ordinal)
            .ThenBy(f => long.Parse(f.Id, System.Globalization.CultureInfo.InvariantCulture))
            .Skip(20).Take(10).Select(f => f.Id)];

        Assert.Equal(expected, page.Select(f => f.Id));
    }

    /// <summary>The FeatureServer asks for the object id among the fields; Oracle refused that once (ORA-00960).</summary>
    [Fact]
    public async Task The_identity_may_be_asked_for_as_a_field_and_ordered_by()
    {
        List<Feature> page = await AllAsync(
            Places(), new FeatureQuery(3, fields: ["OBJECTID", "AD"], orderBy: [new SortKey("OBJECTID", Descending: true)]));

        Assert.Equal(["1421", "1420", "1419"], page.Select(f => f.Id));
        Assert.Equal(1421, Assert.IsType<int>(page[0][0]));

        List<Feature> distinct = await AllAsync(
            Places(), new FeatureQuery(5, fields: ["OBJECTID", "TUR"], distinct: true, orderBy: [new SortKey("OBJECTID", Descending: false)]));

        Assert.Equal(5, distinct.Count);
    }

    [Fact]
    public async Task A_box_selects_what_lies_in_it()
    {
        List<Feature> all = await EveryPlaceAsync();
        Envelope box = new(32, 39, 33.5, 40.5);
        int expected = all.Count(f => f.Geometry!.Envelope.Intersects(box));

        Assert.InRange(expected, 1, 1420);
        Assert.Equal(expected, await Places().CountAsync(new FeatureQuery(1, boundingBox: box), None));
        Assert.Equal(expected, (await AllAsync(Places(), new FeatureQuery(2000, boundingBox: box))).Count);
    }

    [Fact]
    public async Task A_where_clause_is_answered_in_oracles_spelling()
    {
        List<Feature> all = await EveryPlaceAsync();

        Assert.Equal(
            all.Count(f => (string?)f[1] == "city"),
            await Places().CountAsync(new FeatureQuery(1, where: Where("TUR = 'city'")), None));

        Assert.Equal(
            all.Count(f => ((string?)f[0])?.StartsWith("An", StringComparison.Ordinal) == true),
            await Places().CountAsync(new FeatureQuery(1, where: Where("AD LIKE 'An%'")), None));

        Assert.Equal(
            all.Count(f => ((string?)f[0])?.Length > 8),
            await Places().CountAsync(new FeatureQuery(1, where: Where("CHAR_LENGTH(AD) > 8")), None));
    }

    /// <summary>ADR-205 condition 8: the land Oracle refuses as invalid is read, and filters the places exactly.</summary>
    [Fact]
    public async Task Land_oracle_calls_invalid_is_served_and_filters_exactly()
    {
        List<Feature> land = await AllAsync(Land(), new FeatureQuery(10));
        Assert.Equal(2, land.Count);

        Geometry biggest = land.Select(f => f.Geometry!).OrderByDescending(g => g.CoordinateCount).First();
        List<Feature> all = await EveryPlaceAsync();
        int expected = all.Count(f => GeometryPredicates.Intersects(f.Geometry!, biggest));

        Assert.InRange(expected, 1, 1421);
        Assert.Equal(
            expected,
            await Places().CountAsync(new FeatureQuery(1, spatial: new SpatialFilter(biggest)), None));

        IReadOnlyList<long> ids = await Places().ObjectIdsAsync(new FeatureQuery(1, spatial: new SpatialFilter(biggest)), None);
        Assert.Equal(expected, ids.Count);
        Assert.Equal(ids.Order(), ids);
    }

    [Fact]
    public async Task Statistics_are_grouped_and_counted_in_oracle()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await Places().StatisticsAsync(
            new FeatureQuery(10, statistics: [new StatisticRequest(StatisticKind.Count, "OBJECTID", "N")], groupBy: ["TUR"]),
            None);

        Assert.Equal(["city", "town"], rows.Select(r => (string?)r["TUR"]));
        Assert.Equal(1421L, rows.Sum(r => Assert.IsType<long>(r["N"])));
    }

    [Fact]
    public async Task Distinct_values_are_one_row_each()
    {
        List<Feature> rows = await AllAsync(Places(), new FeatureQuery(10, fields: ["TUR"], distinct: true));

        Assert.Equal(["city", "town"], rows.Select(r => (string?)r[0]));
        Assert.Equal(2, await Places().CountAsync(new FeatureQuery(1, fields: ["TUR"], distinct: true), None));
    }

    private static long IdOf(Feature feature) => long.Parse(feature.Id, System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Dictionary<string, FieldType> PlaceFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OBJECTID"] = FieldType.Integer, ["AD"] = FieldType.Text, ["TUR"] = FieldType.Text,
    };

    /// <summary>queryTopFeatures: the highest identities of each kind, and a count and id list that agree with the read.</summary>
    [Fact]
    public async Task Top_features_are_ranked_within_each_group()
    {
        List<Feature> every = await EveryPlaceAsync();
        long[] expected = [.. every.GroupBy(f => (string?)f[1]).SelectMany(g => g.Select(IdOf).OrderDescending().Take(3)).Order()];

        FeatureQuery query = new(100, fields: ["TUR"])
        {
            Top = new TopFilter(["TUR"], 3, [new SortKey("OBJECTID", Descending: true)]),
        };

        Assert.Equal(expected, (await AllAsync(Places(), query)).Select(IdOf).Order());
        Assert.Equal(expected.Length, await Places().CountAsync(query, None));
        Assert.Equal(expected, (await Places().ObjectIdsAsync(query, None)).Order());
    }

    /// <summary>Q-109's having clause, written again in Oracle's dialect from the tree the face parsed.</summary>
    [Fact]
    public async Task A_having_clause_keeps_the_groups_it_names()
    {
        List<Feature> every = await EveryPlaceAsync();
        Dictionary<string, int> counts = every.GroupBy(f => (string)f[1]!).ToDictionary(g => g.Key, g => g.Count());
        int threshold = counts.Values.Min();

        Assert.True(HavingClause.TryParse(
            $"COUNT(OBJECTID) > {threshold}", PlaceFields, ["TUR"], LayerDefinition.Quote, out ParsedWhere having, out string? error), error);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await Places().StatisticsAsync(
            new FeatureQuery(10, statistics: [new StatisticRequest(StatisticKind.Count, "OBJECTID", "N")], groupBy: ["TUR"])
            {
                Having = having,
            },
            None);

        Assert.Equal(counts.Where(c => c.Value > threshold).Select(c => c.Key).Order(), rows.Select(r => (string?)r["TUR"]));
    }

    /// <summary>A statistic over an expression, and a group by an expression, computed by Oracle.</summary>
    [Fact]
    public async Task Statistics_are_computed_over_an_expression_and_grouped_by_one()
    {
        List<Feature> every = await EveryPlaceAsync();

        Assert.True(StatisticExpression.TryParse(
            "OBJECTID * 2 + 1", PlaceFields, LayerDefinition.Quote, 0, out BoundExpression? doubled, out string? error), error);
        Assert.True(StatisticExpression.TryParse(
            "UPPER(TUR)", PlaceFields, LayerDefinition.Quote, 1, out BoundExpression? kind, out error), error);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await Places().StatisticsAsync(
            new FeatureQuery(10, statistics: [new StatisticRequest(StatisticKind.Sum, "OBJECTID * 2 + 1", "S", Expression: doubled)])
            {
                GroupExpressions = [new GroupExpression("KIND", kind!)],
            },
            None);

        var expected = every.GroupBy(f => ((string)f[1]!).ToUpperInvariant())
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Sum(f => (double)(IdOf(f) * 2 + 1))));

        Assert.Equal(expected, rows.Select(r => ((string)r["KIND"]!, Convert.ToDouble(r["S"], System.Globalization.CultureInfo.InvariantCulture))));
    }

    /// <summary>returnCentroid: a label point inside each piece of land, read even when the shape is not returned.</summary>
    [Fact]
    public async Task A_centroid_lies_inside_its_polygon_and_needs_no_shape_returned()
    {
        List<Feature> shapes = await AllAsync(Land(), new FeatureQuery(100));
        List<Feature> centroids = await AllAsync(Land(), new FeatureQuery(100, includeGeometry: false) { ReturnCentroid = true });

        Assert.NotEmpty(centroids);
        Assert.Equal(shapes.Select(s => s.Id), centroids.Select(c => c.Id));

        foreach ((Feature shape, Feature centroid) in shapes.Zip(centroids))
        {
            Assert.Null(centroid.Geometry);
            Point point = Assert.IsType<Point>(centroid.Centroid);
            Assert.True(GeometryPredicates.Intersects(shape.Geometry!, point), $"feature {shape.Id}");
        }
    }

    [Fact]
    public async Task A_relation_it_does_not_answer_is_refused_before_anything_is_read()
    {
        QueryNotSupportedException refused = await Assert.ThrowsAsync<QueryNotSupportedException>(() =>
            Places().CountAsync(new FeatureQuery(1, spatial: new SpatialFilter(new Point(32, 39), SpatialRelation.Within)), None));

        // Without a geometry engine (none is given here) a relation beyond intersection is refused by name.
        Assert.Contains("is not one of them", refused.Message, StringComparison.Ordinal);
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
