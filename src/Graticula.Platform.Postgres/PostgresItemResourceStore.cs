using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>An item's resources in the platform store — ADR-264, migration 128's <c>item_resource</c>.</summary>
/// <remarks>
/// <b>The bytes are read only by <see cref="FindAsync"/></b>, as a document's are: a listing moves names and sizes.
/// </remarks>
public sealed class PostgresItemResourceStore : IItemResourceStore
{
    private const string Columns = "path, media_type, size, access, created_at, modified_at";

    private readonly NpgsqlDataSource _root;

    /// <summary>The tenant's connections where the deployment holds several (ADR-204), otherwise the store's own.</summary>
    private TenantSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Uses the platform store.</summary>
    /// <param name="dataSource">Its data source.</param>
    public PostgresItemResourceStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<ItemResource> Page, int Total)> ListAsync(
        Guid item, int start, int count, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns}, count(*) over () from item_resource where item_id = @item order by path offset @start limit @count");
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("start", Math.Max(0, start));
        command.Parameters.AddWithValue("count", Math.Clamp(count, 1, 1000));

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<ItemResource> page = [];
        int total = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            page.Add(Read(reader));
            total = (int)reader.GetInt64(6);
        }

        if (page.Count == 0 && start > 0)
        {
            await using NpgsqlCommand counted = _dataSource.CreateCommand("select count(*) from item_resource where item_id = @item");
            counted.Parameters.AddWithValue("item", item);
            total = (int)(long)(await counted.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        return (page, total);
    }

    /// <inheritdoc/>
    public async Task<(ItemResource Resource, byte[] Content)?> FindAsync(Guid item, string path, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns}, content from item_resource where item_id = @item and path = @path");
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("path", path);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (Read(reader), reader.GetFieldValue<byte[]>(6))
            : null;
    }

    /// <inheritdoc/>
    public async Task<ItemResourceWrite> WriteAsync(
        Guid item, string path, string mediaType, byte[] content, string access, bool replace, long limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // <b>One statement</b>, so the room left and the write are one view of the item's resources: the sum excludes the
        // file being replaced, and the write happens only where the add-or-replace rule and the limit both hold.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            with held as (
                select coalesce(sum(size) filter (where path <> @path), 0) as others,
                       bool_or(path = @path) as there
                  from item_resource where item_id = @item),
            written as (
                insert into item_resource (item_id, path, media_type, size, content, access)
                select @item, @path, @media, octet_length(@content), @content, @access
                  from held
                 where coalesce(there, false) = @replace and others + octet_length(@content) <= @limit
                on conflict (item_id, path) do update
                   set media_type = excluded.media_type, size = excluded.size, content = excluded.content,
                       access = excluded.access, modified_at = now()
                returning 1)
            select coalesce(there, false), others + octet_length(@content) <= @limit, exists (select 1 from written)
              from held
            """);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("path", path);
        command.Parameters.AddWithValue("media", mediaType);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("access", access);
        command.Parameters.AddWithValue("replace", replace);
        command.Parameters.AddWithValue("limit", limit);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        bool there = reader.GetBoolean(0);
        bool fits = reader.GetBoolean(1);
        bool written = reader.GetBoolean(2);

        return written ? (replace ? ItemResourceWrite.Replaced : ItemResourceWrite.Added)
            : there != replace ? (replace ? ItemResourceWrite.NotThere : ItemResourceWrite.AlreadyThere)
            : !fits ? ItemResourceWrite.TooLarge
            : ItemResourceWrite.NotThere;
    }

    /// <inheritdoc/>
    public async Task<ItemResourceWrite> ReplaceIfAsync(
        Guid item, string path, byte[] expected, string mediaType, byte[] content, string access, long limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(content);

        // <b>The comparison is in the update's own condition</b>, not only in the snapshot the sums are read from: an update
        // that waits on another writer's row lock checks the condition again against the row that writer left, so of two
        // writers that read the same bytes exactly one replaces them and the other is told the file changed.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            with held as (
                select coalesce(sum(size) filter (where path <> @path), 0) as others,
                       bool_or(path = @path) as there
                  from item_resource where item_id = @item),
            written as (
                update item_resource r
                   set media_type = @media, size = octet_length(@content), content = @content, access = @access,
                       modified_at = now()
                  from held
                 where r.item_id = @item and r.path = @path and r.content = @expected
                   and held.others + octet_length(@content) <= @limit
                returning 1)
            select coalesce(there, false), others + octet_length(@content) <= @limit, exists (select 1 from written)
              from held
            """);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("path", path);
        command.Parameters.AddWithValue("expected", expected);
        command.Parameters.AddWithValue("media", mediaType);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("access", access);
        command.Parameters.AddWithValue("limit", limit);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        bool there = reader.GetBoolean(0);
        bool fits = reader.GetBoolean(1);
        bool written = reader.GetBoolean(2);

        return written ? ItemResourceWrite.Replaced
            : !there ? ItemResourceWrite.NotThere
            : !fits ? ItemResourceWrite.TooLarge
            : ItemResourceWrite.Changed;
    }

    /// <inheritdoc/>
    public async Task<int> RemoveAsync(Guid item, string? path, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(path is null
            ? "delete from item_resource where item_id = @item"
            : "delete from item_resource where item_id = @item and path = @path");
        command.Parameters.AddWithValue("item", item);
        if (path is not null)
        {
            command.Parameters.AddWithValue("path", path);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ItemResource Read(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2),
        reader.GetString(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetFieldValue<DateTimeOffset>(5));
}
