using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Drawing a layer's kept thumbnail again — ADR-071.
/// </summary>
/// <remarks>
/// In its own file for the reason <c>AdminEndpoints.FieldOverrides.cs</c> gives.
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapThumbnails(WebApplication app) =>
        app.MapPost("/admin/layers/{name}/thumbnail/redraw", RedrawThumbnailAsync);

    /// <summary>Forgets a layer's kept picture and draws it again in the background.</summary>
    /// <remarks>
    /// <b>Answers at once</b> rather than holding the button for the seconds a dense layer takes; a request
    /// for the picture meanwhile waits for that same draw rather than starting a second.
    /// </remarks>
    private static async Task RedrawThumbnailAsync(
        HttpContext context,
        string name,
        PostgresLayerCatalog layers,
        ThumbnailWarmer warmer,
        IAuditLog audit,
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

        warmer.Redraw(layer.Id);

        await AuditAsync(
            context, audit, "layer.thumbnail.redraw", name, Detail(new { layer = layer.Id }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            note = "The thumbnail is being drawn again.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Draws again the pictures of every layer of this name — what a symbology write changes.</summary>
    /// <remarks>
    /// <b>By name, because the symbology write is by name</b>: it sets every layer called that, so every one
    /// of their pictures is out of date.
    /// </remarks>
    private static async Task ForgetThumbnailsAsync(
        string name, PostgresLayerCatalog layers, ThumbnailWarmer warmer, CancellationToken cancellation)
    {
        IReadOnlyList<PublishedLayer> named = await layers.NamedAsync(name, cancellation).ConfigureAwait(false);

        foreach (PublishedLayer layer in named)
        {
            warmer.Redraw(layer.Id);
        }
    }

    /// <summary>Queues the picture of a layer just published, so its first viewer does not draw it.</summary>
    private static async Task WarmThumbnailAsync(
        string layerName, string serviceName, PostgresLayerCatalog layers, ThumbnailWarmer warmer, CancellationToken cancellation)
    {
        foreach (PublishedLayer layer in await layers.NamedAsync(layerName, cancellation).ConfigureAwait(false))
        {
            if (string.Equals(layer.ServiceName, serviceName, System.StringComparison.OrdinalIgnoreCase))
            {
                warmer.Enqueue(layer.Id);
            }
        }
    }
}
