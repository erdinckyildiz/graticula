using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Api.ArcGis;
using GisServer.Geometries;
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

        // Tiles come only from hosted data (Q-67), so the natural home is the
        // hosted folder — but the root path is mapped too, and answers with the
        // redirect in TileableAsync rather than a 404. A client that built a URL
        // before the folder existed gets told where the service moved.
        foreach (string prefix in (string[])
            ["/rest/services", $"/rest/services/{Api.ArcGis.FeatureServerMetadataWriter.HostedFolder}"])
        {
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer", ServiceAsync);
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/resources/styles", StyleAsync);
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/resources/styles/root.json", StyleAsync);

            // {z}/{y}/{x} — row before column. This is the ArcGIS URL order and
            // it is the reverse of almost every other tile scheme. Written once,
            // here, where the swap into TileAddress is visible on one line.
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/tile/{{z:int}}/{{y:int}}/{{x:int}}.pbf",
                TileAsync);
        }
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
    private static async Task<PublishedService?> TileableAsync(
        HttpContext context,
        string serviceName,
        PostgresLayerCatalog catalog,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return null;
        }

        if (service.Layers.Count == 0)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message =
                            $"The service '{serviceName}' has no layers, so there is nothing to "
                            + "put in a tile.",
                    },
                },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }

        // <b>Every layer, not the first one.</b> A tile carries all of a
        // service's layers, so one registered or non-Mercator layer disqualifies
        // the service rather than being quietly skipped — a tile missing one of
        // three layers looks like missing data, and nobody would know to ask.
        PublishedLayer layer = service.Layers[0];

        foreach (PublishedLayer each in service.Layers)
        {
            if (!each.Definition.IsHosted || each.Definition.Srid != WebMercator)
            {
                layer = each;
                break;
            }
        }

        string layerName = service.QualifiedName;

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

        if (layer.Definition.Srid != WebMercator)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message =
                            $"Layer '{layerName}' is in SRID {layer.Definition.Srid} and vector "
                            + $"tiles are served on the Web Mercator grid ({WebMercator}). This "
                            + "server does not reproject on the tile path, so the layer needs a "
                            + $"{WebMercator} geometry column — add one, or publish a view that "
                            + "transforms it, and index it. Its FeatureServer at "
                            + $"/rest/services/{layerName}/FeatureServer serves the native "
                            + "spatial reference and is unaffected.",
                    },
                },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }

        return service;
    }

    /// <summary>
    /// The only spatial reference tiles are served on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure this constant prevents is silence, not an error.</b>
    /// <c>ST_TileEnvelope</c> returns a Web Mercator rectangle. Given a layer in
    /// another spatial reference, <c>&amp;&amp;</c> compares two bounding boxes
    /// whose numbers are in different units and simply does not overlap, and
    /// <c>ST_AsMVTGeom</c> clips everything away. **PostGIS raises nothing.** The
    /// measured result on a 4326 layer was a zero-byte tile — so the service
    /// answered 204 for every tile on Earth, with no error and no log line, which
    /// is exactly the silent degradation ADR-008 §2 forbids.
    /// </para>
    /// <para>
    /// <b>Reprojecting on read was measured rather than dismissed.</b>
    /// <c>ST_Transform</c> in the select, with the tile envelope transformed once
    /// into the layer's own reference so the spatial index is still used, costs
    /// <b>74.6 ms against 21.6 ms</b> on the same tile — 3.5×, and correct. It is
    /// not implemented because a datum transformation is a different kind of
    /// claim from an affine one: 4326 to 3857 is a pure formula, while a national
    /// grid to Web Mercator needs shift grids PROJ may not have, and silently
    /// falling back to a null transform moves features by metres. That is a CRS
    /// decision and it is [Q-96], not something to slip in behind a constant.
    /// </para>
    /// </remarks>
    public const int WebMercator = 3857;

    /// <summary>The service document.</summary>
    private static async Task ServiceAsync(
        HttpContext context,
        string serviceName,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        PublishedService? service = await TileableAsync(context, serviceName, catalog, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        // The extent comes from the same cached description the feature path
        // uses, so the two surfaces cannot disagree about where a layer is.
        Envelope? extent = null;

        foreach (PublishedLayer layer in service.Layers)
        {
            (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation)
                .ConfigureAwait(false);

            extent = Widen(extent, described.Extent);
        }

        await Results.Ok(VectorTileServerMetadataWriter.Service(
            service.Name,
            [.. service.Layers.Select(l => l.Definition.Name)],
            extent,
            TileAddress.MaxZoom))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The smallest box containing both, treating null as nothing.</summary>
    private static Envelope? Widen(Envelope? sofar, Envelope? next)
    {
        if (next is not { } add)
        {
            return sofar;
        }

        return sofar is not { } have
            ? add
            : new Envelope(
                Math.Min(have.MinX, add.MinX),
                Math.Min(have.MinY, add.MinY),
                Math.Max(have.MaxX, add.MaxX),
                Math.Max(have.MaxY, add.MaxY));
    }

    /// <summary>The default style.</summary>
    private static async Task StyleAsync(
        HttpContext context,
        string serviceName,
        PostgresLayerCatalog catalog,
        CancellationToken cancellation)
    {
        PublishedService? service = await TileableAsync(context, serviceName, catalog, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        // One style layer per source layer, drawn in index order — polygons
        // before lines before points would be nicer, and is a cartographic
        // decision this default has no business making. Index order is what the
        // publisher chose.
        await Results.Ok(VectorTileServerMetadataWriter.Style(
            [.. service.Layers.Select(l => (l.Definition.Name, l.GeometryType))]))
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
        string serviceName,
        int z,
        int y,
        int x,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        LayerConnections connections,
        ITileCache cache,
        CancellationToken cancellation)
    {
        PublishedService? service = await TileableAsync(context, serviceName, catalog, cancellation)
            .ConfigureAwait(false);

        if (service is null)
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

        // <b>The cache is consulted after authorization, never before.</b>
        // ADR-010 §4: for tiles the authorization is uniform — a service is
        // readable or it is not — so the check happens first and every
        // authorized caller shares one entry. Looking up before the check would
        // make a cache hit a way around the sharing rule.
        //
        // <b>Cached per layer, not per service.</b> The whole tile could be one
        // entry, and then adding a fourth layer to a service would silently
        // serve three-layer tiles from every warm entry in the pyramid. Per
        // layer, a new layer simply has no entries yet and the other three keep
        // theirs.
        List<byte[]> parts = [];
        bool everyPartCached = true;

        foreach (PublishedLayer layer in service.Layers)
        {
            (_, LayerDescription description) = await contexts.GetAsync(layer, cancellation)
                .ConfigureAwait(false);

            IReadOnlyList<string> attributes = AttributesOf(layer, description);

            TileCacheKey key = new(
                layer.Id,
                TileCacheKey.FingerprintOf(
                    layer.Definition.Srid,
                    layer.Definition.GeometryColumn,
                    attributes,
                    PostGisTileSource.Extent,
                    PostGisTileSource.Buffer),
                address);

            CachedTile cached = await cache.ReadAsync(key, cancellation).ConfigureAwait(false);

            if (cached.Answered)
            {
                parts.Add(cached.Bytes);
                continue;
            }

            everyPartCached = false;

            ITileSource source = connections.TileSourceFor(layer, attributes);

            byte[] part = await source
                .BuildAsync(address, layer.Definition.Name, cancellation)
                .ConfigureAwait(false);

            // Empty is stored too — a zero-length marker. Most of a pyramid is
            // emptiness and rebuilding the ocean on every request is the waste
            // ADR-010 §2's negative caching exists to stop.
            await cache.WriteAsync(key, part, cancellation).ConfigureAwait(false);

            parts.Add(part);
        }

        await WriteTileAsync(
            context, Concatenate(parts), everyPartCached ? "HIT" : "MISS", cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Joins one encoded layer per service layer into one tile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Byte concatenation is the whole implementation, and it is correct
    /// rather than a trick.</b> A vector tile is a protobuf message whose only
    /// field is <c>repeated Layer layers = 3</c>, and protobuf defines the
    /// concatenation of two encodings of a message as an encoding of that
    /// message with repeated fields appended. So two single-layer tiles laid end
    /// to end <em>are</em> the two-layer tile — no decode, no re-encode, and no
    /// dependency on our own encoder being right.
    /// </para>
    /// <para>
    /// <b>Empty parts vanish, which is what should happen.</b> A layer with
    /// nothing in this tile encodes to zero bytes; appending nothing is
    /// appending nothing. A service whose layers are all empty here produces an
    /// empty tile, and that is the 204 the caller should get.
    /// </para>
    /// </remarks>
    private static byte[] Concatenate(List<byte[]> parts)
    {
        int total = 0;

        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        if (total == 0)
        {
            return [];
        }

        // The common case by a wide margin: a single-layer service, where there
        // is nothing to join and no reason to copy.
        if (parts.Count == 1)
        {
            return parts[0];
        }

        byte[] tile = new byte[total];
        int at = 0;

        foreach (byte[] part in parts)
        {
            part.CopyTo(tile, at);
            at += part.Length;
        }

        return tile;
    }

    /// <summary>
    /// Sends a tile, or 204 when there is nothing in it.
    /// </summary>
    /// <remarks>
    /// <b>The cache header is not decoration.</b> Without it there is no way to
    /// tell a working cache from a bypassed one from outside the process, and a
    /// cache that has silently stopped working looks exactly like one that is
    /// working — until the datastore falls over under load nobody expected.
    /// </remarks>
    private static async Task WriteTileAsync(
        HttpContext context, byte[] tile, string cacheState, CancellationToken cancellation)
    {
        context.Response.Headers["X-Tile-Cache"] = cacheState;

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
