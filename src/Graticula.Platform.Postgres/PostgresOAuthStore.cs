using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Registered apps, codes and refresh tokens in the platform store — ADR-076, migration 51.
/// </summary>
public sealed class PostgresOAuthStore : IOAuthStore
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Uses the platform store.</summary>
    /// <param name="dataSource">Its data source.</param>
    public PostgresOAuthStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OAuthApp>> ListAppsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select client_id, title, redirect_uris, builtin, created_at from oauth_app order by lower(title), client_id");

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<OAuthApp> apps = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            apps.Add(ReadApp(reader));
        }

        return apps;
    }

    /// <inheritdoc/>
    public async Task<OAuthApp?> FindAppAsync(string clientId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientId);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select client_id, title, redirect_uris, builtin, created_at from oauth_app where client_id = @id");
        command.Parameters.AddWithValue("id", clientId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadApp(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<bool> CreateAppAsync(
        string clientId, string title, IReadOnlyList<string> redirectUris, Guid createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(redirectUris);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "insert into oauth_app (client_id, title, redirect_uris, created_by) "
            + "values (@id, @title, @uris, @by) on conflict (client_id) do nothing");

        command.Parameters.AddWithValue("id", clientId);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("uris", NpgsqlDbType.Array | NpgsqlDbType.Text, redirectUris);
        command.Parameters.AddWithValue("by", createdBy);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAppAsync(string clientId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientId);

        // Codes and refresh tokens go with it through `on delete cascade`; access tokens are
        // sessions and live out their own short lifetime (ADR-076 §5).
        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from oauth_app where client_id = @id");
        command.Parameters.AddWithValue("id", clientId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task IssueCodeAsync(
        byte[] codeHash,
        string clientId,
        Guid principalId,
        string redirectUri,
        string? challenge,
        string? challengeMethod,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "insert into oauth_code (code_hash, client_id, principal_id, redirect_uri, challenge, challenge_method, expires_at) "
            + "values (@hash, @client, @principal, @redirect, @challenge, @method, @expires)");

        command.Parameters.AddWithValue("hash", codeHash);
        command.Parameters.AddWithValue("client", clientId);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("redirect", redirectUri);
        command.Parameters.AddWithValue("challenge", (object?)challenge ?? DBNull.Value);
        command.Parameters.AddWithValue("method", (object?)challengeMethod ?? DBNull.Value);
        command.Parameters.AddWithValue("expires", expiresAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>One statement decides first use, so two exchanges racing for one code cannot both win.</b>
    /// The update succeeds for exactly one of them; the other finds the row already used and is
    /// treated as the replay it is — which revokes what the winner was issued, because a code that
    /// two parties hold has leaked and neither can be trusted with a long-lived token.
    /// </remarks>
    public async Task<(CodeRedemption Outcome, OAuthCode? Code)> RedeemCodeAsync(
        byte[] codeHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codeHash);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand redeem = new(
            """
            update oauth_code c set used_at = @now
            from principal p
            where c.code_hash = @hash and c.used_at is null and c.expires_at > @now and p.id = c.principal_id
            returning c.client_id, c.redirect_uri, c.challenge, c.challenge_method,
                      p.id, p.kind, p.name, p.display_name, p.disabled_at
            """,
            connection))
        {
            redeem.Parameters.AddWithValue("hash", codeHash);
            redeem.Parameters.AddWithValue("now", now);

            await using NpgsqlDataReader reader = await redeem.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Principal? principal = reader.IsDBNull(8)
                    ? new Principal(
                        reader.GetGuid(4),
                        reader.GetString(5) == "service" ? PrincipalKind.Service : PrincipalKind.User,
                        reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        isDisabled: false)
                    : null;

                return (CodeRedemption.Redeemed, new OAuthCode(
                    reader.GetString(0),
                    principal,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        await using NpgsqlCommand used = new(
            """
            with was as (select 1 from oauth_code where code_hash = @hash and used_at is not null)
            update oauth_refresh set revoked_at = coalesce(revoked_at, @now)
            where from_code_hash = @hash and exists (select 1 from was)
            returning 1
            """,
            connection);

        used.Parameters.AddWithValue("hash", codeHash);
        used.Parameters.AddWithValue("now", now);
        await used.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand known = new(
            "select used_at is not null from oauth_code where code_hash = @hash", connection);
        known.Parameters.AddWithValue("hash", codeHash);

        return await known.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true
            ? (CodeRedemption.Replayed, null)
            : (CodeRedemption.Unknown, null);
    }

    /// <inheritdoc/>
    public async Task IssueRefreshAsync(
        byte[] tokenHash,
        string clientId,
        Guid principalId,
        DateTimeOffset expiresAt,
        byte[]? fromCodeHash,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "insert into oauth_refresh (token_hash, client_id, principal_id, expires_at, from_code_hash) "
            + "values (@hash, @client, @principal, @expires, @code)");

        command.Parameters.AddWithValue("hash", tokenHash);
        command.Parameters.AddWithValue("client", clientId);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("expires", expiresAt);
        command.Parameters.AddWithValue("code", NpgsqlDbType.Bytea, (object?)fromCodeHash ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<(string ClientId, Principal Principal, DateTimeOffset ExpiresAt)?> FindRefreshAsync(
        byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        // Every reason to refuse in the where clause, as the session lookup does: unknown, expired,
        // revoked and a disabled account are one answer.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            select r.client_id, r.expires_at, p.id, p.kind, p.name, p.display_name
            from oauth_refresh r
            join principal p on p.id = r.principal_id
            where r.token_hash = @hash and r.revoked_at is null and r.expires_at > @now and p.disabled_at is null
            """);

        command.Parameters.AddWithValue("hash", tokenHash);
        command.Parameters.AddWithValue("now", now);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (
            reader.GetString(0),
            new Principal(
                reader.GetGuid(2),
                reader.GetString(3) == "service" ? PrincipalKind.Service : PrincipalKind.User,
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                isDisabled: false),
            reader.GetFieldValue<DateTimeOffset>(1));
    }

    /// <inheritdoc/>
    public async Task RevokeRefreshAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update oauth_refresh set revoked_at = coalesce(revoked_at, now()) where token_hash = @hash");
        command.Parameters.AddWithValue("hash", tokenHash);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OAuthApp ReadApp(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetFieldValue<string[]>(2),
            reader.GetBoolean(3),
            reader.GetFieldValue<DateTimeOffset>(4));
}
