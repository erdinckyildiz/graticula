using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

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

    /// <summary>The header that says a response was built from a remembered catalogue.</summary>
    /// <remarks>
    /// <b>Named once because two paths write it.</b> The resolve below writes it for a document
    /// about one service, and the four enumerating faces write it for a document about all of
    /// them ([D-127](../../docs/architecture-debt.md)). Two spellings of one header is the
    /// recurring debt D-46 names, and a monitor watching for the misspelt one sees a healthy
    /// server through an outage.
    /// </remarks>
    public const string AgeHeader = "X-Catalog-Age";

    /// <summary>Says on the response how old the catalogue behind it is.</summary>
    /// <param name="context">The request.</param>
    /// <param name="age">How long ago the catalogue last answered.</param>
    public static void SayAge(HttpContext context, TimeSpan age) =>
        context.Response.Headers[AgeHeader] =
            ((int)age.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

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
            SayAge(context, answer.Age);
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

        // <b>The one place a service lowers its own request deadline.</b> The middleware has
        // already put this request on the server's bound; here is the first moment the service
        // behind the URL is known, and it is known without a second catalogue read because this
        // method has just done the only one. Every route that serves a service — feature, tile,
        // metadata, editing — resolves through here, so wiring it once is wiring it everywhere,
        // and a route that forgot to would be a route that does not resolve a service at all.
        //
        // <b>After the visibility check, deliberately.</b> A caller who may not see this service
        // must not be able to time the difference between *absent* and *present but slow*.
        RequestDeadline.LowerTo(context, service.Limits.Cost.RequestDeadline);

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
            .Evaluate(
                service.Sharing,
                service.Owner,
                current.Principal,
                current.Authorization,

                // ADR-036: which groups this service is shared with. Read from the catalogue with
                // the service, so the decision costs no round trip — and consulted only when the
                // scope is `group`.
                service.SharedWith)
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
        int age = (int)answer.Age.TotalSeconds;

        // <b>There are three cases here and the message used to describe two.</b> A
        // blind answer means *never seen*, or *seen and now too old to trust* — and both
        // were told they had no record. The second sentence then reported the record's
        // age, so the refusal contradicted itself inside one paragraph: no record of it,
        // from a catalogue 964 s old. An operator reads that as their service having
        // vanished, when what happened is that the outage passed fifteen minutes.
        //
        // <b>The information to tell them apart was already on the wire.</b> `Age` is
        // zero when nothing was ever remembered and non-zero when the memory expired,
        // which is why this needs no new plumbing. Found by the second failure gate.
        bool expired = unknown && age > 0;

        string detail = unknown
            ? expired
                ? "The platform store is unreachable, and this server's memory of the "
                  + $"catalogue has passed the window it is trusted for, so '{serviceName}' "
                  + "can no longer be resolved from it. This is not a 404: the service may "
                  + "well exist and this server has stopped being willing to guess."
                : "The platform store is unreachable, and this server has no record of a "
                  + $"service named '{serviceName}' from before it went quiet. It may exist; "
                  + "this server cannot currently tell you either way, which is why this is "
                  + "not a 404."
            : "The platform store is unreachable. While it is, this server answers only "
              + "services that were public the last time it could read the catalogue, and "
              + $"'{serviceName}' was not one of them. Serving it would mean honouring a "
              + "permission nobody can currently confirm.";

        string closing = expired
            ? $" The catalogue it is working from is {age}s old, past its window."
            : age > 0
                ? $" Public services are still being served, from a catalogue {age}s old."
                : " This server has not managed to read the catalogue since it started.";

        return Results.Json(
            new
            {
                error = new
                {
                    code = 503,
                    message = detail + closing,
                    catalogAgeSeconds = age,
                },
            },
            statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
    }
}
