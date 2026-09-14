using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>What a layer's visible scale range is set to.</summary>
/// <param name="MinScale">The largest scale it draws at, as in 1:<em>n</em>; 0 or null for no limit.</param>
/// <param name="MaxScale">The smallest scale it draws at; 0 or null for no limit.</param>
internal sealed record VisibleRangeRequest(double? MinScale, double? MaxScale);

/// <summary>
/// A layer's visible scale range — ADR-070.
/// </summary>
/// <remarks>
/// In its own file for the reason <c>AdminEndpoints.FieldOverrides.cs</c> gives: the routes are
/// registered from here, and the publish handlers call <see cref="SuggestAndStoreAsync"/>.
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapVisibleRange(WebApplication app)
    {
        app.MapPut("/admin/layers/{name}/visible-range", SetVisibleRangeAsync);
        app.MapPost("/admin/layers/{name}/visible-range/suggestion", SuggestVisibleRangeAsync);
    }

    /// <summary>Stores the scales a layer draws at.</summary>
    /// <remarks>
    /// <b><c>content:publishFeatures</c>, like the time field and the symbology</b>: at which scales a
    /// layer is worth drawing is a statement about the data and how it is meant to be read.
    /// </remarks>
    private static async Task SetVisibleRangeAsync(
        HttpContext context,
        string name,
        VisibleRangeRequest request,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        double min = request.MinScale ?? 0;
        double max = request.MaxScale ?? 0;

        if (VisibleScaleRange.Refusal(min, max) is { } refusal)
        {
            await Refuse(context, 400, refusal).ConfigureAwait(false);
            return;
        }

        if (await OneNamedLayerAsync(context, layers, name, cancellation).ConfigureAwait(false) is not { } layer)
        {
            return;
        }

        if (!await catalog.SetVisibleRangeAsync(layer.Id, min, max, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        contexts.Forget(layer);

        await AuditAsync(
            context, audit, "layer.visibleRange", name, Detail(new { minScale = min, maxScale = max }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            minScale = min,
            maxScale = max,
            note = Describe(new VisibleScaleRange(min, max)),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Measures what the range should be, without storing it.</summary>
    /// <remarks>
    /// A POST because it runs up to a few dozen counts against the layer's source — not something a
    /// crawler following links should start.
    /// </remarks>
    private static async Task SuggestVisibleRangeAsync(
        HttpContext context,
        string name,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (await OneNamedLayerAsync(context, layers, name, cancellation).ConfigureAwait(false) is not { } layer)
        {
            return;
        }

        VisibleRangeSuggestion.Result result;

        try
        {
            result = await SuggestAsync(layer, contexts, projector, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await Refuse(context, 502, "The layer's source could not be counted, so there is no suggestion.").ConfigureAwait(false);
            return;
        }

        await Results.Json(Summary(result)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Measures a newly published layer and stores the suggested range — the default ArcGIS Online sets
    /// when a layer is published from a file.
    /// </summary>
    /// <param name="layerName">The layer as published.</param>
    /// <param name="serviceName">The service it went into, to tell apart layers sharing a name.</param>
    /// <param name="layers">The layer catalogue.</param>
    /// <param name="contexts">The layer readers.</param>
    /// <param name="catalog">Where to store it.</param>
    /// <param name="projector">For the extent.</param>
    /// <param name="cancellation">The request's.</param>
    /// <returns>What to say about it in the publish response.</returns>
    /// <remarks>
    /// <b>A failure is not a failed publish.</b> The layer exists and serves; it draws at every scale,
    /// as every layer did before ADR-070, and the response says why nothing was set.
    /// </remarks>
    internal static async Task<object> SuggestAndStoreAsync(
        string layerName,
        string serviceName,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        IAdminCatalog catalog,
        IProjector projector,
        CancellationToken cancellation)
    {
        try
        {
            IReadOnlyList<PublishedLayer> named = await layers.NamedAsync(layerName, cancellation).ConfigureAwait(false);

            if (named.FirstOrDefault(l => string.Equals(l.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
                is not { } layer)
            {
                return new { set = false, note = "The layer was not found again after publishing, so no range was measured." };
            }

            VisibleRangeSuggestion.Result result =
                await SuggestAsync(layer, contexts, projector, cancellation).ConfigureAwait(false);

            if (result.MinScale is not { } min)
            {
                return new { set = false, note = $"No visible range was set: {result.Reason}. The layer draws at every scale." };
            }

            if (min > 0)
            {
                await catalog.SetVisibleRangeAsync(layer.Id, min, 0, cancellation).ConfigureAwait(false);
                contexts.Forget(layer);
            }

            return new { set = min > 0, suggestion = Summary(result) };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new { set = false, note = "No visible range was set: the source could not be counted. The layer draws at every scale." };
        }
    }

    private static async Task<VisibleRangeSuggestion.Result> SuggestAsync(
        PublishedLayer layer, ServiceContexts contexts, IProjector projector, CancellationToken cancellation)
    {
        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        return await VisibleRangeSuggestion
            .SuggestAsync(source, described, layer.Definition.Srid, projector, cancellation)
            .ConfigureAwait(false);
    }

    private static object Summary(VisibleRangeSuggestion.Result result) => new
    {
        minScale = result.MinScale,
        maxScale = result.MinScale is null ? (double?)null : 0,
        level = result.Level,
        densestTile = result.DensestTile,
        featuresPerTile = VisibleRangeSuggestion.FeaturesPerTile,
        counts = result.Counted,
        note = result.MinScale switch
        {
            null => $"No suggestion: {result.Reason}.",
            0 => $"No tile holds more than {VisibleRangeSuggestion.FeaturesPerTile:N0} features even zoomed all the way out, so the layer can draw at every scale.",
            { } min => $"Zoomed out past 1:{min:N0}, one tile would hold more than {VisibleRangeSuggestion.FeaturesPerTile:N0} features "
                + $"(the densest tile counted at that scale holds {result.DensestTile:N0}). An estimate: the search follows the densest areas it finds.",
        },
    };

    private static string Describe(VisibleScaleRange range) =>
        (range.MinScale, range.MaxScale) switch
        {
            (0, 0) => "The layer draws at every scale.",
            (> 0, 0) => $"The layer draws when zoomed in to 1:{range.MinScale:N0} or closer.",
            (0, > 0) => $"The layer draws until zoomed in past 1:{range.MaxScale:N0}.",
            _ => $"The layer draws between 1:{range.MinScale:N0} and 1:{range.MaxScale:N0}.",
        };
}
