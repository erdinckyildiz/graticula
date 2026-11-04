using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>An item's metadata in the platform store — ADR-267, migration 131's <c>item_metadata</c>.</summary>
public sealed class PostgresItemMetadataStore : IItemMetadataStore
{
    private readonly NpgsqlDataSource _root;

    /// <summary>The tenant's connections where the deployment holds several (ADR-204), otherwise the store's own.</summary>
    private TenantSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Uses the platform store.</summary>
    /// <param name="dataSource">Its data source.</param>
    public PostgresItemMetadataStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<ItemMetadataDocument?> FindAsync(Guid item, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select content, modified_at from item_metadata where item_id = @item");
        command.Parameters.AddWithValue("item", item);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ItemMetadataDocument(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<DateTimeOffset>(1))
            : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<Guid>> WhichHaveAsync(IReadOnlyCollection<Guid> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        HashSet<Guid> have = [];

        if (items.Count == 0)
        {
            return have;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand("select item_id from item_metadata where item_id = any(@items)");
        command.Parameters.AddWithValue("items", items.ToArray());

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            have.Add(reader.GetGuid(0));
        }

        return have;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(Guid item, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            insert into item_metadata (item_id, content, size) values (@item, @content, octet_length(@content))
            on conflict (item_id) do update set content = excluded.content, size = excluded.size, modified_at = now()
            """);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("content", content);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(Guid item, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from item_metadata where item_id = @item");
        command.Parameters.AddWithValue("item", item);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
