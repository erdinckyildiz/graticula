using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Providers.SqlServer.Tests;

/// <summary>
/// A SQL Server table with no geometry column, served as a table — ADR-213 condition 7: listed by the probe, described with
/// no extent, read without shapes whatever filter is sent, and edited with its attributes alone.
/// </summary>
[Trait("Needs", "SqlServer")]
public sealed class SqlServerTableTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static LayerDefinition Table(string name) =>
        new(name, "dbo", name, null, LayerDefinition.TableSrid, "objectid", "objectid", isHosted: false);

    private static async Task<(SqlServerFeatureSource Source, SqlServerFeatureWriter Writer)> MadeAsync(string name)
    {
        await SqlServerFixture.ExecuteAsync(
            $"drop table if exists dbo.{name}",
            $"create table dbo.{name} (objectid int identity(1, 1) primary key, site_code nvarchar(10), score int)",
            $"insert into dbo.{name} (site_code, score) values (N'A1', 4), (N'A1', 2), (N'B2', 5)");

        LayerDefinition layer = Table(name);
        SqlServerFeatureSource source = new(SqlServerFixture.Locator, layer, new NoProjector());
        LayerDescription described = await source.DescribeAsync(None);

        return (source, new SqlServerFeatureWriter(SqlServerFixture.Locator, layer, described.Fields, new NoProjector()));
    }

    [Fact]
    public async Task A_table_is_described_without_an_extent_and_read_without_shapes_whatever_the_filter()
    {
        (SqlServerFeatureSource source, _) = await MadeAsync("zzt_read");

        LayerDescription described = await source.DescribeAsync(None);

        Assert.Null(described.Extent);
        Assert.Null(described.SpatiallyIndexed);
        Assert.Equal(["objectid", "site_code", "score"], described.Fields.Select(f => f.Name));

        // A box and a relation are ignored, as ArcGIS ignores them on a table; every row comes back with no shape.
        FeatureQuery asked = new(
            10,
            boundingBox: new Envelope(0, 0, 1, 1),
            fields: ["objectid", "site_code"],
            includeGeometry: true,
            spatial: new SpatialFilter(new Point(50, 50)))
        {
            ReturnCentroid = true,
        };

        List<Feature> rows = [];

        await foreach (Feature feature in source.ReadAsync(asked, None))
        {
            rows.Add(feature);
        }

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Null(row.Geometry));
        Assert.Equal(3, await source.CountAsync(asked, None));
        Assert.NotNull(await source.VersionOfAsync(1, None));
    }

    [Fact]
    public async Task A_row_is_added_and_updated_with_its_attributes_and_refused_with_a_shape()
    {
        (SqlServerFeatureSource source, SqlServerFeatureWriter writer) = await MadeAsync("zzt_edit");

        EditOutcome added = await writer.ApplyAsync(
            new EditBatch([new FeatureAdd(new Dictionary<string, object?> { ["site_code"] = "C3", ["score"] = 1 }, null)], [], []), None);
        EditResult row = Assert.Single(added.Adds);
        Assert.True(row.Succeeded, row.Error);

        EditOutcome changed = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(row.Identity, new Dictionary<string, object?> { ["score"] = 3 }, null)], []), None);
        Assert.True(Assert.Single(changed.Updates).Succeeded);

        EditOutcome shaped = await writer.ApplyAsync(
            new EditBatch([new FeatureAdd(new Dictionary<string, object?> { ["site_code"] = "D4" }, new Point(1, 2))], [], []), None);
        EditResult refused = Assert.Single(shaped.Adds);
        Assert.False(refused.Succeeded);
        Assert.Contains("keeps no geometry", refused.Error, StringComparison.Ordinal);

        Assert.Equal(4, await source.CountAsync(new FeatureQuery(10), None));
    }

    [Fact]
    public async Task The_probe_lists_a_table_without_geometry_and_never_this_servers_attachment_tables()
    {
        await MadeAsync("zzt_listed");
        await SqlServerFixture.ExecuteAsync(
            "drop table if exists dbo.zzt_listed__attach",
            "create table dbo.zzt_listed__attach (attachmentid int identity primary key, rel_objectid bigint)");

        ProbeResult probed = await new SqlServerDataSourceProbe().ProbeAsync(SqlServerFixture.Locator, None);
        Assert.True(probed.Outcome == ProbeOutcome.Usable, $"The probe answered {probed.Outcome}: {probed.Message} (D-314).");

        SourceTable listed = Assert.Single(probed.Tables, t => t.TableName == "zzt_listed");
        Assert.Null(listed.GeometryColumn);
        Assert.Equal("objectid", listed.CandidateObjectIdColumn);
        Assert.DoesNotContain(probed.Tables, t => t.TableName == "zzt_listed__attach");
    }

    private sealed class NoProjector : IProjector
    {
        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A table has nothing to project.");

        public Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A table has nothing to project.");

        public Task<IReadOnlyList<Geometry>> GeneralizeAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A table has nothing to generalize.");

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) => Task.FromResult<Envelope?>(null);

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A table has nothing to project.");
    }
}
