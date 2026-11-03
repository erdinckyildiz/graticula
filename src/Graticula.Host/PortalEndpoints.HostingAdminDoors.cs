using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// The hosting server's administration doors an ArcGIS administrator's scripts use — a service's status, start and stop,
/// and the logs' query — answered by this server's own status and logs (ADR-261, the ArcGIS veteran's review #7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Doors, not a second implementation.</b> A stop here writes the status Server's own Stop writes, a log query reads
/// the request and audit logs the Logs screen reads, and each asks the privilege its own screen asks:
/// <c>admin:manageServer</c>. Reading a service's status asks only that the caller may see the service, as the services
/// directory does.
/// </para>
/// <para>
/// The shapes are the ArcGIS Server Administrator API's, from its public reference: a service at
/// <c>services/[folder/]name.type</c>, its <c>status</c> as <c>configuredState</c> and <c>realTimeState</c>, <c>start</c>
/// and <c>stop</c> answering <c>{"status":"success"}</c>, and <c>logs/query</c> taking <c>startTime</c> (the newer
/// bound), <c>endTime</c> (the older), <c>level</c>, <c>filter</c> and <c>pageSize</c>.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>The levels a log query names, most severe first; a level includes every one before it.</summary>
    private static readonly string[] LogLevels = ["SEVERE", "WARNING", "INFO", "FINE", "VERBOSE", "DEBUG"];

    /// <summary>One service the admin path names, of whichever of the three kinds holds it.</summary>
    private sealed record AdminTarget(
        string? Folder, string Name, string Type, ServiceStatus Status, PublishedService? Service, PublishedCoverage? Coverage,
        SystemService? System);

    /// <summary>
    /// <c>services/[folder/]name.type[/operation]</c>: the service's document, its status, or a start or a stop.
    /// </summary>
    private static async Task<IResult> AdminServiceDoorAsync(
        HttpContext context, CatalogFallback catalog, PostgresSystemServices system, ICoverageCatalog coverages, string path,
        CancellationToken cancellation)
    {
        string[] parts = path.Split('/');
        int at = Array.FindIndex(parts, p => p.Contains('.', StringComparison.Ordinal));

        if (at is < 0 or > 1 || parts.Length > at + 2)
        {
            return AdminError(404, $"'services/{path}' is not answered by this server.");
        }

        string? folder = at == 1 ? parts[0] : null;
        string[] named = parts[at].Split('.', 2);
        string operation = parts.Length > at + 1 ? parts[at + 1] : string.Empty;

        AdminTarget? target = await AdminTargetAsync(context, catalog, system, coverages, folder, named[0], named[1], cancellation)
            .ConfigureAwait(false);

        if (target is null)
        {
            return AdminError(404, $"Service '{(folder is null ? "" : folder + "/")}{parts[at]}' was not found.");
        }

        string state = target.Status == ServiceStatus.Started ? "STARTED" : "STOPPED";

        switch (operation)
        {
            case "":
                return Results.Ok(new
                {
                    serviceName = target.Name,
                    type = target.Type,
                    description = target.Service?.Description ?? target.Coverage?.Description ?? string.Empty,
                    properties = new { },
                    extensions = Array.Empty<object>(),
                    configuredState = state,
                });

            case "status":
                // Stopped is stopped at once here: every face answers 503 from the moment the status is written.
                return Results.Ok(new { configuredState = state, realTimeState = state });

            case "start" or "stop":
                return await AdminStartOrStopAsync(context, target, operation == "start", system, coverages, cancellation)
                    .ConfigureAwait(false);

            default:
                return AdminError(404, $"'{operation}' is not answered for a service by this server.");
        }
    }

    /// <summary>The service the path names, if this caller may see it; a stopped one only to whoever may start it.</summary>
    private static async Task<AdminTarget?> AdminTargetAsync(
        HttpContext context, CatalogFallback catalog, PostgresSystemServices system, ICoverageCatalog coverages,
        string? folder, string name, string type, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);

        if (type is "FeatureServer" or "MapServer" or "VectorTileServer")
        {
            CatalogListing listing = await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);
            PublishedService? service = listing.Services?.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
                && Program.DirectoryLists(s, folder, current, seesStopped)
                && type switch
                {
                    "FeatureServer" => ServiceFaces.AnswersFeatures(s),
                    "MapServer" => ServiceFaces.AnswersMap(s),
                    _ => ServiceFaces.AnswersTiles(s),
                });

            return service is null ? null : new AdminTarget(service.Folder, service.Name, type, service.Status, service, null, null);
        }

        if (type == "ImageServer")
        {
            PublishedCoverage? coverage = await coverages.FindAsync(folder, name, cancellation).ConfigureAwait(false);
            return coverage is null || !Program.DirectoryLists(coverage, folder, current, seesStopped)
                ? null
                : new AdminTarget(coverage.Folder, coverage.Name, type, coverage.Status, null, coverage, null);
        }

        SystemService? found = (await system.ListAsync(cancellation).ConfigureAwait(false))
            .Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) && s.Kind == type
                && Program.DirectoryLists(s, folder, current))
            .Select(s => (SystemService?)s)
            .FirstOrDefault();

        return found is { } one ? new AdminTarget(one.Folder, one.Name, type, one.Status, null, null, one) : null;
    }

    /// <summary>
    /// A start or a stop: Server's own, under its privilege, written to the status every face reads and audited as Server's
    /// is. A service is started or stopped as a whole, whichever face the path named.
    /// </summary>
    private static async Task<IResult> AdminStartOrStopAsync(
        HttpContext context, AdminTarget target, bool start, PostgresSystemServices system, ICoverageCatalog coverages,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return AdminError(405, $"'{(start ? "start" : "stop")}' changes the service, so it is a POST.");
        }

        if (!current.Authorization.Allows(Privilege.AdminManageServer))
        {
            return AdminError(403, "Starting and stopping a service is the server administrator's (admin:manageServer).");
        }

        ServiceStatus status = start ? ServiceStatus.Started : ServiceStatus.Stopped;
        bool written;

        if (target.Service is { } service)
        {
            if (service.LayersAndTables.Count == 0)
            {
                return AdminError(409, $"'{service.Name}' holds no layers, so it has nothing to start or stop.");
            }

            IAdminCatalog admin = context.RequestServices.GetRequiredService<IAdminCatalog>();
            written = await admin.SetStatusAsync(service.LayersAndTables[0].Id, status, cancellation).ConfigureAwait(false) is not null;
        }
        else if (target.Coverage is not null)
        {
            written = await coverages.SetStatusAsync(target.Folder, target.Name, status, cancellation).ConfigureAwait(false);
        }
        else
        {
            written = await system.SetStatusAsync(target.Name, status, cancellation).ConfigureAwait(false) is not null;
        }

        if (!written)
        {
            return AdminError(404, $"Service '{target.Name}.{target.Type}' was not found.");
        }

        await AdminEndpoints.AuditAsync(
            context, context.RequestServices.GetRequiredService<IAuditLog>(), start ? "service.start" : "service.stop",
            (target.Folder is null ? "" : target.Folder + "/") + target.Name,
            AdminEndpoints.Detail(new { type = target.Type, through = "arcgis/admin" }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(new { status = "success" });
    }

    /// <summary>
    /// <c>logs/query</c>: the request log's failures and, from <c>INFO</c> down, the audit trail, newest first — what the Logs
    /// screen reads, under its privilege.
    /// </summary>
    /// <remarks>
    /// <b>A level is read from what this server keeps.</b> It keeps no level on a line: a request that answered 500 or more is
    /// <c>SEVERE</c>, one that answered 400 or more <c>WARNING</c>, an administrative act <c>INFO</c>, and any other request
    /// <c>FINE</c>. A filter naming services narrows to the first one named.
    /// </remarks>
    private static async Task<IResult> AdminLogsQueryAsync(HttpContext context, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!current.Authorization.Allows(Privilege.AdminManageServer))
        {
            return AdminError(403, "The logs are read by the server administrator (admin:manageServer).");
        }

        IFormCollection? form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : null;
        string? Said(string key) =>
            (form?[key].FirstOrDefault() ?? context.Request.Query[key].FirstOrDefault()) is { Length: > 0 } value ? value.Trim() : null;

        int level = Array.IndexOf(LogLevels, (Said("level") ?? "WARNING").ToUpperInvariant());
        if (level < 0)
        {
            return AdminError(400, $"'{Said("level")}' is not a level: {string.Join(", ", LogLevels)}.");
        }

        int size = int.TryParse(Said("pageSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int asked)
            ? Math.Clamp(asked, 1, 10_000) : 1000;
        DateTimeOffset? newest = Millis(Said("startTime"));
        DateTimeOffset? oldest = Millis(Said("endTime"));
        string? service = ServiceFilter(Said("filter"));

        ILogReader logs = context.RequestServices.GetRequiredService<ILogReader>();
        LogQuery query = new(oldest, newest, service, null, null, null, null, Failed: level < 3, null, size + 1);

        List<(DateTimeOffset Time, object Message)> found = [];

        foreach (LogRow row in await logs.RequestsAsync(query, cancellation).ConfigureAwait(false))
        {
            using JsonDocument detail = JsonDocument.Parse(row.Detail);
            int status = detail.RootElement.TryGetProperty("status", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
            string type = status >= 500 ? "SEVERE" : status >= 400 ? "WARNING" : "FINE";

            if (Array.IndexOf(LogLevels, type) > level)
            {
                continue;
            }

            double? millis = detail.RootElement.TryGetProperty("durationMs", out JsonElement d) && d.ValueKind == JsonValueKind.Number
                ? d.GetDouble() : null;
            string? face = detail.RootElement.TryGetProperty("face", out JsonElement f) && f.ValueKind == JsonValueKind.String
                ? f.GetString() : null;

            found.Add((row.OccurredAt, new
            {
                type,
                message = $"{row.What} answered {status}" + (millis is { } ms ? $" in {ms.ToString("0", CultureInfo.InvariantCulture)} ms." : "."),
                time = row.OccurredAt.ToUnixTimeMilliseconds(),
                source = row.Resource is { } named ? (face is null ? named : $"{named}.{face}") : "Server",
                machine = Environment.MachineName,
                user = row.PrincipalName ?? string.Empty,
                code = status,
                elapsed = millis is { } seconds ? (seconds / 1000).ToString("0.###", CultureInfo.InvariantCulture) : string.Empty,
                process = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                thread = string.Empty,
                methodName = string.Empty,
            }));
        }

        if (level >= 2)
        {
            foreach (LogRow row in await logs.AuditAsync(query with { Failed = false }, cancellation).ConfigureAwait(false))
            {
                found.Add((row.OccurredAt, new
                {
                    type = "INFO",
                    message = row.Resource is null ? row.What : $"{row.What} {row.Resource}" + (row.Succeeded ? "" : " (refused)"),
                    time = row.OccurredAt.ToUnixTimeMilliseconds(),
                    source = row.Resource ?? "Server",
                    machine = Environment.MachineName,
                    user = row.PrincipalName ?? string.Empty,
                    code = 0,
                    elapsed = string.Empty,
                    process = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    thread = string.Empty,
                    methodName = string.Empty,
                }));
            }
        }

        List<(DateTimeOffset Time, object Message)> page = [.. found.OrderByDescending(m => m.Time).Take(size)];

        return Results.Ok(new
        {
            hasMore = found.Count > size,
            startTime = page.Count > 0 ? page[0].Time.ToUnixTimeMilliseconds() : (newest ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            endTime = page.Count > 0 ? page[^1].Time.ToUnixTimeMilliseconds() : (oldest ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            logMessages = page.Select(m => m.Message),
        });
    }

    /// <summary>An epoch-milliseconds bound, or null for none or for what is not one.</summary>
    private static DateTimeOffset? Millis(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    /// <summary>The first service a filter names, as the request log files it — <c>folder/name</c> — or null for all.</summary>
    private static string? ServiceFilter(string? filter)
    {
        if (filter is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(filter);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("services", out JsonElement services)
                && services.ValueKind == JsonValueKind.Array
                && services.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.String } first
                && first.GetString() is { Length: > 0 } named)
            {
                int dot = named.LastIndexOf('.');
                return dot > 0 ? named[..dot] : named;
            }
        }
        catch (JsonException)
        {
            // A filter that is not JSON narrows nothing, as "*" does not.
        }

        return null;
    }

    /// <summary>The Administrator API's error: a status, the messages, a code.</summary>
    private static IResult AdminError(int code, string message) =>
        Results.Json(new { status = "error", messages = new[] { message }, code });
}
