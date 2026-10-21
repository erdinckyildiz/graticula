using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Graticula.Providers.SqlServer.Tests;

/// <summary>
/// ADR-212 on SQL Server: the editor-tracking columns added on an administrator's word and signed by the writer; and
/// ADR-207's attachments kept beside the table, chunked, cascading with the feature.
/// </summary>
[Trait("Needs", "SqlServer")]
public sealed class SqlServerTrackingAndAttachmentsTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Me = "editor@example";
    private const string Them = "someone@example";

    private static readonly EditorTracking Portal = new("created_user", "created_date", "last_edited_user", "last_edited_date");

    private static readonly (string Name, bool Moment)[] PortalColumns =
        [("created_user", false), ("created_date", true), ("last_edited_user", false), ("last_edited_date", true)];

    private static LayerDefinition Layer(string table) => new(table, "dbo", table, "shape", 4326, "objectid", "objectid", isHosted: false);

    private static async Task TableAsync(string table) => await SqlServerFixture.ExecuteAsync(
        $"drop table if exists dbo.{table}__attach_chunk",
        $"drop table if exists dbo.{table}__attach",
        $"drop table if exists dbo.{table}",
        $"create table dbo.{table} (objectid int identity primary key, label nvarchar(20), shape geometry)");

    private static async Task<object?[]> RowAsync(string table, long id, string columns)
    {
        await using SqlConnection connection = await SqlServerConnections.OpenAsync(SqlServerConnections.For(SqlServerFixture.Locator, pooling: true), None);
        await using SqlCommand command = new($"select {columns} from dbo.{table} where objectid = @id", connection);
        command.Parameters.AddWithValue("@id", id);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(None);
        Assert.True(await reader.ReadAsync(None));
        object?[] values = new object?[reader.FieldCount];

        for (int i = 0; i < values.Length; i++)
        {
            values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return values;
    }

    [Fact]
    public async Task The_tracking_columns_are_added_once_and_the_writer_signs_an_add_and_an_update()
    {
        await TableAsync("zzt_tracked");

        IReadOnlyList<string> added = await SqlServerTableChecks.AddColumnsAsync(SqlServerFixture.Locator, "dbo", "zzt_tracked", PortalColumns, None);
        Assert.Equal(PortalColumns.Select(c => c.Name), added);
        Assert.Empty(await SqlServerTableChecks.AddColumnsAsync(SqlServerFixture.Locator, "dbo", "zzt_tracked", PortalColumns, None));

        SqlServerFeatureSource.Forget(SqlServerFixture.Locator, Layer("zzt_tracked"));
        SqlServerFeatureSource source = new(SqlServerFixture.Locator, Layer("zzt_tracked"), new NoProjector());
        LayerDescription described = await source.DescribeAsync(None);
        Assert.Contains(described.Fields, f => f.Name == "created_date" && f.Type == FieldType.Date);

        // The roles, as the host gives them once the columns are added (FieldOverrides) — which also keeps a client from
        // writing them.
        LayerDescription applied = FieldOverrides.Apply(described,
        [
            new FieldOverride("created_user", null, false, EditRole.Creator),
            new FieldOverride("created_date", null, false, EditRole.Created),
            new FieldOverride("last_edited_user", null, false, EditRole.Editor),
            new FieldOverride("last_edited_date", null, false, EditRole.Edited),
        ]);
        Assert.Equal(Portal, applied.Tracking);

        SqlServerFeatureWriter writer = new(SqlServerFixture.Locator, Layer("zzt_tracked"), applied.Fields, new NoProjector(), applied.Tracking);

        EditResult add = Assert.Single((await writer.ApplyAsync(new EditBatch(
            [new FeatureAdd(new Dictionary<string, object?> { ["label"] = "a", ["created_user"] = "an impostor" }, new Point(30, 40))],
            [], [], Editor: Them), None)).Adds);
        Assert.True(add.Succeeded, add.Error);

        object?[] row = await RowAsync("zzt_tracked", add.Identity, "created_user, created_date, last_edited_user, last_edited_date");
        Assert.Equal(Them, row[0]);
        Assert.NotNull(row[1]);
        Assert.Equal(Them, row[2]);
        Assert.NotNull(row[3]);

        EditResult update = Assert.Single((await writer.ApplyAsync(new EditBatch(
            [], [new FeatureUpdate(add.Identity, new Dictionary<string, object?> { ["label"] = "b" }, null)], [], Editor: Me), None)).Updates);
        Assert.True(update.Succeeded, update.Error);

        row = await RowAsync("zzt_tracked", add.Identity, "created_user, last_edited_user");
        Assert.Equal(Them, row[0]);
        Assert.Equal(Me, row[1]);
    }

    [Fact]
    public async Task An_attachment_is_stored_read_replaced_listed_and_goes_with_its_feature()
    {
        await TableAsync("zzt_attach");
        await SqlServerFixture.ExecuteAsync(
            "insert into dbo.zzt_attach (label, shape) values (N'one', geometry::STGeomFromText('POINT(30 40)', 4326)), (N'two', geometry::STGeomFromText('POINT(31 41)', 4326))");

        SqlServerAttachmentStore store = new(SqlServerFixture.Locator, Layer("zzt_attach"), quotaBytes: 4_000_000);
        await store.EnsureTablesAsync(None);
        await store.EnsureTablesAsync(None);

        // Larger than a chunk, so it is read back across two.
        byte[] content = [.. Enumerable.Range(0, SqlServerAttachmentStore.ChunkBytes + 1000).Select(i => (byte)(i % 251))];
        int id = await store.AddAsync(1, "plan.bin", "application/octet-stream", null, new MemoryStream(content), None);

        AttachmentInfo listed = Assert.Single(await store.ListAsync(1, None));
        Assert.Equal((id, "plan.bin", (long)content.Length), (listed.Id, listed.Name, listed.Size));

        OpenAttachment? opened = await store.OpenAsync(id, None);
        Assert.NotNull(opened);
        using (MemoryStream read = new())
        {
            await opened.Content.CopyToAsync(read, None);
            await opened.DisposeAsync();
            Assert.Equal(content, read.ToArray());
        }

        Assert.True(await store.UpdateAsync(1, id, "plan.txt", "text/plain", null, new MemoryStream("new"u8.ToArray()), None));
        Assert.False(await store.UpdateAsync(2, id, "theirs", "text/plain", null, new MemoryStream([1]), None));
        Assert.Equal(("plan.txt", 3L), (Assert.Single(await store.ListAsync(1, None)).Name, (await store.CountAsync(None)).Bytes));

        await Assert.ThrowsAsync<AttachmentFeatureMissingException>(() =>
            store.AddAsync(999, "x", "text/plain", null, new MemoryStream([1]), None));

        // The feature deleted by any tool takes its attachments.
        await SqlServerFixture.ExecuteAsync("delete from dbo.zzt_attach where objectid = 1");
        Assert.Equal(0, (await store.CountAsync(None)).Count);

        await store.DropAsync(None);
        Assert.Empty(await store.ListAsync(2, None));
    }

    [Fact]
    public async Task An_upload_past_the_quota_is_refused_and_nothing_is_kept()
    {
        await TableAsync("zzt_quota");
        await SqlServerFixture.ExecuteAsync("insert into dbo.zzt_quota (label) values (N'one')");

        SqlServerAttachmentStore store = new(SqlServerFixture.Locator, Layer("zzt_quota"), quotaBytes: 10);
        await store.EnsureTablesAsync(None);

        await Assert.ThrowsAsync<AttachmentQuotaExceededException>(() =>
            store.AddAsync(1, "big", "application/octet-stream", null, new MemoryStream(new byte[100]), None));
        Assert.Equal(0, (await store.CountAsync(None)).Count);
    }

    private sealed class NoProjector : IProjector
    {
        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to project.");

        public Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked for a written reference.");

        public Task<IReadOnlyList<Geometry>> GeneralizeAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to generalize.");

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) => Task.FromResult<Envelope?>(null);

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to describe a projection.");
    }
}
