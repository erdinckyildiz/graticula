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
            insert into local_credential (principal_id, algorithm, parameters, password_hash)
            values (@principal, @algorithm, @parameters::jsonb, @hash)
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
    public async Task<bool> SetPasswordAsync(
        string name, PasswordHash password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // <b>Upsert, because a member may have no credential yet.</b> Federation is coming
        // (D-10) and a principal that arrived from an identity provider has no local row; an
        // update alone would report success having changed nothing.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            insert into local_credential (principal_id, algorithm, parameters, password_hash)
            select p.id, @algorithm, @parameters::jsonb, @hash
              from principal p
             where p.name = @name and p.kind = 'user'
            on conflict (principal_id) do update
               set algorithm = @algorithm,
                   parameters = @parameters::jsonb,
                   password_hash = @hash
            """);

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("algorithm", password.Algorithm);
        command.Parameters.AddWithValue("parameters", password.Parameters);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, password.Hash);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
