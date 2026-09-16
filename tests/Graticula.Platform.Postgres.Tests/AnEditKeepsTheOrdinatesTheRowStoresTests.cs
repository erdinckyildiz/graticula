using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// An edit stores the Z and M it carries where the row keeps them, and is refused where either side would
/// lose one — ADR-077 §10.
/// </summary>
/// <remarks>
/// <b>Against a real column, because the half that matters is PostGIS's.</b> What the reader builds was tested
/// on JSON and the WKB it becomes on bytes; what had to be shown here is that the projection, the repair and
/// the typmod keep what was sent, and that the refusals come before the database's own words about typmods.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AnEditKeepsTheOrdinatesTheRowStoresTests : PostgresFixture
{
    private const int Srid = 3857;

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<PostGisFeatureWriter> TableAsync(string name, string type, GeometryKind kind)
    {
        await ExecuteAsync(
            $"create table \"{SchemaName}\".\"{name}\" (objectid serial primary key, label text, geom geometry({type}, {Srid}))");

        LayerDefinition layer = new(
            name: name, schemaName: SchemaName, tableName: name, geometryColumn: "geom", srid: Srid,
            identityColumn: "objectid", integerIdentityColumn: "objectid", isHosted: true);

        LayerDescription description =
            await new PostGisFeatureSource(DataSource, layer).DescribeAsync(CancellationToken.None);

        return new PostGisFeatureWriter(DataSource, layer, description.Fields, geometryKind: kind);
    }

    private static FeatureAdd Add(Geometry geometry, int? srid = null) =>
        new(new Dictionary<string, object?> { ["label"] = "sent" }, geometry, srid);

    private async Task<string?> StoredAsync(string table, long id = 1)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"select st_asewkt(geom) from \"{SchemaName}\".\"{table}\" where objectid = {id}");

        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<EditResult> OneAddAsync(PostGisFeatureWriter writer, FeatureAdd add) =>
        (await writer.ApplyAsync(new EditBatch([add], [], [], RollbackOnFailure: false), CancellationToken.None)).Adds[0];

    private static async Task<EditResult> OneUpdateAsync(PostGisFeatureWriter writer, Geometry geometry) =>
        (await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(1, new Dictionary<string, object?>(), geometry)], [], RollbackOnFailure: false),
            CancellationToken.None)).Updates[0];

    [Fact]
    public async Task A_point_with_z_and_m_is_stored_with_them()
    {
        PostGisFeatureWriter writer = await TableAsync("e_point_zm", "PointZM", GeometryKind.Point);

        EditResult added = await OneAddAsync(writer, Add(Point.Create(10, 20, 300, 7)));

        Assert.True(added.Succeeded, added.Error);
        Assert.Equal("SRID=3857;POINT(10 20 300 7)", await StoredAsync("e_point_zm"));
    }

    [Fact]
    public async Task A_line_with_z_is_updated_with_its_new_elevations()
    {
        PostGisFeatureWriter writer = await TableAsync("e_line_z", "LineStringZ", GeometryKind.LineString);
        await ExecuteAsync(
            $"insert into \"{SchemaName}\".e_line_z (geom) values (st_geomfromtext('LINESTRING Z (0 0 1, 1 1 2)', {Srid}))");

        EditResult updated = await OneUpdateAsync(
            writer, new LineString(XySequence.Wrap([0, 0, 5, 5], z: [10, 20], m: null)));

        Assert.True(updated.Succeeded, updated.Error);
        Assert.Equal("SRID=3857;LINESTRING(0 0 10,5 5 20)", await StoredAsync("e_line_z"));
    }

    /// <summary>A flat shape into a column that declares an elevation is refused in words, not by a typmod.</summary>
    [Fact]
    public async Task A_flat_shape_into_a_z_column_is_refused_before_the_database_is_asked()
    {
        PostGisFeatureWriter writer = await TableAsync("e_flat_into_z", "PointZ", GeometryKind.Point);

        EditResult added = await OneAddAsync(writer, Add(new Point(1, 2)));

        Assert.False(added.Succeeded);
        Assert.Contains("carries a Z ordinate, and the geometry sent has none", added.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_elevation_into_a_flat_row_is_refused_rather_than_dropped()
    {
        PostGisFeatureWriter writer = await TableAsync("e_z_into_flat", "Point", GeometryKind.Point);
        await ExecuteAsync($"insert into \"{SchemaName}\".e_z_into_flat (geom) values (st_setsrid(st_makepoint(1, 2), {Srid}))");

        EditResult updated = await OneUpdateAsync(writer, Point.Create(1, 2, 3, null));

        Assert.False(updated.Succeeded);
        Assert.Contains("stores x and y only", updated.Error!, StringComparison.Ordinal);
    }

    /// <summary>A column typed as bare geometry declares nothing, so an add holds whatever it was sent.</summary>
    [Fact]
    public async Task A_bare_column_takes_the_shape_as_sent()
    {
        PostGisFeatureWriter writer = await TableAsync("e_bare", "Geometry", GeometryKind.Point);

        EditResult added = await OneAddAsync(writer, Add(Point.Create(1, 2, 3, null)));

        Assert.True(added.Succeeded, added.Error);
        Assert.Equal("SRID=3857;POINT(1 2 3)", await StoredAsync("e_bare"));
    }

    /// <summary>Projected on write, as Q-153 decided, and the elevation rides through the transform.</summary>
    [Fact]
    public async Task An_edit_in_another_reference_keeps_its_elevation_through_the_projection()
    {
        PostGisFeatureWriter writer = await TableAsync("e_projected", "PointZ", GeometryKind.Point);

        EditResult added = await OneAddAsync(writer, Add(Point.Create(30, 38, 100, null), srid: 4326));

        Assert.True(added.Succeeded, added.Error);
        Assert.EndsWith(" 100)", await StoredAsync("e_projected"), StringComparison.Ordinal);
    }

    /// <summary>
    /// An invalid polygon with Z is repaired and keeps its elevations; the vertex the repair creates gets one
    /// interpolated along its edge, which is ADR-077 §5.2.
    /// </summary>
    [Fact]
    public async Task A_repaired_polygon_keeps_z_and_interpolates_the_new_vertex()
    {
        PostGisFeatureWriter writer = await TableAsync("e_bowtie_z", "MultiPolygonZ", GeometryKind.MultiPolygon);

        EditResult added = await OneAddAsync(writer, Add(new Polygon(new LinearRing(
            XySequence.Wrap([0, 0, 10, 10, 10, 0, 0, 10, 0, 0], z: [1, 2, 3, 4, 1], m: null)))));

        Assert.True(added.Succeeded, added.Error);
        string stored = (await StoredAsync("e_bowtie_z"))!;
        Assert.Contains("5 5 2.5", stored, StringComparison.Ordinal);
        Assert.Contains("0 10 4", stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// An invalid polygon with M is refused, because the repair returns no measures — measured on PostGIS
    /// 3.4.3, and asserted here so a PostGIS that starts keeping them says so by failing.
    /// </summary>
    [Fact]
    public async Task An_invalid_polygon_with_m_is_refused_because_the_repair_drops_the_measures()
    {
        await using (NpgsqlCommand measure = DataSource.CreateCommand(
            "select st_asewkt(st_makevalid('POLYGONM((0 0 10, 10 10 20, 10 0 30, 0 10 40, 0 0 10))'::geometry))"))
        {
            Assert.Equal("MULTIPOLYGON(((0 0,0 10,5 5,0 0)),((10 0,5 5,10 10,10 0)))", await measure.ExecuteScalarAsync() as string);
        }

        PostGisFeatureWriter writer = await TableAsync("e_bowtie_m", "MultiPolygonM", GeometryKind.MultiPolygon);

        EditResult added = await OneAddAsync(writer, Add(new Polygon(new LinearRing(
            XySequence.Wrap([0, 0, 10, 10, 10, 0, 0, 10, 0, 0], z: null, m: [10, 20, 30, 40, 10])))));

        Assert.False(added.Succeeded);
        Assert.Contains("discards every measure", added.Error!, StringComparison.Ordinal);
        Assert.Null(await StoredAsync("e_bowtie_m"));

        // A valid one with M is stored whole.
        EditResult valid = await OneAddAsync(writer, Add(new Polygon(new LinearRing(
            XySequence.Wrap([0, 0, 1, 0, 1, 1, 0, 0], z: null, m: [5, 6, 7, 5])))));

        Assert.True(valid.Succeeded, valid.Error);
    }
}
