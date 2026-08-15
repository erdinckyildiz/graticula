using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Api.ArcGis;
using GisServer.Platform.Catalog;
using GisServer.Platform.Identity;
using GisServer.Platform.Postgres;
using Microsoft.AspNetCore.Http;

namespace GisServer.Host;

/// <summary>
/// Turns a URL into a service or a layer, or writes the refusal.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, because the checks must be identical everywhere.</b> Six
/// endpoint families resolve a layer from a URL — metadata, query, applyEdits,
/// attachments, related records, tiles — and each must apply the folder
/// redirect, then sharing, then status, in that order. When each wrote its own,
/// the query endpoint skipped the folder check and a conformance test found it.
/// </para>
/// <para>
/// <b>The order is load-bearing.</b> Sharing before status: a caller who may not
/// see the service must not learn whether it is running, so they get the same
/// 404 either way. Only somebody already entitled to know it exists gets the 503
/// that says an operator stopped it.
/// </para>
/// </remarks>
internal static class ServiceLookup
{
    /// <summary>
    /// The service at this URL, or null with the refusal already written.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="catalog">The catalogue.</param>
    /// <param name="serviceName">The name from the route.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The service, or null.</returns>
    public static async Task<PublishedService?> ServiceAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        string serviceName,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);

        string? folder = ServiceFolder.FolderOf(context.Request.Path);

        PublishedService? service = await catalog
            .FindServiceAsync(folder, serviceName, cancellation)
            .ConfigureAwait(false);

        // Asked for in the wrong folder. Look in the other one and redirect,
        // rather than answering "no such service" about a service that exists.
        if (service is null)
        {
            PublishedService? elsewhere = await catalog
                .FindServiceAsync(
                    folder is null ? FeatureServerMetadataWriter.HostedFolder : null,
                    serviceName,
                    cancellation)
                .ConfigureAwait(false);

            if (elsewhere is not null && await VisibleAsync(context, elsewhere, quiet: true)
                .ConfigureAwait(false))
            {
                await ServiceFolder.RedirectAsync(context, elsewhere).ConfigureAwait(false);
                return null;
            }
        }

        if (service is null || !await VisibleAsync(context, service, quiet: false)
            .ConfigureAwait(false))
        {
            if (service is null)
            {
                await Authorize.RefuseReadAsync(context, serviceName).ConfigureAwait(false);
            }

            return null;
        }

        return service;
    }

    /// <summary>
    /// The layer at this URL, or null with the refusal already written.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="catalog">The catalogue.</param>
    /// <param name="serviceName">The service name from the route.</param>
    /// <param name="layerId">The layer number from the route.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The layer, or null.</returns>
    public static async Task<PublishedLayer?> LayerAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        string serviceName,
        int layerId,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return null;
        }

        if (service.Layer(layerId) is not { } layer)
        {
            // <b>Says which layers exist, and that is safe here.</b> The caller
            // has already been admitted to the service, so its layer numbering
            // is not something they can learn by guessing — and a client that
            // asked for layer 3 of a two-layer service is usually holding a URL
            // from a different server.
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 404,
                        message =
                            $"The service '{serviceName}' has no layer {layerId}. It has "
                            + (service.Layers.Count == 0
                                ? "no layers yet."
                                : $"{service.Layers.Count}: "
                                  + string.Join(
                                      ", ",
                                      service.Layers.Select(
                                          l => $"{l.LayerIndex} ({l.Definition.Name})"))),
                    },
                },
                statusCode: StatusCodes.Status404NotFound)
                .ExecuteAsync(context)
                .ConfigureAwait(false);

            return null;
        }

        return layer;
    }

    /// <summary>Sharing, then status. Writes the refusal unless asked not to.</summary>
    private static async Task<bool> VisibleAsync(
        HttpContext context, PublishedService service, bool quiet)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!LayerAccess
            .Evaluate(service.Sharing, service.Owner, current.Principal, current.Authorization)
            .IsAllowed())
        {
            if (!quiet)
            {
                await Authorize.RefuseReadAsync(context, service.Name).ConfigureAwait(false);
            }

            return false;
        }

        if (!service.IsRunning)
        {
            if (!quiet)
            {
                await Authorize.RefuseStoppedAsync(context, service.Name).ConfigureAwait(false);
            }

            return false;
        }

        return true;
    }
}
