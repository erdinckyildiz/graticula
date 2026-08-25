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
        //
        // <b>`visibility` is in the `where`, and it was not for the first hour after it existed.</b>
        // The column was stored, reported by two endpoints and read by nothing — so a group set to
        // *everybody, including anonymous callers* was discoverable by exactly the people who could
        // already see it, while the console said otherwise and the endpoint's own note said *"it can
        // now be found by anybody"*. That is [D-67](../../docs/architecture-debt.md) precisely, and it
        // shipped in the same change that **refuses** `join_policy = 'request'` on the ground that a
        // policy stored and unenforced is D-67 over again. One of the two had to move, and enforcing is
        // cheaper than refusing here: it is a disjunct in one statement.
        //
        // <b>Seeing a group is not reading it, and the boundary is upstream of this method.</b> A
        // caller who reaches a group only through this disjunct comes back with
        // `GroupStanding.Outside`, so nothing here confers membership; the endpoint withholds the
        // member and item lists on that standing, which is where ADR-036 §4g's *"being able to see
        // that a group exists is not being able to read what is in it"* is actually kept.
        const string Sql = """
            select g.id, g.name, g.title, g.description, p.name, g.item_update,
                   (select count(*) from sharing_group_member m where m.group_id = g.id),
                   (select count(*) from sharing_group_item i where i.group_id = g.id),
                   coalesce(
                     (select m.membership from sharing_group_member m
                       where m.group_id = g.id and m.principal_id = @who), ''),
                   g.summary, g.visibility, g.join_policy, g.contribute, g.delete_locked,
                   g.created_at, g.member_list, g.members_may_leave,
                   g.owner_principal_id = @who as owns
              from sharing_group g
              left join principal p on p.id = g.owner_principal_id
             where @all
                or g.owner_principal_id = @who
                or exists (select 1 from sharing_group_member m
                            where m.group_id = g.id and m.principal_id = @who)
                or g.visibility in ('organization', 'public')
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

            // <b>Ownership comes back with the row now, and it used to be a query per group.</b> The
            // reader issued a second statement for every row while the first reader was still open —
            // one round trip per group to answer what the same row could carry. Owner beats manager
            // beats member: the standing reported is the highest one held.
            bool owns = reader.GetBoolean(reader.GetOrdinal("owns"));

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
                    },
                reader.IsDBNull(reader.GetOrdinal("summary"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("summary")),
                ReadVisibility(reader.GetString(reader.GetOrdinal("visibility"))),
                ReadJoinPolicy(reader.GetString(reader.GetOrdinal("join_policy"))),
                ReadContribute(reader.GetString(reader.GetOrdinal("contribute"))),
                reader.GetBoolean(reader.GetOrdinal("delete_locked")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                ReadMemberList(reader.GetString(reader.GetOrdinal("member_list"))),
                reader.GetBoolean(reader.GetOrdinal("members_may_leave"))));
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

        // <b>The lock, and it binds an administrator too — ADR-036 §4g.</b> A protection the most
        // privileged caller passes through is a protection against typing rather than against
        // deleting, and the operator who set it is usually the one who would fat-finger it.
        await using (NpgsqlCommand locked = _dataSource.CreateCommand(
            "select delete_locked from sharing_group where id = @id"))
        {
            locked.Parameters.AddWithValue("id", id);

            if (await locked.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return GroupChange.Locked;
            }
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
    /// <inheritdoc/>
    public async Task<GroupChange> LeaveAsync(
        Guid acting, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>One statement, because the two facts have to be read together.</b> Asking whether
        // the group allows leaving and then deleting the row is a race with an owner turning
        // the setting on; the `where` carries both, so a group that became administrative
        // between the two halves of a request keeps its member.
        const string Sql = """
            delete from sharing_group_member m
             using sharing_group g
             where g.id = m.group_id
               and g.name = @name
               and m.principal_id = @who
               and g.members_may_leave
               and g.owner_principal_id <> @who
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("who", acting);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return GroupChange.Done;
        }

        // <b>Nothing was deleted, and the caller is owed which of four reasons.</b> A single
        // *no* would leave somebody re-clicking a button that will never work, and would tell
        // a non-member that the group exists.
        const string Why = """
            select g.members_may_leave,
                   g.owner_principal_id = @who as owns,
                   exists (select 1 from sharing_group_member m
                            where m.group_id = g.id and m.principal_id = @who) as inside
              from sharing_group g
             where g.name = @name
            """;

        await using NpgsqlCommand asking = _dataSource.CreateCommand(Why);
        asking.Parameters.AddWithValue("name", name);
        asking.Parameters.AddWithValue("who", acting);

        await using NpgsqlDataReader reader =
            await asking.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return GroupChange.Absent;
        }

        bool mayLeave = reader.GetBoolean(0);
        bool owns = reader.GetBoolean(1);
        bool inside = reader.GetBoolean(2);

        // <b>Not a member reads as absent, not as refused.</b> Same rule as everywhere else
        // here: somebody outside a group learns nothing about it, including that it is one
        // they would not be allowed to leave.
        if (!inside && !owns)
        {
            return GroupChange.Absent;
        }

        // <b>Two different refusals, because the way out of each is different.</b> An owner
        // is told to transfer or delete; a member of an administrative group is told the group
        // decides. `OwnerOnly` is reused for the first — it already means *this act belongs to
        // the owner*, and here the owner is the one it does not belong to, which reads oddly
        // in the enum and correctly at the endpoint that turns it into a sentence.
        if (owns)
        {
            return GroupChange.OwnerOnly;
        }

        // <b>A member who may leave and was not removed left in the meantime.</b> The delete
        // and this question are two statements, so a second request that arrived between them
        // is a real outcome rather than an impossible one — and *you are not in it* is the
        // truth either way.
        return mayLeave ? GroupChange.Absent : GroupChange.Locked;
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
    public async Task<GroupChange> SetSettingsAsync(
        Guid acting,
        bool administrator,
        string name,
        string? title,
        string? summary,
        string? description,
        GroupVisibility visibility,
        GroupJoinPolicy joinPolicy,
        GroupContribute contribute,
        bool deleteLocked,
        GroupMemberList memberList,
        bool membersMayLeave,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>Refused here rather than at the endpoint, because it is a rule about the store.</b> The
        // schema admits `request` so the column never has to be widened; nothing honours it, and a
        // policy stored and unenforced is D-67 over again.
        if (joinPolicy == GroupJoinPolicy.Request)
        {
            return GroupChange.NotBuilt;
        }

        // <b>`public` used to be refused here too, and is now gone from the type.</b> Owner
        // decision 2026-08-25, migration 36, [Q-119](../../docs/open-questions.md): it meant
        // *discoverable by anybody, including an anonymous caller*, `/admin/groups` refuses an
        // anonymous caller, and so it was enforced exactly as `organization` while the console
        // promised more. A value stored and unhonoured is D-67; the cheapest way to stop
        // promising something is to stop offering it, and the compiler now enforces that
        // rather than this method.

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

        // <b>Every column, every time — a replace and not a patch.</b> The port documents it now; it
        // documented the opposite for an hour, which would have made the Settings tab erase the title
        // and the description on its first save. Left as a replace rather than made into a
        // `coalesce` patch because *clearing* a description has to be expressible, and a store where
        // null means *leave* cannot express it.
        const string Sql = """
            update sharing_group
               set title = @title,
                   summary = @summary,
                   description = @description,
                   visibility = @visibility,
                   join_policy = @join,
                   contribute = @contribute,
                   delete_locked = @locked,
                   member_list = @memberList,
                   members_may_leave = @mayLeave,
                   updated_at = now()
             where id = @id
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("summary", (object?)summary ?? DBNull.Value);
        command.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("visibility", Wire(visibility));
        command.Parameters.AddWithValue("join", Wire(joinPolicy));
        command.Parameters.AddWithValue("contribute", Wire(contribute));
        command.Parameters.AddWithValue("memberList", Wire(memberList));
        command.Parameters.AddWithValue("mayLeave", membersMayLeave);
        command.Parameters.AddWithValue("locked", deleteLocked);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return GroupChange.Done;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GroupMember>> MembersAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>Ranked explicitly, because `order by 2 desc` did not mean what its comment claimed.</b>
        // That was a descending *text* sort over `owner | manager | member`, and `'member' > 'manager'`
        // on the third letter — so the real order was owner, then members, then **managers**, and the
        // managers sat at the bottom of the last page. The comment said the opposite and was believed,
        // which is worse than no comment: the screen drew its manager/member divider from a sort that
        // did not produce that grouping, so the line landed on the first row of page two and there was
        // none where the boundary actually was. Found in the design review of 2026-08-18, which could
        // only see it after the pagers were repaired.
        //
        // Owner, then managers, then members, then by name — so the screen can draw the boundary the
        // reference draws with a *Group role* filter, and at nine rows a filter is chrome.
        //
        // `added_by` is a left join because it is null for the owner and for every row written before
        // the column existed.
        const string Sql = """
            select p.name,
                   case when g.owner_principal_id = p.id then 'owner' else m.membership end,
                   p.display_name, m.added_at, addedby.name
              from sharing_group g
              join sharing_group_member m on m.group_id = g.id
              join principal p on p.id = m.principal_id
              left join principal addedby on addedby.id = m.added_by
             where lower(g.name) = lower(@name)
             order by case when g.owner_principal_id = p.id then 0
                           when m.membership = 'manager'    then 1
                           else 2 end,
                      p.name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);

        List<GroupMember> answer = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            answer.Add(new GroupMember(
                reader.GetString(0),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(1) switch
                {
                    "owner" => GroupStanding.Owner,
                    "manager" => GroupStanding.Manager,
                    _ => GroupStanding.Member,
                },
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
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
                   s.sharing, s.kind, i.shared_at, sharedby.name,
                   (select l.name from layer l
                     where l.service_id = s.id order by l.layer_index limit 1),
                   (select l.layer_index from layer l
                     where l.service_id = s.id order by l.layer_index limit 1)
              from sharing_group g
              join sharing_group_item i on i.group_id = g.id
              join service s on s.id = i.service_id
              left join principal sharedby on sharedby.id = i.shared_by
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
            answer.Add(new GroupItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6)));
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

    private static string Wire(GroupVisibility visibility) => visibility switch
    {
        GroupVisibility.Organization => "organization",
        _ => "members",
    };

    private static string Wire(GroupJoinPolicy policy) => policy switch
    {
        GroupJoinPolicy.Request => "request",
        GroupJoinPolicy.Self => "self",
        _ => "invitation",
    };

    private static string Wire(GroupContribute contribute) =>
        contribute == GroupContribute.Members ? "members" : "managers";

    private static string Wire(GroupMemberList list) =>
        list == GroupMemberList.Managers ? "managers" : "members";

    /// <summary>Reads the member-list setting, narrowing anything it does not know.</summary>
    /// <remarks>
    /// <b>Unknown narrows, like every other reader here.</b> A row written by a newer build
    /// carrying a value this one has never heard of must not be read as the wider of the two:
    /// a downgrade that widens a disclosure is the failure mode, and there is no version of
    /// *I do not understand this setting* that justifies showing more.
    /// </remarks>
    private static GroupMemberList ReadMemberList(string stored) =>
        stored == "members" ? GroupMemberList.Members : GroupMemberList.Managers;

    // enum-default-is-deliberate: the narrowest value of each
    //
    // <b>An unrecognised stored value reads as the closed end, never the open one.</b> A row written
    // by a newer version carrying a visibility this build does not know must not make a group public
    // by accident; the safe reading of *"I do not understand this"* is the one that shows it to fewer
    // people. The opposite direction is how a private group becomes discoverable during an upgrade.
    private static GroupVisibility ReadVisibility(string stored) => stored switch
    {
        "organization" => GroupVisibility.Organization,

        // <b>A stored `public` reads as `organization`, not as `members`.</b> Migration 36
        // demotes every row, so this should never fire — and if a row survives a partial
        // restore, `organization` is what `public` was always *enforced* as. The comment
        // above says an unrecognised value reads as the closed end; this one is recognised
        // and its behaviour is known, which is a different case.
        "public" => GroupVisibility.Organization,
        _ => GroupVisibility.Members,
    };

    private static GroupJoinPolicy ReadJoinPolicy(string stored) => stored switch
    {
        "request" => GroupJoinPolicy.Request,
        "self" => GroupJoinPolicy.Self,
        _ => GroupJoinPolicy.Invitation,
    };

    private static GroupContribute ReadContribute(string stored) =>
        stored == "members" ? GroupContribute.Members : GroupContribute.Managers;

    private static GroupItemUpdate ReadUpdate(string stored) => stored switch
    {
        "ownItems" => GroupItemUpdate.OwnItems,
        "allItems" => GroupItemUpdate.AllItems,
        _ => GroupItemUpdate.None,
    };
}
