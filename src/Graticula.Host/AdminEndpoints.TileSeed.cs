using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Seeding a vector tile service's cache, and reading back what is in it — ADR-093,
/// [ADR-010](../../docs/adr/ADR-010-caching.md) §6 and §6b.
/// </summary>
/// <remarks>
/// <para>
/// <b>Under the service, because a tile is the service's.</b> A tile carries every layer of its
/// service, so a seed is asked for a service and refused for one; the layer's own <c>…/cache</c>
/// route sets how long its part stays fresh, and this is the other half of the same box.
/// </para>
/// <para>
/// <b>Addressed by folder and name, as the style and sprite routes are since
/// [D-275](../../docs/architecture-debt.md)</b>: <c>?folder=</c>, absent meaning the root and never
/// *any folder*, and the ownership check asked about the service the write will touch.
/// </para>
/// <para>
/// <b>The permission is the layer cache route's</b> — <c>content:publishTiles</c>, and whose service
/// it is (ADR-075) for anything that starts or stops work. A read needs the privilege and that the
/// caller may read the service, as the sprite read-back does.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    /// <summary>The highest level a seed starts at by default — ADR-093 §5.2.</summary>
    /// <remarks>
    /// <b>Fourteen, the owner's number for the console's default</b>: past it a level has four times
    /// the tiles of the one before for a fraction of the value, which is what §6a's measurement says
    /// about where a seed's hours go.
    /// </remarks>
    internal const int DefaultSeedCeiling = 14;

    private static void MapTileSeed(WebApplication app)
    {
        app.MapGet("/admin/services/{name}/cache", GetServiceCacheAsync);
        app.MapPost("/admin/services/{name}/cache/seeds", StartSeedAsync);
        app.MapGet("/admin/services/{name}/cache/seeds", ListSeedsAsync);
        app.MapGet("/admin/services/{name}/cache/seeds/{id:guid}", GetSeedAsync);
        app.MapDelete("/admin/services/{name}/cache/seeds/{id:guid}", CancelSeedAsync);
    }

    /// <summary>What a seed is asked for.</summary>
    /// <param name="MinZoom">The lowest level, or null for the lowest the service draws at.</param>
    /// <param name="MaxZoom">The highest level, or null for <see cref="DefaultSeedCeiling"/> or the highest the service draws at, whichever is lower.</param>
    /// <param name="Extent">The area, or null for the service's whole extent.</param>
    internal sealed record SeedRequest(int? MinZoom, int? MaxZoom, SeedExtent? Extent);

    /// <summary>An area, as an ArcGIS envelope: in Web Mercator or in WGS 84 degrees.</summary>
    internal sealed record SeedExtent(
        double Xmin, double Ymin, double Xmax, double Ymax,
        [property: JsonPropertyName("spatialReference")] SeedReference? SpatialReference);

    /// <summary>An envelope's spatial reference.</summary>
    internal sealed record SeedReference(int? Wkid, int? LatestWkid);

    /// <summary>
    /// The levels a seed of this service covers when the caller names none — the lowest it draws at,
    /// up to <see cref="DefaultSeedCeiling"/> or the highest it draws at.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <returns>The levels, or null when no layer draws at any level.</returns>
    internal static (int Min, int Max)? DefaultSeedLevels(PublishedService service)
    {
        int? first = null;
        int last = 0;

        for (int z = 0; z <= TileAddress.MaxZoom; z++)
        {
            if (service.Layers.Any(layer => layer.VisibleRange.CarriesVectorTile(z)))
            {
                first ??= z;
                last = z;
            }
        }

        return first is { } min ? (min, Math.Max(min, Math.Min(last, DefaultSeedCeiling))) : null;
    }

    /// <summary>
    /// What is cached of a service, level by level, over the area each level was last seeded —
    /// ADR-010 §6b — and the service's seeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The minimum §6b asks for, and it is not a cache browser.</b> For each level some seed has
    /// finished: when, over what area, how many tiles that area holds, and how many of them are cached
    /// and fresh <em>now</em> — for every layer the level draws. The second number falling below the
    /// first is an operator's answer to *the map is showing old data*: the level has expired, or an
    /// edit emptied it, or the cache's budget evicted it.
    /// </para>
    /// <para>
    /// <b>Counted from the cache's own directory, per request.</b> The seed's figures say what it did
    /// when it ran; only the directory says what is there now.
    /// </para>
    /// </remarks>
    private static async Task GetServiceCacheAsync(
        HttpContext context,
        string name,
        string? folder,
        PostgresLayerCatalog owners,
        ITileSeedStore seeds,
        ServiceContexts contexts,
        ITileCache cache,
        GeoParquetSources geoParquet,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (await ReadableSeedServiceAsync(context, owners, name, folder, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        IReadOnlyList<TileSeedZoom> seeded =
            await seeds.LastSeededAsync(service.Id, cancellation).ConfigureAwait(false);

        TimeSpan defaultLifetime = cache is FileSystemTileCache disk ? disk.DefaultLifetime : TimeSpan.FromHours(1);

        List<object> levels = [];

        foreach (TileSeedZoom zoom in seeded)
        {
            TileRange range = TileSeedPlan.RangeOf(zoom.Area, zoom.Zoom);
            PublishedLayer[] drawn = [.. service.Layers.Where(layer => layer.VisibleRange.CarriesVectorTile(zoom.Zoom))];

            long? cached = null;

            if (drawn.Length > 0 && cache is FileSystemTileCache files)
            {
                // A tile is cached when every layer that draws at its level has a fresh part: the
                // route would otherwise build the missing part, which is a miss by another name.
                HashSet<long>? all = null;

                foreach (PublishedLayer layer in drawn)
                {
                    (_, LayerDescription description) =
                        await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

                    TileCacheKey key = VectorTileEndpoints.KeyOf(
                        layer, VectorTileEndpoints.AttributesOf(layer, description),
                        new TileAddress(zoom.Zoom, 0, 0), geoParquet);

                    HashSet<long> fresh = files.FreshIn(key, range, layer.CacheLifetime ?? defaultLifetime);

                    if (all is null)
                    {
                        all = fresh;
                    }
                    else
                    {
                        all.IntersectWith(fresh);
                    }
                }

                cached = all?.Count ?? 0;
            }

            levels.Add(new
            {
                zoom = zoom.Zoom,
                lastSeeded = zoom.Finished,
                seed = zoom.Job,
                area = Wire(zoom.Area),
                tiles = range.Count,

                // Null where no layer draws at the level: there is nothing to cache there, and zero
                // would read as *all of it expired*.
                cached,
            });
        }

        (int Min, int Max)? defaults = DefaultSeedLevels(service);
        IReadOnlyList<TileSeedState> recent = await seeds.ListAsync(service.Id, 10, cancellation).ConfigureAwait(false);
        TileSeedState? running = recent.FirstOrDefault(seed => seed.Job.Status is JobStatus.Queued or JobStatus.Running);

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            pipeline = TilePipeline.Version,
            levels,
            running = running is null ? null : Wire(running, service, DateTimeOffset.UtcNow),
            seeds = recent.Select(seed => Wire(seed, service, DateTimeOffset.UtcNow)),
            defaults = defaults is { } d ? new { minZoom = d.Min, maxZoom = d.Max } : null,
            cap = settings.TileSeedMaximumTiles,
            concurrency = settings.TileSeedConcurrency,
            note = seeded.Count == 0
                ? "No level of this service has been seeded, so every tile is built on its first request. "
                  + "A seed builds them ahead of the callers; after an upgrade that moves the tile pipeline's "
                  + "version the cache starts empty and nothing seeds it by itself (ADR-093 §5.7)."
                : "For each level a seed has finished: how many tiles its area holds and how many of them "
                  + "are in the cache and fresh now, for every layer drawn at that level.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Starts a seed, or with <c>?dryRun=true</c> counts one without starting it.</summary>
    /// <remarks>
    /// <para>
    /// <b>202 and a job, ADR-017's one async pattern.</b> A seed takes minutes to hours and runs on a
    /// job worker (ADR-010 §6); the answer is where to watch it.
    /// </para>
    /// <para>
    /// <b>The count is exact and it is taken before anything starts</b> — ADR-093 §5.2. Over the cap is a
    /// 400 naming the count and the highest level that would fit, because *too large* with no number
    /// cannot be acted on. A second seed while one is queued or running is a 409 naming it.
    /// </para>
    /// <para>
    /// <b>The dry run answers the same count with nothing written</b>, so the console can show what
    /// Start would do before it is pressed. It asks the same permission as the start: counting reads
    /// the extents of the service's layers, and it is the owner's question.
    /// </para>
    /// </remarks>
    private static async Task StartSeedAsync(
        HttpContext context,
        string name,
        string? folder,
        SeedRequest? request,
        PostgresLayerCatalog owners,
        ITileSeedStore seeds,
        ServiceContexts contexts,
        IProjector projector,
        JobSignal signal,
        IAuditLog audit,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishTiles).ConfigureAwait(false))
        {
            return;
        }

        string? at = FolderOf(folder);

        if (await owners.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        // ADR-075: whose service it is. Asked about the service the seed will be recorded against —
        // the one resolved above, by folder and name (D-275).
        if (!await ManagesServiceAsync(
                context, owners, service.Folder, service.Name, "seed the tile cache of", cancellation)
                .ConfigureAwait(false))
        {
            return;
        }

        if (TileSeeder.WhyNotSeedable(service) is { } refusal)
        {
            await Refuse(context, 400, refusal).ConfigureAwait(false);
            return;
        }

        bool dryRun = string.Equals(context.Request.Query["dryRun"], "true", StringComparison.OrdinalIgnoreCase);

        (int Min, int Max)? defaults = DefaultSeedLevels(service);
        int minZoom = request?.MinZoom ?? defaults?.Min ?? 0;
        int maxZoom = request?.MaxZoom ?? (request?.MinZoom is { } asked
            ? Math.Max(asked, defaults?.Max ?? asked)
            : defaults?.Max ?? 0);

        if (minZoom < 0 || maxZoom > TileAddress.MaxZoom || minZoom > maxZoom)
        {
            await Refuse(
                context, 400,
                $"'minZoom' {minZoom} and 'maxZoom' {maxZoom}: a seed covers levels 0 to {TileAddress.MaxZoom}, "
                + "and the first may not be above the last.")
                .ConfigureAwait(false);
            return;
        }

        Envelope area;
        bool whole = request?.Extent is null;

        if (request?.Extent is { } extent)
        {
            int wkid = extent.SpatialReference?.LatestWkid ?? extent.SpatialReference?.Wkid ?? VectorTileEndpoints.WebMercator;

            if (!double.IsFinite(extent.Xmin) || !double.IsFinite(extent.Ymin)
                || !double.IsFinite(extent.Xmax) || !double.IsFinite(extent.Ymax))
            {
                await Refuse(context, 400, "The extent's corners must be numbers.").ConfigureAwait(false);
                return;
            }

            Envelope given = new(extent.Xmin, extent.Ymin, extent.Xmax, extent.Ymax);

            if (wkid is VectorTileEndpoints.WebMercator or 102100 or 102113 or 900913)
            {
                area = given;
            }
            else if (wkid == 4326)
            {
                area = TileSeedPlan.FromGeographic(given);
            }
            else
            {
                await Refuse(
                    context, 400,
                    $"The extent is in {wkid}. A seed's area is given in Web Mercator (3857) — the grid tiles are "
                    + "cut on — or in WGS 84 degrees (4326), or left out for the service's whole extent.")
                    .ConfigureAwait(false);
                return;
            }
        }
        else
        {
            (Envelope? full, string? unknown) =
                await ServiceExtentAsync(service, contexts, projector, cancellation).ConfigureAwait(false);

            if (unknown is not null)
            {
                await Refuse(context, 400, unknown).ConfigureAwait(false);
                return;
            }

            if (full is not { } found)
            {
                await Refuse(
                    context, 400,
                    $"No layer of '{service.QualifiedName}' has any data, so it has no extent to seed. Load the "
                    + "data first, or give an extent.")
                    .ConfigureAwait(false);
                return;
            }

            area = found;
        }

        TileSeedPlan plan;

        try
        {
            plan = TileSeedPlan.For(area, minZoom, maxZoom);
        }
        catch (ArgumentException wrong)
        {
            await Refuse(context, 400, Sentence(wrong)).ConfigureAwait(false);
            return;
        }

        object counted = new
        {
            name = service.Name,
            folder = service.Folder,
            minZoom,
            maxZoom,
            area = Wire(plan.Area),
            whole,
            tiles = plan.Total,
            levels = plan.Levels.Select(level => new
            {
                zoom = level.Z,
                tiles = level.Count,

                // ADR-070: a level no layer draws at is skipped whole, and costs nothing.
                drawn = service.Layers.Any(layer => layer.VisibleRange.CarriesVectorTile(level.Z)),
            }),
            cap = settings.TileSeedMaximumTiles,
            withinCap = plan.Total <= settings.TileSeedMaximumTiles,
            concurrency = settings.TileSeedConcurrency,
        };

        if (plan.Total > settings.TileSeedMaximumTiles)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message = TooManyTiles(plan, settings.TileSeedMaximumTiles),
                        details = TooManyTilesDetail,
                    },
                    tiles = plan.Total,
                    cap = settings.TileSeedMaximumTiles,
                    fits = HighestLevelThatFits(plan, settings.TileSeedMaximumTiles),
                },
                statusCode: 400).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (dryRun)
        {
            await Results.Json(counted).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        TileSeedStart start = await seeds.StartAsync(
            current.Principal.Id,
            new TileSeedRequest(
                service.Id, plan.Area, whole, settings.TileSeedConcurrency,
                [.. plan.Levels.Select(level => (level.Z, level.Count))]),
            $"Seeding the tiles of {service.QualifiedName}, levels {minZoom} to {maxZoom}",

            // What a person should see, and the address the worker resolves the service by. A
            // reference and a size — ADR-011 condition 4 — and nothing that is a payload.
            Detail(new { folder = service.Folder, service = service.Name, minZoom, maxZoom, tiles = plan.Total }),
            cancellation).ConfigureAwait(false);

        if (start.Started is not { } started)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 409,
                        message = $"A seed of '{service.QualifiedName}' is already queued or running ({start.Running}). "
                            + "Wait for it, or cancel it with DELETE on its address, and then start another.",
                        details = Array.Empty<string>(),
                    },
                    running = start.Running,
                    watch = SeedAddress(service, start.Running!.Value),
                },
                statusCode: 409).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        signal.Wake(JobKind.TileSeed);

        await AuditAsync(
            context, audit, "service.cache.seed", service.QualifiedName,
            Detail(new { job = started.Job.Id, minZoom, maxZoom, tiles = plan.Total, whole }),
            succeeded: true, cancellation).ConfigureAwait(false);

        string watch = SeedAddress(service, started.Job.Id);
        context.Response.Headers.Location = watch;

        await Results.Json(
            new
            {
                job = started.Job.Id,
                status = "pending",
                watch,
                jobStatus = $"/admin/jobs/{started.Job.Id}",
                seed = Wire(started, service, DateTimeOffset.UtcNow),
                note = "The seed runs on a job worker, lowest level first, and builds each tile the way a "
                    + "request would, holding a permit against the layer's source as a request does. A tile "
                    + "already cached and fresh is left as it is. The cache's size budget still applies: a "
                    + "seed larger than the budget evicts the tiles it built first.",
            },
            statusCode: 202).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>A service's seeds, newest first.</summary>
    private static async Task ListSeedsAsync(
        HttpContext context,
        string name,
        string? folder,
        PostgresLayerCatalog owners,
        ITileSeedStore seeds,
        CancellationToken cancellation)
    {
        if (await ReadableSeedServiceAsync(context, owners, name, folder, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        IReadOnlyList<TileSeedState> found = await seeds.ListAsync(service.Id, 50, cancellation).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            seeds = found.Select(seed => Wire(seed, service, now)),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One seed: per level how far it has got, and how long the rest should take.</summary>
    private static async Task GetSeedAsync(
        HttpContext context,
        string name,
        Guid id,
        string? folder,
        PostgresLayerCatalog owners,
        ITileSeedStore seeds,
        CancellationToken cancellation)
    {
        if (await ReadableSeedServiceAsync(context, owners, name, folder, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        // <b>A seed of another service is not found here</b>, even when the caller may read that one:
        // the address names a service, and answering for a different one would make the folder in it
        // decoration — which is the confusion D-275 was.
        if (await seeds.FindAsync(id, cancellation).ConfigureAwait(false) is not { } seed || seed.ServiceId != service.Id)
        {
            await Refuse(context, 404, $"No seed '{id}' of '{service.QualifiedName}'.").ConfigureAwait(false);
            return;
        }

        await Results.Json(Wire(seed, service, DateTimeOffset.UtcNow)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Cancels a queued or running seed.</summary>
    /// <remarks>
    /// <b>What it leaves behind is decided, and it is why a seed can be cancelled when an import
    /// cannot</b> — ADR-093 §5.4: every tile it wrote is the tile a request would have written, so the
    /// cache keeps them and a later seed counts them as present. A running seed stops at the tile it
    /// is on when it runs on this server, and at its next checkpoint — two seconds — on another.
    /// </remarks>
    private static async Task CancelSeedAsync(
        HttpContext context,
        string name,
        Guid id,
        string? folder,
        PostgresLayerCatalog owners,
        ITileSeedStore seeds,
        TileSeeder seeder,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishTiles).ConfigureAwait(false))
        {
            return;
        }

        string? at = FolderOf(folder);

        if (await owners.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        if (!await ManagesServiceAsync(
                context, owners, service.Folder, service.Name, "cancel a tile seed of", cancellation)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await seeds.FindAsync(id, cancellation).ConfigureAwait(false) is not { } seed || seed.ServiceId != service.Id)
        {
            await Refuse(context, 404, $"No seed '{id}' of '{service.QualifiedName}'.").ConfigureAwait(false);
            return;
        }

        if (!await seeds.CancelAsync(id, cancellation).ConfigureAwait(false))
        {
            await Refuse(
                context, 409,
                $"The seed '{id}' is {seed.Job.Status.ToString().ToLowerInvariant()}, so there is nothing to cancel.")
                .ConfigureAwait(false);
            return;
        }

        // The store says cancelled first; this makes a seed on this server stop at the tile it is on.
        seeder.Stop(id);

        await AuditAsync(
            context, audit, "service.cache.seed.cancel", service.QualifiedName,
            Detail(new { job = id }), succeeded: true, cancellation).ConfigureAwait(false);

        TileSeedState? now = await seeds.FindAsync(id, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            seed = now is null ? null : Wire(now, service, DateTimeOffset.UtcNow),
            note = "Cancelled. The tiles it built stay in the cache, exactly as a request would have left them; "
                + "a later seed finds them there and counts them as present.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The service a read names, if the caller holds the privilege and may read it.</summary>
    private static async Task<PublishedService?> ReadableSeedServiceAsync(
        HttpContext context, PostgresLayerCatalog owners, string name, string? folder, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishTiles).ConfigureAwait(false))
        {
            return null;
        }

        string? at = FolderOf(folder);

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await owners.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service
            || !LayerAccess.Evaluate(
                service.Sharing, service.Owner, current.Principal, current.Authorization, service.SharedWith).IsAllowed())
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return null;
        }

        return service;
    }

    /// <summary>
    /// A service's extent in Web Mercator — the union of its layers' — or a sentence saying why it
    /// cannot be known.
    /// </summary>
    /// <remarks>
    /// <b>The service document's own computation</b>: each layer's described extent, put in Web
    /// Mercator by <see cref="VectorTileEndpoints.InWebMercatorAsync"/>. A layer with no rows has no
    /// extent and adds nothing; a layer whose extent could not be projected makes the whole unknown,
    /// because seeding the part that could be would leave that layer's area cold without saying so.
    /// </remarks>
    private static async Task<(Envelope? Extent, string? Unknown)> ServiceExtentAsync(
        PublishedService service, ServiceContexts contexts, IProjector projector, CancellationToken cancellation)
    {
        Envelope? union = null;

        foreach (PublishedLayer layer in service.Layers)
        {
            (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            if (described.Extent is not { } extent)
            {
                continue;
            }

            if (await VectorTileEndpoints.InWebMercatorAsync(extent, layer.Definition.Srid, projector, cancellation)
                    .ConfigureAwait(false) is not { } mercator)
            {
                return (null,
                    $"The extent of layer '{layer.Definition.Name}' could not be put in Web Mercator from its own "
                    + $"reference ({layer.Definition.Srid}), so the service's whole extent is not known. Give the "
                    + "seed an extent.");
            }

            union = union is not { } have
                ? mercator
                : new Envelope(
                    Math.Min(have.MinX, mercator.MinX), Math.Min(have.MinY, mercator.MinY),
                    Math.Max(have.MaxX, mercator.MaxX), Math.Max(have.MaxY, mercator.MaxY));
        }

        return (union, null);
    }

    /// <summary>The refusal for a seed over the cap, with the count and how to narrow it.</summary>
    internal static string TooManyTiles(TileSeedPlan plan, long cap)
    {
        int? fits = HighestLevelThatFits(plan, cap);
        int first = plan.Levels[0].Z;
        int last = plan.Levels[^1].Z;

        return $"Levels {first} to {last} over this area are {plan.Total:N0} tiles, and one seed may cover at most "
            + $"{cap:N0} (Graticula:TileSeedMaximumTiles). "
            + (fits is { } top
                ? $"Levels {first} to {top} fit. "
                : $"Level {first} alone is {plan.Levels[0].Count:N0}. ")
            + "Lower the highest level, draw a smaller extent, or seed the rest as a second seed once this one is done.";
    }

    /// <summary>The highest level a seed from the plan's first level could go to and stay within the cap.</summary>
    internal static int? HighestLevelThatFits(TileSeedPlan plan, long cap)
    {
        long sum = 0;
        int? fits = null;

        foreach (TileRange level in plan.Levels)
        {
            sum += level.Count;

            if (sum > cap)
            {
                break;
            }

            fits = level.Z;
        }

        return fits;
    }

    /// <summary>What a refusal over the cap says it is, for a client that branches on it.</summary>
    private static readonly string[] TooManyTilesDetail = ["tooManyTiles"];

    private static string SeedAddress(PublishedService service, Guid job) =>
        $"/admin/services/{Uri.EscapeDataString(service.Name)}/cache/seeds/{job}"
        + (service.Folder is null ? string.Empty : $"?folder={Uri.EscapeDataString(service.Folder)}");

    private static object Wire(Envelope area) => new
    {
        xmin = area.MinX,
        ymin = area.MinY,
        xmax = area.MaxX,
        ymax = area.MaxY,
        spatialReference = new { wkid = 102100, latestWkid = VectorTileEndpoints.WebMercator },
    };

    /// <summary>One seed on the wire.</summary>
    /// <remarks>
    /// <b>In domain terms, as ADR-011 §3.7 asks</b> — *4,120 of 51,400 tiles* beats *8%* — with the
    /// percentage beside it for a progress bar. The estimate is the straight line from the rate so
    /// far, which falls as the seed goes on because the low levels come first and cost the most
    /// (<see cref="TileSeedPlan.Remaining"/>).
    /// </remarks>
    private static object Wire(TileSeedState seed, PublishedService service, DateTimeOffset now)
    {
        long done = seed.Levels.Sum(level => level.Done);
        long skipped = seed.Levels.Sum(level => level.Skipped);

        TimeSpan? remaining = null;

        if (seed.Job.Status == JobStatus.Running
            && seed.Levels.Where(level => level.Started is not null).Select(level => level.Started!.Value)
                .DefaultIfEmpty(DateTimeOffset.MaxValue).Min() is var since && since != DateTimeOffset.MaxValue)
        {
            remaining = TileSeedPlan.Remaining(done - skipped, seed.Total - done, now - since);
        }

        return new
        {
            id = seed.Job.Id,
            status = seed.Job.Status.ToString().ToLowerInvariant(),
            service = service.Name,
            folder = service.Folder,
            minZoom = seed.MinZoom,
            maxZoom = seed.MaxZoom,
            area = Wire(seed.Area),
            whole = seed.Whole,
            concurrency = seed.Concurrency,
            tiles = seed.Total,
            done,
            built = seed.Levels.Sum(level => level.Built),
            present = seed.Levels.Sum(level => level.Present),
            empty = seed.Levels.Sum(level => level.Empty),
            failed = seed.Levels.Sum(level => level.Failed),
            skipped,
            percent = seed.Job.Progress,
            created = seed.Job.Created,
            started = seed.Job.Started,
            finished = seed.Job.Finished,
            failure = seed.Job.Failure,
            pausedUntil = seed.PausedUntil,
            pausedBecause = seed.PausedBecause,
            estimatedRemainingSeconds = remaining is { } left ? (long?)Math.Round(left.TotalSeconds) : null,
            levels = seed.Levels.Select(level => new
            {
                zoom = level.Zoom,
                tiles = level.Total,
                done = level.Done,
                built = level.Built,
                present = level.Present,
                empty = level.Empty,
                failed = level.Failed,
                skipped = level.Skipped,
                started = level.Started,
                finished = level.Finished,
            }),
            watch = SeedAddress(service, seed.Job.Id),
        };
    }
}
