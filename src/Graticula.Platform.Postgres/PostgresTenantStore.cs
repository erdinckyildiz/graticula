using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Tenancy;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// The tenants, in the platform store's <c>tenant</c> table — ADR-204, migration 100. Read and written as the system,
/// never through a tenant's connection: the table is the deployment's, not any tenant's.
/// </summary>
public sealed class PostgresTenantStore : ITenantStore
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store over the platform store's own connections.</summary>
    /// <param name="dataSource">The platform pool, used unrouted.</param>
    public PostgresTenantStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    private const string Columns = "id, key, name, hosts, portal_id, status = 'suspended'";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} from tenant order by (id = @default) desc, key");
        command.Parameters.AddWithValue("default", TenantScope.DefaultTenant);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<Tenant> tenants = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add(Read(reader));
        }

        return tenants;
    }

    /// <inheritdoc/>
    public async Task<Tenant?> CreateAsync(string key, string name, IReadOnlyList<string> hosts, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(hosts);

        if (await HostTakenAsync(hosts, except: null, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"""
            insert into tenant (id, key, name, hosts, portal_id)
            values (@id, @key, @name, @hosts, @portal)
            on conflict (key) do nothing
            returning {Columns}
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("name", name.Trim());
        command.Parameters.AddWithValue("hosts", Normalised(hosts));
        command.Parameters.AddWithValue("portal", Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<Tenant?> UpdateAsync(
        string key, string? name, IReadOnlyList<string>? hosts, bool? suspended, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (hosts is not null && await HostTakenAsync(hosts, except: key, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"""
            update tenant
               set name = coalesce(@name, name),
                   hosts = coalesce(@hosts, hosts),
                   status = case when @suspended is null then status when @suspended then 'suspended' else 'active' end
             where key = @key
            returning {Columns}
            """);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("name", (object?)name?.Trim() ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("hosts", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = hosts is null ? DBNull.Value : Normalised(hosts),
        });
        command.Parameters.Add(new NpgsqlParameter("suspended", NpgsqlTypes.NpgsqlDbType.Boolean)
        {
            Value = (object?)suspended ?? DBNull.Value,
        });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        List<string> owned = [];

        await using (NpgsqlCommand count = new(
            """
            select 'members', count(*) from principal where tenant_id = t.id
            union all select 'services', count(*) from service where tenant_id = t.id
            union all select 'maps', count(*) from web_map where tenant_id = t.id
            union all select 'groups', count(*) from sharing_group where tenant_id = t.id
            """.Replace("t.id", "(select id from tenant where key = @key)", StringComparison.Ordinal),
            connection, transaction))
        {
            count.Parameters.AddWithValue("key", key);
            await using NpgsqlDataReader reader = await count.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt64(1) > 0)
                {
                    owned.Add($"{reader.GetInt64(1)} {reader.GetString(0)}");
                }
            }
        }

        if (owned.Count > 0)
        {
            return owned;
        }

        // An organisation that holds nothing above may still hold custom roles (ADR-204 §5e item 18); they go with it, or a
        // later organisation of the same key would meet names it cannot see. Their privileges go by the cascade.
        await using (NpgsqlCommand roles = new(
            "delete from role where tenant_id = (select id from tenant where key = @key and id <> @default)", connection, transaction))
        {
            roles.Parameters.AddWithValue("key", key);
            roles.Parameters.AddWithValue("default", TenantScope.DefaultTenant);
            await roles.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand delete = new("delete from tenant where key = @key and id <> @default", connection, transaction))
        {
            delete.Parameters.AddWithValue("key", key);
            delete.Parameters.AddWithValue("default", TenantScope.DefaultTenant);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The tables are the floor's, read from <c>pg_policies</c></b> — every table with the <c>tenant_floor</c> policy —
    /// so a table a later migration floors is purged without anybody remembering it here. The order foreign keys need is
    /// found rather than written: each pass deletes what it can, a delete a reference refuses is rolled back to its
    /// savepoint and tried again next pass, and a pass that deletes nothing while something is still refused stops it.
    /// </remarks>
    public async Task<bool> PurgeAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid tenant;

        await using (NpgsqlCommand find = new("select id from tenant where key = @key and id <> @default", connection, transaction))
        {
            find.Parameters.AddWithValue("key", key);
            find.Parameters.AddWithValue("default", TenantScope.DefaultTenant);

            if (await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid id)
            {
                return false;
            }

            tenant = id;
        }

        // The job queue and the request log carry the tenant without a policy (the log is written with COPY).
        List<string> tables = ["job", "request_log"];

        await using (NpgsqlCommand floored = new(
            "select tablename from pg_policies where policyname = 'tenant_floor' and schemaname = current_schema() order by tablename",
            connection, transaction))
        await using (NpgsqlDataReader reader = await floored.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        HashSet<string> left = new(tables, StringComparer.Ordinal);

        for (int pass = 0; left.Count > 0; pass++)
        {
            int removed = 0;

            foreach (string table in left.ToList())
            {
                await transaction.SaveAsync("purge", cancellationToken).ConfigureAwait(false);

                try
                {
                    await using NpgsqlCommand delete = new(
                        $"delete from {table} where tenant_id = @tenant", connection, transaction);
                    delete.Parameters.AddWithValue("tenant", tenant);
                    removed += await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.ReleaseAsync("purge", cancellationToken).ConfigureAwait(false);
                    left.Remove(table);
                }
                catch (PostgresException refused) when (refused.SqlState == PostgresErrorCodes.ForeignKeyViolation)
                {
                    await transaction.RollbackAsync("purge", cancellationToken).ConfigureAwait(false);
                }
            }

            if (left.Count > 0 && removed == 0 && pass > 0)
            {
                throw new InvalidOperationException(
                    $"The tenant '{key}' could not be purged: rows in {string.Join(", ", left)} are referenced from "
                    + "outside it. Nothing was removed.");
            }
        }

        // Its organisation settings, which the setting store keeps under names that carry the tenant.
        await using (NpgsqlCommand settings = new("delete from server_setting where name like @prefix", connection, transaction))
        {
            settings.Parameters.AddWithValue("prefix", $"t:{tenant:N}:%");
            await settings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand delete = new("delete from tenant where id = @tenant", connection, transaction))
        {
            delete.Parameters.AddWithValue("tenant", tenant);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> FloorBindsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select not (rolsuper or rolbypassrls) from pg_roles where rolname = current_user");
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private async Task<bool> HostTakenAsync(IReadOnlyList<string> hosts, string? except, CancellationToken cancellationToken)
    {
        if (hosts.Count == 0)
        {
            return false;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select exists (select 1 from tenant where hosts && @hosts and (@except is null or key <> @except))");
        command.Parameters.AddWithValue("hosts", Normalised(hosts));
        command.Parameters.Add(new NpgsqlParameter("except", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)except ?? DBNull.Value });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>Host names as they are compared: lower case, without a port, without duplicates.</summary>
    public static string[] Normalised(IEnumerable<string> hosts) =>
        [.. hosts.Select(h => h.Trim().ToLowerInvariant())
            .Select(h => h.Contains(':', StringComparison.Ordinal) && !h.StartsWith('[') ? h[..h.IndexOf(':', StringComparison.Ordinal)] : h)
            .Where(h => h.Length > 0).Distinct(StringComparer.Ordinal)];

    private static Tenant Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<string[]>(3),
        reader.GetString(4), reader.GetBoolean(5));
}
