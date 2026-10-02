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
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Counts in a platform store.</summary>
    /// <param name="dataSource">The store.</param>
    public PostgresServiceUsageStore(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

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
              from unnest(@folders, @names, @counts) as c(folder, name, n)
              join service s on lower(s.name) = lower(c.name)
                            and coalesce(lower(s.folder), '') = coalesce(lower(c.folder), '')
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
