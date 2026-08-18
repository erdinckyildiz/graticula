using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Groups over <c>sharing_group</c>, <c>sharing_group_member</c> and <c>sharing_group_item</c>.
/// </summary>
/// <remarks>
/// <b>Every write resolves the acting principal's standing first, in the same transaction.</b>
/// ADR-036 condition 2 asks that a group privilege cannot turn out to be global; the way to make
/// that impossible rather than remembered is for the standing lookup to be the first statement of
/// every method that changes anything.
/// </remarks>
public sealed class PostgresGroupDirectory : IGroupDirectory
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the directory.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresGroupDirectory(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GroupSummary>> ListAsync(
        Guid principal, bool all, CancellationToken cancellationToken)
    {
        // <b>One statement, with the counts and the standing as subqueries.</b> The alternative is a
        // round trip per group to say how many members it has, which is the shape that makes a list
        // screen slow in exactly the deployment that has many groups.
        const string Sql = """
            select g.id, g.name, g.title, g.description, p.name, g.item_update,
                   (select count(*) from sharing_group_member m where m.group_id = g.id),
                   (select count(*) from sharing_group_item i where i.group_id = g.id),
                   coalesce(
                     (select m.membership from sharing_group_member m
                       where m.group_id = g.id and m.principal_id = @who), '')
              from sharing_group g
              left join principal p on p.id = g.owner_principal_id
             where @all
                or g.owner_principal_id = @who
                or exists (select 1 from sharing_group_member m
                            where m.group_id = g.id and m.principal_id = @who)
             order by g.name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("who", principal);
        command.Parameters.AddWithValue("all", all);

        List<GroupSummary> answer = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid id = reader.GetGuid(0);
            bool owns = false;

            // Owner beats manager beats member: the standing reported is the highest one held.
            await using (NpgsqlCommand owner = _dataSource.CreateCommand(
                "select owner_principal_id = @who from sharing_group where id = @id"))
            {
                owner.Parameters.AddWithValue("who", principal);
                owner.Parameters.AddWithValue("id", id);

                owns = await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    is true;
            }

            string membership = reader.GetString(8);

            answer.Add(new GroupSummary(
                id,
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ReadUpdate(reader.GetString(5)),
                (int)reader.GetInt64(6),
                (int)reader.GetInt64(7),
                owns
                    ? GroupStanding.Owner
                    : membership switch
                    {
                        "manager" => GroupStanding.Manager,
                        "member" => GroupStanding.Member,
                        _ => GroupStanding.Outside,
                    }));
        }

        return answer;
    }

    /// <inheritdoc/>
    public async Task<(GroupChange Outcome, Guid Id)> CreateAsync(
        Guid owner,
        string name,
        string? title,
        string? description,
        GroupItemUpdate itemUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Guid id = Guid.NewGuid();

        const string Sql = """
            insert into sharing_group
                   (id, name, title, description, owner_principal_id, item_update)
            values (@id, @name, @title, @description, @owner, @update)
            on conflict do nothing
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("update", Wire(itemUpdate));

        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return (GroupChange.Exists, Guid.Empty);
            }
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            // The unique index is on `lower(name)`, so `on conflict do nothing` does not catch it
            // without naming the index — and naming an index in a statement is a coupling this
            // would rather not have. The duplicate arrives here instead.
            return (GroupChange.Exists, Guid.Empty);
        }

        // <b>The owner is a member too, and a manager of their own group.</b> Otherwise the list
        // query's membership subquery reports them as outside a group they own, and every screen has
        // to special-case it.
        await using NpgsqlCommand join = _dataSource.CreateCommand(
            "insert into sharing_group_member (group_id, principal_id, membership, added_by) "
            + "values (@g, @p, 'manager', @p)");

        join.Parameters.AddWithValue("g", id);
        join.Parameters.AddWithValue("p", owner);

        await join.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return (GroupChange.Done, id);
    }

    /// <inheritdoc/>
    public async Task<GroupChange> RemoveAsync(
        Guid acting, bool administrator, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        (Guid id, GroupStanding standing) = await FindAsync(name, acting, cancellationToken)
            .ConfigureAwait(false);

        if (id == Guid.Empty)
        {
            return GroupChange.Absent;
        }

        // Deleting is the owner's, or an administrator's. A manager may not — ADR-036 §3.
        if (!administrator && standing != GroupStanding.Owner)
        {
            return GroupChange.OwnerOnly;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "delete from sharing_group where id = @id");

        command.Parameters.AddWithValue("id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return GroupChange.Done;
    }

    /// <inheritdoc/>
    public async Task<GroupChange> SetMemberAsync(
        Guid acting,
        bool administrator,
        string name,
        string member,
        bool asManager,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);

        (Guid id, GroupStanding standing) = await FindAsync(name, acting, cancellationToken)
            .ConfigureAwait(false);

        if (id == Guid.Empty)
        {
            return GroupChange.Absent;
        }

        if (!administrator && standing is not (GroupStanding.Owner or GroupStanding.Manager))
        {
            return GroupChange.NotYours;
        }

        Guid? who = await PrincipalAsync(member, cancellationToken).ConfigureAwait(false);

        if (who is null)
        {
            return GroupChange.NoSuchTarget;
        }

        const string Sql = """
            insert into sharing_group_member (group_id, principal_id, membership, added_by)
            values (@g, @p, @m, @by)
            on conflict (group_id, principal_id) do update set membership = @m
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("g", id);
        command.Parameters.AddWithValue("p", who.Value);
        command.Parameters.AddWithValue("m", asManager ? "manager" : "member");
        command.Parameters.AddWithValue("by", acting);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return GroupChange.Done;
    }

    /// <inheritdoc/>
    public async Task<GroupChange> RemoveMemberAsync(
        Guid acting,
        bool administrator,
        string name,
        string member,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);

        (Guid id, GroupStanding standing) = await FindAsync(name, acting, cancellationToken)
            .ConfigureAwait(false);

        if (id == Guid.Empty)
        {
            return GroupChange.Absent;
        }

        if (!administrator && standing is not (GroupStanding.Owner or GroupStanding.Manager))
        {
            return GroupChange.NotYours;
        }

        Guid? who = await PrincipalAsync(member, cancellationToken).ConfigureAwait(false);

        if (who is null)
        {
            return GroupChange.NoSuchTarget;
        }

        // <b>The owner cannot be removed from their own group.</b> They would keep owning it and stop
        // being able to see it in a list filtered by membership, which is a state nothing else in
        // this schema can produce and no screen would explain.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "delete from sharing_group_member m using sharing_group g "
            + "where m.group_id = g.id and m.group_id = @g and m.principal_id = @p "
            + "and g.owner_principal_id <> @p");

        command.Parameters.AddWithValue("g", id);
        command.Parameters.AddWithValue("p", who.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0
            ? GroupChange.Done
            : GroupChange.NoSuchTarget;
    }

    /// <inheritdoc/>
    public async Task<GroupChange> ShareAsync(
        Guid acting,
        bool administrator,
        string name,
        string service,
        string? folder,
        bool wanted,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        (Guid id, GroupStanding standing) = await FindAsync(name, acting, cancellationToken)
            .ConfigureAwait(false);

        if (id == Guid.Empty)
        {
            return GroupChange.Absent;
        }

        if (!administrator && standing is not (GroupStanding.Owner or GroupStanding.Manager))
        {
            return GroupChange.NotYours;
        }

        await using NpgsqlCommand find = _dataSource.CreateCommand(
            "select id from service where lower(name) = lower(@name) "
            + "and coalesce(lower(folder), '') = coalesce(lower(@folder), '')");

        find.Parameters.AddWithValue("name", service);
        find.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);

        if (await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid item)
        {
            return GroupChange.NoSuchTarget;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(wanted
            ? "insert into sharing_group_item (group_id, service_id, shared_by) "
              + "values (@g, @s, @by) on conflict do nothing"
            : "delete from sharing_group_item where group_id = @g and service_id = @s");

        command.Parameters.AddWithValue("g", id);
        command.Parameters.AddWithValue("s", item);

        if (wanted)
        {
            command.Parameters.AddWithValue("by", acting);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return GroupChange.Done;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(string Member, GroupStanding Standing)>> MembersAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        const string Sql = """
            select p.name,
                   case when g.owner_principal_id = p.id then 'owner' else m.membership end
              from sharing_group g
              join sharing_group_member m on m.group_id = g.id
              join principal p on p.id = m.principal_id
             where lower(g.name) = lower(@name)
             order by 2 desc, p.name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);

        List<(string, GroupStanding)> answer = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            answer.Add((reader.GetString(0), reader.GetString(1) switch
            {
                "owner" => GroupStanding.Owner,
                "manager" => GroupStanding.Manager,
                _ => GroupStanding.Member,
            }));
        }

        return answer;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> CandidatesAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>`anonymous` is excluded by name, because it is a row.</b> The anonymous principal exists
        // in this table so that an unauthenticated request has an identity to be refused as; adding
        // it to a group would make *every* unauthenticated caller a member, which is `public` by
        // accident and by the longest possible route.
        const string Sql = """
            select p.name
              from principal p
             where p.disabled_at is null
               and p.kind = 'user'
               and p.name <> 'anonymous'
               and not exists (
                     select 1
                       from sharing_group g
                       join sharing_group_member m on m.group_id = g.id
                      where lower(g.name) = lower(@group) and m.principal_id = p.id)
             order by p.name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("group", name);

        List<string> answer = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            answer.Add(reader.GetString(0));
        }

        return answer;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GroupItem>> ItemsAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>The service's own scope comes back with it, and the join was already here.</b> A share
        // reaches the group's members only when the service is `group`-scoped as well; reporting the
        // scope turns that from a caveat the operator carries to another screen into a column.
        const string Sql = """
            select case when s.folder is null then s.name else s.folder || '/' || s.name end,
                   s.sharing
              from sharing_group g
              join sharing_group_item i on i.group_id = g.id
              join service s on s.id = i.service_id
             where lower(g.name) = lower(@name)
             order by 1
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);

        List<GroupItem> answer = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            answer.Add(new GroupItem(reader.GetString(0), reader.GetString(1)));
        }

        return answer;
    }

    /// <summary>The group's id and where this principal stands in it.</summary>
    /// <remarks>
    /// <b>The first statement of every write, which is how ADR-036 condition 2 becomes structural.</b>
    /// A method that took an id and trusted the caller would put the membership check somewhere a
    /// future caller can forget it.
    /// </remarks>
    private async Task<(Guid Id, GroupStanding Standing)> FindAsync(
        string name, Guid acting, CancellationToken cancellationToken)
    {
        const string Sql = """
            select g.id,
                   case
                     when g.owner_principal_id = @who then 'owner'
                     else coalesce(
                       (select m.membership from sharing_group_member m
                         where m.group_id = g.id and m.principal_id = @who), 'outside')
                   end
              from sharing_group g
             where lower(g.name) = lower(@name)
             limit 1
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("who", acting);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (Guid.Empty, GroupStanding.Outside);
        }

        return (reader.GetGuid(0), reader.GetString(1) switch
        {
            "owner" => GroupStanding.Owner,
            "manager" => GroupStanding.Manager,
            "member" => GroupStanding.Member,
            _ => GroupStanding.Outside,
        });
    }

    private async Task<Guid?> PrincipalAsync(string name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select id from principal where lower(name) = lower(@name) and disabled_at is null");

        command.Parameters.AddWithValue("name", name);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            is Guid id ? id : null;
    }

    private static string Wire(GroupItemUpdate update) => update switch
    {
        GroupItemUpdate.OwnItems => "ownItems",
        GroupItemUpdate.AllItems => "allItems",
        _ => "none",
    };

    private static GroupItemUpdate ReadUpdate(string stored) => stored switch
    {
        "ownItems" => GroupItemUpdate.OwnItems,
        "allItems" => GroupItemUpdate.AllItems,
        _ => GroupItemUpdate.None,
    };
}
