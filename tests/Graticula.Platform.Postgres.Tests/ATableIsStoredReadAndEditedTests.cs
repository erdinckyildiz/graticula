using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A layer with no geometry — ADR-213 — through the catalogue, the source, the writer, the importer and the history.
/// </summary>
/// <remarks>
/// <b>Against PostgreSQL, because each of these is SQL that a null geometry column changes</b>: the catalogue's two
/// columns and its uniqueness, the source's describe and filters, the writer's insert, the importer's DDL and copy, and
/// the history's trigger.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ATableIsStoredReadAndEditedTests : PostgresFixture
{
    private const string Me = "table_editor";

    // ---------- the catalogue ----------

    [Fact]
    public async Task A_table_is_stored_with_no_geometry_and_read_back_beside_the_layers_of_its_service()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(Publication(source, "sites", "geom", GeometryKind.Point, "field"), owner, CancellationToken.None);
        PublishedLayerAddress table = await admin.PublishLayerAsync(
            Publication(source, "inspections", null, null, "field"), owner, CancellationToken.None);

        Assert.Equal(1, table.LayerIndex);

        // Null in both columns, not the empty string `GeometryKind?.ToString()` would have written.
        await using (NpgsqlCommand read = DataSource.CreateCommand(
            "select geometry_column is null, geometry_type is null from layer where name = 'inspections'"))
        await using (NpgsqlDataReader row = await read.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0));
            Assert.True(row.GetBoolean(1));
        }

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));
        PublishedService service = (await catalog.FindServiceAsync(null, "field", CancellationToken.None))!;

        PublishedLayer layer = Assert.Single(service.Layers);
        PublishedLayer read2 = Assert.Single(service.Tables);

        Assert.Equal("sites", layer.Definition.Name);
        Assert.True(read2.IsTable);
        Assert.Null(read2.GeometryType);
        Assert.Null(read2.Definition.GeometryColumn);
        Assert.Same(read2, service.LayerOrTable(1));
        Assert.Null(service.Layer(1));
        Assert.Equal([0, 1], service.LayersAndTables.Select(l => l.LayerIndex));
    }

    [Fact]
    public async Task One_table_is_published_into_one_service_once()
    {
        // `layer_table_unique_in_service` includes the geometry column, and two nulls are not equal — migration 99's
        // second index is what refuses the duplicate.
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(Publication(source, "inspections", null, null, "field"), owner, CancellationToken.None);

        PostgresException refused = await Assert.ThrowsAsync<PostgresException>(() => admin.PublishLayerAsync(
            Publication(source, "inspections_again", null, null, "field") with { TableName = "inspections" },
            owner, CancellationToken.None));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
    }

    [Fact]
    public async Task A_geometry_column_without_a_type_is_refused_by_the_store()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        PostgresException refused = await Assert.ThrowsAsync<PostgresException>(() => admin.PublishLayerAsync(
            Publication(source, "half", "geom", null, "field"), owner, CancellationToken.None));

        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    // ---------- the source and the writer ----------

    [Fact]
    public async Task A_table_is_described_with_no_extent_and_queried_without_geometry()
    {
        await using Table table = await TableAsync();
        await table.AddAsync("A1", 4);
        await table.AddAsync("B2", 5);

        LayerDescription described = await table.Source.DescribeAsync(CancellationToken.None);

        Assert.Null(described.Extent);
        Assert.True(described.Writable);
        Assert.Equal(["objectid", "site_code", "score"], described.Fields.Select(f => f.Name));

        // returnGeometry=true, a box and a relation: answered with every row and no shape, as ArcGIS answers.
        FeatureQuery asked = new(
            10,
            boundingBox: new Envelope(0, 0, 1, 1),
            fields: ["objectid", "site_code"],
            includeGeometry: true,
            spatial: new SpatialFilter(new Point(50, 50), SpatialRelation.Intersects))
        {
            ReturnCentroid = true,
        };

        List<Feature> rows = [];

        await foreach (Feature feature in table.Source.ReadAsync(asked, CancellationToken.None))
        {
            rows.Add(feature);
        }

        Assert.Equal(2, rows.Count);
        Assert.All(rows, feature => Assert.Null(feature.Geometry));
        Assert.Equal(2, await table.Source.CountAsync(asked, CancellationToken.None));
        Assert.Null(await table.Source.GeometryWidthAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_row_is_added_without_a_shape_and_refused_with_one()
    {
        await using Table table = await TableAsync();
        await table.AddAsync("A1", 4);

        EditOutcome shaped = await table.Writer.ApplyAsync(
            new EditBatch(
                [new FeatureAdd(new Dictionary<string, object?> { ["site_code"] = "C3" }, new Point(1, 2))], [], [], Editor: Me),
            CancellationToken.None);

        EditResult refused = Assert.Single(shaped.Adds);
        Assert.False(refused.Succeeded);
        Assert.Contains("keeps no geometry", refused.Error, StringComparison.Ordinal);

        // An attribute update and a delete go through as on a layer, and no tile is emptied for them.
        long id = await table.AddAsync("D4", 1);
        EditOutcome changed = await table.Writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(id, new Dictionary<string, object?> { ["score"] = 3 }, null)], [id], Editor: Me),
            CancellationToken.None);

        Assert.All(changed.Updates.Concat(changed.Deletes), result => Assert.True(result.Succeeded, result.Error));
        Assert.Equal(Envelope.Empty, await table.Writer.ExtentAsync([id], 3857, CancellationToken.None));
    }

    [Fact]
    public async Task A_tables_history_is_kept_and_read_at_a_moment()
    {
        await using Table table = await TableAsync();
        long id = await table.AddAsync("A1", 4);

        await table.History.EnableAsync(CancellationToken.None);
        DateTimeOffset before = await NowAsync();

        EditOutcome changed = await table.Writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(id, new Dictionary<string, object?> { ["score"] = 1 }, null)], [], Editor: Me),
            CancellationToken.None);
        Assert.True(Assert.Single(changed.Updates).Succeeded);

        IReadOnlyList<FeatureChange> changes = await table.History.ChangesAsync(null, 10, CancellationToken.None);
        Assert.Equal("updated", Assert.Single(changes).Kind);

        List<object?> then = [];

        await foreach (Feature feature in table.Source.ReadAsync(
            new FeatureQuery(10, fields: ["score"]) { HistoricMoment = before }, CancellationToken.None))
        {
            then.Add(feature[0]);
        }

        Assert.Equal([(object?)(short)4], then);
    }

    // ---------- the importer ----------

    [Fact]
    public async Task The_importer_makes_a_table_with_no_geometry_column_empty_or_filled()
    {
        PostGisImporter importer = new(DataSource);

        ImportResult defined = await importer.DefineAsync(
            [new FieldDescription("site_code", FieldType.Text, true, 10)], geometryType: null, LayerDefinition.TableSrid,
            "ztable_defined", GeometryOrdinates.None, CancellationToken.None);

        InferredColumn code = new() { Name = "site_code" };
        code.Observe(JsonDocument.Parse("\"A1\"").RootElement);

        ImportedFeature row = new(null, new Dictionary<string, JsonElement> { ["site_code"] = JsonDocument.Parse("\"A1\"").RootElement });
        ImportResult filled = await importer.ImportAsync(
            new ImportedDataset([row, row], [code], GeometryType: null, LayerDefinition.TableSrid), "ztable_filled",
            CancellationToken.None);

        try
        {
            Assert.Equal(2, filled.Rows);

            foreach (ImportResult made in (ImportResult[])[defined, filled])
            {
                await using NpgsqlCommand columns = DataSource.CreateCommand(
                    "select array_agg(attname::text order by attnum) from pg_attribute "
                    + $"where attrelid = '\"{made.SchemaName}\".\"{made.TableName}\"'::regclass and attnum > 0 and not attisdropped");

                string[] names = (string[])(await columns.ExecuteScalarAsync())!;

                Assert.DoesNotContain("geom", names);
                Assert.DoesNotContain("import_wkb", names);
                Assert.Contains("site_code", names);
            }
        }
        finally
        {
            await importer.DropAsync(defined.SchemaName, defined.TableName, CancellationToken.None);
            await importer.DropAsync(filled.SchemaName, filled.TableName, CancellationToken.None);
        }
    }

    // ---------- helpers ----------

    private async Task<(PostgresAdminCatalog Admin, Guid Source, Guid Owner)> ReadyAsync()
    {
        await MigrateAsync();

        PostgresAdminCatalog admin = new(DataSource, new SecretProtector(1, new byte[32]));
        Guid source = await admin.RegisterDataSourceAsync(
            "test-source", "postgis", "Host=nowhere;Database=none", CancellationToken.None);

        Guid owner = Guid.NewGuid();

        await using NpgsqlCommand principal = DataSource.CreateCommand(
            "insert into principal (id, name, kind, user_type) values (@id, 'publisher', 'user', 'unrestricted')");
        principal.Parameters.AddWithValue("id", owner);
        await principal.ExecuteNonQueryAsync(CancellationToken.None);

        return (admin, source, owner);
    }

    private static LayerPublication Publication(
        Guid source, string name, string? geometryColumn, GeometryKind? kind, string service) =>
        new(
            name,
            source,
            "public",
            name,
            geometryColumn,
            "objectid",
            "objectid",
            kind is null ? LayerDefinition.TableSrid : 3857,
            kind,
            SharingScope.Private,
            service);

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<DateTimeOffset> NowAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand("select clock_timestamp()");
        return (DateTime)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Table> TableAsync()
    {
        string name = "table_" + Guid.NewGuid().ToString("n")[..10];

        await ExecuteAsync("create schema if not exists hosted");
        await ExecuteAsync(
            $"""
             create table hosted."{name}" (
                 objectid integer generated always as identity primary key,
                 site_code text,
                 score smallint
             )
             """);

        LayerDefinition definition = new(
            name: name,
            schemaName: PostGisImporter.HostedSchema,
            tableName: name,
            geometryColumn: null,
            srid: LayerDefinition.TableSrid,
            identityColumn: "objectid",
            integerIdentityColumn: "objectid",
            isHosted: true);

        PostGisFeatureSource source = new(DataSource, definition);
        LayerDescription described = await source.DescribeAsync(CancellationToken.None);

        return new Table(
            this,
            name,
            source,
            new PostGisFeatureWriter(DataSource, definition, described.Fields, described.Tracking),
            new PostGisFeatureHistory(DataSource, definition));
    }

    private sealed class Table(
        ATableIsStoredReadAndEditedTests owner,
        string name,
        PostGisFeatureSource source,
        PostGisFeatureWriter writer,
        PostGisFeatureHistory history) : IAsyncDisposable
    {
        public PostGisFeatureSource Source => source;

        public PostGisFeatureWriter Writer => writer;

        public PostGisFeatureHistory History => history;

        public async Task<long> AddAsync(string code, short score)
        {
            EditOutcome outcome = await writer.ApplyAsync(
                new EditBatch(
                    [new FeatureAdd(new Dictionary<string, object?> { ["site_code"] = code, ["score"] = score }, null)],
                    [], [], Editor: Me),
                CancellationToken.None);

            EditResult added = Assert.Single(outcome.Adds);
            Assert.True(added.Succeeded, added.Error);
            return added.Identity;
        }

        public async ValueTask DisposeAsync() =>
            await owner.ExecuteAsync($"drop table if exists hosted.\"{name}__history\", hosted.\"{name}\"");
    }
}
