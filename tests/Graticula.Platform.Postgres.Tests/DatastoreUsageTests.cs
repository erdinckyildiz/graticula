using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// The datastore's size, and whose hosted layers fill it — D-237.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every figure is compared with PostgreSQL's own answer for the same table</b>, asked
/// separately, rather than with a number this test expects. The point of the report is that
/// it reads the source of truth; a test that pinned a byte count would pin a page size.
/// </para>
/// <para>
/// <b>What it must not count is tested as hard as what it must.</b> A registered source is
/// somebody else's database and is not in the store's figure; a table published twice is one
/// table; a table dropped by hand is zero rather than an error that hides every other owner.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DatastoreUsageTests : PostgresFixture
{
    private async Task<(PostgresAdminCatalog Admin, Guid Datastore, Guid Registered, Guid Owner)> ReadyAsync()
    {
        await MigrateAsync();

        PostgresAdminCatalog admin = new(DataSource, new SecretProtector(1, new byte[32]));

        Guid datastore = await admin.EnsureDatastoreAsync(
            "Host=localhost;Database=gis", CancellationToken.None);

        Guid registered = await admin.RegisterDataSourceAsync(
            "somebody-else", "postgis", "Host=nowhere;Database=none", CancellationToken.None);

        Guid owner = Guid.NewGuid();

        await ExecuteAsync(
            "insert into principal (id, name, kind, user_type) values (@id, 'uploader', 'user', "
            + "'unrestricted')",
            ("id", owner));

        return (admin, datastore, registered, owner);
    }

    [Fact]
    public async Task A_hosted_table_is_counted_at_the_size_PostgreSQL_gives_it()
    {
        (PostgresAdminCatalog admin, Guid datastore, _, Guid owner) = await ReadyAsync();

        DatastoreUsage empty = await admin.DatastoreUsageAsync(CancellationToken.None);

        Assert.Empty(empty.Owners);
        Assert.Equal(0, empty.HostedBytes);

        // <b>A store with nothing hosted is still on a disk.</b> The grouped statement
        // returns no row then, and the size has to come from somewhere else.
        Assert.True(empty.DatabaseBytes > 0, "An empty datastore reported a database of no size.");

        await TableAsync("du_parcels", rows: 5000);
        await admin.PublishLayerAsync(Publication(datastore, "du_parcels"), owner, CancellationToken.None);

        DatastoreUsage usage = await admin.DatastoreUsageAsync(CancellationToken.None);
        DatastoreOwnerUsage uploader = Assert.Single(usage.Owners);

        Assert.Equal("uploader", uploader.OwnerName);
        Assert.Equal(1, uploader.Tables);
        Assert.Equal(await SizeAsync("du_parcels"), uploader.FeatureBytes);
        Assert.Equal(0, uploader.AttachmentBytes);
        Assert.Equal(uploader.FeatureBytes, usage.HostedBytes);
        Assert.True(usage.DatabaseBytes >= usage.HostedBytes);
    }

    [Fact]
    public async Task Attachments_are_charged_to_the_layer_they_belong_to()
    {
        (PostgresAdminCatalog admin, Guid datastore, _, Guid owner) = await ReadyAsync();

        await TableAsync("du_photos", rows: 10);
        await admin.PublishLayerAsync(Publication(datastore, "du_photos"), owner, CancellationToken.None);

        // The two companion tables PostGisAttachmentStore creates, with something in them.
        await ExecuteAsync(
            $"create table \"{SchemaName}\".du_photos__attach (attachmentid int primary key, name text)");
        await ExecuteAsync(
            $"insert into \"{SchemaName}\".du_photos__attach select g, 'p' || g from generate_series(1, 50) g");
        await ExecuteAsync(
            $"create table \"{SchemaName}\".du_photos__attach_chunk (attachmentid int, seq int, data bytea)");
        await ExecuteAsync(
            $"insert into \"{SchemaName}\".du_photos__attach_chunk "
            + "select g, 0, decode(repeat('ab', 4096), 'hex') from generate_series(1, 50) g");

        DatastoreOwnerUsage uploader = Assert.Single(
            (await admin.DatastoreUsageAsync(CancellationToken.None)).Owners);

        Assert.Equal(
            await SizeAsync("du_photos__attach") + await SizeAsync("du_photos__attach_chunk"),
            uploader.AttachmentBytes);
    }

    [Fact]
    public async Task What_is_not_hosted_or_not_there_is_not_counted_and_does_not_fail()
    {
        (PostgresAdminCatalog admin, Guid datastore, Guid registered, Guid owner) = await ReadyAsync();

        await TableAsync("du_roads", rows: 2000);
        await TableAsync("du_theirs", rows: 2000);

        // Published twice from one table: one table.
        await admin.PublishLayerAsync(
            Publication(datastore, "du_roads", service: "roads_a"), owner, CancellationToken.None);
        await admin.PublishLayerAsync(
            Publication(datastore, "du_roads", service: "roads_b"), owner, CancellationToken.None);

        // Somebody else's database, which the store's figure is not about.
        await admin.PublishLayerAsync(
            Publication(registered, "du_theirs"), owner, CancellationToken.None);

        // And a hosted layer whose table has been dropped by hand.
        await TableAsync("du_gone", rows: 10);
        await admin.PublishLayerAsync(Publication(datastore, "du_gone"), owner, CancellationToken.None);
        await ExecuteAsync($"drop table \"{SchemaName}\".du_gone");

        DatastoreUsage usage = await admin.DatastoreUsageAsync(CancellationToken.None);
        DatastoreOwnerUsage uploader = Assert.Single(usage.Owners);

        Assert.Equal(2, uploader.Tables);
        Assert.Equal(await SizeAsync("du_roads"), uploader.FeatureBytes);
        Assert.Equal(uploader.FeatureBytes, usage.HostedBytes);
    }

    private LayerPublication Publication(Guid source, string table, string? service = null) =>
        new(
            service is null ? table : $"{table}_{service}",
            source,
            SchemaName,
            table,
            "geom",
            "objectid",
            "objectid",
            3857,
            GeometryKind.Point,
            SharingScope.Private,
            service,
            null,
            null);

    private async Task TableAsync(string table, int rows)
    {
        await ExecuteAsync(
            $"create table \"{SchemaName}\".{table} (objectid int primary key, "
            + "geom geometry(Point, 3857), note text)");
        await ExecuteAsync(
            $"insert into \"{SchemaName}\".{table} select g, "
            + "st_setsrid(st_makepoint(g, g), 3857), repeat('x', 40) "
            + $"from generate_series(1, {rows}) g");
    }

    private async Task<long> SizeAsync(string table)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"select pg_total_relation_size('\"{SchemaName}\".{table}'::regclass)");

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
