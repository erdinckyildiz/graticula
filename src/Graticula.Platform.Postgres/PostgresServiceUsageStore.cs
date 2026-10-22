using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>Services' request counts in the platform store's <c>service_usage</c> — ADR-135, migration 75.</summary>
public sealed class PostgresServiceUsageStore : IServiceUsageStore
{
    private readonly NpgsqlDataSource _root;

    /// <summary>The tenant's connections where the deployment holds several (ADR-204), otherwise the store's own.</summary>
    // <b>The store's own pool, not the organisation's — ADR-204 §5e item 14.</b> Every count carries the organisation
    // it belongs to, and the use is keyed by service id, so a flush asked for from one organisation's request must
    // still write and read every organisation's; routed, it credited the batch to the caller's same-named services.
    private NpgsqlDataSource _dataSource => _root;

    /// <summary>Counts in a platform store.</summary>
    /// <param name="dataSource">The store.</param>
    public PostgresServiceUsageStore(NpgsqlDataSource dataSource) =>
        _root = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <inheritdoc/>
    /// <remarks>
    /// <b>One statement for the whole batch</b>, the counts unnested and joined to the services they name — the name
    /// and folder compared as the catalogue compares them. The batch is summed by service first, by the caller, so no
    /// row is updated twice in one statement.
    /// </remarks>
    public async Task AddAsync(IReadOnlyCollection<ServiceCount> counts, DateOnly day, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counts);

        if (counts.Count == 0)
        {
            return;
        }

        const string Sql = """
            insert into service_usage (service_id, day, requests)
            select s.id, @day, sum(c.n)::bigint
              from unnest(@folders, @names, @counts, @tenants) as c(folder, name, n, tenant)
              join service s on lower(s.name) = lower(c.name)
                            and coalesce(lower(s.folder), '') = coalesce(lower(c.folder), '')
                            -- ADR-204 §5e item 14: the organisation the request was made to; a count with none
                            -- is the default organisation's, as every count was before there were several.
                            and s.tenant_id = coalesce(c.tenant, '10000000-0000-0000-0000-000000000001'::uuid)
             group by s.id
            on conflict (service_id, day) do update set requests = service_usage.requests + excluded.requests
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.Add(new NpgsqlParameter("folders", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = counts.Select(c => (object?)c.Folder ?? DBNull.Value).ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = counts.Select(c => c.Name).ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter("tenants", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = counts.Select(c => (object?)c.Tenant ?? DBNull.Value).ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
            Value = counts.Select(c => c.Requests).ToArray(),
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Guid, ServiceUse>> ReadAsync(DateOnly today, CancellationToken cancellationToken)
    {
        const string Sql = """
            select service_id,
                   sum(requests)::bigint,
                   coalesce(sum(requests) filter (where day > @today - 30), 0)::bigint,
                   coalesce(sum(requests) filter (where day > @today - 7), 0)::bigint,
                   max(day) filter (where requests > 0)
              from service_usage
             group by service_id
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("today", today);

        Dictionary<Guid, ServiceUse> uses = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid id = reader.GetGuid(0);
            uses[id] = new ServiceUse(
                id,
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4));
        }

        return uses;
    }

    /// <inheritdoc/>
    public async Task<DateOnly?> FirstDayAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("select min(day) from service_usage");
        object? first = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return first is DateOnly day ? day : first is DateTime when ? DateOnly.FromDateTime(when) : null;
    }
}
