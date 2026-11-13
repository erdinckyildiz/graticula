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
/// What a replica's synchronisation needs of the attachment table — ADR-290: the GlobalID a device gave an attachment
/// kept, and a record of changes the database writes, read as net changes between two moments.
/// </summary>
/// <remarks>
/// <b>Each test has its own schema</b> (the fixture's), because the record and the function that writes it are one per schema and a
/// test of a first evolution must be the first.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostGisAttachmentSyncTests : PostgresFixture
{
    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private async Task<(PostGisAttachmentStore Store, string Schema, string Table)> StoreAsync(long quota = 1024 * 1024)
    {
        string schema = SchemaName;
        string table = "feat_" + Guid.NewGuid().ToString("N")[..8];

        await using (NpgsqlCommand create = DataSource.CreateCommand(
            $"""

             create table {schema}.{table} (objectid integer generated always as identity primary key, label text);
             insert into {schema}.{table} (label) values ('one'), ('two'), ('three');
             """))
        {
            await create.ExecuteNonQueryAsync();
        }

        LayerDefinition layer = new(
            name: table, schemaName: schema, tableName: table, geometryColumn: "geom", srid: 3857,
            identityColumn: "objectid", integerIdentityColumn: "objectid", isHosted: true);

        return (new PostGisAttachmentStore(DataSource, layer, quota), schema, table);
    }

    private static async Task<DateTimeOffset> NowAsync()
    {
        // Commits are stamped by the database's clock; a test that read its own would race it by whatever the clocks differ.
        await Task.Delay(30);
        return DateTimeOffset.UtcNow;
    }

    private async Task<long> LogRowsAsync(string schema, string table, string? kind = null)
    {
        await using NpgsqlCommand count = DataSource.CreateCommand(
            $"select count(*) from {schema}.graticula_attachment_log where attach_table = @t and (@k::text is null or kind = @k)");
        count.Parameters.AddWithValue("t", table + PostGisAttachmentStore.Suffix);
        count.Parameters.Add(new NpgsqlParameter("k", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)kind ?? DBNull.Value });
        return (long)(await count.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task An_attachment_keeps_the_GlobalID_its_device_gave_it_and_the_same_one_twice_is_one_attachment()
    {
        (PostGisAttachmentStore store, _, _) = await StoreAsync();
        Guid gid = Guid.NewGuid();

        int first = await store.AddAsync(1, "a.txt", "text/plain", null, Bytes("hello"), gid, CancellationToken.None);
        int again = await store.AddAsync(1, "a.txt", "text/plain", null, Bytes("hello"), gid, CancellationToken.None);
        int plain = await store.AddAsync(1, "b.txt", "text/plain", null, Bytes("no id"), CancellationToken.None);

        Assert.Equal(first, again);
        Assert.NotEqual(first, plain);

        IReadOnlyList<AttachmentInfo> listed = await store.ListAsync(1, CancellationToken.None);
        Assert.Equal(2, listed.Count);
        Assert.Equal(gid, listed.Single(a => a.Id == first).GlobalId);
        Assert.Null(listed.Single(a => a.Id == plain).GlobalId);

        IReadOnlyDictionary<Guid, int> byKept = await store.IdsOfAsync([gid, Guid.NewGuid()], CancellationToken.None);
        Assert.Equal(first, Assert.Single(byKept).Value);

        IReadOnlyDictionary<int, Guid?> existing = await store.GlobalIdsAsync([first, plain, 9999], CancellationToken.None);
        Assert.Equal(gid, existing[first]);
        Assert.Null(existing[plain]);
        Assert.False(existing.ContainsKey(9999));
    }

    [Fact]
    public async Task What_is_added_changed_and_removed_after_a_moment_is_read_back_net_from_the_record()
    {
        (PostGisAttachmentStore store, _, _) = await StoreAsync();

        int old = await store.AddAsync(1, "old.txt", "text/plain", null, Bytes("old"), CancellationToken.None);
        int gone = await store.AddAsync(2, "gone.txt", "text/plain", null, Bytes("gone"), CancellationToken.None);
        DateTimeOffset generation = await NowAsync();

        // Inside the window: one added (and one added and removed again, which reached nobody), one rewritten, one removed.
        Guid kept = Guid.NewGuid();
        int added = await store.AddAsync(1, "new.txt", "text/plain", null, Bytes("new"), kept, CancellationToken.None);
        int fleeting = await store.AddAsync(3, "fleeting.txt", "text/plain", null, Bytes("x"), CancellationToken.None);
        await store.DeleteAsync([fleeting, gone], CancellationToken.None);
        Assert.True(await store.UpdateAsync(1, old, "old2.txt", "text/plain", null, Bytes("rewritten"), CancellationToken.None));

        AttachmentChanges changes = await store.ChangesAsync(generation, await NowAsync(), CancellationToken.None);

        Assert.True(changes.Vouched);
        Dictionary<int, AttachmentChange> byId = changes.Changes.ToDictionary(c => c.AttachmentId);

        Assert.Equal(3, byId.Count);
        Assert.Equal(AttachmentChangeKind.Update, byId[old].Kind);
        Assert.Equal(AttachmentChangeKind.Delete, byId[gone].Kind);
        Assert.Equal(AttachmentChangeKind.Insert, byId[added].Kind);
        Assert.Equal(kept, byId[added].GlobalId);
        Assert.False(byId.ContainsKey(fleeting));

        // A window that starts after all of it holds none of it.
        AttachmentChanges later = await store.ChangesAsync(await NowAsync(), await NowAsync(), CancellationToken.None);
        Assert.True(later.Vouched);
        Assert.Empty(later.Changes);
    }

    [Fact]
    public async Task A_feature_deleted_takes_its_attachments_and_the_record_says_so()
    {
        (PostGisAttachmentStore store, string schema, string table) = await StoreAsync();
        int one = await store.AddAsync(1, "a.txt", "text/plain", null, Bytes("a"), CancellationToken.None);
        int two = await store.AddAsync(1, "b.txt", "text/plain", null, Bytes("b"), CancellationToken.None);
        DateTimeOffset generation = await NowAsync();

        await using (NpgsqlCommand delete = DataSource.CreateCommand($"delete from {schema}.{table} where objectid = 1"))
        {
            await delete.ExecuteNonQueryAsync();
        }

        AttachmentChanges changes = await store.ChangesAsync(generation, await NowAsync(), CancellationToken.None);

        Assert.Equal([one, two], changes.Changes.Where(c => c.Kind == AttachmentChangeKind.Delete).Select(c => c.AttachmentId).Order());
        Assert.All(changes.Changes, c => Assert.Equal(1, c.FeatureId));
    }

    [Fact]
    public async Task An_upload_that_is_refused_leaves_nothing_in_the_record()
    {
        (PostGisAttachmentStore store, string schema, string table) = await StoreAsync(quota: 4);

        await Assert.ThrowsAsync<AttachmentQuotaExceededException>(() =>
            store.AddAsync(1, "big.txt", "text/plain", null, Bytes("far too large for four bytes"), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(0, await LogRowsAsync(schema, table));
    }

    [Fact]
    public async Task A_change_is_stamped_when_it_commits_and_not_when_its_transaction_began()
    {
        (PostGisAttachmentStore store, string schema, string table) = await StoreAsync();
        await store.EnsureTableAsync(CancellationToken.None);

        DateTimeOffset began;
        DateTimeOffset beforeCommit;

        await using (NpgsqlConnection connection = await DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            began = DateTimeOffset.UtcNow;

            await using (NpgsqlCommand insert = new(
                $"insert into {schema}.{table}__attach (rel_objectid, att_name, content_type, data_size) values (1, 'slow.bin', 'application/octet-stream', 0)",
                connection, transaction))
            {
                await insert.ExecuteNonQueryAsync();
            }

            // An upload that streams for a while: a download takes its generation meanwhile.
            await Task.Delay(400);
            beforeCommit = DateTimeOffset.UtcNow;
            await transaction.CommitAsync();
        }

        AttachmentChanges seenByAnEarlierDownload = await store.ChangesAsync(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), began.AddMilliseconds(100), CancellationToken.None);
        AttachmentChanges seenByTheNextOne = await store.ChangesAsync(began.AddMilliseconds(100), await NowAsync(), CancellationToken.None);

        Assert.Empty(seenByAnEarlierDownload.Changes);
        Assert.Equal(AttachmentChangeKind.Insert, Assert.Single(seenByTheNextOne.Changes).Kind);

        await using NpgsqlCommand stamp = DataSource.CreateCommand($"select at from {schema}.graticula_attachment_log where kind = 'insert'");
        DateTime at = (DateTime)(await stamp.ExecuteScalarAsync())!;
        Assert.True(new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) >= beforeCommit.AddMilliseconds(-50));
    }

    [Fact]
    public async Task A_table_made_before_the_record_is_evolved_in_place_and_says_where_its_record_begins()
    {
        (PostGisAttachmentStore store, string schema, string table) = await StoreAsync();

        // The table as this server made it before ADR-290: no GlobalID column, no trigger.
        await using (NpgsqlCommand legacy = DataSource.CreateCommand(
            $"""
             create table {schema}.{table}__attach (
               attachmentid   integer generated always as identity primary key,
               rel_objectid   bigint      not null references {schema}.{table} (objectid) on delete cascade,
               att_name       text        not null,
               content_type   text        not null,
               declared_type  text,
               data_size      bigint      not null,
               uploaded_at    timestamptz not null default now());
             create table {schema}.{table}__attach_chunk (
               attachmentid integer not null references {schema}.{table}__attach (attachmentid) on delete cascade,
               seq integer not null, data bytea not null, primary key (attachmentid, seq));
             insert into {schema}.{table}__attach (rel_objectid, att_name, content_type, data_size) values (1, 'before.txt', 'text/plain', 0);
             """))
        {
            await legacy.ExecuteNonQueryAsync();
        }

        // Read before anything evolved it: the list answers, without a GlobalID.
        AttachmentInfo before = Assert.Single(await store.ListAsync(1, CancellationToken.None));
        Assert.Null(before.GlobalId);

        // Nothing records its changes yet, so nothing can be vouched for.
        DateTimeOffset beforeEvolution = await NowAsync();
        Assert.False((await store.ChangesAsync(beforeEvolution, await NowAsync(), CancellationToken.None)).Vouched);

        await store.PrepareAsync(CancellationToken.None);

        // Its attachment keeps the id and the (absent) GlobalID it had, and a replica older than the record is not vouched for.
        AttachmentInfo after = Assert.Single(await store.ListAsync(1, CancellationToken.None));
        Assert.Equal(before.Id, after.Id);
        Assert.Null(after.GlobalId);
        Assert.False((await store.ChangesAsync(beforeEvolution, await NowAsync(), CancellationToken.None)).Vouched);

        // One that starts after it is, and sees the change.
        DateTimeOffset generation = await NowAsync();
        int added = await store.AddAsync(1, "after.txt", "text/plain", null, Bytes("after"), Guid.NewGuid(), CancellationToken.None);
        AttachmentChanges changes = await store.ChangesAsync(generation, await NowAsync(), CancellationToken.None);

        Assert.True(changes.Vouched);
        Assert.Equal(added, Assert.Single(changes.Changes).AttachmentId);

        // Doing it again changes nothing.
        await store.PrepareAsync(CancellationToken.None);
        Assert.Equal(1, await LogRowsAsync(schema, table, "begin"));
    }

    [Fact]
    public async Task A_table_emptied_in_the_window_is_not_vouched_for_because_the_record_cannot_say_what_went()
    {
        (PostGisAttachmentStore store, string schema, string table) = await StoreAsync();
        await store.AddAsync(1, "a.txt", "text/plain", null, Bytes("a"), CancellationToken.None);
        DateTimeOffset generation = await NowAsync();

        await using (NpgsqlCommand truncate = DataSource.CreateCommand($"truncate {schema}.{table}__attach_chunk, {schema}.{table}__attach"))
        {
            await truncate.ExecuteNonQueryAsync();
        }

        Assert.False((await store.ChangesAsync(generation, await NowAsync(), CancellationToken.None)).Vouched);
    }

    [Fact]
    public async Task A_layer_with_no_attachment_table_has_changed_in_no_way()
    {
        (PostGisAttachmentStore store, _, _) = await StoreAsync();

        AttachmentChanges changes = await store.ChangesAsync(await NowAsync(), await NowAsync(), CancellationToken.None);

        Assert.True(changes.Vouched);
        Assert.Empty(changes.Changes);
        await store.PrepareAsync(CancellationToken.None);
    }
}
