using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>
/// A registered Oracle layer's attachments beside its table — ADR-207 — in a BLOB streamed both ways, cascading with
/// the feature, refused for one that is not there, inside the quota.
/// </summary>
[Trait("Needs", "Oracle")]
public sealed class OracleAttachmentStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task ExecuteAsync(string sql)
    {
        await using OracleConnection connection = await OracleConnections.OpenAsync(
            OracleConnections.For(OracleFixture.Locator, pooling: true), None);
        await using OracleCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(None);
    }

    private static async Task<OracleAttachmentStore> StoreAsync(string table, long quota = 1L << 30)
    {
        await ExecuteAsync($"drop table if exists {table}__ATTACH purge");
        await ExecuteAsync($"drop table if exists {table} purge");
        await ExecuteAsync($"create table {table} (objectid number(10) primary key, label varchar2(20))");
        await ExecuteAsync($"insert into {table} values (1, 'one')");
        await ExecuteAsync($"insert into {table} values (2, 'two')");

        LayerDefinition layer = new(
            table, OracleFixture.Source.User.ToUpperInvariant(), table, "SHAPE", 4326, "OBJECTID", "OBJECTID", isHosted: false);
        OracleAttachmentStore store = new(OracleFixture.Locator, layer, quota);
        await store.EnsureTableAsync(None);
        return store;
    }

    private static byte[] Bytes(int size, byte seed = 7) => [.. Enumerable.Range(0, size).Select(i => (byte)((i * 31) + seed))];

    private static async Task<byte[]> ReadAsync(OracleAttachmentStore store, int id)
    {
        await using OpenAttachment? opened = await store.OpenAsync(id, None);
        Assert.NotNull(opened);
        using MemoryStream copy = new();
        await opened.Content.CopyToAsync(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task An_attachment_is_stored_read_replaced_listed_and_goes_with_its_feature()
    {
        OracleAttachmentStore store = await StoreAsync("ZZT_ATTACH");

        int id = await store.AddAsync(1, "photo.jpg", "image/jpeg", "image/jpeg", new MemoryStream(Bytes(300_000)), None);
        Assert.Equal(Bytes(300_000), await ReadAsync(store, id));

        AttachmentInfo listed = Assert.Single(await store.ListAsync(1, None));
        Assert.Equal(("photo.jpg", 300_000L), (listed.Name, listed.Size));

        Assert.True(await store.UpdateAsync(1, id, "smaller.jpg", "image/jpeg", null, new MemoryStream(Bytes(1_000, 3)), None));
        Assert.Equal(Bytes(1_000, 3), await ReadAsync(store, id));

        // The feature is part of the key: another feature's attachment is not replaced by counting.
        Assert.False(await store.UpdateAsync(2, id, "x", "image/jpeg", null, new MemoryStream(Bytes(10)), None));

        await Assert.ThrowsAsync<AttachmentFeatureMissingException>(
            () => store.AddAsync(99, "x", "image/jpeg", null, new MemoryStream(Bytes(10)), None));

        // The foreign key cascades: a feature deleted by any tool takes its attachments.
        await ExecuteAsync("delete from ZZT_ATTACH where objectid = 1");
        Assert.Equal((0L, 0L), await store.CountAsync(None));

        int kept = await store.AddAsync(2, "b", "image/png", null, new MemoryStream(Bytes(10)), None);
        Assert.Equal([kept], await store.DeleteAsync([kept, kept + 1000], None));

        await store.DropAsync(None);
        Assert.Empty(await store.ListAsync(2, None));
    }

    [Fact]
    public async Task An_upload_past_the_quota_is_refused_and_nothing_is_kept()
    {
        OracleAttachmentStore store = await StoreAsync("ZZT_ATTACH_QUOTA", quota: 100_000);

        await store.AddAsync(1, "a", "image/png", null, new MemoryStream(Bytes(60_000)), None);

        await Assert.ThrowsAsync<AttachmentQuotaExceededException>(
            () => store.AddAsync(2, "b", "image/png", null, new MemoryStream(Bytes(60_000)), None));

        Assert.Equal((1L, 60_000L), await store.CountAsync(None));
    }
}
