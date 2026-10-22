using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

internal static partial class AdminEndpoints
{
    /// <summary>
    /// Pinning a service keeps it warm — ADR-218. Read by whoever manages the service; changed by an administrator of
    /// the server, because a pin spends the server's capacity rather than the service's.
    /// </summary>
    private static void MapPinning(WebApplication app)
    {
        app.MapGet("/admin/services/{name}/pinned", GetPinnedAsync);
        app.MapPut("/admin/services/{name}/pinned", SetPinnedAsync);
    }

    /// <summary>A pin asked for: <c>{ "pinned": true }</c>.</summary>
    internal sealed record PinRequest(bool? Pinned);

    private static async Task GetPinnedAsync(
        HttpContext context,
        string name,
        string? folder,
        PostgresLayerCatalog layers,
        PinnedServices pins,
        CancellationToken cancellation)
    {
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (!await ManagesServiceAsync(context, layers, at, name, "read the pin of", cancellation).ConfigureAwait(false)
            || await layers.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            return;
        }

        await Results.Json(await PinStateAsync(service.Id, name, at, pins, cancellation).ConfigureAwait(false))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task SetPinnedAsync(
        HttpContext context,
        string name,
        string? folder,
        PinRequest request,
        PostgresLayerCatalog layers,
        PinnedServices pins,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        if (request?.Pinned is not { } pinned)
        {
            await Refuse(context, 400, "'pinned' is required: true keeps the service warm, false lets it cool.")
                .ConfigureAwait(false);
            return;
        }

        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (await layers.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, $"No service '{name}'" + (at is null ? " at the root." : $" in folder '{at}'."))
                .ConfigureAwait(false);
            return;
        }

        if (pinned && !await pins.IsPinnedAsync(service.Id, cancellation).ConfigureAwait(false))
        {
            int already = await pins.CountAsync(cancellation).ConfigureAwait(false);

            if (already >= pins.Limit)
            {
                var own = await pins.PinnedAsync(
                    Graticula.Platform.Tenancy.TenantScope.MultiTenant ? Graticula.Platform.Tenancy.TenantScope.Current : null,
                    cancellation).ConfigureAwait(false);

                // <b>Named, not dropped.</b> ADR-007 §4.12: when pins contend the administrator is told which, rather
                // than one being let go without a word.
                await Results.Json(
                    new
                    {
                        error = new
                        {
                            code = 409,
                            message = $"This server keeps at most {pins.Limit} services pinned, and {already} are"
                                + (own.Count < already ? $", {already - own.Count} of them another organization's" : string.Empty)
                                + ". Unpin one of them first, or raise Graticula:PinnedServiceLimit.",
                            details = own,
                        },
                    },
                    statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        // <b>An organisation's share of the pins — ADR-204 §5e item 12, the veteran's review:</b> the limit is the
        // deployment's, and without a share one organisation could take every place. The datastore's rule, 2/(n+1).
        if (pinned
            && Graticula.Platform.Tenancy.TenantScope.MultiTenant
            && Graticula.Platform.Tenancy.TenantScope.Current is { } organisation
            && !await pins.IsPinnedAsync(service.Id, cancellation).ConfigureAwait(false))
        {
            int share = ConnectionBudget.ShareOf(pins.Limit, Graticula.Platform.Tenancy.TenantScope.Count);
            var own = await pins.PinnedAsync(organisation, cancellation).ConfigureAwait(false);

            if (own.Count >= share)
            {
                await Results.Json(
                    new
                    {
                        error = new
                        {
                            code = 409,
                            message = $"Your organization keeps {own.Count} services warm, which is its share of the "
                                + $"{pins.Limit} this server keeps for every organization on it. Stop keeping one of "
                                + "these warm first.",
                            details = own,
                        },
                    },
                    statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        await pins.SetAsync(service.Id, pinned, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, pinned ? "service.pin" : "service.unpin", name,
            Detail(new { folder = at, pinned }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(await PinStateAsync(service.Id, name, at, pins, cancellation).ConfigureAwait(false))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task<object> PinStateAsync(
        System.Guid service, string name, string? folder, PinnedServices pins, CancellationToken cancellation)
    {
        bool pinned = await pins.IsPinnedAsync(service, cancellation).ConfigureAwait(false);
        int count = await pins.CountAsync(cancellation).ConfigureAwait(false);

        return new
        {
            name,
            folder,
            pinned,
            // When this server last renewed it; null until the first round after a pin, and on a node that has not.
            warmedAt = pinned ? pins.WarmedAt(service) : null,
            pinnedOnServer = count,
            limit = pins.Limit,
        };
    }
}
