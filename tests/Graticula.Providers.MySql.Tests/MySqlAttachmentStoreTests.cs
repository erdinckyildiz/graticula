using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Xunit;

namespace Graticula.Providers.MySql.Tests;

/// <summary>
/// ADR-208 condition 4: a registered MySQL layer's attachments beside its table — chunked both ways, cascading with the
/// feature, refused for one that is not there, inside the quota.
/// </summary>
[Trait("Needs", "MySql")]
public sealed class MySqlAttachmentStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<MySqlAttachmentStore> StoreAsync(string table, long quota = 1L << 30, string key = "int")
    {
        await MySqlFixture.ExecuteAsync(
            $"drop table if exists {table}__attach_chunk",
            $"drop table if exists {table}__attach",
            $"drop table if exists {table}",
            $"create table {table} (objectid {key} primary key, label varchar(20))",
            $"insert into {table} values (1, 'one'), (2, 'two')");

        LayerDefinition layer = new(table, MySqlFixture.Source.Database, table, "shape", 4326, "objectid", "objectid", isHosted: false);
        MySqlAttachmentStore store = new(MySqlFixture.Locator, layer, quota);
        await store.EnsureTablesAsync(None);
        return store;
    }

    private static byte[] Bytes(int size, byte seed = 7) => [.. Enumerable.Range(0, size).Select(i => (byte)((i * 31) + seed))];

    private static async Task<byte[]> ReadAsync(MySqlAttachmentStore store, int id)
    {
        await using OpenAttachment? opened = await store.OpenAsync(id, None);
        Assert.NotNull(opened);
        using MemoryStream copy = new();
        await opened.Content.CopyToAsync(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task An_attachment_is_stored_in_chunks_read_replaced_listed_and_goes_with_its_feature()
    {
        MySqlAttachmentStore store = await StoreAsync("zzt_attach");

        // Three chunks and a part of a fourth, and one exactly a chunk long.
        int id = await store.AddAsync(1, "photo.jpg", "image/jpeg", "image/jpeg", new MemoryStream(Bytes(800_000)), None);
        Assert.Equal(Bytes(800_000), await ReadAsync(store, id));

        int exact = await store.AddAsync(1, "exact.bin", "application/octet-stream", null, new MemoryStream(Bytes(MySqlAttachmentStore.ChunkBytes, 5)), None);
        Assert.Equal(Bytes(MySqlAttachmentStore.ChunkBytes, 5), await ReadAsync(store, exact));

        AttachmentInfo listed = (await store.ListAsync(1, None))[0];
        Assert.Equal(("photo.jpg", 800_000L), (listed.Name, listed.Size));

        Assert.True(await store.UpdateAsync(1, id, "smaller.jpg", "image/jpeg", null, new MemoryStream(Bytes(1_000, 3)), None));
        Assert.Equal(Bytes(1_000, 3), await ReadAsync(store, id));

        // The feature is part of the key: another feature's attachment is not replaced by counting.
        Assert.False(await store.UpdateAsync(2, id, "x", "image/jpeg", null, new MemoryStream(Bytes(10)), None));

        await Assert.ThrowsAsync<AttachmentFeatureMissingException>(
            () => store.AddAsync(99, "x", "image/jpeg", null, new MemoryStream(Bytes(10)), None));

        // The foreign keys cascade: a feature deleted by any tool takes its attachments, and they their chunks.
        await MySqlFixture.ExecuteAsync("delete from zzt_attach where objectid = 1");
        Assert.Equal((0L, 0L), await store.CountAsync(None));
        Assert.Null(await store.OpenAsync(id, None));

        int kept = await store.AddAsync(2, "b", "image/png", null, new MemoryStream(Bytes(10)), None);
        Assert.Equal([kept], await store.DeleteAsync([kept, kept + 1000], None));

        await store.DropAsync(None);
        Assert.Empty(await store.ListAsync(2, None));
        Assert.Equal((0L, 0L), await store.CountAsync(None));
    }

    [Fact]
    public async Task An_upload_past_the_quota_is_refused_and_nothing_is_kept()
    {
        MySqlAttachmentStore store = await StoreAsync("zzt_attach_quota", quota: 400_000);

        await store.AddAsync(1, "a", "image/png", null, new MemoryStream(Bytes(300_000)), None);

        await Assert.ThrowsAsync<AttachmentQuotaExceededException>(
            () => store.AddAsync(2, "b", "image/png", null, new MemoryStream(Bytes(300_000)), None));

        Assert.Equal((1L, 300_000L), await store.CountAsync(None));
    }

    [Fact]
    public async Task The_key_column_takes_the_layer_key_type_so_an_unsigned_bigint_is_referenced()
    {
        MySqlAttachmentStore store = await StoreAsync("zzt_attach_big", key: "bigint unsigned");

        int id = await store.AddAsync(2, "a", "image/png", null, new MemoryStream(Bytes(10)), None);
        Assert.Equal(2, Assert.Single(await store.ListAsync(2, None)).FeatureId);
        Assert.Equal([id], await store.DeleteAsync([id], None));
    }
}
