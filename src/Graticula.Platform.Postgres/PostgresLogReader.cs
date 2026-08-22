using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary><see cref="ILogReader"/> over the platform store.</summary>
/// <remarks>
/// <para>
/// <b>Three tables, one shape, and the mapping into <see cref="LogRow"/> is where the work
/// is.</b> A reader moving between the audit trail, the request log and studio events should
/// see the same six columns — when, who, from where, what, which resource, did it work —
/// because those are the questions all three answer. What each table means by *what* differs,
/// so each query composes its own, and everything left over goes into
/// <see cref="LogRow.Detail"/> for a reader who opens the row.
/// </para>
/// <para>
/// <b>Paged by cursor, never by offset.</b> A log grows at the head while it is being read;
/// an offset walks backwards through a list that is moving forwards, so page two silently
/// repeats or skips rows. Every query takes <c>id &lt; @before</c> instead, which is stable
/// no matter how much arrives in between.
/// </para>
/// <para>
/// <b>Filters are parameters, and the free-text one is a parameter too.</b> Nothing here
/// concatenates a caller's string into SQL. Free text is matched with <c>ilike</c> against
/// the columns a reader would actually search, with the wildcards added around the
/// parameter rather than inside the statement.
/// </para>
/// </remarks>
public sealed class PostgresLogReader : ILogReader
{
    /// <summary>The most rows one page may hold.</summary>
    /// <remarks>
    /// A screen shows tens; this is the bound that stops a caller asking for the table.
    /// </remarks>
    public const int MostRows = 500;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the reader.</summary>
    /// <param name="dataSource">The platform store pool.</param>
    public PostgresLogReader(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LogRow>> AuditAsync(
        LogQuery query, CancellationToken cancellationToken)
    {
        // <b>`audit_event.id` is a uuid, so it cannot be the cursor.</b> A uuid has no
        // order, and ordering by time alone is not a stable page boundary when two events
        // share a timestamp. `occurred_at` in microseconds since the epoch is monotonic
        // enough to page by and fits a bigint, which is what the shared shape wants.
        StringBuilder sql = new("""
            select (extract(epoch from occurred_at) * 1000000)::bigint as cursor,
                   occurred_at, principal_name, host(source_address), action, resource,
                   succeeded, detail::text
            from audit_event
            where true
            """);

        List<NpgsqlParameter> parameters = [];

        Window(sql, parameters, query);
        Exact(sql, parameters, "principal_name", query.Principal);
        Exact(sql, parameters, "action", query.Action);

        if (query.Failed)
        {
            sql.Append(" and not succeeded");
        }

        if (query.Text is { Length: > 0 })
        {
            sql.Append(
                " and (action ilike @text or coalesce(resource, '') ilike @text"
                + " or principal_name ilike @text or detail::text ilike @text)");

            parameters.Add(Like(query.Text));
        }

        if (query.Before is { } before)
        {
            sql.Append(" and (extract(epoch from occurred_at) * 1000000)::bigint < @before");
            parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Bigint) { Value = before });
        }

        sql.Append(" order by occurred_at desc limit @limit");
        parameters.Add(Limit(query.Limit));

        return ReadAsync(sql.ToString(), parameters, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LogRow>> RequestsAsync(
        LogQuery query, CancellationToken cancellationToken)
    {
        // <b>*What* is the method and path; the status decides success.</b> Anything 400 or
        // above reads as a failure, which is the same judgement the audit trail's
        // `succeeded` column carries and lets one screen colour both the same way.
        StringBuilder sql = new("""
            select id as cursor, occurred_at, principal_name, host(source_address),
                   method || ' ' || path as what, service, status < 400 as succeeded,
                   jsonb_build_object(
                     'status', status, 'durationMs', duration_ms, 'query', query,
                     'face', face, 'bytes', bytes)::text
            from request_log
            where true
            """);

        List<NpgsqlParameter> parameters = [];

        Window(sql, parameters, query);
        Exact(sql, parameters, "principal_name", query.Principal);

        if (query.Status is { } status)
        {
            sql.Append(" and status = @status");
            parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Integer) { Value = status });
        }

        if (query.Failed)
        {
            sql.Append(" and status >= 400");
        }

        if (query.Text is { Length: > 0 })
        {
            sql.Append(
                " and (path ilike @text or coalesce(query, '') ilike @text"
                + " or coalesce(service, '') ilike @text"
                + " or coalesce(principal_name, '') ilike @text)");

            parameters.Add(Like(query.Text));
        }

        Cursor(sql, parameters, query.Before);

        sql.Append(" order by id desc limit @limit");
        parameters.Add(Limit(query.Limit));

        return ReadAsync(sql.ToString(), parameters, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LogRow>> ClientAsync(
        LogQuery query, CancellationToken cancellationToken)
    {
        // <b>Every studio event is a failure by construction.</b> The studio reports errors
        // and refusals, not successes, so `succeeded` is false throughout rather than a
        // column — and a reader filtering for failures should still see all of them.
        StringBuilder sql = new("""
            select id as cursor, occurred_at, principal_name, host(source_address),
                   kind || ': ' || message as what, page, false as succeeded,
                   jsonb_build_object('kind', kind, 'agent', agent, 'detail', detail)::text
            from client_event
            where true
            """);

        List<NpgsqlParameter> parameters = [];

        Window(sql, parameters, query);
        Exact(sql, parameters, "principal_name", query.Principal);
        Exact(sql, parameters, "kind", query.Kind);

        if (query.Text is { Length: > 0 })
        {
            sql.Append(
                " and (message ilike @text or coalesce(page, '') ilike @text"
                + " or kind ilike @text or detail::text ilike @text)");

            parameters.Add(Like(query.Text));
        }

        Cursor(sql, parameters, query.Before);

        sql.Append(" order by id desc limit @limit");
        parameters.Add(Limit(query.Limit));

        return ReadAsync(sql.ToString(), parameters, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(string Action, long Count)>> ActionsAsync(
        CancellationToken cancellationToken)
    {
        // <b>Counted, because a filter that offers a value with nothing behind it wastes a
        // click.</b> The count is also the only cheap answer to *what does this server
        // actually do*, which is a question a new operator asks before any other.
        const string Sql = """
            select action, count(*) from audit_event group by action order by count(*) desc
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<(string, long)> actions = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actions.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return actions;
    }

    /// <inheritdoc/>
    public async Task<long> SweepAsync(TimeSpan keep, CancellationToken cancellationToken)
    {
        // <b>`audit_event` is not swept, and that is the decision rather than an
        // oversight.</b> ADR-045's port documents why: the audit trail answers *who did
        // this last quarter*, and a retention window that forgets it makes the trail
        // decorative. Only the two logs that grow at request rate are capped.
        const string Sql = """
            with gone as (
              delete from request_log where occurred_at < @cutoff returning 1
            ), also as (
              delete from client_event where occurred_at < @cutoff returning 1
            )
            select (select count(*) from gone) + (select count(*) from also)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);

        command.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz)
        {
            Value = DateTimeOffset.UtcNow - keep,
        });

        object? swept = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return swept is long count ? count : 0;
    }

    private static void Window(
        StringBuilder sql, List<NpgsqlParameter> parameters, LogQuery query)
    {
        if (query.From is { } from)
        {
            sql.Append(" and occurred_at >= @from");
            parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from });
        }

        if (query.To is { } to)
        {
            sql.Append(" and occurred_at < @to");
            parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to });
        }
    }

    private static void Exact(
        StringBuilder sql, List<NpgsqlParameter> parameters, string column, string? value)
    {
        if (value is not { Length: > 0 })
        {
            return;
        }

        // The column name is never a caller's string — every call site passes a literal.
        sql.Append(CultureInfo.InvariantCulture, $" and {column} = @{column}");
        parameters.Add(new NpgsqlParameter(column, NpgsqlDbType.Text) { Value = value });
    }

    private static void Cursor(
        StringBuilder sql, List<NpgsqlParameter> parameters, long? before)
    {
        if (before is not { } value)
        {
            return;
        }

        sql.Append(" and id < @before");
        parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Bigint) { Value = value });
    }

    private static NpgsqlParameter Like(string text) =>
        new("text", NpgsqlDbType.Text) { Value = "%" + text + "%" };

    private static NpgsqlParameter Limit(int limit) =>
        new("limit", NpgsqlDbType.Integer) { Value = Math.Clamp(limit, 1, MostRows) };

    private async Task<IReadOnlyList<LogRow>> ReadAsync(
        string sql, List<NpgsqlParameter> parameters, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(sql);

        foreach (NpgsqlParameter parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<LogRow> rows = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new LogRow(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? "{}" : reader.GetString(7)));
        }

        return rows;
    }
}
