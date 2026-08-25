using System;
using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// <see cref="IIdentityStore"/> over the platform store.
/// </summary>
/// <remarks>
/// A Tier 2 adapter: every Npgsql type stops here. The sequencing that makes
/// login safe lives in <see cref="LoginService"/>, which knows nothing about
/// this class — so the security-critical ordering is testable without a
/// database, and this file only has to be correct about SQL.
/// </remarks>
public sealed class PostgresIdentityStore : IIdentityStore
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store.</summary>
    /// <param name="dataSource">The platform store pool.</param>
    public PostgresIdentityStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<AuthenticatedSession?> FindSessionAsync(
        byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        // Every reason to refuse is in the where clause, so unknown, expired,
        // revoked and disabled are one indistinguishable outcome. Returning the
        // row and deciding in C# would make it trivially easy to log or report
        // the difference, and the difference is only useful to someone probing.
        // <b>The must-change flag rides on this query rather than costing a second one.</b> It
        // governs what the caller may do, so it is read per request like sharing and
        // started/stopped — three defects this month came from caching a fact of that kind, and
        // stamping it into the token would be the fourth. A left join because a principal from an
        // identity provider has no local credential, and *no password* is not *a dirty password*.
        const string Sql = """
            select s.id, s.expires_at, p.id, p.kind, p.name, p.display_name,
                   coalesce(c.must_change, false)
            from session s
            join principal p on p.id = s.principal_id
            left join local_credential c on c.principal_id = p.id
            where s.token_hash = @hash
              and s.revoked_at is null
              and s.expires_at > @now
              and p.disabled_at is null
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, tokenHash);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AuthenticatedSession(
            reader.GetGuid(0),
            ReadPrincipal(reader, idOrdinal: 2, isDisabled: false),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetBoolean(6));
    }

    /// <inheritdoc/>
    public async Task<(Principal Principal, PasswordHash? Credential)?> FindForLoginAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        const string Sql = """
            select p.id, p.kind, p.name, p.display_name, p.disabled_at,
                   c.algorithm, c.parameters, c.password_hash
            from principal p
            left join local_credential c on c.principal_id = p.id
            where p.name = @name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        Principal principal = ReadPrincipal(reader, idOrdinal: 0, isDisabled: !reader.IsDBNull(4));

        PasswordHash? credential = reader.IsDBNull(5)
            ? null
            : new PasswordHash(reader.GetString(5), reader.GetString(6), reader.GetFieldValue<byte[]>(7));

        return (principal, credential);
    }

    /// <inheritdoc/>
    public async Task<FailureCounts> CountRecentFailuresAsync(
        string name, IPAddress? address, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        // One round trip for both counts. They are always read together and the
        // window is the same, so two queries would be two chances for the window
        // to shift between them.
        const string Sql = """
            select
              count(*) filter (where attempted_name = @name) as for_account,
              count(*) filter (where @address is not null and source_address = @address) as for_address
            from login_attempt
            where succeeded = false and attempted_at >= @since
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("since", NpgsqlDbType.TimestampTz, since);
        command.Parameters.Add(new NpgsqlParameter("address", NpgsqlDbType.Inet)
        {
            Value = (object?)address ?? DBNull.Value,
        });

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new FailureCounts((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    /// <inheritdoc/>
    public async Task RecordAttemptAsync(
        string name, IPAddress? address, bool succeeded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        const string Sql = """
            insert into login_attempt (id, attempted_name, source_address, succeeded)
            values (@id, @name, @address, @succeeded)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("succeeded", succeeded);
        command.Parameters.Add(new NpgsqlParameter("address", NpgsqlDbType.Inet)
        {
            Value = (object?)address ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Guid> CreateSessionAsync(
        Guid principalId,
        byte[] tokenHash,
        DateTimeOffset expiresAt,
        IPAddress? address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        const string Sql = """
            insert into session (id, principal_id, token_hash, expires_at, source_address)
            values (@id, @principal, @hash, @expires, @address)
            """;

        Guid id = Guid.NewGuid();

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, tokenHash);
        command.Parameters.AddWithValue("expires", NpgsqlDbType.TimestampTz, expiresAt);
        command.Parameters.Add(new NpgsqlParameter("address", NpgsqlDbType.Inet)
        {
            Value = (object?)address ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc/>
    public async Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        // coalesce, so revoking twice does not move the timestamp. The first
        // revocation is the one that matters for audit.
        const string Sql =
            "update session set revoked_at = coalesce(revoked_at, now()) where id = @id";

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", sessionId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SetPasswordAsync(
        Guid principalId, PasswordHash hash, CancellationToken cancellationToken)
    {
        const string Sql = """
            insert into local_credential (principal_id, algorithm, parameters, password_hash)
            values (@principal, @algorithm, @parameters::jsonb, @hash)
            on conflict (principal_id) do update
              set algorithm = excluded.algorithm,
                  parameters = excluded.parameters,
                  password_hash = excluded.password_hash,

                  -- <b>Set by its owner, so it is clean.</b> This is the only path that clears the
                  -- flag, and that is the whole enforcement: an administrator's reset sets it (see
                  -- PostgresMemberDirectory) and nothing else can unset it, so the only way out of
                  -- *must change* is the member changing it.
                  must_change = false,
                  updated_at = now()
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("algorithm", hash.Algorithm);
        command.Parameters.AddWithValue("parameters", hash.Parameters);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, hash.Hash);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> AnyUserExistsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            _dataSource.CreateCommand("select exists (select 1 from principal where kind = 'user')");

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc/>
    public async Task<Principal> CreateUserAsync(
        string name, string? displayName, PasswordHash password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Guid id = Guid.NewGuid();

        // One transaction. A principal that exists with no credential is an
        // account nobody can log into and nobody can see is broken.
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        await using (NpgsqlCommand principal = new(
            "insert into principal (id, kind, name, display_name) values (@id, 'user', @name, @display)",
            connection,
            transaction))
        {
            principal.Parameters.AddWithValue("id", id);
            principal.Parameters.AddWithValue("name", name);
            principal.Parameters.Add(new NpgsqlParameter("display", NpgsqlDbType.Text)
            {
                Value = (object?)displayName ?? DBNull.Value,
            });

            await principal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Principal(id, PrincipalKind.User, name, displayName, isDisabled: false);
    }

    /// <inheritdoc/>
    public async Task<int> RevokeOtherSessionsAsync(
        Guid principalId, Guid? keep, CancellationToken cancellationToken)
    {
        // Only the live ones, and coalesce so a session revoked earlier keeps
        // its original timestamp — the first revocation is the one an audit
        // trail is about.
        const string Sql = """
            update session set revoked_at = coalesce(revoked_at, now())
            where principal_id = @principal
              and revoked_at is null
              and (@keep is null or id <> @keep)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.Add(new NpgsqlParameter("keep", NpgsqlDbType.Uuid)
        {
            Value = (object?)keep ?? DBNull.Value,
        });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> RolesOfAsync(
        Guid principalId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select role_name from principal_role where principal_id = @principal order by role_name");
        command.Parameters.AddWithValue("principal", principalId);

        List<string> roles = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }

    /// <inheritdoc/>
    public async Task GrantRoleAsync(
        Guid principalId, string role, Guid? grantedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        // Idempotent by conflict rather than by checking first. Granting a role
        // twice is not an error worth reporting, and do-nothing keeps the
        // original granted_at, which is the one an audit trail is about.
        const string Sql = """
            insert into principal_role (principal_id, role_name, granted_by)
            values (@principal, @role, @by)
            on conflict (principal_id, role_name) do nothing
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid)
        {
            Value = (object?)grantedBy ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RevokeRoleAsync(
        Guid principalId, string role, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "delete from principal_role where principal_id = @principal and role_name = @role");
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("role", role);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<(string UserType, IReadOnlyList<string> Roles, IReadOnlyList<Guid> Groups,
                       IReadOnlyList<Guid> EditableGroups)> GrantsOfAsync(
        Guid principalId, CancellationToken cancellationToken)
    {
        // A left join, so a principal with no roles still yields its user type.
        // An inner join would return no rows for exactly the common case — a
        // brand-new account — and the ceiling would silently read as unknown,
        // which UserTypes.CeilingOf treats as nothing.
        const string Sql = """
            select p.user_type,
                   r.role_name,

                   -- <b>Aggregated rather than joined.</b> A third join would multiply the rows by
                   -- roles times groups and make the reader deduplicate; the subquery returns one
                   -- array per row and the reader takes it from the first.
                   (select coalesce(array_agg(m.group_id), '{}')
                      from sharing_group_member m
                     where m.principal_id = p.id),

                   -- <b>Shared update, and it is a second array rather than a second query.</b>
                   -- ADR-036 §4a as amended 2026-08-25: a group whose `item_update` is `allItems`
                   -- lets every member edit what is shared with it. Read here because this row is
                   -- already being fetched on every authenticated request, and an edit-time lookup
                   -- would put a round trip on the write path to answer a question the read path
                   -- had the answer to.
                   (select coalesce(array_agg(m.group_id), '{}')
                      from sharing_group_member m
                      join sharing_group g on g.id = m.group_id
                     where m.principal_id = p.id and g.item_update = 'allItems')
            from principal p
            left join principal_role r on r.principal_id = p.id
            where p.id = @principal
            order by r.role_name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("principal", principalId);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        string userType = UserTypes.Unrestricted;
        List<string> roles = [];
        Guid[] groups = [];
        Guid[] editable = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            userType = reader.GetString(0);

            if (!reader.IsDBNull(1))
            {
                roles.Add(reader.GetString(1));
            }

            // The same array on every row, because it is a subquery over the principal — taken from
            // whichever row arrives rather than from the first, so a principal with no roles still
            // gets their groups.
            groups = reader.GetFieldValue<Guid[]>(2);
            editable = reader.GetFieldValue<Guid[]>(3);
        }

        return (userType, roles, groups, editable);
    }

    /// <inheritdoc/>
    public async Task<bool> AnyPrincipalHoldingAsync(
        string role, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select exists (select 1 from principal_role where role_name = @role)");
        command.Parameters.AddWithValue("role", role);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static Principal ReadPrincipal(NpgsqlDataReader reader, int idOrdinal, bool isDisabled) =>
        new(
            reader.GetGuid(idOrdinal),
            ParseKind(reader.GetString(idOrdinal + 1)),
            reader.GetString(idOrdinal + 2),
            reader.IsDBNull(idOrdinal + 3) ? null : reader.GetString(idOrdinal + 3),
            isDisabled);

    private static PrincipalKind ParseKind(string kind) => kind switch
    {
        "user" => PrincipalKind.User,
        "service" => PrincipalKind.Service,
        "anonymous" => PrincipalKind.Anonymous,

        // The column has a check constraint listing exactly these three, so
        // reaching this means the database and this build disagree about what a
        // principal is. Guessing would silently grant or deny.
        _ => throw new InvalidOperationException(
            $"The principal kind '{kind}' is not one this build knows. The platform store has "
            + "been written by a different version of the server."),
    };
}
