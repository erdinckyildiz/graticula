using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.OgcFeatures;
using Graticula.Api.Tiles;
using Graticula.Geometries;
using Graticula.Platform.Postgres;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// OGC API – Tiles, Part 1, over the vector tiles the VectorTileServer face already serves — ADR-097 §5.1,
/// and TileJSON 3.0.0 as the Web Mercator tileset's alternate, §5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>At <c>/ogc/tiles/v1</c>, beside <c>/ogc/features/v1</c></b>, which ADR-042 §5.1 versioned in the
/// path so this face could sit next to it without either moving. Its own collections, because its unit is a
/// service and the features face's is a layer (<see cref="TileDocuments"/>).
/// </para>
/// <para>
/// <b>A tile here is a tile there.</b> The service is found by <see cref="TileFaces"/>, the address is read
/// by <see cref="TileMatrixSet.Address"/> into the service's own grid, and the tile is served by
/// <c>VectorTileEndpoints.ServeTileAsync</c> — the ArcGIS route's own body — so level <i>z</i>, row
/// <i>r</i>, column <i>c</i> here and <c>tile/z/r/c.pbf</c> there are one cache entry and the same bytes,
/// with the same caching headers, ETag and 304.
/// </para>
/// <para>
/// <b>Refusals are RFC 7807 problems with truthful status codes</b>, as on the features face (ADR-042
/// §5.6): 404 for a collection or tileset the caller cannot see or that does not exist — one answer for
/// both (ADR-018) — 400 for an address outside the grid, 406 for TileJSON of a grid TileJSON cannot
/// describe, 503 while the catalogue cannot be listed.
/// </para>
/// </remarks>
internal static class OgcTilesEndpoints
{
    private const string Root = TileNames.OgcBase;

    /// <summary>Maps the face.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // <b>Filtering, as every standard face is governed</b>: each route lists what the caller may see and
        // finds its id there, so a service the caller may not see is absent from every answer (TileFaces).
        app.MapGet(Root, Landing).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/conformance", Conformance).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections", CollectionsAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections/{collectionId}", CollectionAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections/{collectionId}/tiles", TilesetsAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections/{collectionId}/tiles/{tileMatrixSetId}", TilesetAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}", TileAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/tileMatrixSets", TileMatrixSetsAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/tileMatrixSets/{tileMatrixSetId}", TileMatrixSetAsync).Governed(SharingGovernedExtensions.ByFiltering);
    }

    /// <summary>The link the REST directory's root page offers to this face.</summary>
    /// <returns>The label and the address.</returns>
    public static (string Label, string Href) DirectoryLink() => ("OGC API Tiles", Root);

    /// <summary>The face's absolute base URL.</summary>
    private static string Base(HttpContext context) => TileFaces.Origin(context) + Root;

    private static Task Landing(HttpContext context) =>
        AnswerAsync(context, "OGC API Tiles", TileDocuments.Landing(Base(context)));

    private static Task Conformance(HttpContext context) =>
        AnswerAsync(context, "Conformance", TileDocuments.Conformance());

    private static async Task CollectionsAsync(
        HttpContext context, CatalogFallback catalog, ServiceContexts contexts, IProjector projector,
        CancellationToken cancellation)
    {
        if (await VisibleOrRefuseAsync(context, catalog, cancellation).ConfigureAwait(false) is not { } visible)
        {
            return;
        }

        IReadOnlyList<TiledService> described = await TileFaces
            .DescribeAllAsync(context, "OGC API Tiles /collections", visible, contexts, projector, cancellation)
            .ConfigureAwait(false);

        await AnswerAsync(context, "Collections", TileDocuments.Collections(Base(context), described)).ConfigureAwait(false);
    }

    private static async Task CollectionAsync(
        HttpContext context, string collectionId, CatalogFallback catalog, ServiceContexts contexts,
        IProjector projector, CancellationToken cancellation)
    {
        if (await FindAsync(context, collectionId, catalog, cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        TiledService described = await TileFaces.DescribeAsync(found, contexts, projector, cancellation).ConfigureAwait(false);
        await AnswerAsync(context, described.Title, TileDocuments.Collection(Base(context), described)).ConfigureAwait(false);
    }

    private static async Task TilesetsAsync(
        HttpContext context, string collectionId, CatalogFallback catalog, ServiceContexts contexts,
        IProjector projector, CancellationToken cancellation)
    {
        if (await FindAsync(context, collectionId, catalog, cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        TiledService described = await TileFaces.DescribeAsync(found, contexts, projector, cancellation).ConfigureAwait(false);
        await AnswerAsync(context, "Tilesets", TileDocuments.Tilesets(Base(context), described)).ConfigureAwait(false);
    }

    /// <summary>A tileset's metadata, or its TileJSON when <c>f=tilejson</c>.</summary>
    /// <remarks>
    /// <b>TileJSON is the Web Mercator tileset's alternate representation</b> — 17-083r4 Annex H's example
    /// gives exactly this link — rather than a resource of its own, so it lives where OGC API Tiles already
    /// describes the same tiles and shares its sharing, its caching and its address. Asked of a TUREF
    /// tileset it is a 406 saying why: TileJSON has no way to name another grid.
    /// </remarks>
    private static async Task TilesetAsync(
        HttpContext context, string collectionId, string tileMatrixSetId, CatalogFallback catalog,
        ServiceContexts contexts, IProjector projector, CancellationToken cancellation)
    {
        if (await FindAsync(context, collectionId, catalog, cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        if (!string.Equals(found.Set.Id, tileMatrixSetId, StringComparison.Ordinal))
        {
            await RefuseAsync(context, NoSuchTileset(collectionId, tileMatrixSetId, found)).ConfigureAwait(false);
            return;
        }

        TiledService described = await TileFaces.DescribeAsync(found, contexts, projector, cancellation).ConfigureAwait(false);

        if (string.Equals(context.Request.Query["f"], TileNames.TileJsonFormat, StringComparison.OrdinalIgnoreCase))
        {
            if (TileFaces.TileJsonOf(described, found, Base(context)) is not { } tileJson)
            {
                await RefuseAsync(
                        context,
                        OgcProblem.NotAcceptable(
                            $"`{collectionId}` is tiled on {found.Set.Id} (EPSG:{found.Set.Srid}), and TileJSON describes "
                            + "Web Mercator XYZ tiles only: it has no field for another grid, so a TileJSON client would "
                            + "draw these tiles in the wrong place. Use this tileset's OGC API Tiles metadata (f=json) "
                            + "or the service's VectorTileServer document instead."))
                    .ConfigureAwait(false);
                return;
            }

            await JsonAsync(context, tileJson, TileNames.Json).ConfigureAwait(false);
            return;
        }

        await AnswerAsync(context, "Tileset", TileDocuments.Tileset(Base(context), described)).ConfigureAwait(false);
    }

    /// <summary>One tile.</summary>
    /// <remarks>
    /// <b>The address is checked by the set and then by the grid</b> (<see cref="TileMatrixSet.Address"/>),
    /// before the tile path is asked for anything — a tile outside the grid is the caller's arithmetic and
    /// costs no cache read. 400, not 404: 20-057 <c>/req/core/tc-error</c> allows either, and the ArcGIS
    /// face and WMTS answer the same address 400.
    /// </remarks>
    private static async Task TileAsync(
        HttpContext context,
        string collectionId,
        string tileMatrixSetId,
        string tileMatrix,
        string tileRow,
        string tileCol,
        CatalogFallback catalog,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, collectionId, catalog, cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        if (!string.Equals(found.Set.Id, tileMatrixSetId, StringComparison.Ordinal))
        {
            await RefuseAsync(context, NoSuchTileset(collectionId, tileMatrixSetId, found)).ConfigureAwait(false);
            return;
        }

        if (!long.TryParse(tileRow, NumberStyles.None, CultureInfo.InvariantCulture, out long row)
            || !long.TryParse(tileCol, NumberStyles.None, CultureInfo.InvariantCulture, out long column))
        {
            await RefuseAsync(
                    context,
                    OgcProblem.BadRequest($"`{tileRow}` and `{tileCol}` must be a row and a column: whole numbers from 0."))
                .ConfigureAwait(false);
            return;
        }

        if (found.Set.Address(tileMatrix, row, column, out TileAddress address) is { } outside)
        {
            await RefuseAsync(context, OgcProblem.BadRequest(outside)).ConfigureAwait(false);
            return;
        }

        await TileFaces.UsingAsync(context, found).ConfigureAwait(false);
        await TileServing.From(context).ServeAsync(context, found.Service, address, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The tile matrix sets: <c>WebMercatorQuad</c> and the built-in TUREF grids always, and a custom grid when
    /// a service the caller may see is cut on one.
    /// </summary>
    /// <remarks>
    /// <b>A custom grid is listed only beside a service that uses it</b>, because its numbers are that
    /// service's configuration and nobody else's business; the built-ins are the server's own constants.
    /// </remarks>
    private static async Task TileMatrixSetsAsync(HttpContext context, CatalogFallback catalog, CancellationToken cancellation)
    {
        if (await VisibleOrRefuseAsync(context, catalog, cancellation).ConfigureAwait(false) is not { } visible)
        {
            return;
        }

        await AnswerAsync(context, "Tile matrix sets", TileDocuments.TileMatrixSets(Base(context), SetsOf(visible)))
            .ConfigureAwait(false);
    }

    private static async Task TileMatrixSetAsync(
        HttpContext context, string tileMatrixSetId, CatalogFallback catalog, CancellationToken cancellation)
    {
        // A standing set is answered without the catalogue — it is a constant of the build.
        TileMatrixSet? set = TileMatrixSet.Standing.FirstOrDefault(s => string.Equals(s.Id, tileMatrixSetId, StringComparison.Ordinal));

        if (set is null && tileMatrixSetId.StartsWith(TileMatrixSet.CustomPrefix, StringComparison.Ordinal))
        {
            if (await VisibleOrRefuseAsync(context, catalog, cancellation).ConfigureAwait(false) is not { } visible)
            {
                return;
            }

            set = SetsOf(visible).FirstOrDefault(s => string.Equals(s.Id, tileMatrixSetId, StringComparison.Ordinal));
        }

        if (set is null)
        {
            await RefuseAsync(
                    context,
                    OgcProblem.NotFound($"`{tileMatrixSetId}` is not a tile matrix set this server offers you. See {Root}/tileMatrixSets."))
                .ConfigureAwait(false);
            return;
        }

        await AnswerAsync(context, set.Title, TileDocuments.TileMatrixSetDocument(Base(context), set)).ConfigureAwait(false);
    }

    private static IReadOnlyList<TileMatrixSet> SetsOf(TileFaces.Visible visible) =>
        [.. TileMatrixSet.Standing, .. visible.Services
            .Select(c => c.Set)
            .Where(s => !TileMatrixSet.Standing.Any(t => string.Equals(t.Id, s.Id, StringComparison.Ordinal)))
            .DistinctBy(s => s.Id, StringComparer.Ordinal)];

    private static OgcProblem NoSuchTileset(string collectionId, string tileMatrixSetId, TileFaces.Candidate found) =>
        OgcProblem.NotFound(
            $"`{collectionId}` has no tileset on `{tileMatrixSetId}`. A service is cut on one grid, and this one's is "
            + $"`{found.Set.Id}`: see {Root}/collections/{Uri.EscapeDataString(found.Id)}/tiles.");

    private static async Task<TileFaces.Visible?> VisibleOrRefuseAsync(
        HttpContext context, CatalogFallback catalog, CancellationToken cancellation)
    {
        if (await TileFaces.VisibleAsync(context, catalog, cancellation).ConfigureAwait(false) is { } visible)
        {
            return visible;
        }

        await RefuseAsync(context, Unavailable).ConfigureAwait(false);
        return null;
    }

    /// <summary>The answer while the catalogue can say nothing — not a 404, which would be a claim.</summary>
    private static OgcProblem Unavailable => OgcProblem.Unavailable(
        "The catalogue is not reachable and this server has no remembered answer, so it cannot say which services "
        + "it tiles. Retry shortly; see /healthz/ready.");

    /// <summary>The service an id names, or a refusal written and null.</summary>
    private static async Task<TileFaces.Candidate?> FindAsync(
        HttpContext context, string collectionId, CatalogFallback catalog, CancellationToken cancellation)
    {
        if (await TileFaces.FindAsync(context, catalog, collectionId, cancellation).ConfigureAwait(false)
            is not { } lookup)
        {
            await RefuseAsync(context, Unavailable).ConfigureAwait(false);
            return null;
        }

        (TileFaces.Lookup outcome, TileFaces.Candidate? found) = lookup;

        switch (outcome)
        {
            case TileFaces.Lookup.Found:
                return found;

            case TileFaces.Lookup.Ambiguous:
                await RefuseAsync(
                        context,
                        OgcProblem.NotFound(
                            $"`{collectionId}` names two services — one in the root and one in a folder whose name and "
                            + "the service's make the same id — so neither is served under it. Their VectorTileServer "
                            + "addresses under /rest/services tell them apart."))
                    .ConfigureAwait(false);
                return null;

            default:
                await RefuseAsync(
                        context,
                        OgcProblem.NotFound($"`{collectionId}` is not a collection this server tiles for you. See {Root}/collections."))
                    .ConfigureAwait(false);
                return null;
        }
    }

    /// <summary>A JSON document, or its HTML page when the caller asked for one.</summary>
    private static Task AnswerAsync(HttpContext context, string title, byte[] document)
    {
        if (WantsHtml(context))
        {
            return Results.Content(
                    OgcHtml.Document(context.Request.Path, title, Encoding.UTF8.GetString(document)),
                    "text/html; charset=utf-8")
                .ExecuteAsync(context);
        }

        return JsonAsync(context, document, TileNames.Json);
    }

    private static Task JsonAsync(HttpContext context, byte[] document, string mediaType)
    {
        context.Response.ContentType = mediaType + "; charset=utf-8";
        return context.Response.Body.WriteAsync(document, context.RequestAborted).AsTask();
    }

    /// <summary><c>f</c> wins over <c>Accept</c>, as on the features face.</summary>
    private static bool WantsHtml(HttpContext context)
    {
        string? asked = context.Request.Query["f"];

        if (!string.IsNullOrWhiteSpace(asked))
        {
            return string.Equals(asked, TileNames.HtmlFormat, StringComparison.OrdinalIgnoreCase);
        }

        return RestDirectory.WantsHtml(default, context.Request.Headers.Accept);
    }

    private static Task RefuseAsync(HttpContext context, OgcProblem problem)
    {
        context.Response.StatusCode = problem.Status;
        context.Response.ContentType = OgcNames.Problem;
        return context.Response.WriteAsync(problem.ToJson());
    }
}
