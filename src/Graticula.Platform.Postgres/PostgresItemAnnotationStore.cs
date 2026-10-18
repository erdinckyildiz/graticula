using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>Terms of use, content categories and favourites in the platform store — ADR-202, migration 93.</summary>
public sealed class PostgresItemAnnotationStore : IItemAnnotationStore
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store.</summary>
    public PostgresItemAnnotationStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<ItemAnnotationSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        Dictionary<Guid, string> licenses = [];
        Dictionary<Guid, IReadOnlyList<Guid>> categories = [];
        List<ItemCategory> tree = [];

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand command = new("select id, license_info from item where license_info is not null", connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                licenses[reader.GetGuid(0)] = reader.GetString(1);
            }
        }

        await using (NpgsqlCommand command = new(
            "select item_id, array_agg(category_id) from item_in_category group by item_id", connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                categories[reader.GetGuid(0)] = reader.GetFieldValue<Guid[]>(1);
            }
        }

        await using (NpgsqlCommand command = new(
            """
            select c.id, c.parent_id, c.name, (select count(*) from item_in_category a where a.category_id = c.id)
              from item_category c order by lower(c.name)
            """, connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tree.Add(new ItemCategory(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.GetString(2),
                    (int)reader.GetInt64(3)));
            }
        }

        return new ItemAnnotationSnapshot(licenses, categories, tree);
    }

    /// <inheritdoc/>
    public async Task<(Guid? Owner, string Kind)?> FindAsync(Guid item, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("select owner_principal_id, kind from item where id = @id");
        command.Parameters.AddWithValue("id", item);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1))
            : null;
    }

    /// <inheritdoc/>
    public async Task<bool> SetLicenseAsync(Guid item, string? license, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("update item set license_info = @license where id = @id");
        command.Parameters.AddWithValue("id", item);
        command.Parameters.Add(new NpgsqlParameter("license", NpgsqlDbType.Text) { Value = (object?)license ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetCategoriesAsync(Guid item, IReadOnlyList<Guid> categories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(categories);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand exists = new(
            "select (select count(*) from item where id = @item) = 1 "
            + "and (select count(*) from item_category where id = any(@categories)) = cardinality(@categories)",
            connection, transaction))
        {
            exists.Parameters.AddWithValue("item", item);
            exists.Parameters.AddWithValue("categories", categories.Distinct().ToArray());

            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return false;
            }
        }

        await using (NpgsqlCommand clear = new("delete from item_in_category where item_id = @item", connection, transaction))
        {
            clear.Parameters.AddWithValue("item", item);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand add = new(
            "insert into item_in_category (item_id, category_id) select @item, unnest(@categories)", connection, transaction))
        {
            add.Parameters.AddWithValue("item", item);
            add.Parameters.AddWithValue("categories", categories.Distinct().ToArray());
            await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<ItemCategory?> CreateCategoryAsync(Guid? parent, string name, CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            insert into item_category (id, parent_id, name)
            select @id, @parent, @name
             where @parent is null or exists (select 1 from item_category where id = @parent)
            on conflict do nothing
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parent ?? DBNull.Value });
        command.Parameters.AddWithValue("name", name);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0
            ? new ItemCategory(id, parent, name, 0)
            : null;
    }

    /// <inheritdoc/>
    public async Task<bool> RenameCategoryAsync(Guid id, string name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("update item_category set name = @name where id = @id");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteCategoryAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from item_category where id = @id");
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetFavoriteAsync(Guid principal, Guid item, bool favorite, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(favorite
            ? """
              insert into item_favorite (principal_id, item_id)
              select @principal, id from item where id = @item
              on conflict do nothing
              """
            : "delete from item_favorite where principal_id = @principal and item_id = @item");
        command.Parameters.AddWithValue("principal", principal);
        command.Parameters.AddWithValue("item", item);

        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (changed > 0 || !favorite)
        {
            return true;
        }

        // Nothing inserted: already a favourite, or no such item.
        return await FindAsync(item, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(Guid Item, string Kind, DateTimeOffset Added)>> FavoritesAsync(
        Guid principal, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            select f.item_id, i.kind, f.added_at from item_favorite f join item i on i.id = f.item_id
             where f.principal_id = @principal order by f.added_at desc
            """);
        command.Parameters.AddWithValue("principal", principal);

        List<(Guid, string, DateTimeOffset)> favorites = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            favorites.Add((reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return favorites;
    }
}
