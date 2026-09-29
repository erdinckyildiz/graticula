using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.Tiles;
using Graticula.Geometries;
using Graticula.Platform.Postgres;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// WMTS 1.0.0 over the vector tiles the VectorTileServer face already serves — ADR-097 §5.4: KVP at
/// <c>/wmts</c> and the RESTful binding under <c>/wmts/1.0.0</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside <c>/wfs</c> and <c>/wms</c>, and governed the way they are</b>: the service a request names
/// is a parameter or a path segment, not the route, so each request lists what the caller may see and
/// finds the layer there (<see cref="TileFaces"/>). A layer the caller may not see is refused with the
/// words of one that does not exist.
/// </para>
/// <para>
/// <b>One tile path.</b> GetTile, KVP or RESTful, reads its matrix, row and column into the service's own
/// grid (<see cref="TileMatrixSet.Address"/>) and is served by <c>VectorTileEndpoints.ServeTileAsync</c>, so
/// a WMTS tile is the ArcGIS face's and the OGC face's tile at the same address: one cache entry, the same
/// bytes, the same headers.
/// </para>
/// <para>
/// <b>An empty tile is 204, as it is on the other two faces.</b> WMTS 1.0.0 does not say what a tile with
/// nothing in it is; answering it the way the other faces do keeps one tile one answer, and a 204 is what
/// stops a client retrying the ocean.
/// </para>
/// </remarks>
internal static class WmtsEndpoints
{
    /// <summary>The face's path.</summary>
    public const string Path = TileNames.WmtsPath;

    /// <summary>The link the REST directory's root page offers to this face.</summary>
    /// <returns>The label and the address.</returns>
    public static (string Label, string Href) DirectoryLink() =>
        ("WMTS", Path + "?service=WMTS&request=GetCapabilities");

    /// <summary>Maps the face.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Path, KvpAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Path + WmtsCapabilities.RestCapabilities, RestCapabilitiesAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(
                Path + "/1.0.0/{layer}/{style}/{tileMatrixSet}/{tileMatrix}/{tileRow}/{tileCol}" + WmtsCapabilities.Extension,
                RestTileAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
    }

    private static async Task KvpAsync(
        HttpContext context,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        if (!WmtsRequest.TryParse(Parameter(context), out WmtsRequest? request, out WmtsFault? fault))
        {
            await RefuseAsync(context, fault!).ConfigureAwait(false);
            return;
        }

        if (request!.Operation == WmtsOperation.GetCapabilities)
        {
            await CapabilitiesAsync(context, catalog, contexts, projector, cancellation).ConfigureAwait(false);
            return;
        }

        await TileAsync(context, request, catalog, cancellation).ConfigureAwait(false);
    }

    private static Task RestCapabilitiesAsync(
        HttpContext context, CatalogFallback catalog, ServiceContexts contexts, IProjector projector,
        CancellationToken cancellation) =>
        CapabilitiesAsync(context, catalog, contexts, projector, cancellation);

    private static Task RestTileAsync(
        HttpContext context,
        string layer,
        string style,
        string tileMatrixSet,
        string tileMatrix,
        string tileRow,
        string tileCol,
        CatalogFallback catalog,
        CancellationToken cancellation)
    {
        // The RESTful binding is the KVP request written as a path — 07-057r7 §10.2 — so it is read by the
        // same parser, and cannot accept what KVP refuses.
        Dictionary<string, string> parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SERVICE"] = "WMTS",
            ["REQUEST"] = "GetTile",
            ["VERSION"] = WmtsRequest.Version,
            ["LAYER"] = layer,
            ["STYLE"] = style,
            ["FORMAT"] = TileNames.Mvt,
            ["TILEMATRIXSET"] = tileMatrixSet,
            ["TILEMATRIX"] = tileMatrix,
            ["TILEROW"] = tileRow,
            ["TILECOL"] = tileCol,
        };

        return WmtsRequest.TryParse(n => parameters.GetValueOrDefault(n), out WmtsRequest? request, out WmtsFault? fault)
            ? TileAsync(context, request!, catalog, cancellation)
            : RefuseAsync(context, fault!);
    }

    private static async Task CapabilitiesAsync(
        HttpContext context, CatalogFallback catalog, ServiceContexts contexts, IProjector projector,
        CancellationToken cancellation)
    {
        if (await VisibleOrRefuseAsync(context, catalog, cancellation).ConfigureAwait(false) is not { } visible)
        {
            return;
        }

        IReadOnlyList<TiledService> described = await TileFaces
            .DescribeAllAsync(context, "WMTS GetCapabilities", visible, contexts, projector, cancellation)
            .ConfigureAwait(false);

        byte[] document = WmtsCapabilities.Write(
            Endpoint(context),
            [.. described.Select(d => new WmtsLayer(d.Id, d.Title, d.Description, d.Wgs84, d.Set))]);

        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.Body.WriteAsync(document, cancellation).ConfigureAwait(false);
    }

    private static async Task TileAsync(
        HttpContext context, WmtsRequest request, CatalogFallback catalog, CancellationToken cancellation)
    {
        if (request.StyleOrFormatFault() is { } wrong)
        {
            await RefuseAsync(context, wrong).ConfigureAwait(false);
            return;
        }

        if (await TileFaces.FindAsync(context, catalog, request.Layer, cancellation).ConfigureAwait(false)
            is not { } lookup)
        {
            await UnavailableAsync(context).ConfigureAwait(false);
            return;
        }

        (TileFaces.Lookup outcome, TileFaces.Candidate? found) = lookup;

        if (outcome != TileFaces.Lookup.Found)
        {
            // Absent, forbidden and ambiguous are one answer here: the layer is not one this caller can be
            // served under that identifier.
            await RefuseAsync(
                    context,
                    WmtsFault.Invalid(
                        "LAYER",
                        $"'{request.Layer}' is not a layer this server tiles for you, or it names two services. "
                        + "GetCapabilities lists the layers."))
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(found!.Set.Id, request.TileMatrixSet, StringComparison.Ordinal))
        {
            await RefuseAsync(
                    context,
                    WmtsFault.Invalid(
                        "TILEMATRIXSET",
                        $"'{request.TileMatrixSet}' is not a tile matrix set of '{request.Layer}'; its one set is '{found.Set.Id}'."))
                .ConfigureAwait(false);
            return;
        }

        if (!found.Set.Matrices.Any(m => string.Equals(m.Id, request.TileMatrix, StringComparison.Ordinal)))
        {
            await RefuseAsync(
                    context,
                    WmtsFault.Invalid(
                        "TILEMATRIX",
                        $"'{request.TileMatrix}' is not a tile matrix of '{found.Set.Id}', whose matrices are 0 to {found.Set.Matrices.Count - 1}."))
                .ConfigureAwait(false);
            return;
        }

        if (found.Set.Address(request.TileMatrix, request.TileRow, request.TileCol, out TileAddress address) is { } outside)
        {
            // Row before column in the locator's sense: 07-057r7 Table 28 names the offending parameter.
            TileMatrix matrix = found.Set.Matrices.First(m => string.Equals(m.Id, request.TileMatrix, StringComparison.Ordinal));
            string locator = request.TileRow >= matrix.MatrixHeight ? "TILEROW" : "TILECOL";

            await RefuseAsync(context, new WmtsFault(WmtsFault.TileOutOfRange, locator, outside)).ConfigureAwait(false);
            return;
        }

        await TileFaces.UsingAsync(context, found).ConfigureAwait(false);
        await TileServing.From(context).ServeAsync(context, found.Service, address, cancellation).ConfigureAwait(false);
    }

    private static async Task<TileFaces.Visible?> VisibleOrRefuseAsync(
        HttpContext context, CatalogFallback catalog, CancellationToken cancellation)
    {
        if (await TileFaces.VisibleAsync(context, catalog, cancellation).ConfigureAwait(false) is { } visible)
        {
            return visible;
        }

        await UnavailableAsync(context).ConfigureAwait(false);
        return null;
    }

    private static async Task UnavailableAsync(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "5";

        await WriteAsync(
                context,
                503,
                new WmtsFault(
                    WmtsFault.NoApplicableCode,
                    null,
                    "The catalogue is not reachable and this server has no remembered listing to answer from, so it "
                    + "cannot say which layers it tiles. Retry shortly; see /healthz/ready."))
            .ConfigureAwait(false);
    }

    /// <summary>The KVP endpoint's absolute URL.</summary>
    private static string Endpoint(HttpContext context) => TileFaces.Origin(context) + Path;

    /// <summary>A query parameter, its name matched without case — OWS Common 1.1 §11.5.2.</summary>
    private static Func<string, string?> Parameter(HttpContext context) => name =>
    {
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair in context.Request.Query)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value.ToString();
            }
        }

        return null;
    };

    private static Task RefuseAsync(HttpContext context, WmtsFault fault) => WriteAsync(context, fault.Status, fault);

    private static async Task WriteAsync(HttpContext context, int status, WmtsFault fault)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = WmtsFault.MediaType + "; charset=utf-8";
        await context.Response.Body.WriteAsync(fault.ToXml(), context.RequestAborted).ConfigureAwait(false);
    }
}
