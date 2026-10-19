using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>Second factors in the platform store — ADR-201, migration 92's <c>principal_second_factor</c>.</summary>
/// <remarks>
/// <b>Spending is a conditional update</b>, so two requests carrying the same code cannot both pass: the step only moves
/// forward, and a recovery code is removed from the array by the statement that checks it is there.
/// </remarks>
public sealed class PostgresSecondFactorStore : ISecondFactorStore
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store.</summary>
    public PostgresSecondFactorStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// The members a requirement covers who have no second factor on, so will be asked to set one up at their next
    /// sign-in — ADR-201 condition 2. Enabled members with a password here; administrators only, or everyone.
    /// </summary>
    public async Task<IReadOnlyList<string>> WithoutAsync(bool administratorsOnly, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            select p.name
              from principal p
              join local_credential c on c.principal_id = p.id
             where p.kind = 'user' and p.disabled_at is null
               and (not @administrators
                    or exists (select 1 from principal_role r where r.principal_id = p.id and r.role_name = 'administrator'))
               and not exists (select 1 from principal_second_factor f
                                where f.principal_id = p.id and f.enabled_at is not null)
             order by lower(p.name)
            """);
        command.Parameters.AddWithValue("administrators", administratorsOnly);

        List<string> names = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <inheritdoc/>
    public async Task<SecondFactorRecord?> FindAsync(Guid principal, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select secret, key_version, enabled_at is not null, last_step, recovery from principal_second_factor "
            + "where principal_id = @principal");
        command.Parameters.AddWithValue("principal", principal);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SecondFactorRecord(
            reader.GetFieldValue<byte[]>(0),
            reader.GetInt32(1),
            reader.GetBoolean(2),
            reader.GetInt64(3),
            reader.GetFieldValue<byte[][]>(4));
    }

    /// <inheritdoc/>
    public async Task<bool> BeginAsync(Guid principal, byte[] sealedSecret, int keyVersion, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            insert into principal_second_factor (principal_id, secret, key_version)
            values (@principal, @secret, @version)
            on conflict (principal_id) do update
               set secret = excluded.secret, key_version = excluded.key_version, last_step = 0,
                   recovery = '{}', created_at = now()
             where principal_second_factor.enabled_at is null
            """);
        command.Parameters.AddWithValue("principal", principal);
        command.Parameters.AddWithValue("secret", sealedSecret);
        command.Parameters.AddWithValue("version", keyVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> EnableAsync(Guid principal, long timeStep, IReadOnlyList<byte[]> recovery, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update principal_second_factor set enabled_at = now(), last_step = @step, recovery = @recovery
             where principal_id = @principal and enabled_at is null and last_step < @step
            """);
        command.Parameters.AddWithValue("principal", principal);
        command.Parameters.AddWithValue("step", timeStep);
        command.Parameters.Add(new NpgsqlParameter("recovery", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = recovery });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> AdvanceAsync(Guid principal, long timeStep, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update principal_second_factor set last_step = @step where principal_id = @principal and last_step < @step");
        command.Parameters.AddWithValue("principal", principal);
        command.Parameters.AddWithValue("step", timeStep);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SpendRecoveryAsync(Guid principal, byte[] hash, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update principal_second_factor set recovery = array_remove(recovery, @hash)
             where principal_id = @principal and enabled_at is not null and @hash = any(recovery)
            """);
        command.Parameters.AddWithValue("principal", principal);
        command.Parameters.AddWithValue("hash", hash);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(Guid principal, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "delete from principal_second_factor where principal_id = @principal");
        command.Parameters.AddWithValue("principal", principal);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
