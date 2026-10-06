using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.Wms;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// OGC API – Maps 1.0 at <c>/ogc/maps/v1</c> — ADR-175: the layers WMS draws, as collections, drawn by WMS's own renderer.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second grammar over one renderer.</b> A map request here is read, held to the same limits, and handed to WMS's
/// GetMap as a 1.3.0 request; so a map drawn through this face and one drawn through <c>/wms</c> with the same box, size
/// and reference are the same picture, and a layer's symbology, scale range, time and sharing reach both the same way.
/// The collections are the layers WMS's own capabilities list (<see cref="WmsEndpoints.PublishedAsync"/>), named as OGC
/// API Features names them — the layer's name — and an image service by its WCS coverage id.
/// </para>
/// <para>
/// <b>Reference and box as OGC API states them.</b> <c>bbox</c> is CRS84, longitude first, unless <c>bbox-crs</c> names
/// another, whose own axis order it then follows; <c>crs</c> is the map's reference, CRS84 by default. With no box the map
/// is the collection's whole extent; with no size it is 1024 pixels across, the height following the box's shape.
/// </para>
/// </remarks>
internal static class OgcMapsEndpoints
{
    private const string Root = "/ogc/maps/v1";
    private const string Rel = "http://www.opengis.net/def/rel/ogc/1.0/";
    private const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const int DefaultWidth = 1024;

    private static readonly string[] ConformanceClasses =
    [
        "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/core",

        // <b>The same class in https, for OGC's own test suite</b>, which recognises core only as
        // https://…/conf/core (or a capitalised ogcapi-Maps-1 spelling no document uses) — ADR-175. The standard's
        // URI is the http one above; a client that compares strings finds that, and one that does not loses nothing.
        "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/dataset-map",
        "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collection-map",
        "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/scaling",
        "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/spatial-subsetting",
        "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/crs",
        "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/background",
        "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/png",
        "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/jpeg",
        "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-common-2/1.0/conf/collections",
    ];

    /// <summary>The sixteen W3C basic colour names, which bgcolor accepts in any case.</summary>
    private static readonly Dictionary<string, string> WebColours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "0x000000", ["silver"] = "0xC0C0C0", ["gray"] = "0x808080", ["white"] = "0xFFFFFF",
        ["maroon"] = "0x800000", ["red"] = "0xFF0000", ["purple"] = "0x800080", ["fuchsia"] = "0xFF00FF",
        ["green"] = "0x008000", ["lime"] = "0x00FF00", ["olive"] = "0x808000", ["yellow"] = "0xFFFF00",
        ["navy"] = "0x000080", ["blue"] = "0x0000FF", ["teal"] = "0x008080", ["aqua"] = "0x00FFFF",
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // <b>Filtering, as every standard face is governed</b>: a layer the caller may not see is absent from every answer.
        app.MapGet(Root, LandingAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/conformance", ConformanceAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/api", ApiAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections", CollectionsAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections/{collectionId}", CollectionAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections/{collectionId}/map", CollectionMapAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/map", DatasetMapAsync).Governed(SharingGovernedExtensions.ByFiltering);
    }

    public static (string Label, string Href) DirectoryLink() => ("OGC API Maps", Root);

    private static string Base(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{Root}";

    /// <summary>A collection's id: the layer's name, or an image service's coverage id (<c>hosted__name</c>).</summary>
    private static string IdOf(WmsLayer layer) => layer.Name.Replace("/", "__", StringComparison.Ordinal);

    private static async Task<List<WmsLayer>?> LayersAsync(
        HttpContext context, CatalogFallback catalog, ServiceContexts contexts, IMapCanvasFactory canvases, IProjector projector,
        ICoverageCatalog coverages, CancellationToken cancellation) =>
        await WmsEndpoints.PublishedAsync(context, catalog, contexts, canvases, projector, coverages, cancellation).ConfigureAwait(false);

    private static async Task LandingAsync(
        HttpContext context, CatalogFallback catalog, ServiceContexts contexts, IMapCanvasFactory canvases, IProjector projector,
        ICoverageCatalog coverages, CancellationToken cancellation)
    {
        if (await LayersAsync(context, catalog, contexts, canvases, projector, coverages, cancellation).ConfigureAwait(false) is not { } layers)
        {
            return;
        }

        string root = Base(context);
        JsonObject landing = new()
        {
            ["title"] = "Graticula — OGC API Maps",
            ["description"] = "The layers this server draws, as collections, and maps of them.",
            ["links"] = Links(
                (root, "self", "application/json", "This document"),
                (root + "/api", "service-desc", "application/vnd.oai.openapi+json;version=3.0", "The API definition"),
                (root + "/conformance", Rel + "conformance", "application/json", "Conformance classes"),
                (root + "/collections", Rel + "data", "application/json", "The collections"),
                (root + "/map", Rel + "map", "image/png", "A map of every collection")),
        };

        // The dataset map's extent: every collection's, as the map with no box draws it.
        if (Whole(layers) is { } whole)
        {
            landing["extent"] = Extent(whole);
        }

        await JsonAsync(context, landing).ConfigureAwait(false);
    }

    private static Envelope? Whole(IEnumerable<WmsLayer> layers)
    {
        Envelope? whole = null;

        foreach (WmsLayer layer in layers)
        {
            if (layer.Geographic is { } g)
            {
                whole = whole is { } sofar ? sofar.Union(g) : g;
            }
        }

        return whole;
    }

    private static JsonObject Extent(Envelope box) => new()
    {
        ["spatial"] = new JsonObject
        {
            ["bbox"] = new JsonArray(new JsonArray(box.MinX, box.MinY, box.MaxX, box.MaxY)),
            ["crs"] = Crs84,
        },
    };

    private static Task ConformanceAsync(HttpContext context) =>
        JsonAsync(context, new JsonObject { ["conformsTo"] = new JsonArray([.. ConformanceClasses.Select(c => (JsonNode)c)]) });

    private static async Task CollectionsAsync(
        HttpContext context, CatalogFallback catalog, ServiceContexts contexts, IMapCanvasFactory canvases, IProjector projector,
        ICoverageCatalog coverages, CancellationToken cancellation)
    {
        if (await LayersAsync(context, catalog, contexts, canvases, projector, coverages, cancellation).ConfigureAwait(false) is not { } layers)
        {
            return;
        }

        string root = Base(context);
        await JsonAsync(context, new JsonObject
        {
            ["collections"] = new JsonArray([.. layers.Select(l => (JsonNode)Collection(root, l))]),
            ["links"] = Links((root + "/collections", "self", "application/json", "The collections")),
        }).ConfigureAwait(false);
    }

    private static JsonObject Collection(string root, WmsLayer layer)
    {
        string id = IdOf(layer);
        JsonObject collection = new()
        {
            ["id"] = id,
            ["title"] = layer.Title,
            ["crs"] = new JsonArray([.. new[] { Crs84, CrsUri(layer.Published ?? layer.Srid), CrsUri(3857) }.Distinct().Select(c => (JsonNode)c)]),
            ["links"] = Links(
                ($"{root}/collections/{Uri.EscapeDataString(id)}", "self", "application/json", layer.Title),
                ($"{root}/collections/{Uri.EscapeDataString(id)}/map", Rel + "map", "image/png", "A map of " + layer.Title),
                ($"{root}/collections/{Uri.EscapeDataString(id)}/map?f=jpeg", Rel + "map", "image/jpeg", "A map of " + layer.Title)),
        };

        if (layer.Abstract is { Length: > 0 } description)
        {
            collection["description"] = description;
        }

        if (layer.Geographic is { } box)
        {
            collection["extent"] = Extent(box);
        }

        return collection;
    }

    private static async Task CollectionAsync(
        HttpContext context, string collectionId, CatalogFallback catalog, ServiceContexts contexts, IMapCanvasFactory canvases,
        IProjector projector, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        if (await LayersAsync(context, catalog, contexts, canvases, projector, coverages, cancellation).ConfigureAwait(false) is not { } layers)
        {
            return;
        }

        if (layers.FirstOrDefault(l => IdOf(l) == collectionId) is not { } layer)
        {
            await ProblemAsync(context, 404, "Not found", $"There is no collection '{collectionId}' you may see.").ConfigureAwait(false);
            return;
        }

        await JsonAsync(context, Collection(Base(context), layer)).ConfigureAwait(false);
    }

    private static async Task CollectionMapAsync(
        HttpContext context, string collectionId, CatalogFallback catalog, ServiceContexts contexts, IMapCanvasFactory canvases,
        IProjector projector, ICoverageCatalog coverages, ICoverageReaderFactory readers, HostSettings settings,
        CancellationToken cancellation)
    {
        if (await LayersAsync(context, catalog, contexts, canvases, projector, coverages, cancellation).ConfigureAwait(false) is not { } layers)
        {
            return;
        }

        if (layers.FirstOrDefault(l => IdOf(l) == collectionId) is not { } layer)
        {
            await ProblemAsync(context, 404, "Not found", $"There is no collection '{collectionId}' you may see.").ConfigureAwait(false);
            return;
        }

        await DrawAsync(context, [layer], catalog, contexts, canvases, projector, coverages, readers, settings, cancellation).ConfigureAwait(false);
    }

    private static async Task DatasetMapAsync(
        HttpContext context, CatalogFallback catalog, ServiceContexts contexts, IMapCanvasFactory canvases,
        IProjector projector, ICoverageCatalog coverages, ICoverageReaderFactory readers, HostSettings settings,
        CancellationToken cancellation)
    {
        if (await LayersAsync(context, catalog, contexts, canvases, projector, coverages, cancellation).ConfigureAwait(false) is not { } layers)
        {
            return;
        }

        // `collections` chooses and orders; without it every collection is drawn, the first at the bottom.
        if (Query(context, "collections") is { } chosen)
        {
            List<WmsLayer> picked = [];

            foreach (string named in chosen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string id = named.LastIndexOf("/collections/", StringComparison.Ordinal) is int at and >= 0
                    ? Uri.UnescapeDataString(named[(at + "/collections/".Length)..].TrimEnd('/'))
                    : named;

                if (layers.FirstOrDefault(l => IdOf(l) == id) is not { } layer)
                {
                    await ProblemAsync(context, 400, "Bad request", $"There is no collection '{id}' you may see.").ConfigureAwait(false);
                    return;
                }

                picked.Add(layer);
            }

            layers = picked;
        }

        if (layers.Count == 0)
        {
            await ProblemAsync(context, 404, "Not found", "There is nothing you may see to draw.").ConfigureAwait(false);
            return;
        }

        await DrawAsync(context, layers, catalog, contexts, canvases, projector, coverages, readers, settings, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a map request and draws it with WMS's GetMap: the reference, the box in it, the size, the format and the
    /// background, each refused here in OGC API's terms before WMS sees it.
    /// </summary>
    private static async Task DrawAsync(
        HttpContext context, IReadOnlyList<WmsLayer> layers, CatalogFallback catalog, ServiceContexts contexts, IMapCanvasFactory canvases,
        IProjector projector, ICoverageCatalog coverages, ICoverageReaderFactory readers, HostSettings settings, CancellationToken cancellation)
    {
        // The map's reference.
        if (SridOf(Query(context, "crs")) is not { } srid || !await projector.KnowsAsync(srid, cancellation).ConfigureAwait(false))
        {
            await ProblemAsync(context, 400, "Bad request", $"'{Query(context, "crs")}' is not a reference this server draws in. Name one as {Crs84} or http://www.opengis.net/def/crs/EPSG/0/<code>.")
                .ConfigureAwait(false);
            return;
        }

        bool crs84 = IsCrs84(Query(context, "crs"));

        // The box, in its own reference and that reference's axis order, then moved into the map's.
        Envelope? box;

        if (Query(context, "bbox") is { } text)
        {
            double[] numbers = [.. text.Split(',', StringSplitOptions.TrimEntries)
                .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN)];

            if (numbers.Length != 4 || numbers.Any(double.IsNaN))
            {
                await ProblemAsync(context, 400, "Bad request", $"'{text}' is not four numbers: a box is minx,miny,maxx,maxy.").ConfigureAwait(false);
                return;
            }

            string? boxCrs = Query(context, "bbox-crs");

            if (SridOf(boxCrs) is not { } boxSrid || !await projector.KnowsAsync(boxSrid, cancellation).ConfigureAwait(false))
            {
                await ProblemAsync(context, 400, "Bad request", $"'{boxCrs}' is not a reference this server reads.").ConfigureAwait(false);
                return;
            }

            // In the box's reference's own order: CRS84 is longitude first, EPSG:4326 latitude first.
            bool latitudeFirst = !IsCrs84(boxCrs) && AxisOrder.IsLatitudeFirst(boxSrid);
            Envelope given = latitudeFirst
                ? new Envelope(numbers[1], numbers[0], numbers[3], numbers[2])
                : new Envelope(numbers[0], numbers[1], numbers[2], numbers[3]);

            // On the numbers as written: an Envelope orders its corners itself, and would quietly draw 3,2,1,0 as
            // 1,0,3,2. A first longitude above the second is OGC's way of crossing the antimeridian, which this
            // server does not draw, so it is refused rather than drawn the wrong way round the world.
            if (numbers[0] >= numbers[2] || numbers[1] >= numbers[3])
            {
                await ProblemAsync(context, 400, "Bad request",
                    $"'{text}' has a minimum at or above its maximum. A box crossing the antimeridian is not drawn; ask for each side of it.")
                    .ConfigureAwait(false);
                return;
            }

            box = boxSrid == srid ? given : await MoveAsync(projector, given, boxSrid, srid, cancellation).ConfigureAwait(false);
        }
        else
        {
            Envelope? whole = Whole(layers);
            box = whole is null ? null : srid == 4326 ? whole : await MoveAsync(projector, whole.Value, 4326, srid, cancellation).ConfigureAwait(false);
        }

        if (box is not { } extent || extent.Width <= 0 || extent.Height <= 0)
        {
            await ProblemAsync(context, 400, "Bad request", "The map has no box: name one with bbox, or ask for a collection that has an extent.")
                .ConfigureAwait(false);
            return;
        }

        // The size: what was asked, the other side following the box's shape.
        int? width = PositiveInt(Query(context, "width")), height = PositiveInt(Query(context, "height"));

        if ((Query(context, "width") is not null && width is null) || (Query(context, "height") is not null && height is null))
        {
            await ProblemAsync(context, 400, "Bad request", "width and height are whole numbers above zero.").ConfigureAwait(false);
            return;
        }

        double aspect = extent.Width / extent.Height;
        (int w, int h) = (width, height) switch
        {
            ({ } ww, { } hh) => (ww, hh),
            ({ } ww, null) => (ww, Math.Max(1, (int)Math.Round(ww / aspect))),
            (null, { } hh) => (Math.Max(1, (int)Math.Round(hh * aspect)), hh),
            _ => aspect >= 1 ? (DefaultWidth, Math.Max(1, (int)Math.Round(DefaultWidth / aspect))) : (Math.Max(1, (int)Math.Round(DefaultWidth * aspect)), DefaultWidth),
        };

        if (w > settings.MaximumImageWidth || h > settings.MaximumImageHeight)
        {
            await ProblemAsync(context, 400, "Bad request",
                $"{w} × {h} pixels is more than this server draws: at most {settings.MaximumImageWidth} × {settings.MaximumImageHeight}.").ConfigureAwait(false);
            return;
        }

        // The format: f, then Accept, then PNG.
        string? f = Query(context, "f")?.ToLowerInvariant();
        string accept = context.Request.Headers.Accept.ToString();
        string format = f switch
        {
            "png" or "image/png" => "image/png",
            "jpeg" or "jpg" or "image/jpeg" => "image/jpeg",
            null => accept.Contains("image/jpeg", StringComparison.OrdinalIgnoreCase) && !accept.Contains("image/png", StringComparison.OrdinalIgnoreCase)
                ? "image/jpeg" : "image/png",
            _ => "",
        };

        if (format.Length == 0)
        {
            await ProblemAsync(context, 400, "Bad request", $"'{f}' is not a format this server draws: png or jpeg.").ConfigureAwait(false);
            return;
        }

        // The background: a colour, and whether the empty part is transparent — PNG's default here, as WMS's is not.
        string? background = Query(context, "bgcolor");
        string transparent = Query(context, "transparent")?.ToLowerInvariant() switch
        {
            "false" => "FALSE",
            "true" => "TRUE",
            null => format == "image/png" && background is null ? "TRUE" : "FALSE",
            _ => "?",
        };

        if (transparent == "?")
        {
            await ProblemAsync(context, 400, "Bad request", "transparent is true or false.").ConfigureAwait(false);
            return;
        }

        if (background is not null && Hex(background) is null)
        {
            await ProblemAsync(context, 400, "Bad request", $"'{background}' is not a colour: give it as 0xRRGGBB, RRGGBB or one of the sixteen W3C names.")
                .ConfigureAwait(false);
            return;
        }

        bool latitudeFirstOut = !crs84 && AxisOrder.IsLatitudeFirst(srid);
        string Coordinates(Envelope e) => latitudeFirstOut
            ? string.Create(CultureInfo.InvariantCulture, $"{e.MinY},{e.MinX},{e.MaxY},{e.MaxX}")
            : string.Create(CultureInfo.InvariantCulture, $"{e.MinX},{e.MinY},{e.MaxX},{e.MaxY}");

        Dictionary<string, string> wms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SERVICE"] = "WMS",
            ["VERSION"] = "1.3.0",
            ["REQUEST"] = "GetMap",
            ["LAYERS"] = string.Join(',', layers.Select(l => l.Name)),
            ["STYLES"] = string.Empty,
            ["CRS"] = crs84 ? "CRS:84" : $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}",
            ["BBOX"] = Coordinates(extent),
            ["WIDTH"] = w.ToString(CultureInfo.InvariantCulture),
            ["HEIGHT"] = h.ToString(CultureInfo.InvariantCulture),
            ["FORMAT"] = format,
            ["TRANSPARENT"] = transparent,
        };

        if (background is not null)
        {
            wms["BGCOLOR"] = Hex(background)!;
        }

        if (!WmsRequest.TryParse(n => wms.GetValueOrDefault(n), WmsEndpoints.LimitsFor(settings), out WmsRequest? request, out WmsFault? fault))
        {
            await ProblemAsync(context, 400, "Bad request", fault?.Message ?? "The map could not be asked for.").ConfigureAwait(false);
            return;
        }

        // OGC API Maps' response headers: the reference and the box the picture is in.
        // The URI bare, not in Features Part 2's angle brackets: Maps' requirement is "the URI or a safe CURIE", and
        // OGC's suite compares the header to the URI as written.
        context.Response.Headers["Content-Crs"] = crs84 ? Crs84 : CrsUri(srid);
        context.Response.Headers["Content-Bbox"] = Coordinates(extent);
        await WmsEndpoints.MapImageAsync(context, catalog, contexts, canvases, request!, settings, coverages, readers, projector, cancellation)
            .ConfigureAwait(false);
    }

    private static string? Query(HttpContext context, string name) =>
        context.Request.Query.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } pair
        && pair.Value.ToString() is { Length: > 0 } value ? value : null;

    private static int? PositiveInt(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;

    private static bool IsCrs84(string? named) =>
        named is null || named is Crs84 or "[OGC:CRS84]" or "urn:ogc:def:crs:OGC:1.3:CRS84" or "CRS:84" or "OGC:CRS84";

    /// <summary>An OGC CRS URI, a safe CURIE (<c>[EPSG:3857]</c>), a URN or <c>EPSG:n</c>, as a code; CRS84 is 4326.</summary>
    private static int? SridOf(string? named)
    {
        if (IsCrs84(named))
        {
            return 4326;
        }

        string trimmed = named!.Trim('[', ']');
        return int.TryParse(trimmed[(trimmed.LastIndexOfAny(['/', ':']) + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int srid) && srid > 0
            ? srid
            : null;
    }

    private static string CrsUri(int srid) => $"http://www.opengis.net/def/crs/EPSG/0/{srid.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A colour as WMS's BGCOLOR spells it, or null when it is not one.</summary>
    private static string? Hex(string colour)
    {
        if (WebColours.TryGetValue(colour, out string? named))
        {
            return named;
        }

        string digits = colour.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? colour[2..] : colour.TrimStart('#');
        return digits.Length == 6 && digits.All(Uri.IsHexDigit) ? "0x" + digits.ToUpperInvariant() : null;
    }

    private static async Task<Envelope?> MoveAsync(IProjector projector, Envelope box, int from, int to, CancellationToken cancellation)
    {
        try
        {
            (IReadOnlyList<Geometry> corners, _) = await projector.ProjectAsync(
                [new Point(box.MinX, box.MinY), new Point(box.MaxX, box.MinY), new Point(box.MaxX, box.MaxY), new Point(box.MinX, box.MaxY)],
                from, to, cancellation).ConfigureAwait(false);
            Envelope whole = Envelope.Empty;

            foreach (Geometry corner in corners)
            {
                whole = whole.IsEmpty ? corner.Envelope : whole.Union(corner.Envelope);
            }

            return whole.IsEmpty ? null : whole;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static Task ApiAsync(HttpContext context)
    {
        string root = Base(context);
        static JsonObject Op(string summary, string id, params string[] parameters)
        {
            JsonObject op = new()
            {
                ["summary"] = summary,
                ["operationId"] = id,
                ["responses"] = new JsonObject { ["200"] = new JsonObject { ["description"] = "Success" }, ["default"] = new JsonObject { ["description"] = "A problem (RFC 7807)" } },
            };

            if (parameters.Length > 0)
            {
                op["parameters"] = new JsonArray([.. parameters.Select(p => (JsonNode)new JsonObject
                {
                    ["name"] = p,
                    ["in"] = p == "collectionId" ? "path" : "query",
                    ["required"] = p == "collectionId",
                    ["schema"] = new JsonObject { ["type"] = p is "width" or "height" ? "integer" : "string" },
                })]);
            }

            return op;
        }

        string[] map = ["bbox", "bbox-crs", "crs", "width", "height", "bgcolor", "transparent", "f"];

        return JsonAsync(context, new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = "Graticula — OGC API Maps", ["version"] = "1.0.0" },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = root }),
            ["paths"] = new JsonObject
            {
                ["/"] = new JsonObject { ["get"] = Op("Landing page", "getLandingPage") },
                ["/conformance"] = new JsonObject { ["get"] = Op("Conformance classes", "getConformanceClasses") },
                ["/api"] = new JsonObject { ["get"] = Op("This definition", "getAPI") },
                ["/collections"] = new JsonObject { ["get"] = Op("The collections", "getCollections") },
                ["/collections/{collectionId}"] = new JsonObject { ["get"] = Op("A collection", "getCollection", "collectionId") },
                ["/collections/{collectionId}/map"] = new JsonObject { ["get"] = Op("A map of a collection", "getCollectionMap", ["collectionId", .. map]) },
                ["/map"] = new JsonObject { ["get"] = Op("A map of the collections", "getDatasetMap", ["collections", .. map]) },
            },
        }, media: "application/vnd.oai.openapi+json;version=3.0");
    }

    private static JsonArray Links(params (string Href, string Rel, string Type, string Title)[] links) =>
        new([.. links.Select(l => (JsonNode)new JsonObject { ["href"] = l.Href, ["rel"] = l.Rel, ["type"] = l.Type, ["title"] = l.Title })]);

    private static async Task JsonAsync(HttpContext context, JsonNode document, int status = 200, string media = "application/json")
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = media;
        await context.Response.WriteAsync(document.ToJsonString(Indented), context.RequestAborted).ConfigureAwait(false);
    }

    private static Task ProblemAsync(HttpContext context, int status, string title, string detail) =>
        JsonAsync(context, new JsonObject { ["title"] = title, ["status"] = status, ["detail"] = detail }, status, "application/problem+json");
}
