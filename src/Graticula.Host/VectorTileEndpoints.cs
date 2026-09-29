using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
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
using Microsoft.Extensions.Logging;

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

        // Tiles came only from hosted data (Q-67, reversed for registered PostGIS by
        // ADR-095), so the natural home was the hosted folder — but the root path is mapped too, and answers with the
        // redirect in TileableAsync rather than a 404. A client that built a URL
        // before the folder existed gets told where the service moved.
        // Root and any folder. `{folder}` is a parameter and a literal segment wins in
        // routing, so the hosted-prefixed registrations elsewhere still take precedence —
        // and a registered service in a named folder now has a route at all (2026-08-17).
        foreach (string prefix in (string[])["/rest/services", "/rest/services/{folder}"])
        {
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer", ServiceAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/resources/styles", StyleAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/resources/styles/root.json", StyleAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // ADR-094: a service's other named styles, beside the default. The literal route above wins for
            // root.json; this one answers every other name.
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/resources/styles/{{style}}.json", NamedStyleAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // <b>The resources a style needs to draw a label.</b> Without the
            // fonts a client with a text-field renders no text at all and logs a
            // fetch error, which reads as a broken server rather than a missing
            // feature. The sprite sheet is the one the publisher uploaded
            // (ADR-092), or an empty one so that a client probing it gets an
            // answer instead of a 404.
            app.MapGet(
                $"{prefix}/{{serviceName}}/VectorTileServer/resources/fonts/{{fontstack}}/{{range}}.pbf",
                FontAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/resources/sprites/{{sprite}}",
                SpriteAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // <b>The list of those resources, which ArcGIS documents and this server did not serve until
            // 2026-09-29</b> (D-285). No client measured here asks for it; it is served because it is part of the
            // documented service and a client that does ask should not meet a 404.
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/resources/info", ResourceInfoAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // {z}/{y}/{x} — row before column. This is the ArcGIS URL order and
            // it is the reverse of almost every other tile scheme. Written once,
            // here, where the swap into TileAddress is visible on one line.
            app.MapGet($"{prefix}/{{serviceName}}/VectorTileServer/tile/{{z:int}}/{{y:int}}/{{x:int}}.pbf",
                TileAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // V-63, by owner decision: which tiles of a block can hold anything, so a client does not ask for the
            // ones that cannot. The ArcGIS order again — level, then row, then column.
            app.MapGet(
                $"{prefix}/{{serviceName}}/VectorTileServer/tilemap/{{z:int}}/{{top:int}}/{{left:int}}/{{width:int}}/{{height:int}}",
                TilemapAsync)
                .Governed(SharingGovernedExtensions.ByService);
        }
    }

    /// <summary>
    /// Resolves a layer for tile serving, or answers the caller and returns null.
    /// </summary>
    /// <remarks>
    /// <b>The tile rule is enforced here, through <see cref="Tileable"/>.</b> Q-67 put vector
    /// tiles on hosted data only; ADR-066 §9 added GeoParquet and ADR-095 (2026-09-29) added
    /// registered PostGIS databases, so what is still refused is a source of a kind nothing here
    /// can encode. The refusal is separate from the not-found and not-shared answers because it
    /// is a different fact about a layer that genuinely exists and that the caller may genuinely
    /// read: it has a FeatureServer and no VectorTileServer.
    /// </remarks>
    internal static async Task<PublishedService?> TileableAsync(
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
                        details = Array.Empty<string>(),
                    },
                },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }

        // <b>Every layer, not the first one.</b> A tile carries all of a
        // service's layers, so one layer nothing here can encode disqualifies
        // the service rather than being quietly skipped — a tile missing one of
        // three layers looks like missing data, and nobody would know to ask.
        //
        // <b>GeoParquet counts as tileable too, since 2026-09-13 — owner decision, reversing
        // ADR-066 §9 for this one face.</b> A GeoParquet layer is never hosted (`IsHosted` is
        // about the datastore, and a file is not the datastore), so it used to fail this test
        // for the same reason a registered Oracle table does.
        //
        // <b>And a registered PostGIS layer, since 2026-09-29 — ADR-095, owner decision,
        // reversing Q-67 for PostGIS.</b> Its rows are read by the statement a hosted layer's
        // are, in its own database. `Tileable` is what tells a layer that tiles from one that
        // cannot now that `IsHosted` answers neither.
        PublishedLayer layer = service.Layers[0];

        foreach (PublishedLayer each in service.Layers)
        {
            if (!Tileable(each))
            {
                layer = each;
                break;
            }
        }

        string layerName = service.QualifiedName;

        if (!Tileable(layer))
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message =
                            $"Layer '{layer.Definition.Name}' of '{layerName}' is served from a "
                            + $"'{KindOf(layer)}' source, and this server cannot encode that kind "
                            + "of source as vector tiles. Tiles are served from hosted data, from "
                            + "registered PostGIS databases and from GeoParquet and DuckDB sources "
                            + "(ADR-095, ADR-066 §9); other database engines serve features only "
                            + "(Q-67). Its FeatureServer is at "
                            + $"/rest/services/{layerName}/FeatureServer and is unaffected.",
                        details = Array.Empty<string>(),
                    },
                },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }

        // <b>ADR-096: a stored grid this build cannot read is refused, never replaced by Web Mercator.</b>
        // A client holding that grid's tileInfo would draw a Mercator tile in the wrong place, and a map
        // drawn wrong is worse than one that says why it is not drawn. Nothing but a later build writing a
        // shape this one does not know — or a hand edit — produces one.
        if (service.TileSchemeUnreadable is { } unreadable)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 500,
                        message =
                            $"The tiling scheme stored for '{service.QualifiedName}' cannot be read: {unreadable} "
                            + "Set it again with PUT /admin/services/{name}/tiling, or clear it to serve Web "
                            + "Mercator (ADR-096).",
                        details = Array.Empty<string>(),
                    },
                },
                statusCode: StatusCodes.Status500InternalServerError)
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

    /// <summary>Whether a layer's rows can reach a vector tile at all.</summary>
    /// <remarks>
    /// <para>
    /// <b>Hosted, a registered PostGIS database, or a DuckDB-read source — ADR-095, owner decision
    /// 2026-09-29, reversing Q-67 for PostGIS.</b> A hosted layer and a registered PostGIS layer are
    /// both read by <c>PostGisTileSource</c>, the one statement, run in the layer's own database
    /// through <see cref="LayerConnections.TileSourceFor"/>'s pool for that source — so quiesce, the
    /// breaker and the budget are the source's, not the datastore's. A GeoParquet layer is read by
    /// <c>GeoParquetTileSource</c> and encoded by the same statement (ADR-066 §9).
    /// </para>
    /// <para>
    /// <b>The rule is <see cref="Graticula.Platform.Admin.TileSources.Tiled"/>, and this only asks
    /// it</b>, so the catalogue's <c>AdminLayer.Tileable</c> cannot answer differently. What is still
    /// refused is a kind nothing here encodes — none can be registered today, and the SQL Server,
    /// Oracle and MySQL providers v1-scope §3a defers would be — because <c>ST_AsMVT</c> is a PostGIS
    /// function and Q-67's reason still holds for those engines.
    /// </para>
    /// </remarks>
    internal static bool Tileable(PublishedLayer layer) =>
        Graticula.Platform.Admin.TileSources.Tiled(layer.Definition.IsHosted, KindOf(layer));

    /// <summary>The kind of a layer's source, read off its locator — <see cref="Graticula.Platform.Admin.DataSourceKinds"/>.</summary>
    private static string KindOf(PublishedLayer layer) =>
        Graticula.Platform.Admin.GeoParquetLocator.KindOf(layer.ConnectionString);

    /// <summary>
    /// How long a layer's tiles are kept here and downstream: its own lifetime when an administrator set one,
    /// and otherwise the default for its kind of source — ADR-095 §5.3.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="serverDefault">The server's own default — <c>Graticula:TileCacheMinutes</c>.</param>
    /// <returns>The lifetime; zero means never cached.</returns>
    /// <remarks>
    /// <b>One place for the three readers that each wrote <c>layer.CacheLifetime ?? defaultLifetime</c>:</b>
    /// the tile route, the seed's cache report and the query face's default (ADR-069 says a layer's query
    /// answer and its tiles carry the same number). A registered PostGIS layer nobody declared defaults to
    /// <see cref="Graticula.Platform.Admin.TileSources.RegisteredLifetime"/>, because the edits other tools
    /// make to it never reach this server and only the lifetime bounds them (ADR-010 §5.2).
    /// </remarks>
    internal static TimeSpan LifetimeOf(PublishedLayer layer, TimeSpan serverDefault) =>
        layer.CacheLifetime
        ?? Graticula.Platform.Admin.TileSources.DefaultLifetimeOf(
            layer.Definition.IsHosted, KindOf(layer), serverDefault);

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
    /// <b>74.6 ms against 21.6 ms</b> on the same tile — 3.5×, and correct.
    /// </para>
    /// <para>
    /// <b>And it is what the tile path does, since 2026-08-15 (0eaf635).</b> This
    /// paragraph said reprojection was not implemented, which was true for one day:
    /// a layer keeps its own reference and <c>PostGisTileSource</c> transforms the
    /// tile envelope into it once and the geometry out of it per row. The worry
    /// that held it back — a national grid to Web Mercator needs shift grids PROJ
    /// may not have — was answered by Q-141 rather than by refusing: the transform
    /// runs, and a pair that crosses a datum is reported once to the operator, in
    /// the log and under <c>datumShifts</c> on <c>/admin/health</c>, because a tile
    /// has nowhere to carry a caution. ~~What stays true is this constant: the tiles
    /// themselves are Web Mercator only.~~ <b>Since 2026-09-29 (ADR-096) this is the
    /// default grid rather than the only one</b>: a service may be cut on another
    /// (<see cref="PublishedService.TileScheme"/>), and a service nobody set is cut on
    /// this one exactly as before.
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
        HostSettings settings,
        CancellationToken cancellation)
    {
        PublishedService? service = await TileableAsync(context, serviceName, catalog, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        object document = await ServiceDocumentAsync(service, contexts, projector, cancellation).ConfigureAwait(false);

        // <b>ADR-098: `exportTilesAllowed` is true only where this caller could export</b> — the service offers it and,
        // for a caller who is not signed in, offers it to anonymous callers too. Field Maps and Pro read this flag to
        // decide whether to offer an offline area; offering one the next request refuses is the failure the old
        // comment on the flag warned about. Everywhere else the document is byte for byte what it was.
        RequestPrincipal? caller = context.Features.Get<RequestPrincipal>();

        if (VectorTileExportEndpoints.MayExport(service, caller?.Principal.IsAnonymous ?? true))
        {
            document = VectorTileExportEndpoints.Advertised(
                document, service.Limits.Export.MaximumOf(settings.TileExportMaximumTiles));
        }

        await Results.Ok(document).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The service document as the route serves it to a caller who may not export — split out on 2026-09-29 so an
    /// exported package carries the same document (ADR-098 §5.2), unchanged.
    /// </summary>
    internal static async Task<object> ServiceDocumentAsync(
        PublishedService service, ServiceContexts contexts, IProjector projector, CancellationToken cancellation)
    {
        // <b>ADR-096: a service cut on another grid states that grid, and its extent in it.</b> Each
        // layer's extent is moved into the scheme's reference on its own — the union of boxes in two
        // references would be meaningless — sampled along its edges (`ServedExtent`), because a
        // transverse Mercator zone bends a rectangle.
        if (!service.TileScheme.IsWebMercator)
        {
            VectorTileScheme scheme = service.TileScheme;
            Envelope? inScheme = null;

            foreach (PublishedLayer layer in service.Layers)
            {
                (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation)
                    .ConfigureAwait(false);

                inScheme = Widen(
                    inScheme,
                    await InSchemeAsync(described.Extent, layer.Definition.Srid, scheme, projector, cancellation)
                        .ConfigureAwait(false));
            }

            return VectorTileServerMetadataWriter.Service(
                service.Name,
                [.. service.Layers.Select(l => l.Definition.Name)],
                inScheme,
                scheme,
                ServiceRange(service.Layers));
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

        return VectorTileServerMetadataWriter.Service(
            service.Name,
            [.. service.Layers.Select(l => l.Definition.Name)],
            extent,
            TileAddress.MaxZoom,
            WebMercator,
            ServiceRange(service.Layers));
    }

    /// <summary>
    /// The most tiles one tilemap answers about — ImageServer's number (<c>ImageServerEndpoints</c>), a 64 by 64 block,
    /// so the two faces refuse the same request the same way. The ArcGIS JS API asks for 32 by 32.
    /// </summary>
    private const int LargestTilemap = 4096;

    /// <summary>
    /// Which tiles of a block can hold anything — V-63, owner decision 2026-09-25, and the ArcGIS tile map's shape:
    /// <c>data</c> row by row from the block's top left, <c>1</c> where a tile may carry a feature and <c>0</c> where
    /// it cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A tile is 1 where a layer that draws at that level has an extent that touches it</b> — the layer's visible
    /// range (ADR-070) read by the same <see cref="VisibleScaleRange.CarriesVectorTile"/> the tile itself is built
    /// with. That is a promise in one direction only: a 0 is a tile that is empty, and a 1 is one that may be. It
    /// is what spares a client the open sea and the levels a service does not draw at, which is where nearly all of
    /// the empty requests are, without a query per tile against the data.
    /// </para>
    /// <para>
    /// <b>A layer whose extent cannot be put in Web Mercator makes every tile 1</b>: not knowing where the data is
    /// is not knowing that a tile is empty.
    /// </para>
    /// <para>
    /// <b>Touching counts, where ImageServer's does not.</b> A raster tile that only shares an edge with a coverage
    /// has none of its pixels; a vector tile is encoded with a buffer, so a feature on the edge is drawn by the tile
    /// beside it too, and a 0 there would hide it.
    /// </para>
    /// </remarks>
    private static async Task TilemapAsync(
        HttpContext context,
        string serviceName,
        int z,
        int top,
        int left,
        int width,
        int height,
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

        // ADR-096: the grid is the service's scheme — for Web Mercator, 2^z a side and z22 the last level.
        VectorTileScheme scheme = service.TileScheme;
        long side = z < 0 || z > scheme.MaxLevel ? 1 : scheme.TilesAcross(z);

        string? refusal = z < 0 || z > scheme.MaxLevel
            ? $"The level is 0 to {scheme.MaxLevel}."
            : width < 1 || height < 1 || (long)width * height > LargestTilemap
                ? $"A tile map answers about 1 to {LargestTilemap} tiles at once — 64 by 64 — and this asks for {(long)width * height}."
                : top < 0 || left < 0 || top >= side || left >= side
                    ? $"Level {z} has rows and columns 0 to {side - 1}."
                    : null;

        if (refusal is not null)
        {
            await Results.Json(
                new { error = new { code = 400, message = refusal, details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // The extents of the layers that draw at this level; null for one whose extent could not be projected.
        List<Envelope?> drawn = [];

        foreach (PublishedLayer layer in service.Layers.Where(l => scheme.Draws(l.VisibleRange, z)))
        {
            (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            if (described.Extent is not { } extent)
            {
                continue; // no rows: nothing to draw anywhere
            }

            drawn.Add(await InSchemeAsync(extent, layer.Definition.Srid, scheme, projector, cancellation).ConfigureAwait(false));
        }

        int[] data = new int[width * height];

        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                bool inside = top + row < side && left + column < side;

                // The tile's box from the scheme — for Web Mercator the square this computed inline before
                // ADR-096, to the same digits (`TileAddress.WebMercatorEnvelope`).
                Envelope tile = inside
                    ? scheme.Envelope(new TileAddress(z, left + column, top + row))
                    : default;

                bool may = inside && drawn.Any(e =>
                    e is not { } box
                    || (box.MinX <= tile.MaxX && box.MaxX >= tile.MinX
                        && box.MinY <= tile.MaxY && box.MaxY >= tile.MinY));

                data[(row * width) + column] = may ? 1 : 0;
            }
        }

        await Results.Json(new
        {
            adjusted = false,
            location = new { top, left, width, height },
            data,
            valid = true,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The range a whole tile service draws in — ADR-070.</summary>
    /// <remarks>
    /// <b>Only as narrow as its widest layer.</b> A service is hidden only where every layer in it is:
    /// its zoomed-out limit is the largest of its layers', and none when any layer has none; the
    /// zoomed-in limit the other way round. One layer's range on a service of three would hide the
    /// other two.
    /// </remarks>
    internal static VisibleScaleRange ServiceRange(IReadOnlyList<PublishedLayer> layers) =>
        layers.Count == 0
            ? default
            : new VisibleScaleRange(
                layers.Any(l => l.VisibleRange.MinScale <= 0) ? 0 : layers.Max(l => l.VisibleRange.MinScale),
                layers.Any(l => l.VisibleRange.MaxScale <= 0) ? 0 : layers.Min(l => l.VisibleRange.MaxScale));

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
    internal static async Task<Envelope?> InWebMercatorAsync(
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

    /// <summary>
    /// An extent in the reference of a service's tiling scheme — <see cref="InWebMercatorAsync"/> for Web
    /// Mercator, unchanged, and <see cref="ServedExtent"/> for any other (ADR-096).
    /// </summary>
    /// <param name="extent">The box, in <paramref name="srid"/>.</param>
    /// <param name="srid">The layer's reference.</param>
    /// <param name="scheme">The scheme.</param>
    /// <param name="projector">The projector.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The box in the scheme's reference, or null when it could not be put there.</returns>
    /// <remarks>
    /// <b>Sampled, not four corners, for another scheme.</b> <see cref="InWebMercatorAsync"/>'s corners are
    /// exact for 4326 into Mercator and it keeps them; a national grid bends the edges of a box, and
    /// <see cref="ServedExtent"/> is the helper every other face uses to follow them. A failure is null —
    /// not knowing where the data is — for <see cref="InWebMercatorAsync"/>'s reason.
    /// </remarks>
    internal static async Task<Envelope?> InSchemeAsync(
        Envelope? extent, int srid, VectorTileScheme scheme, IProjector projector, CancellationToken cancellation)
    {
        if (scheme.IsWebMercator)
        {
            return await InWebMercatorAsync(extent, srid, projector, cancellation).ConfigureAwait(false);
        }

        try
        {
            return await ServedExtent.InAsync(extent, srid, scheme.Srid, projector, cancellation).ConfigureAwait(false);
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
                        details = Array.Empty<string>(),
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
    /// The sprite sheet: the one the service's publisher uploaded, or an empty one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Uploaded since ADR-092; empty before it, and still empty for a service nobody gave
    /// icons.</b> The empty answer stays because every ArcGIS and Mapbox client probes this
    /// resource, and a 404 is the difference between <em>this service has no icons</em> and
    /// <em>this service is broken</em>.
    /// </para>
    /// <para>
    /// <b><c>@2x</c> falls back to the 1x sheet.</b> A client on a high-density screen asks only for
    /// <c>@2x</c>; a publisher who uploaded one sheet still sees their icons there, drawn from the
    /// 1x picture at the ratio its index states.
    /// </para>
    /// <para>
    /// <b>Revalidated, not immutable, since the sheet stopped being a constant.</b> The empty sheet
    /// was sent <c>immutable</c> for a year because nothing could change it; an uploaded sheet
    /// changes when its publisher uploads the next one, so every answer — the empty one included,
    /// or a browser would keep it past the first upload — carries an ETag computed from the bytes
    /// and <c>no-cache</c>, which makes an unchanged sheet a 304. Public only for an anonymous
    /// caller of a public service: the rule the tiles follow, for the reason they follow it.
    /// </para>
    /// </remarks>
    private static async Task SpriteAsync(
        HttpContext context,
        string serviceName,
        string sprite,
        CatalogFallback catalog,
        CancellationToken cancellation)
    {
        // Matched exactly rather than by extension, so the name in the URL never
        // becomes a lookup of any kind.
        (int ratio, bool image) = sprite switch
        {
            "sprite.json" => (1, false),
            "sprite.png" => (1, true),
            "sprite@2x.json" => (2, false),
            "sprite@2x.png" => (2, true),
            _ => (0, false),
        };

        PublishedService? service = await TileableAsync(context, serviceName, catalog, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        if (ratio == 0)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 404,
                        message =
                            "A sprite sheet is sprite.json, sprite.png, sprite@2x.json or "
                            + "sprite@2x.png. A service with no uploaded sheet answers an empty one.",
                        details = Array.Empty<string>(),
                    },
                },
                statusCode: StatusCodes.Status404NotFound)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // <b>Straight to the catalogue, past the fallback, as a related record is read</b> — there is no
        // remembered copy of a sheet to serve from, so while the store is unreachable this read fails and the
        // caller is told so, rather than being handed an empty sheet as though it were this service's.
        Graticula.Platform.Admin.StoredSprite? stored = catalog.Catalog is { } layers
            ? await layers.FindSpriteAsync(service.Id, ratio, image, cancellation).ConfigureAwait(false)
            : null;

        byte[] bytes = image
            ? stored?.Image ?? EmptySheet
            : System.Text.Encoding.UTF8.GetBytes(stored?.Index ?? "{}");

        context.Response.Headers.CacheControl = QueryResponseCaching.RevalidateFor(context, service.Layers);

        // Weak, as the query face's is: the index is JSON, which the compression middleware may send
        // brotli, gzip or identity under the one tag (ADR-068).
        string etag = "W/\"" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes).AsSpan(0, 16)) + "\"";

        context.Response.Headers.ETag = etag;

        if (Matches(context.Request.Headers.IfNoneMatch, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.Headers.ContentLength = null;
            return;
        }

        await Results.Bytes(bytes, image ? "image/png" : "application/json; charset=utf-8")
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The service's resource files — ArcGIS's <i>Vector Tile Resource Info</i>, <c>resources/info</c> — D-285.
    /// </summary>
    private static async Task ResourceInfoAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        GlyphStore glyphs,
        StyleOriginList origins,
        CancellationToken cancellation)
    {
        PublishedService? service = await TileableAsync(context, serviceName, catalog, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        // The style the style route serves, so the fonts listed are the ones that style fetches.
        string style = await TileExportPackage.StyleOfAsync(service, glyphs, origins, catalog.Catalog, cancellation)
            .ConfigureAwait(false);

        await Results.Ok(ResourceInfo(style, glyphs)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>What <c>resources/info</c> answers for a service serving this style — the route's and a package's.</summary>
    /// <param name="style">The style as served.</param>
    /// <param name="glyphs">The server's glyphs.</param>
    /// <returns><c>{"resourceInfo": [...]}</c>.</returns>
    /// <remarks>
    /// <b>The documented resource is "relative paths to a list of resource files"; the rest is Esri's own
    /// answer, read 2026-09-29</b> from <c>World_Basemap_v2</c>'s: each glyph range as
    /// <c>../fonts/{stack}/{range}.pbf</c>, then the four sprite files as <c>../sprites/…</c>, relative to the
    /// resource itself and with no style in the list. Every range the style can fetch is listed, which is what
    /// a package packs (<see cref="TileExportPackage.FontFiles"/>); the four sprite files always, because the
    /// sprite routes always answer, with an empty sheet when nobody uploaded one.
    /// </remarks>
    internal static object ResourceInfo(string style, GlyphStore glyphs) => new
    {
        resourceInfo = TileExportPackage.FontFiles(style, glyphs)
            .Select(f => $"../fonts/{f.Stack}/{f.Range}.pbf")
            .Concat(SpriteFiles.Select(file => "../sprites/" + file))
            .ToArray(),
    };

    /// <summary>The four files a sprite route answers, in the order Esri's resource list names them.</summary>
    internal static readonly string[] SpriteFiles = ["sprite.json", "sprite.png", "sprite@2x.json", "sprite@2x.png"];

    /// <summary>A one-pixel transparent PNG: an atlas with nothing in it.</summary>
    internal static readonly byte[] EmptySheet = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNgYGBgAAAABQABeqhXUAAAAABJRU5ErkJggg==");

    private static async Task StyleAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        GlyphStore glyphs,
        StyleOriginList origins,
        ILoggerFactory logs,
        CancellationToken cancellation)
    {
        PublishedService? service = await TileableAsync(context, serviceName, catalog, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        await ServeStyleAsync(context, service, service.Style, catalog, glyphs, origins, logs, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One of the service's named styles, at <c>resources/styles/{name}.json</c> — ADR-094.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ArcGIS has no address for a second style on a service, so this one is ours.</b> An ArcGIS
    /// VectorTileServer serves one style, at <c>resources/styles/root.json</c>; other styles of the same
    /// tiles are separate portal items, each carrying its own <c>root.json</c>. There is no portal item
    /// model here (ADR-019), so the others sit beside <c>root.json</c> under their names, where a
    /// MapLibre client can be pointed at them and an ArcGIS client, which never asks, is not disturbed.
    /// </para>
    /// <para>
    /// <b>The same checks as the default, and the same way out.</b> A named style that no longer fits
    /// its layers, its sprite sheet or the allowed origins is not served; the generated style is, with
    /// the header saying so — a client asked for a map, and a map it can draw is the better answer
    /// than an error it cannot act on. A name the service does not carry is a 404: that is an address
    /// that does not exist, not a style that went stale.
    /// </para>
    /// <para>
    /// <b>The literal <c>root.json</c> route wins in routing</b>, so this never sees it; a default asked
    /// for by its own name is served as the default is.
    /// </para>
    /// </remarks>
    private static async Task NamedStyleAsync(
        HttpContext context,
        string serviceName,
        string style,
        CatalogFallback catalog,
        GlyphStore glyphs,
        StyleOriginList origins,
        ILoggerFactory logs,
        CancellationToken cancellation)
    {
        PublishedService? service = await TileableAsync(context, serviceName, catalog, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        // Matched against the name rule before it reaches the store, so the path segment is never a lookup of
        // anything it is not allowed to be.
        (string Style, bool IsDefault)? found =
            StyleNames.TryValidate(style, out _) && catalog.Catalog is { } store
                ? await store.FindNamedStyleAsync(service.Id, style, cancellation).ConfigureAwait(false)
                : null;

        if (found is not { } named)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 404,
                        message = $"This service has no style '{style}'. Its default style is resources/styles/root.json.",
                        details = Array.Empty<string>(),
                    },
                },
                statusCode: StatusCodes.Status404NotFound)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await ServeStyleAsync(context, service, named.Style, catalog, glyphs, origins, logs, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>A stored style when it still fits the service, else the generated one.</summary>
    private static async Task ServeStyleAsync(
        HttpContext context,
        PublishedService service,
        string? stored,
        CatalogFallback catalog,
        GlyphStore glyphs,
        StyleOriginList origins,
        ILoggerFactory logs,
        CancellationToken cancellation)
    {
        // <b>A stored style wins, unchanged — while it still fits the service.</b> Nothing here rewrites it: a
        // cartographer should get back the file they sent, not a normalised version of it (ADR-028).
        //
        // <b>Checked again here, and not only when it was written — ADR-028 condition 3.</b> A layer unpublished,
        // taken out of the service or renamed leaves a style drawing a source that no longer exists, and nothing
        // noticed: the write-time check had been passed once, by a service that has since changed. Every door that
        // changes a service's layers arrives here, so this is the one place the check cannot be missed. A style
        // that no longer fits gives way to the generated one, which always does, and says so in the server log.
        if (stored is { Length: > 0 })
        {
            (bool fits, string? stale) = await StoredStyleFitsNowAsync(service, stored, catalog.Catalog, origins, cancellation)
                .ConfigureAwait(false);

            if (fits)
            {
                await Results.Content(stored, "application/json; charset=utf-8")
                    .ExecuteAsync(context).ConfigureAwait(false);

                return;
            }

            Log.StyleStale(logs.CreateLogger("Graticula.Tiles"), service.Name, stale!);
            context.Response.Headers["Graticula-Style-Stale"] = "true";
        }

        await Results.Ok(GeneratedStyle(service, glyphs)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a stored style may be served for this service now — its layers, its icons and its origins — and why
    /// not when it may not. Split out of the style route unchanged on 2026-09-29 so an exported package carries the
    /// style the route would serve (ADR-098 §5.2).
    /// </summary>
    internal static async Task<(bool Fits, string? Stale)> StoredStyleFitsNowAsync(
        PublishedService service,
        string stored,
        PostgresLayerCatalog? store,
        StyleOriginList origins,
        CancellationToken cancellation)
    {
        // <b>The sprite sheet's icon names, read only when the style could need them — ADR-092.</b> A style that
        // draws no icon is checked against its layers alone, as before, and pays nothing for the sheet.
        //
        // <b>And skipped when the store cannot be reached.</b> The service itself may be the remembered one
        // (ADR-026, Q-95), and a sheet is not remembered; refusing the style over that would take the map down to
        // protect its icons. The icons would be missing anyway — the sprite routes cannot read the sheet either.
        IReadOnlyList<string>? icons = null;
        bool checkable = true;

        if (stored.Contains("icon-image", StringComparison.Ordinal) && store is { } sprites)
        {
            try
            {
                icons = await sprites.FindSpriteAsync(service.Id, 1, withImage: false, cancellation)
                        .ConfigureAwait(false) is { } sheet
                    ? SpriteSheet.IconNames(sheet.Index)
                    : null;
            }
            catch (Exception e) when (CatalogFallback.IsUnreachable(e))
            {
                checkable = false;
            }
        }

        // <b>The allowed origins, read only when the style could name one — ADR-094.</b> An origin taken off
        // the list stops a style that names it being served, here, where every viewer's browser would
        // otherwise be sent to it. Unlike the icons this is not skipped when the store is unreachable: the
        // list fails closed (StyleOriginList), because an origin nobody can confirm is still allowed is one
        // the browser should not be sent to.
        IReadOnlyList<StyleOrigin> allowed = StyleDocument.MayNameAnotherHost(stored)
            ? await origins.CurrentAsync(cancellation).ConfigureAwait(false)
            : [];

        bool fits = StoredStyleFits(
            stored,
            [.. service.Layers.Select(l => l.Definition.Name)],
            icons,
            allowed,
            out string? stale,
            iconsCheckable: checkable);

        return (fits, stale);
    }

    /// <summary>The generated style — the one served when no stored style fits — split out for ADR-098 §5.2.</summary>
    internal static object GeneratedStyle(PublishedService service, GlyphStore glyphs) =>

        // One style layer per source layer, drawn in index order — polygons
        // before lines before points would be nicer, and is a cartographic
        // decision this default has no business making. Index order is what the
        // publisher chose.
        VectorTileServerMetadataWriter.Style(
            // ADR-033 §5a: each layer's canonical document travels with it, so the
            // tile face draws what the feature face derives from rather than
            // generating a second opinion about the same layer.
            [.. service.Layers.Select(l => (l.Definition.Name, l.GeometryType, l.Symbology))],
            glyphs.Any ? GlyphStore.Fallback : null,

            // ADR-070: each layer's range narrows its style layers' zooms. The first layer of a
            // name wins, which is the one the tile carries under that name.
            service.Layers
                .GroupBy(l => l.Definition.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().VisibleRange, StringComparer.Ordinal),

            // ADR-096: a style's zooms count the service's own levels; null keeps Web Mercator's table.
            service.TileScheme.IsWebMercator ? null : service.TileScheme.Level0Scale,

            // <b>The service's name, as the style's own (D-285).</b> Only the generated style: a stored one is
            // served byte for byte (ADR-028), and its author's `name`, or its absence, is theirs.
            service.Name);

    /// <summary>
    /// Whether a stored style still draws only layers the service has — ADR-028 condition 3: checked when it is
    /// served, because a layer can leave a service by more doors than the one that wrote the style.
    /// </summary>
    /// <param name="stored">The style as stored.</param>
    /// <param name="layers">The service's layers now, by name.</param>
    /// <param name="icons">The service's 1x sprite sheet's icon names, or null when it has no sheet (ADR-092).</param>
    /// <param name="stale">Why it no longer fits, or null.</param>
    /// <returns>Whether it may be served.</returns>
    internal static bool StoredStyleFits(
        string stored, IReadOnlyList<string> layers, IReadOnlyList<string>? icons, out string? stale) =>
        StyleDocument.TryValidate(stored, layers, icons, out stale);

    /// <summary>
    /// Whether a stored style still fits the service and names only allowed origins — ADR-028 condition 3,
    /// ADR-092, ADR-094.
    /// </summary>
    /// <param name="stored">The style as stored.</param>
    /// <param name="layers">The service's layers now, by name.</param>
    /// <param name="icons">The 1x sheet's icon names, or null when there is no sheet.</param>
    /// <param name="allowed">The origins an administrator allows now.</param>
    /// <param name="stale">Why it may not be served, or null.</param>
    /// <param name="iconsCheckable">
    /// False when the sheet could not be read; the style is then judged on everything but its icons.
    /// </param>
    /// <returns>Whether it may be served.</returns>
    /// <remarks>
    /// <b>An unreadable sheet excuses the icons and nothing else.</b> Until ADR-094 an unreadable sheet
    /// skipped the whole check, which was harmless while the check was about layers the service read
    /// from the same remembered copy. With origins in it, skipping would serve a style naming an origin
    /// removed from the list, so the icons are judged by the sheet's own absence instead: a style is
    /// checked as if every literal icon it names were there.
    /// </remarks>
    internal static bool StoredStyleFits(
        string stored,
        IReadOnlyList<string> layers,
        IReadOnlyList<string>? icons,
        IReadOnlyCollection<StyleOrigin> allowed,
        out string? stale,
        bool iconsCheckable = true) =>
        StyleDocument.TryValidate(
            stored,
            layers,
            iconsCheckable ? icons : StyleDocument.LiteralIcons(stored),
            allowed,
            out stale);

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
        IProjector projector,
        DatumShiftNotices datumShifts,
        UnindexedLayerNotices unindexed,
        ILoggerFactory loggerFactory,
        GeoParquetSources geoParquet,
        StaleTileNotices stale,
        CancellationToken cancellation)
    {
        PublishedService? service = await TileableAsync(context, serviceName, catalog, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        TileAddress address = new(z, x, y);

        // <b>In the service's own grid — ADR-096.</b> For Web Mercator this is `TileAddress.Rejection`, as
        // it always was; for another scheme, its own levels and its own tile counts.
        VectorTileScheme scheme = service.TileScheme;

        if (scheme.Rejection(address) is { } rejection)
        {
            await Results.Json(
                new { error = new { code = 400, message = rejection, details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await ServeTileAsync(
                context, service, address, contexts, connections, cache, building, projector, datumShifts,
                unindexed, loggerFactory, geoParquet, stale, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One tile of a service the caller may read, at an address already checked against its grid: every
    /// layer's part from the cache or built, joined, and sent with its cache headers — or 204.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="service">The service, already found tileable and readable by <see cref="TileableAsync"/> or its equal.</param>
    /// <param name="address">The tile, already in <see cref="PublishedService.TileScheme"/>'s grid.</param>
    /// <param name="contexts">Where layer descriptions are remembered.</param>
    /// <param name="connections">Where tile sources come from, and the build admission (D-277).</param>
    /// <param name="cache">The tile cache.</param>
    /// <param name="building">The builds in flight.</param>
    /// <param name="projector">For the datum notice.</param>
    /// <param name="datumShifts">Where a datum crossing is said once.</param>
    /// <param name="unindexed">Where a layer with no spatial index is said once.</param>
    /// <param name="loggerFactory">For the notices.</param>
    /// <param name="geoParquet">For a GeoParquet layer's file version.</param>
    /// <param name="stale">Where a stale answer is counted and said — ADR-010 §5.1a.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>When the response is written.</returns>
    /// <remarks>
    /// <para>
    /// <b>Stale-while-error lives here and nowhere below — ADR-010 §5.1a, owner decision 2026-09-29, D-278.</b>
    /// When a layer's build is refused because its source cannot build it — the breaker is open or the database
    /// cannot be reached, its <c>ConnectionBudget</c> is full, or an operator has quiesced it — the cache's copy
    /// of that exact tile stands in if it is no more than the layer's stale limit past its lifetime
    /// (<see cref="PartOrStandInAsync"/>). A seed and an export call <see cref="LayerPartAsync"/> directly and
    /// never reach this, so neither ever takes a stale copy for a built tile: a seed pauses on the refusal as it
    /// did, and an export packages only tiles built or fresh.
    /// </para>
    /// <para>
    /// <b>Split out of the tile route unchanged on 2026-09-29 so the standard tile faces serve through it —
    /// ADR-097.</b> OGC API Tiles and WMTS each find the service and the address in their own vocabulary,
    /// and from here on a tile is a tile: the same keys, the same single-flight, the same admission, the
    /// same bytes, the same <c>Cache-Control</c>, <c>Age</c>, weak ETag and 304, and the same
    /// <c>X-Tile-Cache</c>. A second copy of this loop would be the seed's old risk (ADR-093 §5.5) on three
    /// faces — a cache filled under keys one of them never reads.
    /// </para>
    /// </remarks>
    internal static async Task ServeTileAsync(
        HttpContext context,
        PublishedService service,
        TileAddress address,
        ServiceContexts contexts,
        LayerConnections connections,
        ITileCache cache,
        TileSingleFlight building,
        IProjector projector,
        DatumShiftNotices datumShifts,
        UnindexedLayerNotices unindexed,
        ILoggerFactory loggerFactory,
        GeoParquetSources geoParquet,
        StaleTileNotices stale,
        CancellationToken cancellation)
    {
        VectorTileScheme scheme = service.TileScheme;

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
        List<(PublishedLayer Layer, LayerPart Part)> parts = [];

        TimeSpan defaultLifetime = cache is FileSystemTileCache disk
            ? disk.DefaultLifetime
            : TimeSpan.FromHours(1);

        // The server's stale limit, for a layer that names none; a cache that cannot say has none to offer.
        TimeSpan defaultStaleLimit = cache is FileSystemTileCache held ? held.DefaultStaleLimit : TimeSpan.Zero;

        // <b>The service's quota travels with every write — ADR-010 §3</b>, so the service's own tiles make room for
        // this one when it is over; null for a service with none, which is the ordinary case and costs nothing.
        TileCacheQuota? quota = QuotaOf(service);

        // What refused a build that a stale copy then stood in for — the log line's reason.
        Exception? refusedFor = null;

        foreach (PublishedLayer layer in service.Layers)
        {
            /*
              <b>ADR-070: a layer outside its visible range is not in the tile at all.</b> Checked
              before anything else is paid for — the describe, the cache, the build — because the
              case this exists for is a zoomed-out map over a dense layer, where the build is the
              whole cost: 33 MB and 46 seconds for one level-10 tile of Istanbul's buildings, and a
              503 after 75 seconds at level 8, measured on the showcase before this was written.
              The service's other layers are still drawn, and a tile with no layer left in it is
              the empty answer ITileSource already defines for no features.
            */
            if (!scheme.Draws(layer.VisibleRange, address.Z))
            {
                continue;
            }

            // <b>A cold tile takes a permit from `ConnectionBudget`, as a seed's does — D-277, owner
            // decision 2026-09-29.</b> `LayerConnections.AdmitTileBuildAsync` is the one admission a
            // seed already takes: quiesce, breaker, budget, keyed on the source. Only a build asks for
            // it, so a tile the cache answers costs no permit; a refusal propagates to the exception
            // handler, which answers it as every read path's is answered — 503 with `Retry-After`.
            //
            // <b>And a refusal is where stale-while-error begins — ADR-010 §5.1a.</b> The refusal is caught for this
            // layer alone, and the cache's copy of this layer's part stands in when there is one young enough; when
            // there is not, the refusal goes on to the handler exactly as before.
            (LayerPart part, Exception? refused) = await PartOrStandInAsync(
                    () => LayerPartAsync(
                        layer, address, defaultLifetime, contexts, connections, cache, building,
                        projector, datumShifts, unindexed, loggerFactory, geoParquet,
                        admit: permit => connections.AdmitTileBuildAsync(layer, permit),
                        cancellation,
                        scheme,
                        quota),
                    () => StandInAsync(
                        layer, address, defaultLifetime, defaultStaleLimit, contexts, cache, geoParquet, scheme,
                        cancellation))
                .ConfigureAwait(false);

            if (part.Came == PartCame.Stale)
            {
                refusedFor ??= refused;
            }

            parts.Add((layer, part));
        }

        if (refusedFor is not null)
        {
            stale.Note(service.Id, service.QualifiedName, refusedFor.Message, loggerFactory.CreateLogger("tiles"));
        }

        await RespondAsync(context, parts, defaultLifetime, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// How much longer a browser or a proxy may keep a tile that went out stale: a minute — owner decision
    /// 2026-09-29. Sent as <c>max-age</c> = the tile's <c>Age</c> plus this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Short, because the source may be back in a minute and the stale copy should not outlive the outage
    /// downstream.</b> A layer a browser must revalidate (V-56) still gets <c>no-cache</c>, which is shorter.
    /// </para>
    /// <para>
    /// <b>Added to the <c>Age</c>, not sent alone — corrected the same day, by owner direction.</b> RFC 9111 §4.2.3
    /// has a downstream cache compare <c>max-age</c> with the response's current age, which starts at the
    /// <c>Age</c> header. A stale tile is by definition older than its lifetime and so nearly always older than a
    /// minute, and a bare <c>max-age=60</c> made every browser and proxy that honours <c>Age</c> treat it as expired
    /// on arrival — the minute was never kept. The real <c>Age</c> is still sent; <c>max-age</c> is that age plus a
    /// minute, so the minute is counted from receipt.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan StaleMaxAge = TimeSpan.FromMinutes(1);

    /// <summary>The whole seconds since the stalest cached part was written, or zero when nothing came from the cache.</summary>
    private static long AgeOf(DateTimeOffset oldest, DateTimeOffset now) =>
        oldest == DateTimeOffset.MaxValue ? 0 : (long)Math.Max(0, (now - oldest).TotalSeconds);

    /// <summary>
    /// The one tile's response, from its layers' parts: the joined bytes, <c>X-Tile-Cache</c>, <c>Cache-Control</c>,
    /// <c>Age</c>, the weak ETag and its 304 — or 204.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="parts">Each drawn layer and its part, in the service's order.</param>
    /// <param name="defaultLifetime">The server's tile lifetime, for a tile no layer drew.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>When the response is written.</returns>
    /// <remarks>
    /// <b>Split out of <see cref="ServeTileAsync"/> on 2026-09-29, unchanged but for the stale state, so what a
    /// stale tile's headers are can be tested without a database</b> — <c>StaleWhileErrorTests</c>.
    /// </remarks>
    internal static Task RespondAsync(
        HttpContext context,
        IReadOnlyList<(PublishedLayer Layer, LayerPart Part)> parts,
        TimeSpan defaultLifetime,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parts);

        List<byte[]> bytes = [];
        List<PublishedLayer> layers = [];
        bool builtSomething = false;
        bool waitedForSomething = false;
        bool staleSomething = false;

        // <b>The shortest of the service's layers wins.</b> One tile carries
        // every layer, so it can only be as fresh as its most volatile part —
        // telling a browser to keep it for a day because two of three layers
        // are static would serve the third stale for a day.
        TimeSpan shortest = TimeSpan.MaxValue;

        // Whether any layer in the tile is one a browser must ask about before reusing — V-56.
        bool revalidate = false;

        // <b>The stalest cached part, for `Age`.</b> `MaxValue` means nothing came
        // from the cache, which is the case where there is no age to report.
        DateTimeOffset oldest = DateTimeOffset.MaxValue;

        foreach ((PublishedLayer layer, LayerPart part) in parts)
        {
            layers.Add(layer);

            if (part.Lifetime < shortest)
            {
                shortest = part.Lifetime;
            }

            if (part.Revalidate)
            {
                revalidate = true;
            }

            // <b>The oldest part bounds the whole response's age.</b> One tile can be
            // several layers' parts with different lifetimes and different write
            // times, and `Age` means *how long ago this response was generated* — so
            // the answer for a composite is the staleness of its stalest piece.
            // Anything else would understate it. D-248. A stale part's is its real age, past its lifetime.
            if (part.Written is { } when && when < oldest)
            {
                oldest = when;
            }

            switch (part.Came)
            {
                case PartCame.Built:
                    builtSomething = true;
                    break;
                case PartCame.Coalesced:
                    waitedForSomething = true;
                    break;
                case PartCame.Stale:
                    staleSomething = true;
                    break;
            }

            bytes.Add(part.Bytes);
        }

        // <b>Three states, not two, because the third is the one worth
        // seeing.</b> MISS means this request made the datastore work.
        // COALESCED means it wanted a cold tile and got somebody else's build
        // for free — which is the whole point of TileSingleFlight, and is
        // invisible if both are reported as a miss.
        //
        // <b>And STALE before all of them — ADR-010 §5.1a's explicit header.</b> One stale part makes the whole
        // tile one a client should know is old, whatever its other layers were.
        string disposition = staleSomething
            ? "STALE"
            : builtSomething
                ? "MISS"
                : waitedForSomething ? "COALESCED" : "HIT";

        TimeSpan lifetime = shortest == TimeSpan.MaxValue ? defaultLifetime : shortest;

        // One clock reading for both headers, so `max-age` is exactly the `Age` sent plus the stale minute.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long age = AgeOf(oldest, now);

        return WriteTileAsync(
            context,
            Concatenate(bytes),
            disposition,
            revalidate
                ? QueryResponseCaching.RevalidateFor(context, layers)
                : QueryResponseCaching.CacheControlFor(
                    context,
                    layers,
                    staleSomething && lifetime > TimeSpan.Zero
                        ? TimeSpan.FromSeconds(age) + StaleMaxAge
                        : lifetime),
            oldest == DateTimeOffset.MaxValue ? null : age,
            cancellation);
    }

    /// <summary>
    /// A layer's part, or — when its source refused to build it — the cache's copy standing in for it, with the
    /// refusal it stood in for.
    /// </summary>
    /// <param name="build">The part as <see cref="LayerPartAsync"/> makes it.</param>
    /// <param name="standIn">The cached copy, or null when there is none young enough — <see cref="StandInAsync"/>.</param>
    /// <returns>The part, and the refusal when a stand-in answered.</returns>
    /// <remarks>
    /// <para>
    /// <b>Only the refusals that mean <em>the source cannot build this now</em> — owner decision 2026-09-29.</b>
    /// <see cref="SourceRefused"/>: the breaker open or the database unreachable, the budget full, the source
    /// quiesced. A query error, a bad tile address or a bug is not an outage, and a stale tile over it would hide
    /// the fault the error is reporting.
    /// </para>
    /// <para>
    /// <b>With no stand-in, the refusal is rethrown as it was</b>, so the answer is the 503 with
    /// <c>Retry-After</c> it was before this existed.
    /// </para>
    /// </remarks>
    internal static async Task<(LayerPart Part, Exception? Refused)> PartOrStandInAsync(
        Func<Task<LayerPart>> build, Func<Task<LayerPart?>> standIn)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(standIn);

        try
        {
            return (await build().ConfigureAwait(false), null);
        }
        catch (Exception refused) when (SourceRefused(refused))
        {
            if (await standIn().ConfigureAwait(false) is { } held)
            {
                return (held, refused);
            }

            throw;
        }
    }

    /// <summary>Whether a failure means the layer's source cannot build a tile now, rather than that something is wrong.</summary>
    /// <param name="failure">What the build threw.</param>
    /// <returns>True for the three refusals ADR-010 §5.1a serves stale over.</returns>
    /// <remarks>
    /// <b>Unreachable is <see cref="SourceBreaker.Unreachable"/>'s answer, called rather than restated</b> — the
    /// same discriminator <c>ServiceContexts</c> falls back on — so a <c>PostgresException</c> that is an answer
    /// from a live database is never read as an outage here and there differently.
    /// </remarks>
    internal static bool SourceRefused(Exception failure) =>
        failure is not OperationCanceledException
        && (failure is ConnectionBudgetFullException or SourceQuiescedException
            || SourceBreaker.Unreachable(failure));

    /// <summary>
    /// A layer's cached part as it stands in for a refused build: fresh, or past its lifetime by no more than its
    /// stale limit — or null.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="address">The tile.</param>
    /// <param name="defaultLifetime">The server's tile lifetime, for a layer that set none.</param>
    /// <param name="defaultStaleLimit">The server's stale limit, for a layer that set none.</param>
    /// <param name="contexts">Where the layer's last shape is remembered.</param>
    /// <param name="cache">The tile cache.</param>
    /// <param name="geoParquet">For a GeoParquet layer's file version.</param>
    /// <param name="scheme">The service's grid.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The part, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>The key is the one <see cref="LayerPartAsync"/> reads, built from the shape this process last read</b>
    /// (<c>ServiceContexts.Remembered</c>) because the source that will not build cannot describe either. Same
    /// layer, same pipeline, same grid, same columns: so only this exact tile's copy is found, and a copy an edit,
    /// a refresh, a data-source move (ADR-095), a scheme switch (ADR-096) or a new pipeline made unreachable or
    /// deleted is not — ADR-010 §5.1's <em>wrong</em> class stays purged.
    /// </para>
    /// <para>
    /// <b>A fresh copy is found too</b>, and goes out as a hit: a quiesced source refuses its describe before its
    /// build, so a tile still in its lifetime needs this door as much as an expired one.
    /// </para>
    /// <para>
    /// <b>Anything that goes wrong here answers null</b>, so the caller rethrows the refusal it was standing in
    /// for: this is the cache failing soft, and the operator is owed the source's sentence rather than the cache's.
    /// </para>
    /// </remarks>
    internal static async Task<LayerPart?> StandInAsync(
        PublishedLayer layer,
        TileAddress address,
        TimeSpan defaultLifetime,
        TimeSpan defaultStaleLimit,
        ServiceContexts contexts,
        ITileCache cache,
        GeoParquetSources geoParquet,
        VectorTileScheme scheme,
        CancellationToken cancellation)
    {
        try
        {
            if (contexts.Remembered(layer) is not { } description)
            {
                return null;
            }

            return await StaleOrNothingAsync(
                    KeyOf(layer, AttributesOf(layer, description), address, geoParquet, scheme),
                    LifetimeOf(layer, defaultLifetime),
                    layer.StaleLimit ?? defaultStaleLimit,
                    layer.CacheLifetime is null && QueryResponseCaching.Editable(layer, description.Writable),
                    cache,
                    cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The cache's copy of one key as a stand-in part, or null — the half of <see cref="StandInAsync"/> that
    /// needs no description, so it can be tested on a cache alone.</summary>
    /// <param name="key">The part's key.</param>
    /// <param name="lifetime">How long the layer's tiles stay fresh.</param>
    /// <param name="staleLimit">How long past that a copy may stand in.</param>
    /// <param name="revalidate">Whether a browser must ask before reusing it — V-56.</param>
    /// <param name="cache">The tile cache.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The part — <see cref="PartCame.Stale"/> when past its lifetime — or null.</returns>
    internal static async Task<LayerPart?> StaleOrNothingAsync(
        TileCacheKey key,
        TimeSpan lifetime,
        TimeSpan staleLimit,
        bool revalidate,
        ITileCache cache,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(cache);

        CachedTile held = await cache.ReadExpiredAsync(key, lifetime, staleLimit, cancellation).ConfigureAwait(false);

        return held.Answered
            ? new LayerPart(
                held.Bytes, held.Expired ? PartCame.Stale : PartCame.Cached, held.Written, lifetime, revalidate)
            : null;
    }

    /// <summary>The service's tile cache quota as a write carries it, or null for a service with none — ADR-010 §3.</summary>
    /// <param name="service">The service.</param>
    /// <returns>The quota.</returns>
    internal static TileCacheQuota? QuotaOf(PublishedService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return service.TileCacheQuotaBytes is { } bytes
            ? new TileCacheQuota(service.Id, [.. service.Layers.Select(layer => layer.Id)], bytes)
            : null;
    }

    /// <summary>Where one layer's part of a tile came from.</summary>
    internal enum PartCame
    {
        /// <summary>The cache held it and it was fresh.</summary>
        Cached,

        /// <summary>This caller built it and stored it.</summary>
        Built,

        /// <summary>Somebody else was building it, and this caller waited for their build.</summary>
        Coalesced,

        /// <summary>
        /// Its source refused to build it and the cache's copy, past its lifetime, stood in — ADR-010 §5.1a. Only
        /// <see cref="ServeTileAsync"/> makes one; a seed and an export never see it.
        /// </summary>
        Stale,
    }

    /// <summary>One layer's encoded part of a tile, and what serving needs to know about it.</summary>
    /// <param name="Bytes">The encoded layer; empty when it has nothing in this tile.</param>
    /// <param name="Came">Where it came from.</param>
    /// <param name="Written">When the cached copy was written, for a part read from the cache.</param>
    /// <param name="Lifetime">How long the layer's tiles stay fresh.</param>
    /// <param name="Revalidate">Whether a browser must ask before reusing it — V-56.</param>
    internal readonly record struct LayerPart(
        byte[] Bytes, PartCame Came, DateTimeOffset? Written, TimeSpan Lifetime, bool Revalidate);

    /// <summary>
    /// The cache key of one layer's part of a tile — the one serving reads and writes under.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="attributes">The columns its tiles carry, from <see cref="AttributesOf"/>.</param>
    /// <param name="address">The tile.</param>
    /// <param name="geoParquet">Where a GeoParquet layer's file version is read.</param>
    /// <param name="scheme">
    /// The grid the layer's service is cut on, or null for Web Mercator — ADR-096. Web Mercator adds nothing
    /// to the fingerprint, so every key a Mercator service had it still has; any other grid is in it, so a
    /// service switched between grids never reads the other's tiles.
    /// </param>
    /// <returns>The key.</returns>
    /// <remarks>
    /// <b>Named so that the seed and the cache report ask for the same key the tile route
    /// does</b> — ADR-093 §5.5. A second computation of it anywhere else would be a seed that fills a
    /// cache nobody reads, and nothing would fail: every request would simply miss.
    /// </remarks>
    internal static TileCacheKey KeyOf(
        PublishedLayer layer,
        IReadOnlyList<FieldDescription> attributes,
        TileAddress address,
        GeoParquetSources geoParquet,
        VectorTileScheme? scheme = null)
    {
        // <b>A GeoParquet layer's own version rides in the fingerprint, so a replaced file
        // invalidates its tiles structurally instead of waiting out the cache lifetime.</b>
        // A hosted table's schema changing already moves the fingerprint through the
        // attribute list; a file can be replaced by another with the same columns and a
        // different geometry, and nothing above would notice without this. Null for a hosted
        // layer, which keeps every existing cache key unchanged.
        //
        // <b>A registered PostGIS layer's database rides in it the same way — ADR-095 §5.4.</b>
        // `PUT /admin/datasources/{id}` can point a source at another database and keep every layer
        // id, and the key is the layer id plus this fingerprint: without the database in it, the
        // new database's map would be served the old one's tiles until they expired. The identity
        // is `SourceQuiesce.DatabaseKey` — host, port and database, the fold ADR-059 §5d already
        // uses to say *the same database* — so a rotated password or a changed pool setting keeps
        // the pyramid, and only somewhere else loses it. It is hashed, never written into the path.
        // The update also purges the source's layers (`UpdateDataSourceAsync`); this is what still
        // holds on a node that purge did not reach. Null for a hosted layer, whose keys stay what
        // they were.
        string? version = Graticula.Platform.Admin.GeoParquetLocator.Is(layer.ConnectionString)
            ? geoParquet.VersionOf(layer.ConnectionString, layer.Definition.TableName)
            : Graticula.Platform.Admin.TileSources.Registered(layer.Definition.IsHosted, KindOf(layer))
                ? "source=" + SourceQuiesce.DatabaseKey(layer.ConnectionString)
                : null;

        return new TileCacheKey(
            layer.Id,
            TileCacheKey.FingerprintOf(
                layer.Definition.Srid,
                layer.Definition.GeometryColumn,
                attributes.Select(a => a.Name),
                PostGisTileSource.Extent,
                PostGisTileSource.Buffer,
                version,
                scheme?.Fingerprint),
            address);
    }

    /// <summary>
    /// One layer's part of a tile: from the cache when it is fresh there, and otherwise built once,
    /// stored, and shared with whoever else is waiting for it.
    /// </summary>
    /// <param name="layer">The layer, which the caller has already found draws at this level.</param>
    /// <param name="address">The tile.</param>
    /// <param name="defaultLifetime">The server's tile lifetime, for a layer that set none.</param>
    /// <param name="contexts">Where the layer's description is remembered.</param>
    /// <param name="connections">Where its tile source comes from.</param>
    /// <param name="cache">The tile cache.</param>
    /// <param name="building">The builds in flight — §2c.</param>
    /// <param name="projector">For the datum notice.</param>
    /// <param name="datumShifts">Where a datum crossing is said once.</param>
    /// <param name="unindexed">Where a layer with no spatial index is said once — ADR-095 §5.2.</param>
    /// <param name="loggerFactory">For the datum notice.</param>
    /// <param name="geoParquet">For a GeoParquet layer's file version.</param>
    /// <param name="admit">
    /// Taken before a build and released after it, or null for none. <b>Serving and a seed both pass
    /// <c>LayerConnections.AdmitTileBuildAsync</c> here</b> — ADR-093 §5.5 for the seed, and since
    /// 2026-09-29 for serving too (D-277) — so a build is bounded by the same limiter every read path
    /// draws from, and a tile merely found cached costs no permit.
    /// </param>
    /// <param name="cancellation">The caller's; a shared build does not carry it.</param>
    /// <param name="scheme">
    /// The grid the layer's service is cut on, or null for Web Mercator — ADR-096. It decides the key, the
    /// source's envelope and the reference the datum notice names; serving and a seed pass the service's.
    /// </param>
    /// <param name="quota">The service's tile cache quota, or null — ADR-010 §3. Serving, a seed and an export
    /// all pass it, so a service stays inside it however its tiles are built.</param>
    /// <returns>The part.</returns>
    /// <remarks>
    /// <para>
    /// <b>The tile route's own loop body, moved here unchanged on 2026-09-29 so a seed can call
    /// it</b> — ADR-093 §5.5's requirement that a seeded tile be indistinguishable from a served one.
    /// Same key, same encoder, same single-flight, same write; a seed that built tiles any other way
    /// would fill the cache with bytes the route would not have made, or under keys it never reads.
    /// </para>
    /// <para>
    /// <b>The visible-range test stays with the caller</b>, because the two callers do different
    /// things with a layer left out: serving draws the tile without it, and a seed counts a level no
    /// layer draws at as skipped. Both ask <see cref="VisibleScaleRange.CarriesVectorTile"/>.
    /// </para>
    /// </remarks>
    internal static async Task<LayerPart> LayerPartAsync(
        PublishedLayer layer,
        TileAddress address,
        TimeSpan defaultLifetime,
        ServiceContexts contexts,
        LayerConnections connections,
        ITileCache cache,
        TileSingleFlight building,
        IProjector projector,
        DatumShiftNotices datumShifts,
        UnindexedLayerNotices unindexed,
        ILoggerFactory loggerFactory,
        GeoParquetSources geoParquet,
        Func<CancellationToken, ValueTask<IDisposable>>? admit,
        CancellationToken cancellation,
        VectorTileScheme? scheme = null,
        TileCacheQuota? quota = null)
    {
        scheme ??= VectorTileScheme.WebMercator;

        (_, LayerDescription description) = await contexts.GetAsync(layer, cancellation)
            .ConfigureAwait(false);

        IReadOnlyList<FieldDescription> attributes = AttributesOf(layer, description);

        /*
          <b>Q-141: the operator hears about the datum, because nobody else can.</b> A
          tile is Web Mercator by definition, so a layer stored in anything else is
          transformed on every cold tile — and a protobuf tile has nowhere to carry a
          caution even if the client could act on one. This is the half of
          [D-32](../../docs/architecture-debt.md) that the three response-shaped answers
          could not have reached at all.

          <b>Before the cache is consulted, not after.</b> A warm tile is served without
          transforming anything, and putting the notice on the miss would mean a
          deployment whose tiles are all cached never hears about a layer it is
          nonetheless serving across a datum. The dictionary makes the repeat free.
        */
        await datumShifts
            .NoteAsync(
                layer.Id,
                layer.Definition.Name,
                layer.Definition.Srid,

                // The tile's own reference: Web Mercator, or the service's scheme (ADR-096) — a TUREF
                // layer tiled on a TUREF grid crosses no datum at all.
                scheme.Srid,
                projector,
                loggerFactory.CreateLogger("tiles"),
                cancellation)
            .ConfigureAwait(false);

        // <b>ADR-095 §5.2: a table with no spatial index still tiles, slowly, and the operator hears
        // it once.</b> A registered table is somebody else's and may have none; the `&&` in the tile
        // statement then reads the whole table for every cold tile. Refusing would take away a map
        // that works; saying nothing would leave the operator to find out from a slow one. The
        // describe already read the index from the catalogue, so this costs nothing per tile.
        if (description.SpatiallyIndexed is false)
        {
            unindexed.Note(layer.Id, layer.Definition.Name, loggerFactory.CreateLogger("tiles"));
        }

        TileCacheKey key = KeyOf(layer, attributes, address, geoParquet, scheme);

        // <b>The layer's own lifetime, not the server's.</b> D-25: a
        // cadastral layer and an incident layer need opposite answers, and
        // A-028 records that only the administrator knows which is which. When
        // nobody said, a registered PostGIS layer gets the shorter default ADR-095
        // §5.3 gives it, because other tools write to it and nothing tells us.
        TimeSpan lifetime = LifetimeOf(layer, defaultLifetime);

        // <b>V-56's tile half: a layer somebody can edit is revalidated, not kept.</b> The
        // lifetime above still governs this server's own copy, which an edit empties; what
        // changes is what the browser is told. A lifetime an administrator set on the layer is
        // honoured as it is for `query` — `QueryResponseCaching.LifetimeOf`'s rule.
        bool revalidate =
            layer.CacheLifetime is null && QueryResponseCaching.Editable(layer, description.Writable);

        return await CachedOrBuiltAsync(
                key, address, layer.Definition.Name, lifetime, revalidate, cache, building,
                () => connections.TileSourceFor(layer, attributes, scheme.IsWebMercator ? null : scheme),
                admit, cancellation, quota)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One layer's part from the cache when it is fresh there; otherwise built once — admitted once —
    /// stored, and shared with whoever else is waiting for it.
    /// </summary>
    /// <param name="key">The part's cache key, from <see cref="KeyOf"/>.</param>
    /// <param name="address">The tile.</param>
    /// <param name="layerName">The layer's name, which the encoder writes into the part.</param>
    /// <param name="lifetime">How long the layer's tiles stay fresh.</param>
    /// <param name="revalidate">Whether a browser must ask before reusing it — V-56.</param>
    /// <param name="cache">The tile cache.</param>
    /// <param name="building">The builds in flight — §2c.</param>
    /// <param name="sourceFor">The layer's tile source, asked for only on a miss.</param>
    /// <param name="admit">Taken around a build, or null — see <see cref="LayerPartAsync"/>.</param>
    /// <param name="cancellation">The caller's; a shared build does not carry it.</param>
    /// <param name="quota">The service's tile cache quota, or null — carried to the write.</param>
    /// <returns>The part.</returns>
    /// <remarks>
    /// <b>Split from <see cref="LayerPartAsync"/> on 2026-09-29, unchanged, so the order of the three
    /// things D-277 is about can be tested without a database</b> — the cache read before any permit,
    /// the permit inside the shared build, and one permit per build however many callers wait on it
    /// (<c>TileBuildAdmissionTests</c>).
    /// </remarks>
    internal static async Task<LayerPart> CachedOrBuiltAsync(
        TileCacheKey key,
        TileAddress address,
        string layerName,
        TimeSpan lifetime,
        bool revalidate,
        ITileCache cache,
        TileSingleFlight building,
        Func<ITileSource> sourceFor,
        Func<CancellationToken, ValueTask<IDisposable>>? admit,
        CancellationToken cancellation,
        TileCacheQuota? quota = null)
    {
        CachedTile cached = await cache.ReadAsync(key, lifetime, cancellation)
            .ConfigureAwait(false);

        if (cached.Answered)
        {
            return new LayerPart(cached.Bytes, PartCame.Cached, cached.Written, lifetime, revalidate);
        }

        ITileSource source = sourceFor();

        // <b>One build per cold tile, however many callers arrive at
        // once.</b> Measured before this existed: twelve simultaneous
        // requests for one cold tile produced twelve datastore builds and
        // threw eleven of the results away. See TileSingleFlight.
        TileSingleFlight.Result made = await building.BuildAsync(
            key,
            async () =>
            {
                // <b>Inside the shared build, so only a build takes a permit.</b> A caller that
                // joins a build somebody else started waits without holding one — a request behind
                // a seed's build, a seed behind a request's, or eleven requests behind a twelfth —
                // one build, one permit. A refusal is the build's outcome, so everyone waiting on
                // it is refused together rather than each asking the budget again (§2c, D-277).
                using IDisposable? admitted = admit is null
                    ? null
                    : await admit(CancellationToken.None).ConfigureAwait(false);

                byte[] bytes = await source
                    .BuildAsync(address, layerName, CancellationToken.None)
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
                await cache.WriteAsync(key, bytes, quota, CancellationToken.None)
                    .ConfigureAwait(false);

                return bytes;
            },
            cancellation).ConfigureAwait(false);

        return new LayerPart(
            made.Bytes, made.Built ? PartCame.Built : PartCame.Coalesced, null, lifetime, revalidate);
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
    internal static byte[] Concatenate(List<byte[]> parts)
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
        string cacheControl,
        long? age,
        CancellationToken cancellation)
    {
        context.Response.Headers["X-Tile-Cache"] = cacheState;

        // <b>`Age`, because this server is the cache and said nothing about it —
        // [D-248](../../docs/architecture-debt.md).</b> A stored tile came back with
        // `Cache-Control: max-age` and a `HIT`, and no `Age`. RFC 9111 §5.1 has a proxy
        // treat a missing `Age` as zero, so every cache in front of us restarted our
        // whole lifetime from its own receipt: worst-case staleness was ours plus theirs,
        // per layer, and the operator's own diagnostic agreed with the complaint while
        // being useless about it — which is [ADR-017](../../docs/adr/ADR-017-admin-api.md)
        // §3.1's scenario, *the map is showing old data*, partly caused here.
        //
        // <b>Sent only when something came from the store.</b> A freshly built tile has
        // an age of zero and `Age: 0` is legal, but saying it on every miss would make
        // the header noise rather than a signal — and the number this cache can stand
        // behind is the one it stamped, not one inferred for a response it just made.
        //
        // <b>Computed by the caller since 2026-09-29</b> (`RespondAsync`), from the same clock reading a stale
        // tile's `max-age` is built from, so the two cannot disagree by the second between two reads.
        if (age is { } seconds)
        {
            context.Response.Headers["Age"] =
                seconds.ToString(CultureInfo.InvariantCulture);
        }

        // <b>The same number the server caches by, told to everyone downstream.</b>
        // A browser and a CDN each keep their own copy, and until now we told
        // them nothing — so they either re-fetched every tile or invented a
        // policy. Sending the layer's own volatility means one setting governs
        // every cache in the chain, which is the only way they can agree.
        //
        // <b>Public only for an anonymous caller and a tile whose every layer is
        // public</b> — `QueryResponseCaching.CacheControlFor`, the rule the query
        // face already applied. This wrote `public` for every tile until
        // 2026-09-15, private layers included.
        context.Response.Headers.CacheControl = cacheControl;

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
        // <b>Weak since 2026-09-29, and it was strong until then.</b> It was
        // strong because a tile is bytes and there is no notion of a
        // semantically equivalent one. Then ADR-068 §9 started compressing
        // tiles, so the same tile goes out brotli, gzip or identity under
        // one tag, and RFC 9110 §8.8.1 reserves a strong tag for
        // byte-identical bodies. `QueryResponseCaching.ComputeETag` made the same
        // call for query answers for the same reason. If-None-Match compares
        // weakly, so a client holding the old strong tag still gets its 304.
        string etag = "W/\"" + Convert.ToHexString(
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
                // RFC 9110 §8.8.3.2's weak comparison, which is what If-None-Match
                // uses: W/"x" and "x" identify the same tile. It also keeps a
                // client that stored the strong tag sent before 2026-09-29 on 304s.
                if (candidate == "*"
                    || string.Equals(Opaque(candidate), Opaque(etag), StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;

        static string Opaque(string tag) =>
            tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
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
    internal static IReadOnlyList<FieldDescription> AttributesOf(
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
    /// as</b>, since PostGIS types are not standard SQL types and
    /// <c>PostGisFeatureSource.MapType</c> has no arm for them. (It used to say *from
    /// <c>information_schema</c>*; the field list moved to <c>pg_attribute</c> on 2026-09-09 —
    /// [D-231](../../docs/architecture-debt.md) — and the answer here is unchanged, because it
    /// is the mapping rather than the catalogue that decides it.) Excluding it by type as well as by name means a layer
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
