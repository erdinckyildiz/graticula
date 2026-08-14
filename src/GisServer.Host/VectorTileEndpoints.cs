using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Api.ArcGis;
using GisServer.Features;
using GisServer.Platform.Catalog;
using GisServer.Platform.Identity;
using GisServer.Platform.Postgres;
using GisServer.Providers.PostGis;
using GisServer.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GisServer.Host;

/// <summary>
/// The ArcGIS VectorTileServer surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three documents and one tile.</b> The FeatureServer work established that
/// a working endpoint nobody can discover is not a service, so the metadata
/// comes first: the service document says where the tiles are, the style says
/// how to draw them, and only then is a tile worth serving.
/// </para>
/// <para>
/// <b>Reading is governed exactly as the feature path governs it</b> — ADR-018
/// §3b sharing, then ADR-020 §3 status, in that order, so a caller who may not
/// see a layer learns nothing about whether it is running.
/// </para>
/// </remarks>
internal static class VectorTileEndpoints
{
    /// <summary>Maps the surface.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/rest/services/{layerName}/VectorTileServer", ServiceAsync);
        app.MapGet("/rest/services/{layerName}/VectorTileServer/resources/styles", StyleAsync);
        app.MapGet("/rest/services/{layerName}/VectorTileServer/resources/styles/root.json", StyleAsync);

        // {z}/{y}/{x} — row before column. This is the ArcGIS URL order and it
        // is the reverse of almost every other tile scheme. Written once, here,
        // where the swap into TileAddress is visible on one line.
        app.MapGet("/rest/services/{layerName}/VectorTileServer/tile/{z:int}/{y:int}/{x:int}.pbf",
            TileAsync);
    }

    /// <summary>
    /// Resolves a layer for tile serving, or answers the caller and returns null.
    /// </summary>
    /// <remarks>
    /// <b>Q-67 is enforced here and nowhere else.</b> Vector tiles come only from
    /// hosted data — data this server owns as system of record — and never from a
    /// registered database. The refusal is separate from the not-found and
    /// not-shared answers because it is a different fact about a layer that
    /// genuinely exists and that the caller may genuinely read: it has a
    /// FeatureServer and will never have a VectorTileServer.
    /// </remarks>
    private static async Task<PublishedLayer?> TileableAsync(
        HttpContext context,
        string layerName,
        PostgresLayerCatalog catalog,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await catalog.FindAsync(layerName, cancellation).ConfigureAwait(false);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (layer is null
            || !LayerAccess
                .Evaluate(layer.Sharing, layer.Owner, current.Principal, current.Authorization)
                .IsAllowed())
        {
            await Authorize.RefuseReadAsync(context, layerName).ConfigureAwait(false);
            return null;
        }

        if (!layer.IsRunning)
        {
            await Authorize.RefuseStoppedAsync(context, layerName).ConfigureAwait(false);
            return null;
        }

        if (!layer.Definition.IsHosted)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message =
                            $"Layer '{layerName}' is registered rather than hosted, so it has no "
                            + "vector tile service. Tiles are served only from hosted data — data "
                            + "this server owns as system of record (Q-67). Its FeatureServer is "
                            + $"at /rest/services/{layerName}/FeatureServer and is unaffected.",
                    },
                },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }

        return layer;
    }

    /// <summary>The service document.</summary>
    private static async Task ServiceAsync(
        HttpContext context,
        string layerName,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await TileableAsync(context, layerName, catalog, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
        }

        // The extent comes from the same cached description the feature path
        // uses, so the two surfaces cannot disagree about where a layer is.
        (_, LayerDescription description) = await contexts.GetAsync(layer, cancellation)
            .ConfigureAwait(false);

        await Results.Ok(VectorTileServerMetadataWriter.Service(
            layer.Definition.Name,
            layer.Definition.Name,
            description.Extent,
            TileAddress.MaxZoom))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The default style.</summary>
    private static async Task StyleAsync(
        HttpContext context,
        string layerName,
        PostgresLayerCatalog catalog,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await TileableAsync(context, layerName, catalog, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
        }

        await Results.Ok(VectorTileServerMetadataWriter.Style(
            layer.Definition.Name, layer.GeometryType))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One tile.</summary>
    /// <remarks>
    /// <para>
    /// <b>An empty tile is 204, not 404.</b> Most of a pyramid is empty. A 404 is
    /// a failure a client may retry and may not cache; 204 says *correct answer,
    /// nothing here*, which is what stops the ocean becoming a retry storm.
    /// </para>
    /// <para>
    /// <b>The attribute list comes from the database, not the request.</b> Column
    /// names reach SQL as identifiers, which cannot be bound as parameters, so
    /// the whitelist is the safety — the same two-step that makes the feature
    /// select list safe (ADR-008 §4.6).
    /// </para>
    /// </remarks>
    private static async Task TileAsync(
        HttpContext context,
        string layerName,
        int z,
        int y,
        int x,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        LayerConnections connections,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await TileableAsync(context, layerName, catalog, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
        }

        TileAddress address = new(z, x, y);

        if (address.Rejection() is { } rejection)
        {
            await Results.Json(
                new { error = new { code = 400, message = rejection } },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        (_, LayerDescription description) = await contexts.GetAsync(layer, cancellation)
            .ConfigureAwait(false);

        ITileSource source = connections.TileSourceFor(layer, AttributesOf(layer, description));

        byte[] tile = await source
            .BuildAsync(address, layer.Definition.Name, cancellation)
            .ConfigureAwait(false);

        if (tile.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        context.Response.ContentType = "application/vnd.mapbox-vector-tile";
        context.Response.Headers.ContentLength = tile.Length;
        await context.Response.Body.WriteAsync(tile, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Which columns ride along in the tile.
    /// </summary>
    /// <remarks>
    /// <b>Not every column.</b> A tag table is repeated in every tile of the
    /// pyramid, so a wide table turns a 12 KB tile into a 200 KB one carrying
    /// attributes nothing draws. Geometry columns are excluded because the
    /// geometry is already the tile, and the identity column is excluded because
    /// <c>ST_AsMVT</c> has nowhere to put a feature id from a named column
    /// without it also becoming a tag.
    /// </remarks>
    private static IReadOnlyList<string> AttributesOf(
        PublishedLayer layer, LayerDescription description)
    {
        HashSet<string> skip = new(StringComparer.Ordinal)
        {
            layer.Definition.GeometryColumn,
        };

        return
        [
            .. description.Fields
                .Where(field => !skip.Contains(field.Name) && CanBeATag(field.Type))
                .Select(field => field.Name)
                .Take(MaximumAttributes),
        ];
    }

    /// <summary>
    /// How many attribute columns a tile carries.
    /// </summary>
    /// <remarks>
    /// A cap rather than a decision, and a visible one. The right answer is a
    /// per-layer choice made when the service is published — which is a
    /// cartographic decision this project has not designed yet — and until then a
    /// wide table would otherwise silently inflate every tile in its pyramid.
    /// </remarks>
    private const int MaximumAttributes = 12;

    /// <summary>
    /// Whether a column's type belongs in a tile as a tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An allow-list, not a deny-list.</b> The MVT tag value union is string,
    /// float, double, int, uint, sint and bool — so anything outside it has to be
    /// converted, and a conversion nobody chose is a wrong answer waiting to be
    /// found. A new <see cref="FieldType"/> added later is excluded by default,
    /// which is the safe direction.
    /// </para>
    /// <para>
    /// <b><see cref="FieldType.Unknown"/> is what the geometry column comes back
    /// as</b> from <c>information_schema</c>, since PostGIS types are not
    /// standard SQL types. Excluding it by type as well as by name means a layer
    /// with a second geometry column does not ship it as a tag — which would put
    /// a whole WKB blob in every tile of the pyramid.
    /// </para>
    /// </remarks>
    private static bool CanBeATag(FieldType type) => type switch
    {
        FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger
            or FieldType.Single or FieldType.Double
            or FieldType.Text or FieldType.Boolean
            or FieldType.Date or FieldType.Guid => true,
        _ => false,
    };
}
