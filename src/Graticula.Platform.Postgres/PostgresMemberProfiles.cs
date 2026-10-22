using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>Profiles, photos and member categories in the platform store — migration 90.</summary>
public sealed class PostgresMemberProfiles : IMemberProfiles
{
    private readonly NpgsqlDataSource _root;

    /// <summary>The tenant's connections where the deployment holds several (ADR-204), otherwise the store's own.</summary>
    private NpgsqlDataSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Creates the store over the platform store's pool.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresMemberProfiles(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<MemberProfile?> GetProfileAsync(string name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            select p.id, p.name, p.display_name, p.email, p.bio, p.profile_visibility, p.created_at,
                   p.disabled_at is not null,
                   (select ph.updated_at from principal_photo ph where ph.principal_id = p.id),
                   coalesce((select array_agg(pc.category_id) from principal_category pc where pc.principal_id = p.id),
                            '{}')
            from principal p
            where p.kind = 'user' and p.id = graticula_principal_named(@name)
            """);
        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MemberProfile(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<Guid[]>(9));
    }

    /// <inheritdoc/>
    public async Task<bool> SetProfileAsync(string name, ProfileEdit edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            update principal
               set display_name = @display, email = @email, bio = @bio, profile_visibility = @visibility
             where kind = 'user' and id = graticula_principal_named(@name)
            """);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.Add(new NpgsqlParameter("display", NpgsqlDbType.Text) { Value = Blank(edit.DisplayName) });
        command.Parameters.Add(new NpgsqlParameter("email", NpgsqlDbType.Text) { Value = Blank(edit.Email) });
        command.Parameters.Add(new NpgsqlParameter("bio", NpgsqlDbType.Text) { Value = Blank(edit.Bio) });
        command.Parameters.AddWithValue("visibility", edit.Visibility);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<MemberPhoto?> GetPhotoAsync(string name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            select ph.content_type, ph.content, ph.updated_at
            from principal_photo ph join principal p on p.id = ph.principal_id
            where p.kind = 'user' and p.id = graticula_principal_named(@name)
            """);
        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new MemberPhoto(reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<DateTimeOffset>(2))
            : null;
    }

    /// <inheritdoc/>
    public async Task<bool> SetPhotoAsync(
        string name, string contentType, byte[] content, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            insert into principal_photo (principal_id, content_type, content)
            select p.id, @type, @content from principal p where p.kind = 'user' and p.id = graticula_principal_named(@name)
            on conflict (principal_id) do update
               set content_type = excluded.content_type, content = excluded.content, updated_at = now()
            """);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("type", contentType);
        command.Parameters.Add(new NpgsqlParameter("content", NpgsqlDbType.Bytea) { Value = content });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> DeletePhotoAsync(string name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            delete from principal_photo ph using principal p
             where p.id = ph.principal_id and p.kind = 'user' and p.id = graticula_principal_named(@name)
            """);
        command.Parameters.AddWithValue("name", name);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MemberCategory>> ListCategoriesAsync(CancellationToken cancellationToken)
    {
        // Parents before children: a recursive walk from the top, ordered by the path it took.
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            with recursive tree as (
                select c.id, c.parent_id, c.name, array[lower(c.name)] as path
                from member_category c where c.parent_id is null
                union all
                select c.id, c.parent_id, c.name, t.path || lower(c.name)
                from member_category c join tree t on c.parent_id = t.id
            )
            select t.id, t.parent_id, t.name,
                   (select count(*) from principal_category pc where pc.category_id = t.id)
            from tree t
            order by t.path
            """);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<MemberCategory> categories = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            categories.Add(new MemberCategory(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                (int)reader.GetInt64(3)));
        }

        return categories;
    }

    /// <inheritdoc/>
    public async Task<MemberCategory?> CreateCategoryAsync(string name, Guid? parent, CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            insert into member_category (id, parent_id, name) values (@id, @parent, @name)
            on conflict do nothing
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parent ?? DBNull.Value });
        command.Parameters.AddWithValue("name", name);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
            ? new MemberCategory(id, parent, name, 0)
            : null;
    }

    /// <inheritdoc/>
    public async Task<bool?> RenameCategoryAsync(Guid id, string name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update member_category set name = @name where id = @id");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteCategoryAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from member_category where id = @id");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<int> CategorizeAsync(
        IReadOnlyList<string> members, IReadOnlyList<Guid> add, IReadOnlyList<Guid> remove,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        string[] names = [.. members];

        await using (NpgsqlCommand drop = new("""
            delete from principal_category pc using principal p
             where p.id = pc.principal_id and p.kind = 'user' and p.name = any (@names) and pc.category_id = any (@remove)
            """, connection, transaction))
        {
            drop.Parameters.AddWithValue("names", names);
            drop.Parameters.AddWithValue("remove", remove.ToArray());
            await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand put = new("""
            insert into principal_category (principal_id, category_id)
            select p.id, c.id from principal p cross join member_category c
             where p.kind = 'user' and p.name = any (@names) and c.id = any (@add)
            on conflict do nothing
            """, connection, transaction))
        {
            put.Parameters.AddWithValue("names", names);
            put.Parameters.AddWithValue("add", add.ToArray());
            await put.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int found;

        await using (NpgsqlCommand count = new(
            "select count(*) from principal where kind = 'user' and name = any (@names)", connection, transaction))
        {
            count.Parameters.AddWithValue("names", names);
            found = (int)(long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    private static object Blank(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}
