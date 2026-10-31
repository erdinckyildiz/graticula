using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// The server log as the site's — ADR-012 §5 phase 0 item 13: on a site of several machines up, the log screen's
/// <c>server</c> source shows every machine's warnings and errors, each with the machine that logged it, as ArcGIS
/// Server Manager queries logs across the site.
/// </summary>
/// <remarks>
/// <para>
/// <b>The other machines' from the store, this one's from its own ring</b>, merged newest first: each node writes its ring
/// to <c>site_log</c> every ten seconds (<see cref="SiteNode"/>), so another machine's last ten seconds may be missing and
/// this machine's never are.
/// </para>
/// <para>
/// <b>A site of one reads its ring exactly as before</b>, so nothing changes until a second machine is up. The cursor
/// on a site is a time (microseconds since 1970) rather than one process's sequence, since rows come from several.
/// </para>
/// </remarks>
internal static class SiteLog
{
    /// <summary>Answers the site's log where more than one machine is up; false, having written nothing, otherwise.</summary>
    public static async Task<bool> TryReadAsync(
        HttpContext context, NpgsqlDataSource store, SiteNode node, ServerLogBuffer serverLog, CancellationToken cancellation)
    {
        if (!node.Registered)
        {
            return false;
        }

        int up;

        try
        {
            await using NpgsqlCommand count = store.CreateCommand("select count(*) from site_node where last_seen > now() - @silence");
            count.Parameters.AddWithValue("silence", SiteNode.Silence);
            up = Convert.ToInt32(await count.ExecuteScalarAsync(cancellation).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            return false;
        }

        if (up < 2)
        {
            return false;
        }

        LogLevel minimum = LogEndpoints.Text(context, "level")?.ToLowerInvariant() switch
        {
            "error" or "severe" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            _ => string.Equals(LogEndpoints.Text(context, "failed"), "true", StringComparison.OrdinalIgnoreCase)
                ? LogLevel.Error
                : LogLevel.Warning,
        };

        string? text = LogEndpoints.Text(context, "q");
        DateTimeOffset? from = LogEndpoints.Time(context, "from");
        DateTimeOffset? to = LogEndpoints.Time(context, "to");
        long? before = LogEndpoints.Cursor(context);
        int limit = Math.Clamp(LogEndpoints.Whole(context, "limit") ?? 100, 1, 500);
        DateTimeOffset? beforeAt = before is { } micros ? DateTimeOffset.UnixEpoch.AddTicks(micros * 10) : null;

        List<(string Node, ServerLogEntry Entry)> rows = [];

        try
        {
            await using NpgsqlCommand read = store.CreateCommand(
                """
                select node, at, level, category, event_id, event_name, message, exception
                  from site_log
                 where node <> @me and level >= @minimum
                   and (@from::timestamptz is null or at >= @from::timestamptz)
                   and (@to::timestamptz is null or at <= @to::timestamptz)
                   and (@before::timestamptz is null or at < @before::timestamptz)
                   and (@text::text is null or message ilike '%' || @text::text || '%' or category ilike '%' || @text::text || '%'
                        or coalesce(exception, '') ilike '%' || @text::text || '%')
                 order by at desc
                 limit @limit
                """);
            read.Parameters.AddWithValue("me", node.Name);
            read.Parameters.AddWithValue("minimum", (int)minimum);
            read.Parameters.Add(new NpgsqlParameter("from", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)from?.UtcDateTime ?? DBNull.Value });
            read.Parameters.Add(new NpgsqlParameter("to", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)to?.UtcDateTime ?? DBNull.Value });
            read.Parameters.Add(new NpgsqlParameter("before", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)beforeAt?.UtcDateTime ?? DBNull.Value });
            read.Parameters.Add(new NpgsqlParameter("text", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)text ?? DBNull.Value });
            read.Parameters.AddWithValue("limit", limit);
            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                DateTimeOffset at = reader.GetFieldValue<DateTimeOffset>(1);
                rows.Add((reader.GetString(0), new ServerLogEntry(
                    Micros(at), at, (LogLevel)reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7))));
            }
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            return false;
        }

        foreach (ServerLogEntry mine in serverLog.Read(minimum, text, from, to, null, 500)
                     .Where(e => beforeAt is null || e.At < beforeAt)
                     .Take(limit))
        {
            rows.Add((node.Name, mine with { Cursor = Micros(mine.At) }));
        }

        List<(string Node, ServerLogEntry Entry)> page = [.. rows.OrderByDescending(r => r.Entry.At).Take(limit)];

        await Results.Ok(new
        {
            source = "server",
            since = serverLog.Since,
            kept = ServerLogBuffer.Capacity,
            site = true,
            scope = $"Every machine of this site's warnings and errors, {up} machines up: the most recent {ServerLogBuffer.Capacity} "
                + "of each, kept thirty days. Another machine's last ten seconds may not be here yet.",
            rows = page.Select(r => new
            {
                cursor = r.Entry.Cursor,
                at = r.Entry.At,
                who = (string?)null,
                from = r.Node,
                what = r.Entry.Level switch
                {
                    LogLevel.Critical => "critical",
                    LogLevel.Error => "error",
                    _ => "warning",
                },
                resource = r.Entry.Category,
                ok = r.Entry.Level < LogLevel.Error,
                detail = new
                {
                    message = r.Entry.Message, eventId = r.Entry.EventId, eventName = r.Entry.EventName,
                    exception = r.Entry.Exception, machine = r.Node,
                },
            }),
            next = page.Count > 0 ? page[^1].Entry.Cursor : (long?)null,
        }).ExecuteAsync(context).ConfigureAwait(false);

        return true;
    }

    private static long Micros(DateTimeOffset at) => (at - DateTimeOffset.UnixEpoch).Ticks / 10;
}
