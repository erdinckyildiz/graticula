using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Attachments against real PostgreSQL, including the part that must not buffer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The streaming test is the one that matters.</b> ADR-013 §4a forbids
/// materialising an attachment at any layer, and the first implementation broke
/// that invisibly — a single <c>bytea</c> parameter looks like streaming and is
/// not, because PostgreSQL needs a parameter's length before its bytes and
/// Npgsql reads the whole stream to find it. Nothing about the API says so. Only
/// a test that hands over a stream which cannot be read twice will notice.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostGisAttachmentStoreTests : PostgresFixture
{
    private const string Schema = "public";

    private static string Unique => "att_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Creates a feature table and returns a store over it.</summary>
    private async Task<(PostGisAttachmentStore Store, string Table)> StoreAsync(
        long quota = 1024 * 1024 * 1024)
    {
        string table = Unique;

        await using (NpgsqlCommand create = DataSource.CreateCommand(
            $"""
             create table {Schema}.{table} (
               objectid integer generated always as identity primary key,
               label    text);
             insert into {Schema}.{table} (label) values ('one'), ('two');
             """))
        {
            await create.ExecuteNonQueryAsync();
        }

        LayerDefinition layer = new(
            name: table,
            schemaName: Schema,
            tableName: table,
            geometryColumn: "geom",
            srid: 3857,
            identityColumn: "objectid",
            objectIdColumn: "objectid",
            isHosted: true);

        return (new PostGisAttachmentStore(DataSource, layer, quota), table);
    }

    private async Task DropAsync(string table)
    {
        await using NpgsqlCommand drop = DataSource.CreateCommand(
            $"drop table if exists {Schema}.{table} cascade; "
            + $"drop table if exists {Schema}.{table}__attach cascade;");

        await drop.ExecuteNonQueryAsync();
    }

    private static MemoryStream Bytes(int size, byte seed = 7)
    {
        byte[] data = new byte[size];

        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)((i * 31) + seed);
        }

        return new MemoryStream(data);
    }

    private static async Task<byte[]> DrainAsync(Stream stream)
    {
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    // ---------- the round trip ----------

    [Fact]
    public async Task An_attachment_comes_back_byte_for_byte()
    {
        (PostGisAttachmentStore store, string table) = await StoreAsync();

        try
        {
            byte[] original = await DrainAsync(Bytes(200_000));

            int id = await store.AddAsync(
                1, "photo.png", "image/png", "text/html", new MemoryStream(original),
                CancellationToken.None);

            await using OpenAttachment? open = await store.OpenAsync(id, CancellationToken.None);

            Assert.NotNull(open);
            Assert.Equal(original, await DrainAsync(open!.Content));
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task An_attachment_spanning_many_chunks_reassembles_in_order()
    {
        // Three and a bit chunks, so a reader that returned them in the wrong
        // order or dropped the partial last one fails rather than passing by
        // luck on a single-chunk file.
        (PostGisAttachmentStore store, string table) = await StoreAsync();

        try
        {
            int size = (PostGisAttachmentStore.ChunkBytes * 3) + 1_234;
            byte[] original = await DrainAsync(Bytes(size));

            int id = await store.AddAsync(
                1, "big.bin", "application/octet-stream", null, new MemoryStream(original),
                CancellationToken.None);

            await using OpenAttachment? open = await store.OpenAsync(id, CancellationToken.None);

            byte[] back = await DrainAsync(open!.Content);

            Assert.Equal(size, back.Length);
            Assert.Equal(original, back);
            Assert.Equal(size, open.Info.Size);
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task A_stream_that_cannot_be_read_twice_still_stores_correctly()
    {
        // <b>The test that would have caught the original design.</b> A single
        // bytea parameter buffers the stream to measure it, which works on a
        // MemoryStream — seekable, so its length is free — and fails on a
        // request body. This stream reports no length, refuses to seek, and
        // yields its bytes exactly once.
        (PostGisAttachmentStore store, string table) = await StoreAsync();

        try
        {
            byte[] original = await DrainAsync(Bytes(150_000, seed: 3));

            int id = await store.AddAsync(
                1, "once.bin", "application/octet-stream", null,
                new OnceOnlyStream(original), CancellationToken.None);

            await using OpenAttachment? open = await store.OpenAsync(id, CancellationToken.None);

            Assert.Equal(original, await DrainAsync(open!.Content));
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task An_empty_attachment_is_stored_and_returns_nothing()
    {
        (PostGisAttachmentStore store, string table) = await StoreAsync();

        try
        {
            int id = await store.AddAsync(
                1, "empty.txt", "text/plain", null, new MemoryStream([]), CancellationToken.None);

            await using OpenAttachment? open = await store.OpenAsync(id, CancellationToken.None);

            Assert.NotNull(open);
            Assert.Equal(0, open!.Info.Size);
            Assert.Empty(await DrainAsync(open.Content));
        }
        finally
        {
            await DropAsync(table);
        }
    }

    // ---------- metadata ----------

    [Fact]
    public async Task What_the_uploader_claimed_is_kept_beside_what_we_determined()
    {
        // The gap between the two is the interesting part of an incident, so
        // storing only ours would throw away the evidence.
        (PostGisAttachmentStore store, string table) = await StoreAsync();

        try
        {
            int id = await store.AddAsync(
                1, "x.png", "image/png", "text/html", Bytes(64), CancellationToken.None);

            AttachmentInfo info = (await store.ListAsync(1, CancellationToken.None)).Single();

            Assert.Equal(id, info.Id);
            Assert.Equal("image/png", info.ContentType);
            Assert.Equal("text/html", info.DeclaredContentType);
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task Listing_a_feature_shows_only_its_own_attachments()
    {
        (PostGisAttachmentStore store, string table) = await StoreAsync();

        try
        {
            await store.AddAsync(1, "a", "text/plain", null, Bytes(10), CancellationToken.None);
            await store.AddAsync(2, "b", "text/plain", null, Bytes(10), CancellationToken.None);

            Assert.Equal("a", (await store.ListAsync(1, CancellationToken.None)).Single().Name);
            Assert.Equal("b", (await store.ListAsync(2, CancellationToken.None)).Single().Name);
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task Listing_a_layer_that_has_never_had_an_attachment_is_empty_rather_than_an_error()
    {
        // The companion table is created on first upload, so its absence is the
        // ordinary case and must not surface as a missing-relation error.
        (PostGisAttachmentStore store, string table) = await StoreAsync();

        try
        {
            Assert.Empty(await store.ListAsync(1, CancellationToken.None));
            Assert.Null(await store.OpenAsync(1, CancellationToken.None));
            Assert.Equal(0, (await store.UsageAsync(CancellationToken.None)).Used);
        }
        finally
        {
            await DropAsync(table);
        }
    }

    // ---------- the quota ----------

    [Fact]
    public async Task An_upload_past_the_quota_is_refused_and_stores_nothing()
    {
        (PostGisAttachmentStore store, string table) = await StoreAsync(quota: 1_000);

        try
        {
            await store.AddAsync(1, "ok", "text/plain", null, Bytes(600), CancellationToken.None);

            await Assert.ThrowsAsync<AttachmentQuotaExceededException>(
                () => store.AddAsync(1, "too big", "text/plain", null, Bytes(600),
                    CancellationToken.None));

            // <b>Rolled back, not merely refused.</b> Checking the quota before
            // the write races; checking after without a transaction leaves the
            // bytes on disk and the caller believing nothing happened.
            AttachmentUsage usage = await store.UsageAsync(CancellationToken.None);

            Assert.Equal(600, usage.Used);
            Assert.Single(await store.ListAsync(1, CancellationToken.None));
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task Usage_counts_every_attachment_in_the_layer()
    {
        (PostGisAttachmentStore store, string table) = await StoreAsync(quota: 10_000);

        try
        {
            await store.AddAsync(1, "a", "text/plain", null, Bytes(100), CancellationToken.None);
            await store.AddAsync(2, "b", "text/plain", null, Bytes(250), CancellationToken.None);

            AttachmentUsage usage = await store.UsageAsync(CancellationToken.None);

            Assert.Equal(350, usage.Used);
            Assert.Equal(10_000, usage.Quota);
            Assert.True(usage.Admits(9_650));
            Assert.False(usage.Admits(9_651));
        }
        finally
        {
            await DropAsync(table);
        }
    }

    // ---------- deletion ----------

    [Fact]
    public async Task Deleting_an_attachment_frees_its_quota_and_its_chunks()
    {
        (PostGisAttachmentStore store, string table) = await StoreAsync();

        try
        {
            int id = await store.AddAsync(
                1, "gone", "text/plain", null, Bytes(PostGisAttachmentStore.ChunkBytes * 2),
                CancellationToken.None);

            Assert.Equal([id], await store.DeleteAsync([id], CancellationToken.None));
            Assert.Equal(0, (await store.UsageAsync(CancellationToken.None)).Used);

            // The chunks go with it, or the bytes stay on disk unreachable and
            // uncounted — which is the worst of both.
            await using NpgsqlCommand chunks = DataSource.CreateCommand(
                $"select count(*) from {Schema}.{table}__attach_chunk");

            Assert.Equal(0L, (long)(await chunks.ExecuteScalarAsync())!);
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task Deleting_an_id_that_is_not_there_reports_it_rather_than_failing()
    {
        (PostGisAttachmentStore store, string table) = await StoreAsync();

        try
        {
            int id = await store.AddAsync(1, "a", "text/plain", null, Bytes(10), CancellationToken.None);

            Assert.Equal([id], await store.DeleteAsync([id, id + 999], CancellationToken.None));
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task Deleting_the_feature_takes_its_attachments_with_it()
    {
        // The cascade. Without it an attachment whose feature is gone is
        // unreachable through every interface and counts against the quota
        // forever — which is exactly the orphan problem Esri's __ATTACH tables
        // are known for.
        (PostGisAttachmentStore store, string table) = await StoreAsync();

        try
        {
            await store.AddAsync(1, "a", "text/plain", null, Bytes(100), CancellationToken.None);

            await using (NpgsqlCommand delete = DataSource.CreateCommand(
                $"delete from {Schema}.{table} where objectid = 1"))
            {
                await delete.ExecuteNonQueryAsync();
            }

            Assert.Empty(await store.ListAsync(1, CancellationToken.None));
            Assert.Equal(0, (await store.UsageAsync(CancellationToken.None)).Used);
        }
        finally
        {
            await DropAsync(table);
        }
    }

    /// <summary>A stream with no length, no seeking, and one pass over its bytes.</summary>
    /// <remarks>
    /// Deliberately as unhelpful as an HTTP request body. Anything that works
    /// against this works against a real upload.
    /// </remarks>
    private sealed class OnceOnlyStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            // A few bytes at a time, so a caller that assumes one read returns
            // everything it asked for is caught.
            int take = Math.Min(Math.Min(buffer.Length, 1_024), data.Length - _position);

            if (take <= 0)
            {
                return 0;
            }

            data.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
