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
        Assert.False(description.Writable);
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

    [Fact]
    public async Task A_relation_it_does_not_answer_is_refused_before_anything_is_read()
    {
        QueryNotSupportedException refused = await Assert.ThrowsAsync<QueryNotSupportedException>(() =>
            Places().CountAsync(new FeatureQuery(1, spatial: new SpatialFilter(new Point(32, 39), SpatialRelation.Within)), None));

        Assert.Contains("ADR-205 phase 1", refused.Message, StringComparison.Ordinal);
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
