using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>What the server's settings are set to.</summary>
/// <param name="PageSize">
/// The server's page size, or null to go back to the configured one — ADR-084. A service that set its own
/// keeps it.
/// </param>
internal sealed record ServerSettingsRequest(int? PageSize);

/// <summary>
/// The server's own settings — V-70, ADR-084: set for the whole server from the console, not per service.
/// </summary>
/// <remarks>
/// In its own file for the reason <c>AdminEndpoints.FieldOverrides.cs</c> gives.
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapServerSettings(WebApplication app)
    {
        app.MapGet("/admin/settings", ServerSettingsAsync);
        app.MapPut("/admin/settings", SetServerSettingsAsync);
    }

    /// <summary>The server's settings, each with where its value came from.</summary>
    /// <remarks>
    /// <b>Where it came from is half the answer.</b> A page size of 1000 reads the same whether an operator
    /// chose it or nobody did, and the screen has to say which, because *reset* means something only for the
    /// first.
    /// </remarks>
    private static async Task ServerSettingsAsync(
        HttpContext context,
        ServerPageSize pageSize,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        ServerPageSize.Reading reading = await pageSize.ReadAsync(cancellation).ConfigureAwait(false);

        await Results.Json(Describe(reading)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Stores the server's settings.</summary>
    /// <remarks>
    /// <b><c>admin:manageServer</c></b>, as a service's own page size is: how much one query may carry is
    /// server administration, and the privilege that sets it for one service sets it for all.
    /// </remarks>
    private static async Task SetServerSettingsAsync(
        HttpContext context,
        ServerSettingsRequest request,
        ServerPageSize pageSize,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        if (request.PageSize is { } asked && pageSize.Refusal(asked) is { } refusal)
        {
            await AuditAsync(
                context, audit, "server.settings", ServerPageSize.Name,
                Detail(new { pageSize = asked }), succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 400, refusal).ConfigureAwait(false);
            return;
        }

        RequestPrincipal? current = context.Features.Get<RequestPrincipal>();

        (StoredSetting? before, ServerPageSize.Reading now) = await pageSize
            .SetAsync(request.PageSize, current?.Principal.Id, cancellation)
            .ConfigureAwait(false);

        await AuditAsync(
            context, audit, "server.settings", ServerPageSize.Name,
            Detail(new
            {
                from = before is null ? null : before.Value,
                to = request.PageSize?.ToString(CultureInfo.InvariantCulture),
                inForce = now.Value,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(Describe(now)).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static object Describe(ServerPageSize.Reading reading) => new
    {
        pageSize = new
        {
            value = reading.Value,

            // `stored` when an operator set it here, `configured` when it is the deployment's
            // Graticula:DefaultRecordCount or ArcGIS's 1000.
            source = reading.Stored is null ? "configured" : "stored",
            stored = reading.Stored,
            configured = reading.Fallback,
            ceiling = reading.Ceiling,
            changedAt = reading.ChangedAt,

            // A stored value the ceiling has since come down under is applied as the ceiling, and said so.
            clamped = reading.Stored is { } stored && stored != reading.Value,
        },
    };
}
