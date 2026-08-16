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
/// <para>
/// <b>And there is now a third state: blind.</b> Q-95, answered by the owner
/// 2026-08-15. When the platform store cannot be reached, this resolver answers
/// from the last catalogue entry it saw — but only for services whose remembered
/// sharing was <c>Public</c>. Everything else is refused while blind, because a
/// remembered grant on data somebody chose not to make public is the one stale
/// value with a real cost. See <see cref="CatalogFallback"/>.
/// </para>
/// </remarks>
internal static class ServiceLookup
{
    private const string BlindKey = "gis-catalog-blind";

    /// <summary>
    /// The stale-catalogue answer behind this request, if it was one.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The answer, or null when the store was reachable.</returns>
    public static CatalogAnswer? Blind(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(BlindKey, out object? value)
            && value is CatalogAnswer answer
                ? answer
                : null;
    }

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
        CatalogFallback catalog,
        string serviceName,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);

        string? folder = ServiceFolder.FolderOf(context.Request.Path);

        CatalogAnswer answer = await catalog
            .FindServiceAsync(folder, serviceName, cancellation)
            .ConfigureAwait(false);

        // Asked for in the wrong folder. Look in the other one and redirect,
        // rather than answering "no such service" about a service that exists.
        if (answer.Service is null && !answer.Blind)
        {
            CatalogAnswer elsewhere = await catalog
                .FindServiceAsync(
                    folder is null ? FeatureServerMetadataWriter.HostedFolder : null,
                    serviceName,
                    cancellation)
                .ConfigureAwait(false);

            if (elsewhere.Service is { } other
                && await VisibleAsync(context, other, elsewhere, quiet: true)
                    .ConfigureAwait(false))
            {
                await ServiceFolder.RedirectAsync(context, other).ConfigureAwait(false);
                return null;
            }
        }

        // <b>Blind, with no memory of this name.</b> A 404 here would be a
        // claim, and the claim is wrong for every service published since this
        // process last read the catalogue — and for every service at all after
        // a restart. 503 is the honest answer: ask again later.
        if (answer.Service is null && answer.Blind)
        {
            await RefuseBlindAsync(context, serviceName, answer, unknown: true)
                .ConfigureAwait(false);

            return null;
        }

        // <b>Left where the endpoints can find it.</b> A document built from a
        // remembered catalogue must say so, and threading the answer through
        // eight signatures to carry one boolean is how a signature grows a
        // parameter nobody reads. HttpContext.Items rather than a static, which
        // this project has already got wrong once.
        if (answer.Blind)
        {
            context.Items[BlindKey] = answer;

            // <b>On every blind response, including tiles and query results.</b>
            // The ArcGIS JSON contract has nowhere to say this for most
            // documents, and an operator watching a dashboard needs one signal
            // that covers all of them rather than a field on the two that have
            // room for it. Cheap, ignorable, and impossible to miss in a log.
            context.Response.Headers["X-Catalog-Age"] =
                ((int)answer.Age.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (answer.Service is not { } service
            || !await VisibleAsync(context, service, answer, quiet: false).ConfigureAwait(false))
        {
            if (answer.Service is null)
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
        CatalogFallback catalog,
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

        // <b>The feature face, if it has been turned off, answers as absent</b> —
        // ADR-031 condition 2, and the same refusal ADR-018 gives for a service
        // nobody may see. The gate is here rather than in `ServiceAsync` because
        // the tile path resolves through that method too: gating there would turn
        // off both faces at once and make the tiles-only configuration
        // unreachable, which is the configuration this feature was asked for.
        if (!service.Limits.AllowsFeatures(dataSupportsIt: true))
        {
            await Authorize.RefuseReadAsync(context, service.Name).ConfigureAwait(false);
            return null;
        }

        if (service.Layer(layerId) is not { } layer)
        {
            // <b>A group layer exists at this index and has no features.</b>
            // Answering "no layer 1" about an index the service document
            // advertises sends somebody hunting for a routing bug; naming what
            // is actually there ends the search in one read.
            if (service.Group(layerId) is { } group)
            {
                await Results.Json(
                    new
                    {
                        error = new
                        {
                            code = 400,
                            message =
                                $"Layer {layerId} of '{serviceName}' is the group layer "
                                + $"'{group.Name}'. A group organises other layers and holds no "
                                + "data of its own, so it has nothing to query or edit. Its "
                                + "children are listed in subLayerIds on "
                                + $"/rest/services/{service.QualifiedName}/FeatureServer"
                                + $"/{layerId}.",
                        },
                    },
                    statusCode: StatusCodes.Status400BadRequest)
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);

                return null;
            }

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

    /// <summary>Blind, then sharing, then status. Writes the refusal unless asked not to.</summary>
    /// <remarks>
    /// <b>Blind goes first, and it has to.</b> The sharing check below is a
    /// function of a remembered value when the store is unreachable, so running
    /// it first would mean answering <em>yes, you may see this</em> on evidence
    /// that may be minutes out of date. While blind the only scope that survives
    /// is <c>Public</c> — the one where being wrong about it costs nothing that
    /// was not already given away.
    /// </remarks>
    private static async Task<bool> VisibleAsync(
        HttpContext context, PublishedService service, CatalogAnswer answer, bool quiet)
    {
        if (answer.Blind && service.Sharing != SharingScope.Public)
        {
            if (!quiet)
            {
                await RefuseBlindAsync(context, service.Name, answer, unknown: false)
                    .ConfigureAwait(false);
            }

            return false;
        }

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

    /// <summary>
    /// Refuses because the platform store is unreachable, and says exactly that.
    /// </summary>
    /// <remarks>
    /// <b>503, and the two cases are worth distinguishing.</b> A service we
    /// remember but may not serve, and a service we have no memory of at all.
    /// Neither is the caller's doing and neither is permanent, so both should be
    /// retried — but only one of them tells an operator that this server is
    /// running on a stale catalogue, which is the thing they need to know.
    /// </remarks>
    private static Task RefuseBlindAsync(
        HttpContext context, string serviceName, CatalogAnswer answer, bool unknown)
    {
        string detail = unknown
            ? "The platform store is unreachable, and this server has no record of a service "
              + $"named '{serviceName}' from before it went quiet. It may exist; this server "
              + "cannot currently tell you either way, which is why this is not a 404."
            : "The platform store is unreachable. While it is, this server answers only "
              + "services that were public the last time it could read the catalogue, and "
              + $"'{serviceName}' was not one of them. Serving it would mean honouring a "
              + "permission nobody can currently confirm.";

        return Results.Json(
            new
            {
                error = new
                {
                    code = 503,
                    message = detail
                        + " Public services are still being served, from a catalogue "
                        + $"{(int)answer.Age.TotalSeconds}s old.",
                    catalogAgeSeconds = (int)answer.Age.TotalSeconds,
                },
            },
            statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
    }
}
