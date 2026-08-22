using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// How many events one address may report before it is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>In memory, per address, in fixed windows, and bounded in the number of addresses it
/// will remember.</b> That last part is the one that is easy to miss: a counter keyed by
/// address is itself a place to put unbounded data, so an attacker rotating source addresses
/// would fill the map instead of the table.
/// [ADR-045](../../docs/adr/ADR-045-the-server-keeps-a-log-you-can-ask-questions-of.md)
/// condition 4.
/// </para>
/// <para>
/// <b>Not in the store, and not shared between processes.</b> A rate limit that reads the
/// database to decide whether to write to the database has doubled the cost of the thing it
/// is protecting against. Two processes each allowing the limit is an acceptable answer for
/// a bound whose job is to stop a flood, not to be exact.
/// </para>
/// </remarks>
internal sealed class IngestThrottle
{
    /// <summary>Events one address may report per window.</summary>
    /// <remarks>
    /// 60 a minute. A studio page having a bad time reports a handful; a page in a render
    /// loop reports thousands, and the point of the limit is that the second case costs one
    /// row a second rather than one row a frame.
    /// </remarks>
    public const int PerWindow = 60;

    /// <summary>How many addresses are remembered at once.</summary>
    public const int Addresses = 4096;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (long Window, int Count)> _seen = new();

    /// <summary>Whether this address may report another event.</summary>
    /// <param name="address">Where it came from.</param>
    /// <param name="now">The clock, injected so a test does not have to wait a minute.</param>
    /// <returns>True to accept.</returns>
    public bool Allow(string? address, DateTimeOffset now)
    {
        string key = address ?? "unknown";
        long window = now.ToUnixTimeSeconds() / (long)Window.TotalSeconds;

        // <b>Cleared wholesale when it is full rather than evicted one at a time.</b> An LRU
        // here would be a cache with a policy to tune; this is a counter, and the worst case
        // of clearing it is that one window's limit is generous. Doing it before the insert
        // is what keeps the bound a bound.
        if (_seen.Count >= Addresses)
        {
            _seen.Clear();
        }

        while (true)
        {
            if (!_seen.TryGetValue(key, out (long Window, int Count) current))
            {
                if (_seen.TryAdd(key, (window, 1)))
                {
                    return true;
                }

                continue;
            }

            (long Window, int Count) next = current.Window == window
                ? (window, current.Count + 1)
                : (window, 1);

            if (!_seen.TryUpdate(key, next, current))
            {
                continue;
            }

            return next.Count <= PerWindow;
        }
    }
}

/// <summary>
/// Reading the three logs, and the one endpoint that writes to one of them.
/// </summary>
/// <remarks>
/// <b>The read routes and the ingest route live together because they are one feature, and
/// they could not be less alike otherwise.</b> Reading needs
/// <see cref="Privilege.AdminManageServer"/> — a log holds paths, principals and addresses,
/// which is most of what an attacker wants to know about a deployment. Writing is anonymous,
/// because the studio is, and is therefore the most carefully bounded route in this server.
/// </remarks>
internal static class LogEndpoints
{
    private static readonly IngestThrottle Throttle = new();

    /// <summary>The three logs this server keeps, in the order a screen offers them.</summary>
    private static readonly string[] Sources = ["audit", "requests", "studio"];

    /// <summary>The largest event body this server will read.</summary>
    /// <remarks>
    /// 8 KB. A stack trace fits; a payload does not. Enforced by reading at most this many
    /// bytes rather than by trusting <c>Content-Length</c>, which a caller writes.
    /// </remarks>
    public const int MostBytes = 8 * 1024;

    /// <summary>Registers the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/admin/logs/{source}", ReadAsync);
        app.MapGet("/admin/logs", ReadIndexAsync);
        app.MapPost("/rest/studio/events", ReportAsync);
    }

    /// <summary>What the Logs screen needs before it can draw a filter.</summary>
    private static async Task ReadIndexAsync(
        HttpContext context,
        ILogReader logs,
        PostgresRequestLogHealth health,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<(string Action, long Count)> actions =
            await logs.ActionsAsync(cancellation).ConfigureAwait(false);

        await Results.Ok(new
        {
            sources = Sources,
            actions = actions.Select(a => new { action = a.Action, count = a.Count }),

            // <b>ADR-045 condition 6: a dropped record is visible.</b> The request log is
            // lossy under load by design, so a screen that showed its rows without showing
            // what it lost would be quietly claiming completeness it does not have.
            writer = new { dropped = health.Dropped, waiting = health.Waiting },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ReadAsync(
        HttpContext context,
        string source,
        ILogReader logs,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        LogQuery query = new(
            Time(context, "from"),
            Time(context, "to"),
            Text(context, "q"),
            Text(context, "principal"),
            Text(context, "action"),
            Whole(context, "status"),
            Text(context, "kind"),
            string.Equals(Text(context, "failed"), "true", StringComparison.OrdinalIgnoreCase),
            Cursor(context),
            Whole(context, "limit") ?? 100);

        IReadOnlyList<LogRow> rows = source switch
        {
            "audit" => await logs.AuditAsync(query, cancellation).ConfigureAwait(false),
            "requests" => await logs.RequestsAsync(query, cancellation).ConfigureAwait(false),
            "studio" => await logs.ClientAsync(query, cancellation).ConfigureAwait(false),
            _ => null!,
        };

        if (rows is null)
        {
            await Results.BadRequest(new
            {
                error = new
                {
                    code = 400,
                    message = $"`{source}` is not a log this server keeps. It keeps audit, "
                        + "requests and studio.",
                },
            }).ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await Results.Ok(new
        {
            source,
            rows = rows.Select(r => new
            {
                cursor = r.Cursor,
                at = r.OccurredAt,
                who = r.PrincipalName,
                from = r.SourceAddress,
                what = r.What,
                resource = r.Resource,
                ok = r.Succeeded,
                detail = r.Detail,
            }),

            // <b>The next cursor rather than a total.</b> Counting a log to tell a reader
            // how far they have to go means scanning it, every page, for a number that
            // changes while they read.
            next = rows.Count > 0 ? rows[^1].Cursor : (long?)null,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Takes one event from a browser.</summary>
    /// <remarks>
    /// <para>
    /// <b>Anonymous, because the studio is.</b> A viewer looking at a public map is not
    /// signed in, and the failures worth hearing about are exactly the ones a visitor hits.
    /// That makes this the one write in this server a stranger can reach, so every bound is
    /// here rather than assumed elsewhere: the body is read up to a cap, the address is rate
    /// limited, the strings are clipped, and nothing is echoed back.
    /// </para>
    /// <para>
    /// <b>It answers 204 whatever happens, including when it refuses.</b> A page reporting
    /// its own errors must not be told anything it could act on — a distinct status for
    /// *throttled* would let a caller measure the limit, and an error body would give a
    /// page in a render loop something new to fail on.
    /// </para>
    /// </remarks>
    private static async Task ReportAsync(
        HttpContext context,
        IClientEventLog events,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCodes.Status204NoContent;

        string? address = context.Connection.RemoteIpAddress?.ToString();

        if (!Throttle.Allow(address, DateTimeOffset.UtcNow))
        {
            return;
        }

        byte[] buffer = new byte[MostBytes];
        int read = 0;

        while (read < buffer.Length)
        {
            int got = await context.Request.Body
                .ReadAsync(buffer.AsMemory(read), cancellation)
                .ConfigureAwait(false);

            if (got == 0)
            {
                break;
            }

            read += got;
        }

        if (read == 0)
        {
            return;
        }

        string kind;
        string message;
        string? page;
        string detail;

        try
        {
            using JsonDocument body = JsonDocument.Parse(buffer.AsMemory(0, read));
            JsonElement root = body.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            kind = Read(root, "kind") ?? "error";
            message = Read(root, "message") ?? string.Empty;
            page = Read(root, "page");

            detail = root.TryGetProperty("detail", out JsonElement value)
                ? value.GetRawText()
                : "{}";
        }
        catch (JsonException)
        {
            // A browser sending something that is not JSON is a bug in the page, and
            // there is nobody to tell: the reporter is the thing that is broken.
            return;
        }

        if (message.Length == 0)
        {
            return;
        }

        await events.RecordAsync(
                new ClientEntry(
                    kind,
                    page,
                    message,
                    detail,
                    context.Features.Get<RequestPrincipal>() is { } who
                        && !who.Principal.IsAnonymous
                            ? who.Principal.Name
                            : null,
                    address,
                    context.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent
                        ? agent
                        : null),
                cancellation)
            .ConfigureAwait(false);
    }

    private static string? Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string? Text(HttpContext context, string name) =>
        context.Request.Query[name].ToString() is { Length: > 0 } value ? value : null;

    private static int? Whole(HttpContext context, string name) =>
        int.TryParse(
            Text(context, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : null;

    private static long? Cursor(HttpContext context) =>
        long.TryParse(
            Text(context, "before"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value)
                ? value
                : null;

    private static DateTimeOffset? Time(HttpContext context, string name) =>
        DateTimeOffset.TryParse(
            Text(context, name),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset value)
                ? value
                : null;
}

/// <summary>
/// How much the request log is losing, for the screen that must say so.
/// </summary>
/// <remarks>
/// <b>A tiny wrapper rather than putting these on <see cref="IRequestLog"/>.</b> The port is
/// the thing on the hot path and it should carry the smallest possible surface; how healthy
/// the writer is, is a question the console asks and nothing else does.
/// </remarks>
internal sealed class PostgresRequestLogHealth
{
    private readonly Graticula.Platform.Postgres.PostgresRequestLog _log;

    /// <summary>Wraps the writer.</summary>
    /// <param name="log">The writer.</param>
    public PostgresRequestLogHealth(Graticula.Platform.Postgres.PostgresRequestLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
    }

    /// <summary>How many entries have been dropped since this process started.</summary>
    public long Dropped => _log.Dropped;

    /// <summary>How many are waiting to be written.</summary>
    public long Waiting => _log.Waiting;
}
