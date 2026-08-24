using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Member administration against the platform store.
/// </summary>
/// <remarks>
/// <b>Its own class, beside <see cref="PostgresIdentityStore"/> rather than inside it</b>, because
/// <see cref="IMemberDirectory"/> is its own port — see that interface for why the two are apart.
/// The connection is the same platform store; nothing else is shared, and that is the point.
/// </remarks>
public sealed class PostgresMemberDirectory : IMemberDirectory
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the directory.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresMemberDirectory(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<Principal?> CreateMemberAsync(
        string name,
        string? displayName,
        PasswordHash password,
        string role,
        string userType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(userType);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        Guid id = Guid.NewGuid();

        // <b>The insert decides whether the name is free, rather than a check before it.</b>
        // `on conflict do nothing` returning no row is the answer: checking first and inserting
        // second leaves a window in which two administrators each see a free name. The same
        // reasoning as the setup token's conditional update, which had to be race-free for one
        // account and is worth being race-free for the rest.
        await using (NpgsqlCommand principal = new(
            """
            insert into principal (id, kind, name, display_name, user_type)
            values (@id, 'user', @name, @display, @type)
            on conflict (name) do nothing
            """,
            connection,
            transaction))
        {
            principal.Parameters.AddWithValue("id", id);
            principal.Parameters.AddWithValue("name", name);
            principal.Parameters.Add(new NpgsqlParameter("display", NpgsqlDbType.Text)
            {
                Value = (object?)displayName ?? DBNull.Value,
            });
            principal.Parameters.AddWithValue("type", userType);

            if (await principal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        await using (NpgsqlCommand credential = new(
            """
            insert into local_credential
                (principal_id, algorithm, parameters, password_hash, must_change)
            values (@principal, @algorithm, @parameters::jsonb, @hash, true)
            """,
            connection,
            transaction))
        {
            credential.Parameters.AddWithValue("principal", id);
            credential.Parameters.AddWithValue("algorithm", password.Algorithm);
            credential.Parameters.AddWithValue("parameters", password.Parameters);
            credential.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, password.Hash);

            await credential.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand grant = new(
            "insert into principal_role (principal_id, role_name) values (@principal, @role)",
            connection,
            transaction))
        {
            grant.Parameters.AddWithValue("principal", id);
            grant.Parameters.AddWithValue("role", role);

            await grant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // All three or none: an account without its role can do nothing, and one without a
        // credential is an account nobody can sign in to. Either would be a member an
        // administrator has to notice is broken.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Principal(id, PrincipalKind.User, name, displayName, isDisabled: false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Member>> ListMembersAsync(CancellationToken cancellationToken)
    {
        // <b>Roles aggregated in SQL rather than one query per member.</b> The schema allows
        // several roles and the listing must not become N+1 the day somebody holds two.
        //
        // <b>And the owned-service count is here for a reason the UI needs:</b> there is no
        // delete on this surface because a member owns content, so the number that explains the
        // absence should be on the row where somebody looks for the button.
        const string Sql = """
            select p.id, p.name, p.display_name, p.user_type, p.disabled_at, p.created_at,
                   coalesce(array_agg(r.role_name) filter (where r.role_name is not null), '{}'),
                   (select count(*) from service s where s.owner_principal_id = p.id)
            from principal p
            left join principal_role r on r.principal_id = p.id
            where p.kind = 'user'
            group by p.id, p.name, p.display_name, p.user_type, p.disabled_at, p.created_at
            order by lower(p.name)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<Member> members = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            members.Add(new Member(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetFieldValue<string[]>(6),
                reader.GetString(3),
                !reader.IsDBNull(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                (int)reader.GetInt64(7)));
        }

        return members;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>?> SetRoleAsync(
        string name, string? role, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        Guid id;

        await using (NpgsqlCommand find = new(
            "select id from principal where name = @name and kind = 'user'",
            connection,
            transaction))
        {
            find.Parameters.AddWithValue("name", name);

            if (await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                is not Guid found)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            id = found;
        }

        List<string> before = [];

        await using (NpgsqlCommand held = new(
            "select role_name from principal_role where principal_id = @principal order by role_name",
            connection,
            transaction))
        {
            held.Parameters.AddWithValue("principal", id);

            await using NpgsqlDataReader reader =
                await held.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                before.Add(reader.GetString(0));
            }
        }

        await using (NpgsqlCommand clear = new(
            "delete from principal_role where principal_id = @principal",
            connection,
            transaction))
        {
            clear.Parameters.AddWithValue("principal", id);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (role is { Length: > 0 })
        {
            await using NpgsqlCommand grant = new(
                "insert into principal_role (principal_id, role_name) values (@principal, @role)",
                connection,
                transaction);

            grant.Parameters.AddWithValue("principal", id);
            grant.Parameters.AddWithValue("role", role);

            await grant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The clear and the grant together, so a failed grant cannot leave a member with no role
        // at all — which on the last administrator would be a server nobody can administer.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return before;
    }

    /// <inheritdoc/>
    public async Task<bool?> SetDisabledAsync(
        string name, bool disabled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Returns what it replaced, in the statement that replaces it: *already disabled* and
        // *disabled just now* are different answers to an administrator revoking access.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update principal
               set disabled_at = case when @disabled then now() else null end
             where name = @name and kind = 'user'
             returning (select q.disabled_at is not null from principal q where q.name = @name)
            """);

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("disabled", disabled);

        object? before = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return before is bool was ? was : null;
    }

    /// <inheritdoc/>
    public async Task<MemberHoldings?> HoldingsOfAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>One round trip, and the names rather than the counts.</b> ADR-015 6c refuses a
        // removal that did not say what to do with what is owned, and the refusal has to name it:
        // *3 services* is not enough for an operator to judge whether transferring is right.
        //
        // <b>Groups are queried now, and this comment used to say there was no table.</b> ADR-036
        // built one on 2026-08-18. §6c wrote the disposition around *owned things* rather than around
        // services so that this would be an addition rather than a redesign, and it was: one more
        // subquery here, and two more statements in each disposition.
        const string Sql = """
            select p.id,
                   coalesce((select array_agg(
                              coalesce(nullif(s.folder, '') || '/', '') || s.name order by s.name)
                             from service s where s.owner_principal_id = p.id), '{}'),
                   coalesce((select array_agg(f.name order by f.name)
                             from folder f where f.owner_principal_id = p.id), '{}'),
                   (select count(*) from sharing_group g
                     where g.owner_principal_id = p.id)
              from principal p
             where lower(p.name) = lower(@name)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MemberHoldings(
            reader.GetFieldValue<string[]>(1),
            reader.GetFieldValue<string[]>(2),
            (int)reader.GetInt64(3));
    }

    /// <inheritdoc/>
    public async Task<int> TransferOwnershipAsync(
        string current, string receiver, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiver);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int moved = await MoveAsync(connection, transaction, current, receiver, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return moved;
    }

    /// <inheritdoc/>
    public async Task<MemberRemoval> TransferAndRemoveAsync(
        string name, string transferTo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(transferTo);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await CheckAsync(connection, transaction, name, transferTo, cancellationToken)
                .ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        await MoveAsync(connection, transaction, name, transferTo, cancellationToken)
            .ConfigureAwait(false);

        await DeletePrincipalAsync(connection, transaction, name, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MemberRemoval.Removed;
    }

    /// <inheritdoc/>
    public async Task<MemberRemoval> RemoveAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await CheckAsync(connection, transaction, name, null, cancellationToken)
                .ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        // <b>Groups are removed here, and services are not — ADR-036 condition 4.</b> Deleting a
        // group takes its membership rows and its shares and touches nothing else: the services
        // shared into it keep existing and are read under their own scope. So there is no tile to
        // purge and no catalogue to notify, and the orchestration the caller does for services would
        // be ceremony. `sharing_group.owner_principal_id` is `on delete restrict`, so this is also
        // the only way the delete disposition can succeed at all for somebody who owns one.
        await using (NpgsqlCommand groups = connection.CreateCommand())
        {
            groups.Transaction = transaction;

            groups.CommandText = """
                delete from sharing_group g
                 using principal p
                 where p.id = g.owner_principal_id and lower(p.name) = lower(@name)
                """;

            groups.Parameters.AddWithValue("name", name);

            await groups.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // <b>Still owning something is a refusal here, not a cascade.</b> The caller disposes of
        // the holdings through the paths that also purge tiles; if anything is left, that
        // orchestration missed something, and the result would be a principal nothing points at --
        // D-66, where the live owner columns carry no foreign key to catch it.
        await using (NpgsqlCommand owned = connection.CreateCommand())
        {
            owned.Transaction = transaction;

            owned.CommandText = """
                select (select count(*) from service s
                         join principal p on p.id = s.owner_principal_id
                        where lower(p.name) = lower(@name))
                     + (select count(*) from folder f
                         join principal p on p.id = f.owner_principal_id
                        where lower(p.name) = lower(@name))
                """;

            owned.Parameters.AddWithValue("name", name);

            long still = (long)(await owned.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false))!;

            if (still > 0)
            {
                return MemberRemoval.HoldsThings;
            }
        }

        await DeletePrincipalAsync(connection, transaction, name, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MemberRemoval.Removed;
    }

    /// <summary>
    /// The refusals both removals share, asked inside their transaction.
    /// </summary>
    /// <returns>A refusal, or null to proceed.</returns>
    /// <remarks>
    /// <b>Inside the transaction, and locking the row, because this is a race.</b> Counting
    /// administrators and then deleting one in a separate statement lets two concurrent removals
    /// each see two administrators and each remove one -- which is how a server ends up with none,
    /// and D-14 records that there is no way back in band.
    /// </remarks>
    private static async Task<MemberRemoval?> CheckAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string name,
        string? transferTo,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // <b>`p.disabled_at is null` on the first column, added 2026-08-23.</b> The count below
        // is of administrators who can still sign in — a disabled one cannot recover a
        // server, so it does not count them. Without the same condition on the left, removing a
        // *disabled* administrator was refused whenever one enabled administrator remained: the
        // member being removed was not in the count, so taking them away could not reduce it,
        // and the refusal fired anyway. A fresh install has exactly one enabled administrator,
        // which made this the ordinary case rather than a corner.
        // [D-101](../../../docs/architecture-debt.md).
        command.CommandText = """
            select p.id,
                   p.disabled_at is null
                   and exists (select 1 from principal_role r
                                where r.principal_id = p.id and r.role_name = 'administrator'),
                   (select count(*) from principal a
                     join principal_role ar on ar.principal_id = a.id
                    where ar.role_name = 'administrator' and a.disabled_at is null)
              from principal p
             where lower(p.name) = lower(@name)
               for no key update of p
            """;

        command.Parameters.AddWithValue("name", name);

        await using (NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return MemberRemoval.Absent;
            }

            if (reader.GetBoolean(1) && reader.GetInt64(2) <= 1)
            {
                return MemberRemoval.LastAdministrator;
            }
        }

        if (transferTo is null)
        {
            return null;
        }

        await using NpgsqlCommand target = connection.CreateCommand();
        target.Transaction = transaction;
        // <b>`disabled_at`, not `is_disabled`.</b> Disabled is a timestamp here, so that the
        // record says *when* rather than only *whether* — and writing the boolean name from
        // memory answered 42703 on every removal until it was read out of the schema.
        target.CommandText =
            "select disabled_at is not null from principal where lower(name) = lower(@to)";
        target.Parameters.AddWithValue("to", transferTo);

        object? disabled = await target.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (disabled is null)
        {
            return MemberRemoval.TargetAbsent;
        }

        return (bool)disabled ? MemberRemoval.TargetDisabled : null;
    }

    /// <summary>Moves every owned thing from one principal to another.</summary>
    /// <remarks>
    /// <b>The dead column moves too, and that is deliberate.</b> `layer.owner_principal_id` has
    /// been vestigial since migration 11 (D-33) and nothing reads it -- but leaving a stale
    /// principal id in a column somebody may one day read is how the next D-24 begins, and it costs
    /// one statement inside a transaction that is open anyway.
    /// </remarks>
    private static async Task<int> MoveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string from,
        string to,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            with giver as (select id from principal where lower(name) = lower(@from)),
                 taker as (select id from principal where lower(name) = lower(@to)),
                 s as (update service set owner_principal_id = (select id from taker)
                        where owner_principal_id = (select id from giver) returning 1),
                 f as (update folder set owner_principal_id = (select id from taker)
                        where owner_principal_id = (select id from giver) returning 1),
                 -- <b>The layer's owner column is not updated here — D-24.</b> A layer's
                 -- owner is its service's owner since migration 11, and `s` above is what
                 -- actually moves it. This CTE wrote `layer.owner_principal_id` to keep a
                 -- dead column in step, which is the opposite of D-24's repayment: keeping
                 -- them in step is what makes them look alive. Its count was never in the
                 -- total below either, so nothing observable changes.

                 -- <b>Groups move too — ADR-036, and ADR-015 6c said this would be an addition.</b>
                 -- The receiver becomes the owner; the group's members and its shares are untouched,
                 -- so nothing stops being readable by anybody.
                 g as (update sharing_group set owner_principal_id = (select id from taker),
                              updated_at = now()
                        where owner_principal_id = (select id from giver) returning id),

                 -- And the new owner is put in the group, as a manager, for the reason the create
                 -- path does it: otherwise they own a group that a membership-filtered list omits.
                 gm as (insert into sharing_group_member (group_id, principal_id, membership, added_by)
                        select g.id, (select id from taker), 'manager', (select id from taker)
                          from g
                        on conflict (group_id, principal_id) do update set membership = 'manager'
                        returning 1)
            select (select count(*) from s) + (select count(*) from f)
                 + (select count(*) from g)
            """;

        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);

        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false))!;
    }

    /// <summary>Removes the principal row, and lets the cascades take the rest.</summary>
    /// <remarks>
    /// The credential, the sessions, the roles and the api keys cascade; the audit trail is
    /// `on delete set null`, so what somebody did survives them without naming them.
    /// </remarks>
    private static async Task DeletePrincipalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string name,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from principal where lower(name) = lower(@name)";
        command.Parameters.AddWithValue("name", name);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> SetPasswordAsync(
        string name, PasswordHash password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>Upsert, because a member may have no credential yet.</b> Federation is coming
        // (D-10) and a principal that arrived from an identity provider has no local row; an
        // update alone would report success having changed nothing.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            insert into local_credential
                (principal_id, algorithm, parameters, password_hash, must_change)
            select p.id, @algorithm, @parameters::jsonb, @hash, true
              from principal p
             where p.name = @name and p.kind = 'user'
            on conflict (principal_id) do update
               set algorithm = @algorithm,
                   parameters = @parameters::jsonb,
                   password_hash = @hash,

                   -- <b>Dirty, always, and not a parameter.</b> Owner rule 2026-08-17: a password
                   -- the system issued and an administrator passed along is one its owner must
                   -- replace. Making it an argument would let a caller ask for a permanent password
                   -- on somebody else's account, which is the thing being removed.
                   must_change = true
            """);

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("algorithm", password.Algorithm);
        command.Parameters.AddWithValue("parameters", password.Parameters);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, password.Hash);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
