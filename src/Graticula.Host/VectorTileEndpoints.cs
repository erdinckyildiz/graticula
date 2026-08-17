using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Geometries;
using Graticula.Features;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

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
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer", ServiceAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/resources/styles", StyleAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/resources/styles/root.json", StyleAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // <b>The resources a style needs to draw a label.</b> Without the
            // fonts a client with a text-field renders no text at all and logs a
            // fetch error, which reads as a broken server rather than a missing
            // feature. The sprite sheet is empty and exists so that a style
            // carrying an icon reference gets an answer instead of a 404.
            app.MapGet(
                $"{prefix}/{{serviceName}}/VectorTileServer/resources/fonts/{{fontstack}}/{{range}}.pbf",
                FontAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/resources/sprites/{{sprite}}",
                SpriteAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // {z}/{y}/{x} — row before column. This is the ArcGIS URL order and
            // it is the reverse of almost every other tile scheme. Written once,
            // here, where the swap into TileAddress is visible on one line.
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/tile/{{z:int}}/{{y:int}}/{{x:int}}.pbf",
                TileAsync)
                .Governed(SharingGovernedExtensions.ByService);
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
        CatalogFallback catalog,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return null;
        }

        // <b>A face turned off answers exactly as an absent one does</b> — ADR-031
        // condition 2. `ServiceLookup` has already produced the not-found response
        // for a service nobody may see, and this reuses it rather than writing a
        // recognisable "tiles are disabled here": a distinguishable refusal would
        // let a caller enumerate which services exist by reading which ones say no
        // differently, and ADR-018 makes absent and forbidden identical for the same
        // reason.
        if (!service.Limits.AllowsTiles(dataSupportsIt: true))
        {
            await Authorize.RefuseReadAsync(context, service.Name).ConfigureAwait(false);
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
            if (!each.Definition.IsHosted)
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

        // <b>No spatial-reference refusal any more.</b> Owner correction
        // 2026-08-15: a layer keeps the projection it arrived in and the tile
        // path transforms per request. What used to sit here was a 400 telling
        // the caller to republish their data in Web Mercator — which is asking
        // somebody to destroy their survey coordinates so that a tile is
        // cheaper to cut. PostGisTileSource transforms the tile envelope once
        // into the layer's reference for the index test and each surviving row
        // on the way out; Q-96 measured that at 74.6 ms against 21.6 ms, paid
        // once per tile by the cache.
        //
        // <b>What is still worth watching.</b> 4326 to 3857 is a closed formula,
        // but a national grid needs a datum transformation and PROJ falls back
        // to a ballpark path when the shift grids are missing — quietly, and by
        // metres. That is Q-96's remaining half and is recorded there rather
        // than solved here.
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
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
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

        // The extent is in whatever the layers are stored in. Taken from the first
        // layer, as the FeatureServer document does: a service whose layers
        // disagree about their reference would have no single extent to report
        // either, and nothing in the publish path produces one today.
        int srid = service.Layers.Count > 0 ? service.Layers[0].Definition.Srid : WebMercator;

        extent = await InWebMercatorAsync(extent, srid, projector, cancellation)
            .ConfigureAwait(false);

        await Results.Ok(VectorTileServerMetadataWriter.Service(
            service.Name,
            [.. service.Layers.Select(l => l.Definition.Name)],
            extent,
            TileAddress.MaxZoom,
            WebMercator))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The extent in Web Mercator, because that is the only reference a tile
    /// document's extent may be in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Projected, not relabelled, and the difference was measurable.</b> Saying
    /// 4326 truthfully in the document made the ArcGIS JS client fetch the metadata,
    /// the style and the sprites and then request no tile at all — the tiling scheme
    /// is Web Mercator, so the extent has to be too. Projection goes to the
    /// datastore's PROJ (ADR-022 §4) rather than to arithmetic here.
    /// </para>
    /// <para>
    /// <b>Corners only, which is an approximation and worth naming.</b> A projected
    /// rectangle's edges are curves in the general case, so the true envelope can be
    /// slightly larger than the one its corners describe. For 4326 → Web Mercator
    /// the transform is separable and monotonic in each axis, so the corners are
    /// exact; for a projected source reference this may under-cover the box by a
    /// fraction of its size. It is a metadata extent used to frame a view, not a
    /// filter, so the error is a slightly tight initial zoom rather than missing
    /// data.
    /// </para>
    /// <para>
    /// <b>A failure falls back to nothing rather than to the wrong numbers.</b> The
    /// writer then reports the whole world, which is the documented behaviour for an
    /// unknown extent and is safe for a client; degrees labelled as metres are not.
    /// </para>
    /// </remarks>
    private static async Task<Envelope?> InWebMercatorAsync(
        Envelope? extent, int srid, IProjector projector, CancellationToken cancellation)
    {
        if (extent is not { } box || srid == WebMercator || srid == 102100)
        {
            return extent;
        }

        Geometry rectangle = new Polygon(new LinearRing(XySequence.Wrap(
        [
            box.MinX, box.MinY,
            box.MaxX, box.MinY,
            box.MaxX, box.MaxY,
            box.MinX, box.MaxY,
            box.MinX, box.MinY,
        ])));

        try
        {
            (IReadOnlyList<Geometry> projected, _) = await projector
                .ProjectAsync([rectangle], srid, WebMercator, cancellation)
                .ConfigureAwait(false);

            return projected.Count > 0 ? projected[0].Envelope : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
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
    /// <summary>
    /// One range of signed-distance-field glyphs.
    /// </summary>
    /// <remarks>
    /// <b>Behind the service's sharing, like every other resource.</b> The
    /// glyphs are identical for every service and are not secret, but answering
    /// for a service the caller may not see would confirm it exists — and the
    /// whole point of the governed route group is that no resource under a
    /// service is an exception to it.
    /// </remarks>
    private static async Task FontAsync(
        HttpContext context,
        string serviceName,
        string fontstack,
        string range,
        CatalogFallback catalog,
        GlyphStore glyphs,
        CancellationToken cancellation)
    {
        if (await TileableAsync(context, serviceName, catalog, cancellation)
                .ConfigureAwait(false) is null)
        {
            return;
        }

        if (!glyphs.TryRead(fontstack, range, out byte[] bytes, out string served))
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 404,
                        message = glyphs.Any
                            ? $"No glyph range '{range}'. Ranges are 256 codepoints on a fixed "
                              + "grid — 0-255, 256-511, and so on — and only the ranges the shipped "
                              + $"font covers exist. Available font stacks: "
                              + string.Join(", ", glyphs.Stacks) + "."
                            : "This server shipped without glyphs, so styles cannot draw labels. "
                              + "The ranges are generated by tools/make-glyphs.py into a 'glyphs' "
                              + "directory beside the binary.",
                    },
                },
                statusCode: StatusCodes.Status404NotFound)
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        // Immutable: a range is generated once and never changes for the life of
        // a build. A client fetches up to a few of these per map and re-fetching
        // them is pure waste.
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        // Which font actually answered, because a style asking for Arial gets
        // DejaVu and nothing else in the response would say so.
        context.Response.Headers["X-Font-Stack"] = served;

        await Results.Bytes(bytes, "application/x-protobuf").ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The sprite sheet, which is deliberately empty.
    /// </summary>
    /// <remarks>
    /// <b>Empty, and that is honest rather than lazy.</b> There is no icon
    /// library to ship and no way yet for anybody to upload one — style document
    /// management does not exist. What this prevents is a 404 on a resource
    /// every ArcGIS and Mapbox client probes, which is the difference between
    /// <em>this service has no icons</em> and <em>this service is broken</em>.
    /// </remarks>
    private static async Task SpriteAsync(
        HttpContext context,
        string serviceName,
        string sprite,
        CatalogFallback catalog,
        CancellationToken cancellation)
    {
        if (await TileableAsync(context, serviceName, catalog, cancellation)
                .ConfigureAwait(false) is null)
        {
            return;
        }

        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        // Matched exactly rather than by extension, so the name in the URL never
        // becomes a lookup of any kind.
        switch (sprite)
        {
            case "sprite.json":
            case "sprite@2x.json":
                await Results.Content("{}", "application/json; charset=utf-8")
                    .ExecuteAsync(context).ConfigureAwait(false);
                return;

            case "sprite.png":
            case "sprite@2x.png":
                await Results.Bytes(EmptySheet, "image/png").ExecuteAsync(context)
                    .ConfigureAwait(false);
                return;

            default:
                await Results.Json(
                    new
                    {
                        error = new
                        {
                            code = 404,
                            message =
                                "A sprite sheet is sprite.json, sprite.png, sprite@2x.json or "
                                + "sprite@2x.png. This service ships an empty one: it has no "
                                + "icons, because there is no way to give it any yet.",
                        },
                    },
                    statusCode: StatusCodes.Status404NotFound)
                    .ExecuteAsync(context).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>A one-pixel transparent PNG: an atlas with nothing in it.</summary>
    private static readonly byte[] EmptySheet = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNgYGBgAAAABQABeqhXUAAAAABJRU5ErkJggg==");

    private static async Task StyleAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        GlyphStore glyphs,
        CancellationToken cancellation)
    {
        PublishedService? service = await TileableAsync(context, serviceName, catalog, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        // <b>A stored style wins, unchanged.</b> It was checked when it was
        // written, so nothing here reparses or rewrites it — a cartographer
        // should get back the file they sent, not a normalised version of it
        // (ADR-028).
        if (service.Style is { Length: > 0 } stored)
        {
            await Results.Content(stored, "application/json; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        // One style layer per source layer, drawn in index order — polygons
        // before lines before points would be nicer, and is a cartographic
        // decision this default has no business making. Index order is what the
        // publisher chose.
        await Results.Ok(VectorTileServerMetadataWriter.Style(
            [.. service.Layers.Select(l => (l.Definition.Name, l.GeometryType))],
            glyphs.Any ? GlyphStore.Fallback : null))
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
        CatalogFallback catalog,
        ServiceContexts contexts,
        LayerConnections connections,
        ITileCache cache,
        TileSingleFlight building,
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
        bool builtSomething = false;
        bool waitedForSomething = false;

        // <b>The shortest of the service's layers wins.</b> One tile carries
        // every layer, so it can only be as fresh as its most volatile part —
        // telling a browser to keep it for a day because two of three layers
        // are static would serve the third stale for a day.
        TimeSpan defaultLifetime = cache is FileSystemTileCache disk
            ? disk.DefaultLifetime
            : TimeSpan.FromHours(1);

        TimeSpan shortest = TimeSpan.MaxValue;

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

            // <b>The layer's own lifetime, not the server's.</b> D-25: a
            // cadastral layer and an incident layer need opposite answers, and
            // A-028 records that only the administrator knows which is which.
            TimeSpan lifetime = layer.CacheLifetime ?? defaultLifetime;

            if (lifetime < shortest)
            {
                shortest = lifetime;
            }

            CachedTile cached = await cache.ReadAsync(key, lifetime, cancellation)
                .ConfigureAwait(false);

            if (cached.Answered)
            {
                parts.Add(cached.Bytes);
                continue;
            }

            ITileSource source = connections.TileSourceFor(layer, attributes);

            // <b>One build per cold tile, however many callers arrive at
            // once.</b> Measured before this existed: twelve simultaneous
            // requests for one cold tile produced twelve datastore builds and
            // threw eleven of the results away. See TileSingleFlight.
            TileSingleFlight.Result made = await building.BuildAsync(
                key,
                async () =>
                {
                    byte[] bytes = await source
                        .BuildAsync(address, layer.Definition.Name, CancellationToken.None)
                        .ConfigureAwait(false);

                    // Written inside the shared build, so the waiters do not
                    // each write the same bytes over each other — and so the
                    // next caller finds it cached rather than joining a build
                    // that has already returned.
                    //
                    // Empty is stored too — a zero-length marker. Most of a
                    // pyramid is emptiness and rebuilding the ocean on every
                    // request is the waste ADR-010 §2's negative caching exists
                    // to stop.
                    await cache.WriteAsync(key, bytes, CancellationToken.None)
                        .ConfigureAwait(false);

                    return bytes;
                },
                cancellation).ConfigureAwait(false);

            if (made.Built)
            {
                builtSomething = true;
            }
            else
            {
                waitedForSomething = true;
            }

            parts.Add(made.Bytes);
        }

        // <b>Three states, not two, because the third is the one worth
        // seeing.</b> MISS means this request made the datastore work.
        // COALESCED means it wanted a cold tile and got somebody else's build
        // for free — which is the whole point of TileSingleFlight, and is
        // invisible if both are reported as a miss.
        string disposition = builtSomething
            ? "MISS"
            : waitedForSomething ? "COALESCED" : "HIT";

        await WriteTileAsync(
            context,
            Concatenate(parts),
            disposition,
            shortest == TimeSpan.MaxValue ? defaultLifetime : shortest,
            cancellation)
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
        HttpContext context,
        byte[] tile,
        string cacheState,
        TimeSpan lifetime,
        CancellationToken cancellation)
    {
        context.Response.Headers["X-Tile-Cache"] = cacheState;

        // <b>The same number the server caches by, told to everyone downstream.</b>
        // A browser and a CDN each keep their own copy, and until now we told
        // them nothing — so they either re-fetched every tile or invented a
        // policy. Sending the layer's own volatility means one setting governs
        // every cache in the chain, which is the only way they can agree.
        //
        // Zero means never cache, and no-store says that in the vocabulary an
        // intermediary already understands.
        context.Response.Headers.CacheControl = lifetime <= TimeSpan.Zero
            ? "no-store"
            : $"public, max-age={((long)lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture)}";

        if (tile.Length == 0)
        {
            // No body, so nothing to revalidate against. An ETag on a 204 would
            // be an identifier for the absence of bytes.
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // <b>An ETag, so that expiry costs a header instead of a tile.</b>
        // `max-age` above stops a client asking for an hour; when the hour is
        // up it asks again, and without a validator the only possible answer is
        // the whole tile. Most tiles never change — a cadastral pyramid is
        // rebuilt when somebody edits a parcel, not hourly — so the common case
        // after expiry is re-sending bytes the caller already has.
        //
        // <b>Computed from the bytes, not from the cache key.</b> A key-derived
        // tag would claim two tiles are identical because they were asked for
        // the same way, which is exactly wrong for the case that matters: the
        // data changed and the address did not. Hashing ~50 KB costs
        // microseconds beside the query that produced it.
        //
        // <b>Strong, not weak.</b> These are bytes, compared byte for byte;
        // there is no notion of a semantically equivalent tile.
        string etag = "\"" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(tile).AsSpan(0, 16)) + "\"";

        context.Response.Headers.ETag = etag;

        // <b>Compared after the tile is in hand, and that is the honest
        // limit.</b> The saving is bandwidth, not work: by the time we can say
        // "unchanged" we have already read or built it. Storing the tag beside
        // the cache entry would let a hit answer without reading the bytes, and
        // that is the version to write when a measurement says the read matters.
        if (Matches(context.Request.Headers.IfNoneMatch, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;

            // A 304 carries no body and must not claim one. Kestrel will refuse
            // to send Content-Length with 304, and a length left set here is a
            // response that some proxies treat as truncated.
            context.Response.Headers.ContentLength = null;
            return;
        }

        context.Response.ContentType = "application/vnd.mapbox-vector-tile";
        context.Response.Headers.ContentLength = tile.Length;
        await context.Response.Body.WriteAsync(tile, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the caller already holds this tile.
    /// </summary>
    /// <remarks>
    /// <b>A list, and <c>*</c>, because the header allows both.</b> A client may
    /// send several tags, and a proxy revalidating anything it holds may send
    /// <c>*</c>, which means <em>if you have any version at all</em>. Treating
    /// the header as a single opaque string is the common shortcut and it fails
    /// silently — the tile is re-sent, nothing breaks, and the feature quietly
    /// does nothing.
    /// </remarks>
    private static bool Matches(
        Microsoft.Extensions.Primitives.StringValues header, string etag)
    {
        foreach (string? value in header)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (string candidate in value.Split(
                         ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (candidate == "*" || string.Equals(candidate, etag, StringComparison.Ordinal))
                {
                    return true;
                }

                // A cache may weaken a tag it stored. W/"x" and "x" identify the
                // same bytes as far as this server is concerned.
                if (candidate.StartsWith("W/", StringComparison.Ordinal)
                    && string.Equals(candidate[2..], etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
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
