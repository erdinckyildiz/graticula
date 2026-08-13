using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Identity;
using Npgsql;
using NpgsqlTypes;

namespace GisServer.Platform.Postgres;

/// <summary><see cref="ISetupStore"/> over the platform store.</summary>
public sealed class PostgresSetupStore : ISetupStore
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store.</summary>
    /// <param name="dataSource">The platform store pool.</param>
    public PostgresSetupStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<bool> HasUsableTokenAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select exists (select 1 from setup_token where used_at is null and expires_at > @now)");
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc/>
    public async Task<string> IssueAsync(
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        string token = SessionToken.Generate();

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "insert into setup_token (id, token_hash, expires_at) values (@id, @hash, @expires)");
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, SessionToken.HashOf(token));
        command.Parameters.AddWithValue("expires", NpgsqlDbType.TimestampTz, expiresAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return token;
    }

    /// <inheritdoc/>
    public async Task<Principal?> RedeemAsync(
        string token,
        string name,
        string? displayName,
        PasswordHash password,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        // The conditional update IS the single-use guarantee. `used_at is null`
        // in the where clause means the second concurrent redemption updates
        // zero rows and gets null back, with no window between checking and
        // acting. Read-then-write here would let two requests each see an unused
        // token and each create an administrator.
        int claimed;
        await using (NpgsqlCommand claim = new(
            """
            update setup_token set used_at = @now
            where token_hash = @hash and used_at is null and expires_at > @now
            """,
            connection,
            transaction))
        {
            claim.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, SessionToken.HashOf(token));
            claim.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);

            claimed = await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        Guid id = Guid.NewGuid();

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

        // Committed together: the token is spent if and only if the
        // administrator exists. A failure between the two — a duplicate name, a
        // dropped connection — rolls the token back to unused, which is the
        // recoverable direction. Spending it without creating anybody would
        // leave a server nobody can ever administer.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Principal(id, PrincipalKind.User, name, displayName, isDisabled: false);
    }
}
