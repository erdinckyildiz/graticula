using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Xunit;

namespace Graticula.Providers.MySql.Tests;

/// <summary>ADR-208 condition 4: a relationship followed in one MySQL query, and the two checks a publish asks of a table.</summary>
[Trait("Needs", "MySql")]
public sealed class MySqlRelatedRecordsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static string Database => MySqlFixture.Source.Database;

    [Fact]
    public async Task Related_rows_come_back_grouped_by_origin_feature_within_the_limit()
    {
        await MySqlFixture.ExecuteAsync(
            "drop table if exists zzt_rel_child",
            "drop table if exists zzt_rel_parent",
            "create table zzt_rel_parent (objectid int primary key, code varchar(5), shape point not null srid 4326, spatial index (shape))",
            "create table zzt_rel_child (objectid int primary key, parent_code varchar(5), reading double)",
            "insert into zzt_rel_parent values (1, 'A', st_geomfromtext('POINT(30 40)', 4326)), (2, 'B', st_geomfromtext('POINT(30 40)', 4326)), "
            + "(3, 'C', st_geomfromtext('POINT(30 40)', 4326))",
            "insert into zzt_rel_child values (10, 'A', 1.5), (11, 'A', 2.5), (12, 'B', 3.5)");

        LayerDefinition parent = new("zzt_rel_parent", Database, "zzt_rel_parent", "shape", 4326, "objectid", "objectid", isHosted: false);
        LayerDefinition child = new("zzt_rel_child", Database, "zzt_rel_child", "shape", 4326, "objectid", "objectid", isHosted: false);

        MySqlRelatedRecords related = new(MySqlFixture.Locator, parent, child, sameDatabase: true);
        FieldDescription[] fields = [new("objectid", FieldType.Integer, false, null), new("reading", FieldType.Double, true, null)];

        IReadOnlyList<RelatedGroup> groups = await related.QueryAsync("code", "parent_code", [1, 2, 3], fields, 100, None);

        Assert.Equal([1L, 2L], groups.Select(g => g.ObjectId));
        Assert.Equal([1.5, 2.5], groups[0].Records.Select(r => (double)r["reading"]!).Order());
        Assert.Equal(12, Assert.Single(groups[1].Records)["objectid"]);

        Assert.Equal(2, (await related.QueryAsync("code", "parent_code", [1, 2], fields, 2, None)).Sum(g => g.Records.Count));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new MySqlRelatedRecords(MySqlFixture.Locator, parent, child, sameDatabase: false).QueryAsync("code", "parent_code", [1], fields, 10, None));
    }

    [Fact]
    public async Task A_publish_learns_how_much_geometry_MySQL_calls_valid_and_whether_the_reference_is_the_one_held()
    {
        await MySqlFixture.ExecuteAsync(
            "drop table if exists zzt_checks",
            "create table zzt_checks (objectid int primary key, shape geometry not null srid 4326, spatial index (shape))",
            "insert into zzt_checks values (1, st_geomfromtext('POLYGON((30 40, 31 40, 31 41, 30 41, 30 40))', 4326, 'axis-order=long-lat')), "
            + "(2, st_geomfromtext('POLYGON((30 40, 31 41, 31 40, 30 41, 30 40))', 4326, 'axis-order=long-lat'))");

        MySqlTableChecks.Validity? validity = await MySqlTableChecks.ValidityAsync(
            MySqlFixture.Locator, Database, "zzt_checks", "shape", TimeSpan.FromSeconds(30), None);
        if (MySqlFixture.IsMariaDb)
        {
            // MariaDB has no ST_IsValid (ADR-209 §1a): not measured, which is not the same as none invalid.
            Assert.Null(validity);
        }
        else
        {
            Assert.Equal((2L, 1L), (validity!.Rows, validity.Invalid));
            Assert.Single(validity.Reasons);
        }

        MySqlTableChecks.Reference? same = await MySqlTableChecks.ReferenceAsync(MySqlFixture.Locator, Database, "zzt_checks", "shape", 4326, None);
        Assert.Equal((4326, (string?)null), (same!.Stored, same.Complaint));

        // MariaDB keeps whatever SRID a shape is given, whatever its column declares: the publish check counts and says it.
        if (MySqlFixture.IsMariaDb)
        {
            await MySqlFixture.ExecuteAsync("insert into zzt_checks values (3, st_geomfromtext('POINT(3656000 4855000)', 3857))");
            MySqlTableChecks.Reference? mixed = await MySqlTableChecks.ReferenceAsync(MySqlFixture.Locator, Database, "zzt_checks", "shape", 4326, None);
            Assert.Contains("1 row holds a shape in another reference", mixed!.Complaint!, StringComparison.Ordinal);
            await MySqlFixture.ExecuteAsync("delete from zzt_checks where objectid = 3");
        }

        MySqlTableChecks.Reference? other = await MySqlTableChecks.ReferenceAsync(MySqlFixture.Locator, Database, "zzt_checks", "shape", 3857, None);
        Assert.Contains("EPSG:4326", other!.Complaint!, StringComparison.Ordinal);

        Assert.Null(await MySqlTableChecks.ReferenceAsync(MySqlFixture.Locator, Database, "zzt_no_such_table", "shape", 4326, None));

        // ADR-205 condition 8's rule, held of MySQL: a geometry MySQL calls invalid is still served, and still filtered —
        // the bow tie is read back and a point inside one of its lobes finds it, as it is, never a repaired copy.
        LayerDefinition layer = new("zzt_checks", Database, "zzt_checks", "shape", 4326, "objectid", "objectid", isHosted: false);
        MySqlFeatureSource source = new(MySqlFixture.Locator, layer, new NoProjector());

        List<string> served = [];
        await foreach (Feature feature in source.ReadAsync(new FeatureQuery(10), None))
        {
            Assert.NotNull(feature.Geometry);
            served.Add(feature.Id);
        }

        Assert.Equal(["1", "2"], served.Order(StringComparer.Ordinal));
        Assert.Equal(2, await source.CountAsync(
            new FeatureQuery(10, spatial: new SpatialFilter(new Graticula.Geometries.Point(30.9, 40.5), SpatialRelation.Intersects)), None));
    }

    private sealed class NoProjector : Graticula.Geometries.IProjector
    {
        public Task<(IReadOnlyList<Graticula.Geometries.Geometry> Projected, Graticula.Geometries.ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Graticula.Geometries.Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to project.");

        public Task<IReadOnlyList<Graticula.Geometries.Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Graticula.Geometries.Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked for a written reference.");

        public Task<IReadOnlyList<Graticula.Geometries.Geometry>> GeneralizeAsync(
            IReadOnlyList<Graticula.Geometries.Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to generalize.");

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Graticula.Geometries.Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) =>
            Task.FromResult<Graticula.Geometries.Envelope?>(null);

        public Task<IReadOnlyList<Graticula.Geometries.KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Graticula.Geometries.KnownReference>>([]);

        public Task<Graticula.Geometries.ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to describe a projection.");
    }
}
