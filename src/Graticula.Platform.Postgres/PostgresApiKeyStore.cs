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
/// Service accounts and their API keys in the platform store — ADR-200, on the <c>api_key</c> table migration 2 made
/// and nothing wrote until now.
/// </summary>
/// <remarks>
/// <b>A service account is a principal of kind <c>service</c></b>, so its roles, its user type and every grant are
/// read by the paths a member's are; the member screens list <c>kind = 'user'</c> and never show it. Its description
/// is the principal's display name.
/// </remarks>
public sealed class PostgresApiKeyStore : IApiKeyStore
{
    private readonly NpgsqlDataSource _root;

    /// <summary>The tenant's connections where the deployment holds several (ADR-204), otherwise the store's own.</summary>
    private NpgsqlDataSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Creates the store.</summary>
    public PostgresApiKeyStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<ServiceAccount?> CreateServiceAccountAsync(
        string name, string? description, string userType, IReadOnlyList<string> roles, Guid createdBy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(roles);

        Guid id = Guid.NewGuid();

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand principal = new(
            """
            insert into principal (id, kind, name, display_name, user_type)
            values (@id, 'service', @name, @description, @type)
            on conflict (name, tenant_id) do nothing
            """, connection, transaction))
        {
            principal.Parameters.AddWithValue("id", id);
            principal.Parameters.AddWithValue("name", name);
            principal.Parameters.Add(new NpgsqlParameter("description", NpgsqlDbType.Text) { Value = (object?)description ?? DBNull.Value });
            principal.Parameters.AddWithValue("type", userType);

            if (await principal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return null;
            }
        }

        foreach (string role in roles)
        {
            await using NpgsqlCommand grant = new(
                """
                insert into principal_role (principal_id, role_name, granted_by)
                values (@principal, graticula_role_named(@role), @by)
                on conflict (principal_id, role_name) do nothing
                """, connection, transaction);
            grant.Parameters.AddWithValue("principal", id);
            grant.Parameters.AddWithValue("role", role);
            grant.Parameters.AddWithValue("by", createdBy);
            await grant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ServiceAccount(id, name, description, userType, [.. roles], DateTimeOffset.UtcNow, Disabled: false, Keys: 0);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ServiceAccount>> ListServiceAccountsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            select p.id, p.name, p.display_name, p.user_type, p.created_at, p.disabled_at is not null,
                   coalesce(array(select graticula_role_label(r.role_name) from principal_role r where r.principal_id = p.id
                                  order by r.role_name), '{}'),
                   (select count(*) from api_key k where k.principal_id = p.id and k.kind = 'key' and k.revoked_at is null
                       and (k.expires_at is null or k.expires_at > now())),
                   (select count(*) from api_key k where k.principal_id = p.id and k.kind = 'secret' and k.revoked_at is null
                       and (k.expires_at is null or k.expires_at > now()))
              from principal p
             where p.kind = 'service'
             order by p.name
            """);

        List<ServiceAccount> accounts = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            accounts.Add(new ServiceAccount(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<string[]>(6),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetBoolean(5),
                (int)reader.GetInt64(7),
                (int)reader.GetInt64(8)));
        }

        return accounts;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteServiceAccountAsync(string name, CancellationToken cancellationToken)
    {
        // The keys go by cascade (api_key.principal_id references principal on delete cascade).
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "delete from principal where name = @name and kind = 'service'");
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetDisabledAsync(string name, bool disabled, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update principal set disabled_at = case when @disabled then coalesce(disabled_at, now()) else null end
             where name = @name and kind = 'service'
            """);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("disabled", disabled);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public Task<ApiKeyInfo?> IssueAsync(
        string account, string keyName, byte[] hash, DateTimeOffset? expiresAt, CancellationToken cancellationToken) =>
        IssueKindAsync("key", account, keyName, hash, expiresAt, cancellationToken);

    /// <inheritdoc/>
    public Task<ApiKeyInfo?> IssueSecretAsync(
        string account, string secretName, byte[] hash, DateTimeOffset? expiresAt, CancellationToken cancellationToken) =>
        IssueKindAsync("secret", account, secretName, hash, expiresAt, cancellationToken);

    private async Task<ApiKeyInfo?> IssueKindAsync(
        string kind, string account, string keyName, byte[] hash, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hash);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            insert into api_key (id, principal_id, name, token_hash, expires_at, kind)
            select @id, p.id, @key, @hash, @expires, @kind from principal p where p.name = @account and p.kind = 'service'
            on conflict (principal_id, name) do nothing
            returning created_at
            """);
        Guid id = Guid.NewGuid();
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("key", keyName);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = (object?)expiresAt ?? DBNull.Value });
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("kind", kind);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is DateTime created
            ? new ApiKeyInfo(id, keyName, new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc)), expiresAt, null, null)
            : null;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ApiKeyInfo>?> KeysAsync(string account, CancellationToken cancellationToken) =>
        ListKindAsync("key", account, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<ApiKeyInfo>?> SecretsAsync(string account, CancellationToken cancellationToken) =>
        ListKindAsync("secret", account, cancellationToken);

    private async Task<IReadOnlyList<ApiKeyInfo>?> ListKindAsync(string kind, string account, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            select p.id, k.id, k.name, k.created_at, k.expires_at, k.revoked_at, k.last_used_at
              from principal p
              left join api_key k on k.principal_id = p.id and k.kind = @kind
             where p.name = @account and p.kind = 'service'
             order by k.created_at
            """);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("kind", kind);

        List<ApiKeyInfo>? keys = null;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys ??= [];

            if (reader.IsDBNull(1))
            {
                continue;
            }

            keys.Add(new ApiKeyInfo(
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return keys;
    }

    /// <inheritdoc/>
    public Task<bool> RevokeAsync(string account, Guid key, DateTimeOffset at, CancellationToken cancellationToken) =>
        RevokeKindAsync("key", account, key, at, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> RevokeSecretAsync(string account, Guid secret, DateTimeOffset at, CancellationToken cancellationToken) =>
        RevokeKindAsync("secret", account, secret, at, cancellationToken);

    private async Task<bool> RevokeKindAsync(string kind, string account, Guid key, DateTimeOffset at, CancellationToken cancellationToken)
    {
        // <b>A secret's tokens go with it</b> (ADR-200 condition 2), in the same statement: a revocation that left the
        // tokens it had already bought alive for their lifetime would be a revocation in name. A key has none: it is the
        // token, so for a key the second half touches nothing.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            with revoked as (
                update api_key k set revoked_at = @at
                  from principal p
                 where k.id = @key and k.principal_id = p.id and p.name = @account and p.kind = 'service'
                   and k.kind = @kind and k.revoked_at is null
                returning k.id
            ), ended as (
                update session s set revoked_at = @at
                 where s.credential_id in (select id from revoked) and s.revoked_at is null
                returning s.id
            )
            select (select count(*) from revoked), (select count(*) from ended)
            """);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("kind", kind);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetInt64(0) > 0;
    }

    /// <inheritdoc/>
    public async Task<(Guid Key, Principal Principal, DateTimeOffset? ExpiresAt)?> FindAsync(
        byte[] hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hash);

        // Every reason to refuse is in the where clause, as FindSessionAsync's are: unknown, revoked, expired and a
        // disabled account are one outcome.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            select k.id, k.expires_at, p.id, p.name, p.display_name
              from api_key k
              join principal p on p.id = k.principal_id
             where k.token_hash = @hash
               and k.revoked_at is null
               and (k.expires_at is null or k.expires_at > @now)
               and k.kind = 'key'
               and p.kind = 'service'
               and p.disabled_at is null
            """);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("now", now);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (
            reader.GetGuid(0),
            new Principal(reader.GetGuid(2), PrincipalKind.Service, reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), isDisabled: false),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    /// <inheritdoc/>
    public async Task TouchAsync(Guid key, DateTimeOffset at, CancellationToken cancellationToken)
    {
        // A write a minute at most per key, so a script's thousand requests are one row update, not a thousand.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update api_key set last_used_at = @at
             where id = @key and (last_used_at is null or last_used_at < @at - interval '1 minute')
            """);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("at", at);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<(Guid Secret, Principal Principal, DateTimeOffset? ExpiresAt)?> FindSecretAsync(
        string account, byte[] hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(hash);

        // As FindAsync: unknown, another account's, revoked, expired and a disabled account are one outcome. The use is
        // recorded in the same statement, a minute at most, as a key's is.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            with found as (
                select k.id, k.expires_at, p.id as principal, p.name, p.display_name
                  from api_key k
                  join principal p on p.id = k.principal_id
                 where k.token_hash = @hash
                   and k.kind = 'secret'
                   and k.revoked_at is null
                   and (k.expires_at is null or k.expires_at > @now)
                   and p.kind = 'service'
                   and p.name = @account
                   and p.disabled_at is null
            ), touched as (
                update api_key set last_used_at = @now
                 where id in (select id from found) and (last_used_at is null or last_used_at < @now - interval '1 minute')
            )
            select id, expires_at, principal, name, display_name from found
            """);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("account", account);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (
            reader.GetGuid(0),
            new Principal(reader.GetGuid(2), PrincipalKind.Service, reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), isDisabled: false),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    /// <inheritdoc/>
    public async Task LinkSessionAsync(Guid session, Guid secret, CancellationToken cancellationToken)
    {
        // A secret revoked between its lookup and this link takes the new session with it: the revocation's own
        // statement found no session to end, so this one ends it.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update session s
               set credential_id = @secret,
                   revoked_at = coalesce(s.revoked_at, (select k.revoked_at from api_key k where k.id = @secret))
             where s.id = @session
            """);
        command.Parameters.AddWithValue("session", session);
        command.Parameters.AddWithValue("secret", secret);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
