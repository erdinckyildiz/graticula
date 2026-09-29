using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>An origin, as a request gives it.</summary>
/// <param name="X">Easting of the grid's top-left corner.</param>
/// <param name="Y">Northing of the grid's top-left corner.</param>
internal sealed record TilingOrigin(double X, double Y);

/// <summary>
/// The grid a service's vector tiles should be cut on — ADR-096 §5.4. Exactly one of four shapes.
/// </summary>
/// <param name="Scheme">A built-in by name (<c>turef-tm30</c>), or <c>webmercator</c> — or null with
/// nothing else set, which is Web Mercator too.</param>
/// <param name="Wkid">A projected EPSG code: alone, the grid is derived from its area of use; with an
/// origin, it is a custom grid.</param>
/// <param name="Origin">A custom grid's top-left corner.</param>
/// <param name="Level0Resolution">A custom grid's level-zero resolution, halving for <paramref name="Levels"/> levels.</param>
/// <param name="Levels">How many levels a halving custom grid has.</param>
/// <param name="Resolutions">A custom grid's resolutions, coarsest first, instead of a halving pair.</param>
internal sealed record TilingSchemeRequest(
    string? Scheme,
    int? Wkid,
    TilingOrigin? Origin,
    double? Level0Resolution,
    int? Levels,
    double[]? Resolutions);

/// <summary>
/// A vector tile service's tiling scheme — ADR-096.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own route, beside <c>/srid</c> and not folded into it</b> (ADR-096 §5.4). The service's reference
/// (ADR-057 §5c) is the one its queries answer in; this is the grid its tiles are cut on. A service
/// answering in TUREF / TM30 and drawn over a Mercator basemap is the ordinary case, and a service whose
/// tiles moved because somebody changed what its queries answer in would be a surprise nobody asked for.
/// </para>
/// <para>
/// <b>The same privilege, addressing and audit as <c>/srid</c> and <c>/capabilities</c></b>:
/// <c>admin:manageServer</c>, because which grid a service is served on is a serving decision; the
/// service by folder and name with <c>?folder=</c>, absent meaning the root and never *any folder*
/// (D-275); an audit record of every change.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapTilingScheme(WebApplication app)
    {
        app.MapGet("/admin/tiling-schemes", ListTilingSchemesAsync);
        app.MapGet("/admin/services/{name}/tiling", GetServiceTilingAsync);
        app.MapPut("/admin/services/{name}/tiling", SetServiceTilingAsync);
    }

    /// <summary>The schemes this server offers by name, and Web Mercator first.</summary>
    /// <remarks>
    /// <b>For the console's choice and for a script that wants to see a grid before it sets it.</b> The
    /// numbers are the ones a service set to one would carry — its <c>tileInfo</c>, before anything else
    /// about the service is known.
    /// </remarks>
    private static async Task ListTilingSchemesAsync(HttpContext context, CancellationToken cancellation)
    {
        _ = cancellation;

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        await Results.Json(new
        {
            schemes = new[]
            {
                new
                {
                    id = VectorTileScheme.WebMercatorId,
                    title = "Web Mercator (the default)",
                    areaOfUse = (object?)null,
                    grid = WireGrid(VectorTileScheme.WebMercator),
                },
            }.Concat(VectorTileSchemes.BuiltIn.Select(b => new
            {
                id = b.Id,
                title = b.Title,
                areaOfUse = (object?)new
                {
                    xmin = b.AreaOfUse.MinX,
                    ymin = b.AreaOfUse.MinY,
                    xmax = b.AreaOfUse.MaxX,
                    ymax = b.AreaOfUse.MaxY,
                    spatialReference = new { wkid = 4326 },
                },
                grid = WireGrid(b.Scheme),
            })),
            note = "A service is cut on Web Mercator unless it is set to another scheme. A scheme may also be "
                + "given by its numbers — wkid, origin and resolutions — or by a projected wkid alone, which "
                + "derives the grid from that reference's area of use (ADR-096).",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>What a service's tiles are cut on.</summary>
    private static async Task GetServiceTilingAsync(
        HttpContext context,
        string name,
        string? folder,
        PostgresLayerCatalog layers,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        string? at = FolderOf(folder);

        if (await layers.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        // <b>The built-ins laid out in a reference the service's layers are stored in</b>, so the console
        // can offer the one that matches the data first — a TM30 layer's natural grid is TM30.
        int[] stored = [.. service.Layers.Select(l => l.Definition.Srid).Distinct()];

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            scheme = WireGrid(service.TileScheme),
            unreadable = service.TileSchemeUnreadable,
            suggested = VectorTileSchemes.BuiltIn
                .Where(b => stored.Contains(b.Scheme.Srid))
                .Select(b => b.Id),
            layerReferences = stored,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Sets the grid a service's tiles are cut on, or clears it back to Web Mercator.</summary>
    /// <remarks>
    /// <para>
    /// <b>A change empties the service's cached tiles here and cancels its seeds</b> — ADR-096 §5.5. The
    /// cache key carries the grid, so a node this purge does not reach still never serves the other grid's
    /// tiles; the purge is what gives the disk back, and the cancel stops a seed walking rectangles counted
    /// on the grid the service just left. Setting the grid it already has changes nothing and says so.
    /// </para>
    /// <para>
    /// <b>A grid is refused rather than stored when it cannot be served</b>: an unknown reference (the
    /// projector is asked, as the Publish screen asks it), a geographic one, resolutions that do not go from
    /// coarse to fine, a request mixing two shapes. The sentence says which.
    /// </para>
    /// </remarks>
    private static async Task SetServiceTilingAsync(
        HttpContext context,
        string name,
        string? folder,
        TilingSchemeRequest? request,
        IAdminCatalog catalog,
        PostgresLayerCatalog layers,
        IProjector projector,
        ITileCache tiles,
        ITileSeedStore seeds,
        TileSeeder seeder,
        ServerGround ground,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        if (catalog is not PostgresAdminCatalog postgres)
        {
            await Refuse(context, 501, "This catalogue cannot set a service's tiling scheme.").ConfigureAwait(false);
            return;
        }

        string? at = FolderOf(folder);

        if (await layers.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        (VectorTileScheme? wanted, string? refusal) =
            await SchemeFromAsync(request, projector, cancellation).ConfigureAwait(false);

        if (refusal is not null)
        {
            await Refuse(context, 400, refusal).ConfigureAwait(false);
            return;
        }

        VectorTileScheme scheme = wanted!;
        VectorTileScheme had = service.TileScheme;

        // <b>The map ground stays Web Mercator</b> (ServerGround.CheckAsync): a service the ground draws is
        // refused another grid rather than left drawing in the wrong place beneath every map.
        if (!scheme.IsWebMercator
            && (await ground.ReadAsync(cancellation).ConfigureAwait(false)).Services
                .Contains(service.QualifiedName, StringComparer.OrdinalIgnoreCase))
        {
            await Refuse(
                context, 409,
                $"'{service.QualifiedName}' is drawn as the map ground, beneath Web Mercator maps, so it cannot be "
                + "cut on another grid. Take it out of the ground under Server › Settings first.")
                .ConfigureAwait(false);
            return;
        }

        if (service.TileSchemeUnreadable is null && had.Key == scheme.Key)
        {
            await Results.Json(new
            {
                name = service.Name,
                folder = service.Folder,
                scheme = WireGrid(scheme),
                changed = false,
                tilesPurged = 0,
                seedsCancelled = 0,
                note = "The service is already cut on this grid, so nothing changed and no tile was purged.",
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!await postgres
                .SetServiceTilingSchemeAsync(service.Folder, service.Name, scheme.ToJson(), cancellation)
                .ConfigureAwait(false))
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        int purged = 0;

        foreach (PublishedLayer layer in service.Layers)
        {
            purged += tiles.Purge(layer.Id);
        }

        // <b>A seed counted on the old grid is cancelled</b>: its area and levels are the old grid's. The
        // worker would refuse it anyway when it resumed (`TileSeeder.SchemeOf`); cancelling here stops one
        // running on this node at the tile it is on, and says so in the answer.
        int cancelled = 0;

        foreach (TileSeedState seed in await seeds.ListAsync(service.Id, 10, cancellation).ConfigureAwait(false))
        {
            if (seed.Job.Status is JobStatus.Queued or JobStatus.Running
                && await seeds.CancelAsync(seed.Job.Id, cancellation).ConfigureAwait(false))
            {
                seeder.Stop(seed.Job.Id);
                cancelled++;
            }
        }

        await AuditAsync(
            context, audit, "service.tiling", service.QualifiedName,
            Detail(new
            {
                folder = service.Folder,
                from = had.Id,
                fromWkid = had.Srid,
                to = scheme.Id,
                toWkid = scheme.Srid,
                scheme = scheme.Key,
                tilesPurged = purged,
                seedsCancelled = cancelled,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            scheme = WireGrid(scheme),
            changed = true,
            tilesPurged = purged,
            seedsCancelled = cancelled,
            note = scheme.IsWebMercator
                ? "The service is cut on Web Mercator again. Its tiles on the other grid were emptied here, and "
                  + "clients reload the service document to follow."
                : $"The service is cut on EPSG:{scheme.Srid} from now on. Its cached tiles were emptied here and "
                  + "its tile addresses mean the new grid: a client that had loaded the service document "
                  + "before this must load it again. Rolling the server back to a build before ADR-096 serves "
                  + "the service in Web Mercator.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The scheme a request asks for, or the sentence refusing it.
    /// </summary>
    /// <remarks>
    /// <b>Four shapes, and a request that mixes them is refused rather than read one way.</b> A built-in's
    /// name with a custom origin beside it could mean either, and guessing is how a grid ends up somewhere
    /// nobody chose. <b>A wkid alone is derived</b> from the reference's area of use (<c>DomainOfAsync</c>)
    /// by the rule the built-ins were made by — INFERRED from the owner's description of how a TUREF grid
    /// should be found, applied to any projected reference — and stored as numbers, so a later PROJ does not
    /// move it.
    /// </remarks>
    internal static async Task<(VectorTileScheme? Scheme, string? Refusal)> SchemeFromAsync(
        TilingSchemeRequest? request, IProjector projector, CancellationToken cancellation)
    {
        bool named = !string.IsNullOrWhiteSpace(request?.Scheme);
        bool numbered = request?.Origin is not null || request?.Level0Resolution is not null
            || request?.Levels is not null || request?.Resolutions is not null;

        if (request is null || (!named && request.Wkid is null && !numbered))
        {
            return (VectorTileScheme.WebMercator, null);
        }

        if (named)
        {
            if (request.Wkid is not null || numbered)
            {
                return (null, "Name a scheme, or give its numbers — not both. 'scheme' is a built-in's name; "
                    + "'wkid', 'origin' and the resolutions define one of your own.");
            }

            string id = request.Scheme!.Trim();

            if (string.Equals(id, VectorTileScheme.WebMercatorId, StringComparison.OrdinalIgnoreCase))
            {
                return (VectorTileScheme.WebMercator, null);
            }

            return VectorTileSchemes.Find(id) is { } built
                ? (built.Scheme, null)
                : (null, $"There is no built-in tiling scheme '{id}'. The built-ins are "
                    + string.Join(", ", VectorTileSchemes.BuiltIn.Select(b => b.Id))
                    + $", and '{VectorTileScheme.WebMercatorId}' — GET /admin/tiling-schemes lists them with their grids.");
        }

        if (request.Wkid is not { } wkid)
        {
            return (null, "A tiling scheme given by its numbers needs 'wkid', the projected reference it is laid out in.");
        }

        if (wkid is 3857 or 102100 or 102113 or 900913)
        {
            return (VectorTileScheme.WebMercator, numbered
                ? "Web Mercator is the default scheme and is not defined by numbers; send {\"scheme\": \"webmercator\"} or nothing."
                : null);
        }

        if (!await projector.KnowsAsync(wkid, cancellation).ConfigureAwait(false))
        {
            return (null, $"EPSG:{wkid} is not a reference this server can project into, so nothing could be cut on it.");
        }

        if (!numbered)
        {
            // <b>Derived from the reference's own area of use</b>, sampled on a 16-cell grid a side.
            Envelope? degrees = await projector.DomainOfAsync(wkid, cancellation).ConfigureAwait(false);

            if (degrees is null)
            {
                return (null, $"This server cannot say what ground EPSG:{wkid} is meant for (PostGIS 3.4 or later "
                    + "publishes it), so no grid can be derived from it. Give 'origin' and 'level0Resolution' "
                    + "with 'levels', or 'resolutions'.");
            }

            Envelope? area;

            try
            {
                area = await ServedExtent.InAsync(degrees, 4326, wkid, 16, projector, cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                area = null;
            }

            if (area is not { } projected)
            {
                return (null, $"The area EPSG:{wkid} is meant for could not be moved into it, so no grid can be derived.");
            }

            string? wrong = VectorTileScheme.Derive(VectorTileScheme.CustomId, wkid, projected, out VectorTileScheme? derived);

            return wrong is null ? (derived, null) : (null, wrong);
        }

        if (request.Origin is not { } origin)
        {
            return (null, "A custom tiling scheme needs 'origin', the grid's top-left corner, as {\"x\": …, \"y\": …}.");
        }

        double[] resolutions;

        if (request.Resolutions is { Length: > 0 } list)
        {
            if (request.Level0Resolution is not null || request.Levels is not null)
            {
                return (null, "Give 'resolutions', or 'level0Resolution' with 'levels' — not both.");
            }

            resolutions = list;
        }
        else if (request.Level0Resolution is { } first && request.Levels is { } count)
        {
            if (count < 1 || count > VectorTileScheme.MostLevels)
            {
                return (null, $"'levels' is 1 to {VectorTileScheme.MostLevels}.");
            }

            resolutions = VectorTileScheme.Halving(first, count);
        }
        else
        {
            return (null, "A custom tiling scheme needs its resolutions: 'resolutions', coarsest first, or "
                + "'level0Resolution' with 'levels', halving from it.");
        }

        string? invalid = VectorTileScheme.Create(
            VectorTileScheme.CustomId, wkid, origin.X, origin.Y, resolutions, out VectorTileScheme? custom);

        return invalid is null ? (custom, null) : (null, invalid);
    }

    /// <summary>A grid on the wire — the numbers its <c>tileInfo</c> carries.</summary>
    private static object WireGrid(VectorTileScheme scheme)
    {
        Envelope frame = scheme.Frame;

        return new
        {
            id = scheme.Id,
            wkid = scheme.IsWebMercator ? 102100 : scheme.Srid,
            latestWkid = scheme.Srid,
            key = scheme.Key,
            origin = new { x = scheme.OriginX, y = scheme.OriginY },
            tileSize = VectorTileScheme.TileSize,
            levels = scheme.LevelCount,
            frame = new { xmin = frame.MinX, ymin = frame.MinY, xmax = frame.MaxX, ymax = frame.MaxY },
            lods = Enumerable.Range(0, scheme.LevelCount).Select(level => new
            {
                level,
                resolution = scheme.Resolution(level),
                scale = scheme.Scale(level),
            }),
        };
    }
}
