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

    /// <summary>Forgets a layer's kept picture, so the next list that shows it draws it again.</summary>
    /// <remarks>
    /// <b>Forgets rather than draws.</b> The picture is drawn by the request that next asks for it — the
    /// same path, the same frame and the same sharing check as every other picture — so this answers at
    /// once instead of holding the button for the seconds a dense layer takes to draw.
    /// </remarks>
    private static async Task RedrawThumbnailAsync(
        HttpContext context,
        string name,
        PostgresLayerCatalog layers,
        ServiceThumbnails thumbnails,
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

        int forgotten = thumbnails.Forget(layer.Id);

        await AuditAsync(
            context, audit, "layer.thumbnail.redraw", name, Detail(new { forgotten }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            note = "The thumbnail will be drawn again the next time a list shows this layer.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Forgets the kept pictures of every layer of this name — what a symbology write changes.</summary>
    /// <remarks>
    /// <b>By name, because the symbology write is by name</b>: it sets every layer called that, so every one
    /// of their pictures is out of date.
    /// </remarks>
    private static async Task ForgetThumbnailsAsync(
        string name, PostgresLayerCatalog layers, ServiceThumbnails thumbnails, CancellationToken cancellation)
    {
        IReadOnlyList<PublishedLayer> named = await layers.NamedAsync(name, cancellation).ConfigureAwait(false);

        foreach (PublishedLayer layer in named)
        {
            thumbnails.Forget(layer.Id);
        }
    }
}
