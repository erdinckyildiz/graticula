using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// D-390: an item resource replaced on condition — the compare-and-replace a 3D object layer's edits write through, so
/// that two nodes editing one layer cannot each write back what they read.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AResourceIsReplacedOnlyWhileItHoldsWhatWasReadTests : PostgresFixture
{
    private const string Path = "graticula/mesh/features.json";

    private async Task<(PostgresItemResourceStore Store, Guid Item)> ReadyAsync()
    {
        await MigrateAsync();

        Guid principal = Guid.NewGuid();
        await using (NpgsqlCommand command = DataSource.CreateCommand("insert into principal (id, kind, name) values (@id, 'user', @name)"))
        {
            command.Parameters.AddWithValue("id", principal);
            command.Parameters.AddWithValue("name", "mesher_" + principal.ToString("N")[..8]);
            await command.ExecuteNonQueryAsync();
        }

        Document item = await new PostgresDocumentStore(DataSource).CreateAsync(
            "Trees", "3D objects", principal, SharingScope.Private, "trees.json", Documents.MeshLayerKind, "{}"u8.ToArray(), CancellationToken.None);
        PostgresItemResourceStore store = new(DataSource);
        Assert.Equal(ItemResourceWrite.Added, await store.WriteAsync(
            Guid.Parse(item.Id), Path, "application/json", Bytes("first"), "private", replace: false, long.MaxValue, CancellationToken.None));
        return (store, Guid.Parse(item.Id));
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<string> HeldAsync(PostgresItemResourceStore store, Guid item) =>
        Encoding.UTF8.GetString((await store.FindAsync(item, Path, CancellationToken.None))!.Value.Content);

    [Fact]
    public async Task Of_two_writers_that_read_the_same_bytes_the_second_is_told_it_changed()
    {
        (PostgresItemResourceStore store, Guid item) = await ReadyAsync();
        byte[] read = Bytes("first");

        Assert.Equal(ItemResourceWrite.Replaced, await store.ReplaceIfAsync(
            item, Path, read, "application/json", Bytes("second"), "private", long.MaxValue, CancellationToken.None));
        Assert.Equal(ItemResourceWrite.Changed, await store.ReplaceIfAsync(
            item, Path, read, "application/json", Bytes("lost"), "private", long.MaxValue, CancellationToken.None));

        Assert.Equal("second", await HeldAsync(store, item));
    }

    /// <summary>
    /// The race itself: the second writer's update waits on the first's row lock, and checks its condition again against
    /// the row the first left — not against the snapshot it started from.
    /// </summary>
    [Fact]
    public async Task A_writer_waiting_on_another_finds_the_row_it_left_and_writes_nothing()
    {
        (PostgresItemResourceStore store, Guid item) = await ReadyAsync();

        await using NpgsqlConnection first = await DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await first.BeginTransactionAsync();
        await using (NpgsqlCommand update = new(
            "update item_resource set content = @c, size = octet_length(@c) where item_id = @i and path = @p", first, transaction))
        {
            update.Parameters.AddWithValue("c", Bytes("other node"));
            update.Parameters.AddWithValue("i", item);
            update.Parameters.AddWithValue("p", Path);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        Task<ItemResourceWrite> second = store.ReplaceIfAsync(
            item, Path, Bytes("first"), "application/json", Bytes("this node"), "private", long.MaxValue, CancellationToken.None);
        await Task.Delay(300);
        Assert.False(second.IsCompleted, "The second writer did not wait for the first's row lock.");

        await transaction.CommitAsync();

        Assert.Equal(ItemResourceWrite.Changed, await second);
        Assert.Equal("other node", await HeldAsync(store, item));
    }

    [Fact]
    public async Task A_conditional_replace_says_what_it_found_when_it_writes_nothing_else()
    {
        (PostgresItemResourceStore store, Guid item) = await ReadyAsync();

        Assert.Equal(ItemResourceWrite.NotThere, await store.ReplaceIfAsync(
            item, "graticula/mesh/none.json", Bytes("first"), "application/json", Bytes("x"), "private", long.MaxValue, CancellationToken.None));
        Assert.Equal(ItemResourceWrite.TooLarge, await store.ReplaceIfAsync(
            item, Path, Bytes("first"), "application/json", Enumerable.Repeat((byte)'x', 100).ToArray(), "private", 50, CancellationToken.None));
        Assert.Equal("first", await HeldAsync(store, item));
    }
}
