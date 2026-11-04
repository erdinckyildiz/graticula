using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>Comments on items in the platform store — ADR-267, migration 131's <c>item_comment</c>.</summary>
public sealed class PostgresItemCommentStore : IItemCommentStore
{
    private const string Columns = "id, item_id, author_principal_id, author_name, comment, created_at";

    private readonly NpgsqlDataSource _root;

    /// <summary>The tenant's connections where the deployment holds several (ADR-204), otherwise the store's own.</summary>
    private TenantSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Uses the platform store.</summary>
    /// <param name="dataSource">Its data source.</param>
    public PostgresItemCommentStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<ItemComment> Page, int Total)> ListAsync(
        Guid item, int start, int count, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns}, count(*) over () from item_comment where item_id = @item order by created_at, id offset @start limit @count");
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("start", Math.Max(0, start));
        command.Parameters.AddWithValue("count", Math.Clamp(count, 1, 1000));

        List<ItemComment> page = [];
        int total = 0;

        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                page.Add(Read(reader));
                total = (int)reader.GetInt64(6);
            }
        }

        if (page.Count == 0 && start > 0)
        {
            total = (await CountAsync([item], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(item);
        }

        return (page, total);
    }

    /// <inheritdoc/>
    public async Task<ItemComment?> FindAsync(Guid item, Guid comment, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} from item_comment where item_id = @item and id = @id");
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("id", comment);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Guid, int>> CountAsync(IReadOnlyCollection<Guid> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        Dictionary<Guid, int> counts = [];

        if (items.Count == 0)
        {
            return counts;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select item_id, count(*) from item_comment where item_id = any(@items) group by item_id");
        command.Parameters.AddWithValue("items", items.ToArray());

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetGuid(0)] = (int)reader.GetInt64(1);
        }

        return counts;
    }

    /// <inheritdoc/>
    public async Task<ItemComment?> AddAsync(
        Guid item, Guid author, string authorName, string text, int limit, CancellationToken cancellationToken)
    {
        // One statement, so the count and the insert are one view of the item's comments.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"""
            insert into item_comment (id, item_id, author_principal_id, author_name, comment)
            select @id, @item, @author, @name, @text
             where (select count(*) from item_comment where item_id = @item) < @limit
            returning {Columns}
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("author", author);
        command.Parameters.AddWithValue("name", authorName);
        command.Parameters.AddWithValue("text", text);
        command.Parameters.AddWithValue("limit", (long)limit);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateAsync(Guid item, Guid comment, string text, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update item_comment set comment = @text where item_id = @item and id = @id");
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("id", comment);
        command.Parameters.AddWithValue("text", text);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(Guid item, Guid comment, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from item_comment where item_id = @item and id = @id");
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("id", comment);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static ItemComment Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetFieldValue<DateTimeOffset>(5));
}
