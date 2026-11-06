using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// A scene layer package served as an ArcGIS <c>SceneServer</c> — ADR-269 §5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>At <c>/rest/services/hosted/{item id}/SceneServer</c>.</b> The package is a document (ADR-188) whose kind is a
/// scene layer package, so its owner, sharing, groups, protection and recycle bin are a document's, and the service's
/// address is its item's. Its I3S resources are served from the package as the I3S REST paths name them: the
/// specification's rule is that a package entry is the REST path below <c>layers/0</c> plus an extension, with
/// <c>3dSceneLayer.json.gz</c> as the layer document.
/// </para>
/// <para>
/// <b>Gzipped entries go out as they are</b> when the client accepts gzip, with <c>Content-Encoding: gzip</c> and the
/// inner type, and are inflated for one that does not. The specification says nothing about HTTP; this is what a
/// static I3S host does, and it means a node's geometry costs a seek and a copy.
/// </para>
/// <para>
/// <b>The service document is this server's</b>: I3S 1.7 and later do not define <c>…/SceneServer</c>, and 1.6's
/// <c>SceneServiceInfo</c> names five fields. It carries those, the layer documents with their <c>href</c>, and the
/// item and version fields an ArcGIS client reads from a service.
/// </para>
/// </remarks>
internal static class SceneServerEndpoints
{
    private const string Root = "/rest/services/hosted/{id}/SceneServer";

    /// <summary>Maps the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        app.MapGet(Root, ServiceAsync).Governed(SharingGovernedExtensions.ByService);
        app.MapGet(Root + "/layers", LayersAsync).Governed(SharingGovernedExtensions.ByService);
        app.MapGet(Root + "/layers/{layer:int}", LayerAsync).Governed(SharingGovernedExtensions.ByService);
        app.MapGet(Root + "/layers/{layer:int}/{**path}", ResourceAsync).Governed(SharingGovernedExtensions.ByService);
    }

    /// <summary>The address a package's scene service answers at, on this request's host.</summary>
    /// <param name="request">The request.</param>
    /// <param name="id">The document's id.</param>
    /// <returns>The absolute <c>…/SceneServer</c> address.</returns>
    public static string AddressOf(HttpRequest request, string id) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}/rest/services/hosted/{id}/SceneServer";

    private static async Task<(ScenePackages.Package? Package, Document? Document, IResult? Refusal)> OpenAsync(
        HttpContext context, string id, CancellationToken cancellation)
    {
        string lower = (id ?? string.Empty).ToLowerInvariant();
        IDocumentStore? documents = context.RequestServices.GetService<IDocumentStore>();
        ScenePackages? packages = context.RequestServices.GetService<ScenePackages>();

        Document? document = WebMaps.IsId(lower) && documents is not null
            ? await documents.FindAsync(lower, cancellation).ConfigureAwait(false)
            : null;

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (document is null || !Documents.IsScenePackage(document.Kind) || documents is null || packages is null
            || !LayerAccess.Evaluate(document.Sharing, document.Owner, current.Principal, current.Authorization, document.SharedWith).IsAllowed())
        {
            // The same answer for a package that is private and one that does not exist (TokenRequired).
            return (null, null, TokenRequired.Applies(context) ? TokenRequired.Result(context) : NotFound());
        }

        ScenePackages.Package? package = await packages!.OpenAsync(document!, documents!, cancellation).ConfigureAwait(false);

        return package is null
            ? (null, null, Results.Json(new { error = new { code = 500, message = "The scene layer package could not be read as a zip.", details = Array.Empty<string>() } }, statusCode: 500))
            : (package, document, null);
    }

    private static IResult NotFound() =>
        Results.Json(new { error = new { code = 404, message = "Service not found.", details = Array.Empty<string>() } }, statusCode: 404);

    /// <summary>The layer document, parsed, with its <c>href</c> as the service document lists it.</summary>
    private static async Task<JsonObject?> LayerDocumentAsync(ScenePackages.Package package, CancellationToken cancellation)
    {
        string? name = package.Find("3dscenelayer.json.gz", "3dscenelayer.json");
        if (name is null)
        {
            return null;
        }

        byte[] bytes = Plain(name, await package.ReadAsync(name, cancellation).ConfigureAwait(false));
        return JsonNode.Parse(bytes) as JsonObject;
    }

    /// <summary>
    /// A scene layer package's extent in WGS 84 — its layer document's <c>fullExtent</c>, in the reference that names, or
    /// its <c>store.extent</c>, which I3S writes in degrees — or empty when it says neither (ADR-271).
    /// </summary>
    internal static async Task<double[][]> PackageExtentAsync(
        HttpContext context, Document document, Graticula.Geometries.IProjector projector, CancellationToken cancellation)
    {
        IDocumentStore? documents = context.RequestServices.GetService<IDocumentStore>();
        ScenePackages? packages = context.RequestServices.GetService<ScenePackages>();

        if (documents is null || packages is null
            || await packages.OpenAsync(document, documents, cancellation).ConfigureAwait(false) is not { } package
            || await LayerDocumentAsync(package, cancellation).ConfigureAwait(false) is not { } layer)
        {
            return [];
        }

        static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double read) && double.IsFinite(read) ? read : null;

        int srid = 4326;
        double?[] corners;

        if (layer["fullExtent"] is JsonObject full)
        {
            corners = [Number(full["xmin"]), Number(full["ymin"]), Number(full["xmax"]), Number(full["ymax"])];
            JsonNode? reference = full["spatialReference"];
            srid = (int)(Number(reference?["latestWkid"]) ?? Number(reference?["wkid"]) ?? 4326);
        }
        else if (layer["store"]?["extent"] is JsonArray box && box.Count == 4)
        {
            corners = [Number(box[0]), Number(box[1]), Number(box[2]), Number(box[3])];
        }
        else
        {
            return [];
        }

        if (corners.Any(c => c is null) || corners[0] > corners[2] || corners[1] > corners[3])
        {
            return [];
        }

        IReadOnlyList<Graticula.Geometries.Envelope?> geographic = await Graticula.Geometries.GeographicExtents
            .InWgs84Async(projector, [(srid == 102100 ? 3857 : srid, new Graticula.Geometries.Envelope(corners[0]!.Value, corners[1]!.Value, corners[2]!.Value, corners[3]!.Value))], cancellation)
            .ConfigureAwait(false);

        return geographic is [{ } wgs] ? [[wgs.MinX, wgs.MinY], [wgs.MaxX, wgs.MaxY]] : [];
    }

    private static async Task<IResult> ServiceAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        (ScenePackages.Package? package, Document? document, IResult? refusal) = await OpenAsync(context, id, cancellation).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        JsonObject? layer = await LayerDocumentAsync(package!, cancellation).ConfigureAwait(false);
        if (layer is null)
        {
            return Results.Json(new { error = new { code = 500, message = "The package has no layer document (3dSceneLayer.json.gz).", details = Array.Empty<string>() } }, statusCode: 500);
        }

        layer["href"] = $"./layers/{LayerId(layer)}";
        JsonObject service = new()
        {
            ["serviceItemId"] = document!.Id,
            ["serviceName"] = document.Title,
            ["name"] = document.Title,
            ["currentVersion"] = Graticula.Api.ArcGis.FeatureServerMetadataWriter.CurrentVersion,
            // The I3S version is the store's; the layer's own `version` is an identifier of this version of the layer.
            ["serviceVersion"] = layer["store"]?["version"] is JsonValue stored && stored.TryGetValue(out string? i3s) ? i3s : "1.7",
            ["supportedBindings"] = new JsonArray("REST"),
            ["supportedOperations"] = new JsonArray("Base"),
            ["layers"] = new JsonArray(layer),
        };

        return Results.Text(service.ToJsonString(), "application/json; charset=utf-8");
    }

    private static async Task<IResult> LayersAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        (ScenePackages.Package? package, _, IResult? refusal) = await OpenAsync(context, id, cancellation).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        JsonObject? layer = await LayerDocumentAsync(package!, cancellation).ConfigureAwait(false);
        if (layer is null)
        {
            return NotFound();
        }

        layer["href"] = $"./{LayerId(layer)}";
        return Results.Text(new JsonObject { ["layers"] = new JsonArray(layer) }.ToJsonString(), "application/json; charset=utf-8");
    }

    private static async Task<IResult> LayerAsync(HttpContext context, string id, int layer, CancellationToken cancellation)
    {
        (ScenePackages.Package? package, _, IResult? refusal) = await OpenAsync(context, id, cancellation).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        string? name = await IsTheLayerAsync(package!, layer, cancellation).ConfigureAwait(false)
            ? package!.Find("3dscenelayer.json.gz", "3dscenelayer.json") : null;
        return name is null ? NotFound() : await EntryAsync(context, package!, name, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The layer document's own id. I3S says a client must not assume a building scene layer's is 0 (its *layer.bld*),
    /// and the service answers at the id the document gives; 0 is also answered, as it always was.
    /// </summary>
    private static int LayerId(JsonObject layer) =>
        layer["id"] is JsonValue id && id.TryGetValue(out int value) ? value : 0;

    private static async Task<bool> IsTheLayerAsync(ScenePackages.Package package, int layer, CancellationToken cancellation) =>
        layer == 0 || (await LayerDocumentAsync(package, cancellation).ConfigureAwait(false) is { } document && LayerId(document) == layer);

    private static async Task<IResult> ResourceAsync(HttpContext context, string id, int layer, string path, CancellationToken cancellation)
    {
        (ScenePackages.Package? package, _, IResult? refusal) = await OpenAsync(context, id, cancellation).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        string below = (path ?? string.Empty).Trim('/');
        if (below.Length == 0 || below.Contains("..", StringComparison.Ordinal)
            || !await IsTheLayerAsync(package!, layer, cancellation).ConfigureAwait(false))
        {
            return NotFound();
        }

        string? name = EntryFor(below, package!.Find, package!.FindStem);

        return name is null ? NotFound() : await EntryAsync(context, package!, name, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The package entry an I3S REST path below <c>layers/{id}</c> names, or null — one rule for every profile.
    /// </summary>
    /// <remarks>
    /// A building scene layer's sublayer is its own scene layer under <c>sublayers/{id}</c> (I3S layer.bld, ADR-270 §5.6):
    /// its document is <c>sublayers/{id}/3dSceneLayer.json</c>, and its resources follow the same rules below it. The
    /// legacy 1.6 node document is <c>nodes/{id}</c> itself, and a node's shared resource <c>nodes/{id}/shared</c>, at any
    /// depth; every other resource is its path plus an extension — one of those named, or failing them, any (a point
    /// cloud's <c>.bin.pccxyz</c>, a voxel layer's).
    /// </remarks>
    /// <param name="below">The path below the layer, without leading or trailing slashes.</param>
    /// <param name="find">The package's lookup of exact names, first found wins.</param>
    /// <param name="stem">The package's lookup by a path without its extension.</param>
    internal static string? EntryFor(string below, Func<string[], string?> find, Func<string, string?> stem)
    {
        string[] parts = below.Split('/');
        int n = parts.Length;

        return n == 2 && parts[0].Equals("sublayers", StringComparison.OrdinalIgnoreCase)
            ? find([$"{below}/3dscenelayer.json.gz", $"{below}/3dscenelayer.json"])
            : n >= 2 && parts[n - 2].Equals("nodes", StringComparison.OrdinalIgnoreCase)
                ? find([$"{below}/3dnodeindexdocument.json.gz", $"{below}/3dnodeindexdocument.json"])
                : n >= 3 && parts[n - 3].Equals("nodes", StringComparison.OrdinalIgnoreCase) && parts[n - 1].Equals("shared", StringComparison.OrdinalIgnoreCase)
                    ? find([$"{below}/sharedresource.json.gz", $"{below}/sharedresource.json"])
                    : find([$"{below}.json.gz", $"{below}.bin.gz", $"{below}.json", $"{below}.bin", $"{below}.jpg", $"{below}.png",
                        $"{below}.bin.dds.gz", $"{below}.bin.dds", $"{below}.ktx2", $"{below}.ktx", $"{below}.glb.gz", below])
                      ?? stem(below);
    }

    /// <summary>One entry, as the client can take it: a gzipped one sent as it is when gzip is accepted.</summary>
    private static async Task<IResult> EntryAsync(HttpContext context, ScenePackages.Package package, string name, CancellationToken cancellation)
    {
        byte[] stored = await package.ReadAsync(name, cancellation).ConfigureAwait(false);
        bool gzipped = name.EndsWith(".gz", StringComparison.Ordinal);
        string inner = gzipped ? name[..^3] : name;
        string type = inner.EndsWith(".json", StringComparison.Ordinal) ? "application/json; charset=utf-8"
            : inner.EndsWith(".jpg", StringComparison.Ordinal) ? "image/jpeg"
            : inner.EndsWith(".png", StringComparison.Ordinal) ? "image/png"
            : inner.EndsWith(".ktx2", StringComparison.Ordinal) ? "image/ktx2"
            : "application/octet-stream";

        context.Response.Headers.Vary = "Accept-Encoding";
        context.Response.Headers.CacheControl = "private, max-age=600";

        if (gzipped && context.Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.ContentEncoding = "gzip";
            return Results.Bytes(stored, type);
        }

        return Results.Bytes(gzipped ? Plain(name, stored) : stored, type);
    }

    /// <summary>An entry's content without its gzip, when it has one.</summary>
    private static byte[] Plain(string name, byte[] stored)
    {
        if (!name.EndsWith(".gz", StringComparison.Ordinal) || stored.Length < 2 || stored[0] != 0x1f || stored[1] != 0x8b)
        {
            return stored;
        }

        using MemoryStream plain = new();
        using (GZipStream gzip = new(new MemoryStream(stored), CompressionMode.Decompress))
        {
            gzip.CopyTo(plain);
        }

        return plain.ToArray();
    }
}
