using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.Tiles;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Tiles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// What the standard tile faces — OGC API Tiles and WMTS — share: which services a caller may see as
/// tiles, how one is found by its id, how it is described, and the one tile path — ADR-097.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ArcGIS tile face's rules, applied by listing rather than by lookup.</b> The VectorTileServer
/// routes find one service by its path through <see cref="ServiceLookup"/> and refuse in ArcGIS JSON; a
/// standard face lists what the caller may see and finds the id in that list, as OGC API Features, WFS
/// and WMS do, so a refusal can be written in the face's own vocabulary (ADR-049) and a service the
/// caller may not see is simply not there (ADR-018). The checks are <see cref="ServiceLookup"/>'s and
/// <c>VectorTileEndpoints.TileableAsync</c>'s, in their order and with their outcomes, not a looser copy:
/// a service is served here exactly when <c>/rest/services/…/VectorTileServer</c> would serve it.
/// </para>
/// <para>
/// <b>Running, even for an administrator.</b> OGC API Features lists a stopped service to someone who
/// may manage the server; the ArcGIS tile face refuses to serve one to anybody (<c>RefuseStoppedAsync</c>),
/// and these faces serve tiles, so they follow the tile face.
/// </para>
/// <para>
/// <b>While the catalogue is unreachable only public services survive</b> — <see cref="ServiceLookup"/>'s
/// rule: a sharing decision made from a remembered copy may be minutes out of date, and public is the one
/// scope where being wrong gives nothing away. The listing these faces read already holds only public
/// services while blind — <c>CatalogFallback.RememberedListing</c> filters it once for every face
/// (ADR-026) — and a blind store makes every caller anonymous besides. The check in <c>Readable</c> is a
/// second barrier, not the only one: checked 2026-09-29, the other standard faces do not leak either.
/// </para>
/// </remarks>
internal static class TileFaces
{
    /// <summary>A service a caller may read as tiles, with its id and its tile matrix set.</summary>
    /// <param name="Service">The service.</param>
    /// <param name="Id">Its id on both faces — <see cref="TileDocuments.CollectionId"/>.</param>
    /// <param name="Set">Its grid as a tile matrix set.</param>
    /// <param name="Reason">Why the caller may read it — kept so an administrator's override is audited on use.</param>
    internal sealed record Candidate(PublishedService Service, string Id, TileMatrixSet Set, LayerAccess.Reason Reason);

    /// <summary>What a caller may see, and the ids two services answer to.</summary>
    /// <param name="Services">The services, one per id.</param>
    /// <param name="Ambiguous">The ids more than one visible service answers to, which neither is served under.</param>
    internal sealed record Visible(IReadOnlyList<Candidate> Services, IReadOnlySet<string> Ambiguous);

    /// <summary>How finding an id went.</summary>
    internal enum Lookup
    {
        /// <summary>One service answers to it.</summary>
        Found,

        /// <summary>None the caller may see does — including one they may not see.</summary>
        Absent,

        /// <summary>Two do, so neither is served under it.</summary>
        Ambiguous,
    }

    /// <summary>Every service the caller may read as tiles, or null when the catalogue cannot be listed.</summary>
    /// <param name="context">The request.</param>
    /// <param name="catalog">The catalogue.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The services, or null — the caller answers 503 in its own vocabulary.</returns>
    public static async Task<Visible?> VisibleAsync(
        HttpContext context, CatalogFallback catalog, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);

        CatalogListing listing = await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        // Null is not empty: no listing at all is an outage, and saying "none" would be a false answer.
        if (listing.Services is not { } services)
        {
            return null;
        }

        if (listing.Blind)
        {
            ServiceLookup.SayAge(context, listing.Age);
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        List<Candidate> found = [];

        foreach (PublishedService service in services)
        {
            if (Readable(current, service, listing.Blind) is { } candidate)
            {
                found.Add(candidate);
            }
        }

        // <b>An id two services answer to is served under neither</b> — TileDocuments.CollectionId. Case is
        // ignored because the catalogue's names are unique without it (`service_name_in_folder_ci`).
        HashSet<string> ambiguous = new(
            found
                .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key),
            StringComparer.OrdinalIgnoreCase);

        return new Visible([.. found.Where(c => !ambiguous.Contains(c.Id))], ambiguous);
    }

    /// <summary>
    /// Whether a service is one the ArcGIS tile face would serve at all, before sharing: running, its tile
    /// face on, at least one layer, every layer of a kind that tiles, and a grid this build can read.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <returns>True when its tiles can be served.</returns>
    /// <remarks>
    /// <b>The same predicates <c>VectorTileEndpoints.TileableAsync</c> asks, one by one.</b> The tile face
    /// answers a service with no layers or an untileable one with a 400 and an unreadable grid with a 500;
    /// on a listing face those are services with no tiles to list, so they are left out, and asked for by id
    /// they are absent. A service with its tile face off is absent on every face (ADR-031 condition 2).
    /// </remarks>
    public static bool Serves(PublishedService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return service.IsRunning
            && service.Limits.AllowsTiles(dataSupportsIt: true)
            && service.Layers.Count > 0
            && service.Layers.All(VectorTileEndpoints.Tileable)
            && service.TileSchemeUnreadable is null;
    }

    /// <summary>
    /// The one service an id names, read from the catalogue by name rather than by listing it — for the routes
    /// a tile client calls many times a second.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="catalog">The catalogue.</param>
    /// <param name="id">The id asked for.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>How it went and the service, or null when the catalogue cannot say — the caller answers 503.</returns>
    /// <remarks>
    /// <para>
    /// <b>By name, because a tile request is the hot path.</b> <see cref="VisibleAsync"/> lists every service
    /// with its layers, which is right for a capabilities document and wrong for each of the hundreds of tile
    /// requests a map pan makes. The ArcGIS tile route reads one service by name
    /// (<see cref="ServiceLookup.ServiceAsync"/>); this reads the one or two the id can mean — the id itself at
    /// the root, and every <c>folder.name</c> split of it at a dot — and applies <see cref="VisibleAsync"/>'s
    /// rule to each, so the two ways of finding a service cannot disagree about which the caller may read.
    /// </para>
    /// <para>
    /// <b>Two readable answers are ambiguous and neither is served</b> (<see cref="TileDocuments.CollectionId"/>).
    /// A reading the store cannot answer and has no memory of is not taken as absence: when nothing else was
    /// found, the caller is told the catalogue cannot say.
    /// </para>
    /// </remarks>
    public static async Task<(Lookup Outcome, Candidate? Found)?> FindAsync(
        HttpContext context, CatalogFallback catalog, string id, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(id);

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        List<Candidate> found = [];
        bool unanswered = false;

        foreach ((string? folder, string name) in Readings(id))
        {
            CatalogAnswer answer = await catalog.FindServiceAsync(folder, name, cancellation).ConfigureAwait(false);

            if (answer.Service is not { } service)
            {
                unanswered |= answer.Blind;
                continue;
            }

            if (answer.Blind)
            {
                ServiceLookup.SayAge(context, answer.Age);
            }

            if (Readable(current, service, answer.Blind) is { } candidate
                && string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(candidate);
            }
        }

        return found.Count switch
        {
            1 => (Lookup.Found, found[0]),
            > 1 => (Lookup.Ambiguous, null),
            _ => unanswered ? null : (Lookup.Absent, null),
        };
    }

    /// <summary>Every folder and name an id can be read as: the whole id at the root, then each split at a dot.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The readings.</returns>
    internal static IEnumerable<(string? Folder, string Name)> Readings(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (id.Length > 0)
        {
            yield return (null, id);
        }

        for (int dot = id.IndexOf('.', StringComparison.Ordinal); dot >= 0; dot = id.IndexOf('.', dot + 1))
        {
            if (dot > 0 && dot < id.Length - 1)
            {
                yield return (id[..dot], id[(dot + 1)..]);
            }
        }
    }

    /// <summary>A service as a candidate when the caller may read it as tiles now, or null.</summary>
    /// <param name="current">The caller.</param>
    /// <param name="service">The service.</param>
    /// <param name="blind">Whether the catalogue's answer is a remembered one.</param>
    /// <returns>The candidate, or null.</returns>
    private static Candidate? Readable(RequestPrincipal current, PublishedService service, bool blind)
    {
        if (!Serves(service) || (blind && service.Sharing != SharingScope.Public))
        {
            return null;
        }

        LayerAccess.Reason reason = LayerAccess.Evaluate(
            service.Sharing, service.Owner, current.Principal, current.Authorization, service.SharedWith);

        return reason.IsAllowed()
            ? new Candidate(
                service,
                TileDocuments.CollectionId(service.Folder, service.Name),
                TileMatrixSet.For(service.TileScheme),
                reason)
            : null;
    }

    /// <summary>
    /// What <see cref="ServiceLookup"/> does once a service is found and read: an administrator's override is
    /// audited, and the request's deadline is lowered to the service's own.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="candidate">The service being read.</param>
    /// <returns>When done.</returns>
    public static async Task UsingAsync(HttpContext context, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // ADR-018 condition 3: *you saw this because you may see everything* is recorded when it is used.
        if (candidate.Reason == LayerAccess.Reason.AdministrativeOverride)
        {
            await SharingAudit
                .RecordOverrideAsync(context, candidate.Service.QualifiedName, candidate.Service.Sharing)
                .ConfigureAwait(false);
        }

        RequestDeadline.LowerTo(context, candidate.Service.Limits.Cost.RequestDeadline);
    }

    /// <summary>A service described for the documents: its layers, their fields and levels, and its extents.</summary>
    /// <param name="candidate">The service.</param>
    /// <param name="contexts">Where layer descriptions are remembered.</param>
    /// <param name="projector">For the extents.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// <b>Every number is one the tile path also uses</b>: the fields are <c>VectorTileEndpoints.AttributesOf</c>
    /// — what the tile carries, not what the table has — the levels are the ones
    /// <see cref="VectorTileScheme.Draws"/> puts the layer in a tile at, and the extent in the grid's reference
    /// is <c>VectorTileEndpoints.InSchemeAsync</c>, the service document's.
    /// </remarks>
    public static async Task<TiledService> DescribeAsync(
        Candidate candidate, ServiceContexts contexts, IProjector projector, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(contexts);

        PublishedService service = candidate.Service;
        VectorTileScheme scheme = service.TileScheme;
        List<TileLayerInfo> layers = [];
        List<(int Srid, Envelope? Extent)> extents = [];
        Envelope? inSet = null;

        foreach (PublishedLayer layer in service.Layers)
        {
            (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
            (int? min, int? max) = LevelsOf(scheme, layer.VisibleRange);

            layers.Add(new TileLayerInfo(
                layer.Definition.Name,
                DimensionOf(layer.GeometryType),
                VectorTileEndpoints.AttributesOf(layer, described),
                min,
                max));

            extents.Add((layer.Definition.Srid, described.Extent));
            inSet = Widen(
                inSet,
                await VectorTileEndpoints.InSchemeAsync(described.Extent, layer.Definition.Srid, scheme, projector, cancellation)
                    .ConfigureAwait(false));
        }

        Envelope? wgs84 = null;

        foreach (Envelope? box in await GeographicExtents.InWgs84Async(projector, extents, cancellation).ConfigureAwait(false))
        {
            wgs84 = Widen(wgs84, box);
        }

        return new TiledService(
            candidate.Id,
            service.QualifiedName,
            service.Description,
            candidate.Set,
            wgs84,
            inSet,
            layers);
    }

    /// <summary>Every visible service described, leaving out one whose description fails — V-43.</summary>
    /// <param name="context">The request.</param>
    /// <param name="face">Which document is being written, for the log.</param>
    /// <param name="visible">What the caller may see.</param>
    /// <param name="contexts">Where layer descriptions are remembered.</param>
    /// <param name="projector">For the extents.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The descriptions, in catalogue order.</returns>
    public static async Task<IReadOnlyList<TiledService>> DescribeAllAsync(
        HttpContext context,
        string face,
        Visible visible,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(visible);
        List<TiledService> described = [];

        foreach (Candidate candidate in visible.Services)
        {
            // One service whose source fails is left out, not allowed to take the whole list down; it is
            // logged under its first layer, which is the unit the guard names.
            if (await ListingGuard.DescribeOrLeaveOutAsync(
                    context, face, candidate.Service.Layers[0],
                    () => DescribeAsync(candidate, contexts, projector, cancellation), cancellation)
                    .ConfigureAwait(false) is { } one)
            {
                described.Add(one);
            }
        }

        return described;
    }

    /// <summary>The first and last level a layer is in a tile at, on a grid — ADR-070 through the tile path's own test.</summary>
    /// <param name="scheme">The grid.</param>
    /// <param name="range">The layer's visible range.</param>
    /// <returns>The levels, or nulls when it is in no tile at all.</returns>
    public static (int? Min, int? Max) LevelsOf(VectorTileScheme scheme, VisibleScaleRange range)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        int? min = null;
        int? max = null;

        for (int level = 0; level <= scheme.MaxLevel; level++)
        {
            if (!scheme.Draws(range, level))
            {
                continue;
            }

            min ??= level;
            max = level;
        }

        return (min, max);
    }

    /// <summary>
    /// The TileJSON of a Web Mercator service — ADR-097 §5.3 — or null for any other grid, which TileJSON
    /// cannot describe.
    /// </summary>
    /// <param name="described">The service described.</param>
    /// <param name="candidate">The service.</param>
    /// <param name="root">The OGC face's base URL; the template is its tile route.</param>
    /// <returns>The document, or null.</returns>
    public static byte[]? TileJsonOf(TiledService described, Candidate candidate, string root)
    {
        ArgumentNullException.ThrowIfNull(described);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!candidate.Service.TileScheme.IsWebMercator)
        {
            return null;
        }

        // OGC API Tiles' {tileMatrix}/{tileRow}/{tileCol} is TileJSON's {z}/{y}/{x}: level, row from the
        // north, column from the west. The template therefore resolves to the OGC face's own tile route.
        string template = TileDocuments.TilesUrl(root, described.Id) + "/" + TileMatrixSet.WebMercatorQuadId + "/{z}/{y}/{x}";

        (int? serviceMin, int? serviceMax) = LevelsOf(
            VectorTileScheme.WebMercator, VectorTileEndpoints.ServiceRange(candidate.Service.Layers));

        // A layer that is in no tile at all is left out of vector_layers: listing it would promise a source
        // layer no tile has.
        List<TileJsonLayer> layers = [.. described.Layers
            .Where(l => l.MinLevel is not null && l.MaxLevel is not null)
            .Select(l => new TileJsonLayer(l.Id, l.Fields, l.MinLevel!.Value, l.MaxLevel!.Value))];

        return TileJson.Write(
            described.Title,
            template,
            layers,
            serviceMin ?? 0,
            serviceMax ?? TileAddress.MaxZoom,
            described.Wgs84,
            described.Description);
    }

    /// <summary>The absolute base URL of the server, with the path base a proxy may put in front of it.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The URL, no trailing slash.</returns>
    public static string Origin(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
    }

    /// <summary>A layer's geometry as OGC's <c>geometryDimension</c>.</summary>
    /// <param name="kind">The geometry kind.</param>
    /// <returns>0, 1 or 2.</returns>
    public static int? DimensionOf(GeometryKind kind) => kind switch
    {
        GeometryKind.Point or GeometryKind.MultiPoint => 0,
        GeometryKind.LineString or GeometryKind.MultiLineString => 1,
        GeometryKind.Polygon or GeometryKind.MultiPolygon => 2,
        _ => null,
    };

    private static Envelope? Widen(Envelope? sofar, Envelope? next)
    {
        if (next is not { IsEmpty: false } add)
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
}

/// <summary>What serving one tile needs, from the container — the arguments of <c>VectorTileEndpoints.ServeTileAsync</c>.</summary>
/// <param name="Contexts">Where layer descriptions are remembered.</param>
/// <param name="Connections">Tile sources and build admission.</param>
/// <param name="Cache">The tile cache.</param>
/// <param name="Building">The builds in flight.</param>
/// <param name="Projector">The projector.</param>
/// <param name="DatumShifts">The datum notices.</param>
/// <param name="Unindexed">The unindexed-layer notices.</param>
/// <param name="Logs">The logger factory.</param>
/// <param name="GeoParquet">GeoParquet file versions.</param>
/// <param name="Stale">Where a stale answer is counted — ADR-010 §5.1a.</param>
internal sealed record TileServing(
    ServiceContexts Contexts,
    LayerConnections Connections,
    ITileCache Cache,
    TileSingleFlight Building,
    IProjector Projector,
    DatumShiftNotices DatumShifts,
    UnindexedLayerNotices Unindexed,
    ILoggerFactory Logs,
    GeoParquetSources GeoParquet,
    StaleTileNotices Stale)
{
    /// <summary>The request's services, resolved by type — the same instances the ArcGIS tile route is handed.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The set.</returns>
    public static TileServing From(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IServiceProvider services = context.RequestServices;

        return new TileServing(
            services.GetRequiredService<ServiceContexts>(),
            services.GetRequiredService<LayerConnections>(),
            services.GetRequiredService<ITileCache>(),
            services.GetRequiredService<TileSingleFlight>(),
            services.GetRequiredService<IProjector>(),
            services.GetRequiredService<DatumShiftNotices>(),
            services.GetRequiredService<UnindexedLayerNotices>(),
            services.GetRequiredService<ILoggerFactory>(),
            services.GetRequiredService<GeoParquetSources>(),
            services.GetRequiredService<StaleTileNotices>());
    }

    /// <summary>Serves one tile of a found service through the one tile path.</summary>
    /// <param name="context">The request.</param>
    /// <param name="service">The service.</param>
    /// <param name="address">The tile, already in its grid.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>When written.</returns>
    public Task ServeAsync(HttpContext context, PublishedService service, TileAddress address, CancellationToken cancellation) =>
        VectorTileEndpoints.ServeTileAsync(
            context, service, address, Contexts, Connections, Cache, Building, Projector, DatumShifts, Unindexed,
            Logs, GeoParquet, Stale, cancellation);
}
