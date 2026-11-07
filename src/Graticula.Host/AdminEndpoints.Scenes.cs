using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Graticula.Platform.Identity;
using Graticula.Scenes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>What a caller sends to make a scene layer from a layer (ADR-269 §5.3).</summary>
/// <param name="Service">The feature service's name.</param>
/// <param name="Folder">Its folder.</param>
/// <param name="Layer">The layer's index in it.</param>
/// <param name="HeightField">The field holding each building's height in metres, none drawing the footprints flat; for
/// a point layer, each point's height above the ground, none standing the points at their own z, or on the ground.</param>
/// <param name="BaseField">The field holding the height a building's floor stands at, or none for the ground (0). A point
/// layer has none.</param>
/// <param name="Title">The scene layer's title; the layer's otherwise.</param>
/// <param name="Colour">The colour as "#rrggbb"; a light grey for buildings and a blue for points otherwise.</param>
/// <param name="HeightScale">Metres per unit of the height field — 3 for a field of floors; 1 otherwise.</param>
/// <param name="OnTheGround">A point layer with z placed on the ground instead of at its z.</param>
internal sealed record SceneFromLayerRequest(
    string? Service, string? Folder, int? Layer, string? HeightField, string? BaseField, string? Title, string? Colour,
    double? HeightScale = null, bool? OnTheGround = null);

/// <summary>
/// How a scene layer was made, kept with it so it can be made again — ADR-270 §5.4. The request that made it, the
/// feature service it was read from, and when.
/// </summary>
internal sealed record SceneSource(
    string Service, string? Folder, int Layer, string? HeightField, string? BaseField, string? Title, string? Colour,
    double? HeightScale, bool? OnTheGround, Guid ServiceId, DateTimeOffset Built, bool? AutoRebuild = null)
{
    /// <summary>The item resource it is kept as: private, so only the scene layer's owner and administrators read it.</summary>
    internal const string Path = "graticula/scene-source.json";

    internal SceneFromLayerRequest Request(string title) =>
        new(Service, Folder, Layer, HeightField, BaseField, title, Colour, HeightScale, OnTheGround);
}

/// <summary>A package made from a layer, before it is kept.</summary>
internal sealed record SceneMade(
    byte[] Package, string Title, string Counted, string Noun, int Count, PublishedService Service, int Index,
    string? Partial = null);

internal static partial class AdminEndpoints
{
    /// <summary>The most buildings one request turns into a scene layer — the rest wait for ADR-269's revisit.</summary>
    internal const int MaximumSceneBuildings = 200_000;

    /// <summary>The most points one request turns into a scene layer: sixteen bytes each before gzip, a node of a
    /// few thousand at a time.</summary>
    internal const int MaximumScenePoints = 1_000_000;

    private static void MapScenes(WebApplication app)
    {
        // ADR-269 §5.3: a polygon layer's footprints, extruded, or a point layer's points, written as a scene layer
        // package the caller owns.
        app.MapPost("/content/scenes", CreateSceneFromLayerAsync);

        // ADR-270 §5.4: a scene layer made here is made again from the layer it was made from — Enterprise's
        // *Rebuild cache* — and says where it came from.
        app.MapPost("/content/scenes/{id}/rebuild", RebuildSceneAsync);
        app.MapGet("/content/scenes/{id}/source", SceneSourceAsync);
        // ADR-277: whether the scene layer is made again on its own when its layer changes.
        app.MapPut("/content/scenes/{id}/source", SetSceneSourceAsync);
    }

    /// <summary>
    /// Makes a 3D object scene layer from a polygon layer the caller can read, or a point scene layer from a point
    /// layer (D-386), and keeps it as a package they own.
    /// </summary>
    /// <remarks>
    /// <b>Synchronous, and bounded.</b> A district's buildings take a second; the request reads at most
    /// <see cref="MaximumSceneBuildings"/> features in pages and the package must fit the document limit, so a caller
    /// is told what stopped it rather than left waiting on a job nobody can see. The package is a snapshot: an edit to
    /// the layer reaches the scene when it is made again (ADR-269 §6).
    /// </remarks>
    private static async Task<IResult> CreateSceneFromLayerAsync(
        HttpContext context, SceneFromLayerRequest request, CatalogFallback catalog, ServiceContexts contexts,
        IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentCreate).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        (SceneMade? made, IResult? refused) = await MakeSceneAsync(context, request, catalog, contexts, cancellation).ConfigureAwait(false);
        if (made is null)
        {
            return refused!;
        }

        return await KeepSceneAsync(context, request, made, documents, audit, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the layer a request names and writes its scene layer package; a refusal says why it could not. Shared by
    /// making a scene layer and making it again (ADR-270 §5.4), so the two cannot drift.
    /// </summary>
    private static async Task<(SceneMade? Made, IResult? Refused)> MakeSceneAsync(
        HttpContext context, SceneFromLayerRequest request, CatalogFallback catalog, ServiceContexts contexts, CancellationToken cancellation,
        byte[]? previous = null)
    {
        if (string.IsNullOrWhiteSpace(request.Service) || request.Layer is null)
        {
            return (null, Refuse(400, "Name the layer: its service, its folder if it has one, and its layer index."));
        }

        if (await ServiceLookup.ServiceAsync(context, catalog, request.Service, cancellation, request.Folder ?? string.Empty).ConfigureAwait(false)
            is not { } service)
        {
            return (null, Results.Empty);
        }

        (SceneMade? made, int status, string? why) = await MakeSceneFromAsync(request, service, contexts, cancellation, previous).ConfigureAwait(false);
        return made is null ? (null, Refuse(status, why!)) : (made, null);
    }

    /// <summary>
    /// The package a request makes from a service already found and allowed — the request path's and the rebuild sweep's
    /// one maker (ADR-277), so a scene layer rebuilt on its own is made exactly as one rebuilt by hand. A refusal is its
    /// status and its sentence.
    /// </summary>
    internal static async Task<(SceneMade? Made, int Status, string? Why)> MakeSceneFromAsync(
        SceneFromLayerRequest request, PublishedService service, ServiceContexts contexts, CancellationToken cancellation,
        byte[]? previous = null)
    {
        (SceneMade? made, IResult? refused) = await MakeSceneCoreAsync(request, service, contexts, cancellation, previous).ConfigureAwait(false);
        return made is not null ? (made, 200, null)
            : refused is IStatusCodeHttpResult { StatusCode: { } code } and IValueHttpResult { Value: { } value }
                ? (null, code, JsonSerializer.Serialize(value) is var text && JsonDocument.Parse(text).RootElement.TryGetProperty("error", out JsonElement e)
                    && e.TryGetProperty("message", out JsonElement m) ? m.GetString() : text)
                : (null, 500, "The scene layer could not be made.");
    }

    private static async Task<(SceneMade? Made, IResult? Refused)> MakeSceneCoreAsync(
        SceneFromLayerRequest request, PublishedService service, ServiceContexts contexts, CancellationToken cancellation,
        byte[]? previous = null)
    {
        int index = request.Layer!.Value;

        if (service.LayerOrTable(index) is not { } layer)
        {
            return (null, Refuse(404, $"'{service.QualifiedName}' has no layer {index}."));
        }

        bool points = layer.GeometryType is GeometryKind.Point;
        if (!points && layer.GeometryType is not (GeometryKind.Polygon or GeometryKind.MultiPolygon))
        {
            return (null, Refuse(400, $"'{layer.Definition.Name}' is neither a polygon nor a point layer. A scene layer is made from footprints or from points."));
        }

        if (points && !string.IsNullOrWhiteSpace(request.BaseField))
        {
            return (null, Refuse(400, "A point layer has no base: its height field is the point's height above the ground."));
        }

        double scale = request.HeightScale ?? 1;
        if (!double.IsFinite(scale) || scale <= 0 || scale > 1000)
        {
            return (null, Refuse(400, "heightScale is the metres one unit of the height field stands for: a number above 0, at most 1000."));
        }

        int[] colour = points ? [0, 112, 192] : [220, 220, 215];
        if (!string.IsNullOrWhiteSpace(request.Colour))
        {
            string hex = request.Colour.Trim().TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                return (null, Refuse(400, $"'{request.Colour}' is not a colour; send it as #rrggbb."));
            }

            colour = [(rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff];
        }

        (IFeatureSource source, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        if (points)
        {
            return await MakePointSceneAsync(request, service, layer, index, source, described, scale, colour, cancellation).ConfigureAwait(false);
        }

        FieldDescription? height = null, floor = null;
        if (!string.IsNullOrWhiteSpace(request.HeightField))
        {
            height = described.Fields.FirstOrDefault(f => f.Name.Equals(request.HeightField, StringComparison.OrdinalIgnoreCase));
            if (height is null || !Numeric(height.Value.Type))
            {
                return (null, Refuse(400, $"'{request.HeightField}' is not a number field of '{layer.Definition.Name}'."));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.BaseField))
        {
            floor = described.Fields.FirstOrDefault(f => f.Name.Equals(request.BaseField, StringComparison.OrdinalIgnoreCase));
            if (floor is null || !Numeric(floor.Value.Type))
            {
                return (null, Refuse(400, $"'{request.BaseField}' is not a number field of '{layer.Definition.Name}'."));
            }
        }

        List<FieldDescription> carried = Carried(layer, described);

        List<SceneBuilding> buildings = [];
        int page = FeatureQuery.MaximumLimit;

        // Named, because a query that names no fields answers with none.
        string[] asked = [.. carried.Select(f => f.Name)
            .Concat(new[] { height?.Name, floor?.Name }.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase)];

        for (int offset = 0; ; offset += page)
        {
            int read = 0;
            await foreach (Feature feature in source.ReadAsync(new FeatureQuery(page, fields: asked, offset: offset, outSrid: 4326), cancellation).ConfigureAwait(false))
            {
                read++;
                if (!long.TryParse(feature.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                {
                    id = buildings.Count + 1;
                }

                List<IReadOnlyList<IReadOnlyList<(double, double)>>> parts = feature.Geometry switch
                {
                    Polygon polygon => [Rings(polygon)],
                    MultiPolygon multi => [.. multi.Parts.Select(Rings)],
                    _ => [],
                };

                if (parts.Count == 0)
                {
                    continue;
                }

                double tall = height is { } h ? Number(feature[h.Name]) * scale : 0;
                double bottom = floor is { } b ? Number(feature[b.Name]) : 0;
                buildings.Add(new SceneBuilding(id, parts, bottom, tall, [.. carried.Select(f => feature[f.Name])]));
            }

            if (read < page || buildings.Count >= MaximumSceneBuildings)
            {
                break;
            }
        }

        if (buildings.Count == 0)
        {
            return (null, Refuse(400, $"'{layer.Definition.Name}' has no footprints to build."));
        }

        if (buildings.Count > MaximumSceneBuildings)
        {
            return (null, Refuse(413, $"'{layer.Definition.Name}' has more than {MaximumSceneBuildings:N0} footprints, more than one scene layer is made from here."));
        }

        string title = string.IsNullOrWhiteSpace(request.Title) ? $"{layer.Definition.Name} (3D)" : request.Title.Trim();
        // ADR-280 §4: the layer's GlobalID is typed GlobalID in the scene layer, as the SDK edits by it.
        string? globalId = GlobalIds.FieldOf(carried);
        SceneField[] sceneFields = [.. carried.Select(f => new SceneField(f.Name, f.Type, f.Alias, f.Name == globalId))];

        // ADR-277 §5.2: made again from a package this server wrote, only the nodes whose buildings changed are written
        // again; when too much changed, or the package has no plan, it is made whole.
        if (previous is not null
            && SceneLayerPackageWriter.Update(previous, title, sceneFields, buildings, colour, DateTimeOffset.UtcNow) is { } updated)
        {
            string partial = updated.Changed == 0 ? "nothing had changed"
                : $"{updated.Changed:N0} {(updated.Changed == 1 ? "building" : "buildings")} changed, {updated.Rewritten:N0} {(updated.Rewritten == 1 ? "node" : "nodes")} written again";
            return (new SceneMade(updated.Package, title, $"{buildings.Count:N0} buildings", "buildings", buildings.Count, service, index, partial), null);
        }

        byte[] package = SceneLayerPackageWriter.Write(title, sceneFields, buildings, colour, DateTimeOffset.UtcNow);

        return (new SceneMade(package, title, $"{buildings.Count:N0} buildings", "buildings", buildings.Count, service, index), null);
    }

    private static IResult Refuse(int status, string message) =>
        Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status);

    /// <summary>The fields a scene layer carries: every one but the geometry and the object id.</summary>
    private static List<FieldDescription> Carried(PublishedLayer layer, LayerDescription described) =>
        [.. described.Fields.Where(f =>
            !f.Name.Equals(layer.Definition.GeometryColumn, StringComparison.OrdinalIgnoreCase)
            && !f.Name.Equals(layer.Definition.IdentityColumn, StringComparison.OrdinalIgnoreCase)
            && f.Type is not (FieldType.Binary or FieldType.Unknown or FieldType.Boolean))];

    /// <summary>
    /// A point layer as a point scene layer (D-386): each point at its height above the ground when a field gives one,
    /// at its own z when it has one, and on the ground otherwise.
    /// </summary>
    private static async Task<(SceneMade? Made, IResult? Refused)> MakePointSceneAsync(
        SceneFromLayerRequest request, PublishedService service, PublishedLayer layer, int index, IFeatureSource source,
        LayerDescription described, double scale, int[] colour, CancellationToken cancellation)
    {
        FieldDescription? height = null;
        if (!string.IsNullOrWhiteSpace(request.HeightField))
        {
            height = described.Fields.FirstOrDefault(f => f.Name.Equals(request.HeightField, StringComparison.OrdinalIgnoreCase));
            if (height is null || !Numeric(height.Value.Type))
            {
                return (null, Refuse(400, $"'{request.HeightField}' is not a number field of '{layer.Definition.Name}'."));
            }
        }

        List<FieldDescription> carried = Carried(layer, described);
        string[] asked = [.. carried.Select(f => f.Name).Concat(new[] { height?.Name }.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase)];
        List<ScenePoint> made = [];
        bool anyZ = false;
        int page = FeatureQuery.MaximumLimit;

        for (int offset = 0; ; offset += page)
        {
            int read = 0;
            await foreach (Feature feature in source.ReadAsync(new FeatureQuery(page, fields: asked, offset: offset, outSrid: 4326), cancellation).ConfigureAwait(false))
            {
                read++;
                if (feature.Geometry is not Point { IsEmpty: false } point)
                {
                    continue;
                }

                if (!long.TryParse(feature.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                {
                    id = made.Count + 1;
                }

                if (id is < int.MinValue or > int.MaxValue)
                {
                    return (null, Refuse(400, $"'{layer.Definition.Name}' has object id {id}; a point scene layer holds 32-bit ids only."));
                }

                bool ground = request.OnTheGround == true;
                anyZ |= point.Z is not null && !ground;
                double z = height is { } hf ? Number(feature[hf.Name]) * scale : ground ? 0 : point.Z ?? 0;
                made.Add(new ScenePoint(id, point.X, point.Y, double.IsFinite(z) ? z : 0, [.. carried.Select(f => feature[f.Name])]));
            }

            if (read < page || made.Count > MaximumScenePoints)
            {
                break;
            }
        }

        if (made.Count == 0)
        {
            return (null, Refuse(400, $"'{layer.Definition.Name}' has no points to place."));
        }

        if (made.Count > MaximumScenePoints)
        {
            return (null, Refuse(413, $"'{layer.Definition.Name}' has more than {MaximumScenePoints:N0} points, more than one scene layer is made from here."));
        }

        SceneElevation elevation = height is null && anyZ ? SceneElevation.AbsoluteHeight : SceneElevation.RelativeToGround;
        string title = string.IsNullOrWhiteSpace(request.Title) ? $"{layer.Definition.Name} (3D)" : request.Title.Trim();
        byte[] package = SceneLayerPackageWriter.WritePoints(
            title, [.. carried.Select(f => new SceneField(f.Name, f.Type, f.Alias, f.Name == GlobalIds.FieldOf(carried)))], made, colour, 8, elevation, DateTimeOffset.UtcNow);

        return (new SceneMade(package, title, $"{made.Count:N0} points", "points", made.Count, service, index), null);
    }

    /// <summary>
    /// Keeps a generated package as a private document of the caller's, with how it was made and the feature layer it was
    /// made from (ADR-270 §5.4), and answers with where it is served.
    /// </summary>
    private static async Task<IResult> KeepSceneAsync(
        HttpContext context, SceneFromLayerRequest request, SceneMade made, IDocumentStore documents, IAuditLog audit,
        CancellationToken cancellation)
    {
        if (made.Package.Length > MaximumDocumentBytes(context))
        {
            return TooLarge(context, made);
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        Document kept = await documents.CreateAsync(
            made.Title, $"{made.Counted} from {made.Service.QualifiedName}, layer {made.Index}.", current.Principal.Id, SharingScope.Private,
            SafeFileName(made.Title) + ".slpk", Documents.Named("slpk")!, made.Package, cancellation).ConfigureAwait(false);

        await RememberSourceAsync(context, kept.Id, request, made, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "document.create", kept.Id,
            Detail(new { title = kept.Title, kind = kept.Kind.Name, bytes = kept.Size, from = made.Service.QualifiedName, layer = made.Index, features = made.Count, of = made.Noun }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.Headers.Location = $"/content/documents/{kept.Id}";
        return Results.Json(Answer(context, kept, made), statusCode: StatusCodes.Status201Created);
    }

    /// <summary>Makes a 3D object layer's scene layer again from its models (ADR-280), in part where it can (ADR-277).</summary>
    private static async Task<IResult> RebuildFromMeshAsync(
        HttpContext context, Document scene, string meshId, IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        if (await documents.FindAsync(meshId, cancellation).ConfigureAwait(false) is not { } mesh || !Documents.IsMeshLayer(mesh.Kind))
        {
            return Refuse(409, $"The 3D object layer '{scene.Title}' was made from no longer exists, so it cannot be made again.");
        }

        byte[]? previous = await documents.ContentAsync(scene.Id, cancellation).ConfigureAwait(false);
        if (await MeshPackageAsync(context.RequestServices, mesh, previous, cancellation).ConfigureAwait(false) is not { } made)
        {
            return Refuse(500, "The 3D object layer could not be read.");
        }

        if (made.Package.Length > MaximumDocumentBytes(context))
        {
            return Refuse(413, $"The scene layer came to {made.Package.Length / (1 << 20)} MB, more than the {MaximumDocumentBytes(context) / (1 << 20)} MB a package may be here.");
        }

        if (!await documents.ReplaceAsync(scene.Id, scene.FileName, scene.Kind, made.Package, cancellation).ConfigureAwait(false)
            || await documents.FindAsync(scene.Id, cancellation).ConfigureAwait(false) is not { } after)
        {
            return Refuse(404, NoDocument(scene.Id));
        }

        await AuditAsync(context, audit, "document.replace", scene.Id,
            Detail(new { rebuilt = true, from = $"meshes/{mesh.Id}", features = made.Count, of = made.Noun, bytes = after.Size }), succeeded: true, cancellation).ConfigureAwait(false);
        return Results.Json(new
        {
            id = after.Id, title = after.Title, features = made.Count, of = made.Noun, bytes = after.Size,
            url = SceneServerEndpoints.AddressOf(context.Request, after.Id), source = new { service = $"meshes/{mesh.Id}", layer = 0 },
            partial = made.Partial,
        });
    }

    private static object Answer(HttpContext context, Document kept, SceneMade made) => new
    {
        id = kept.Id, title = kept.Title, features = made.Count, of = made.Noun,
        buildings = made.Noun == "buildings" ? made.Count : 0, bytes = kept.Size, url = SceneServerEndpoints.AddressOf(context.Request, kept.Id),
        source = new { service = made.Service.QualifiedName, layer = made.Index },
        partial = made.Partial,
    };

    private static IResult TooLarge(HttpContext context, SceneMade made) =>
        Refuse(413, $"The scene layer came to {made.Package.Length / (1 << 20)} MB, more than the {MaximumDocumentBytes(context) / (1 << 20)} MB a package may be here.");

    /// <summary>
    /// Keeps the request as the scene layer's private <see cref="SceneSource.Path"/> resource and relates it to its
    /// feature service as ArcGIS's <c>Service2Service</c>, so a client that asks for the scene layer's associated
    /// feature layer finds it, and so it can be made again.
    /// </summary>
    private static Task RememberSourceAsync(
        HttpContext context, string documentId, SceneFromLayerRequest request, SceneMade made, CancellationToken cancellation,
        bool? autoRebuild = null) =>
        RememberSourceAsync(context.RequestServices, documentId, request, made, autoRebuild, cancellation);

    /// <summary>The same, for a caller with no request — the rebuild sweep (ADR-277).</summary>
    internal static async Task RememberSourceAsync(
        IServiceProvider services, string documentId, SceneFromLayerRequest request, SceneMade made, bool? autoRebuild,
        CancellationToken cancellation)
    {
        Guid item = Guid.Parse(documentId);
        SceneSource source = new(
            made.Service.Name, string.IsNullOrEmpty(made.Service.Folder) ? null : made.Service.Folder, made.Index, request.HeightField,
            request.BaseField, request.Title, request.Colour, request.HeightScale, request.OnTheGround, made.Service.Id, DateTimeOffset.UtcNow,
            autoRebuild);

        // The store adds only where nothing is, or replaces only where something is: a first build adds, a rebuild replaces.
        IItemResourceStore resources = services.GetRequiredService<IItemResourceStore>();
        bool kept = await resources.FindAsync(item, SceneSource.Path, cancellation).ConfigureAwait(false) is not null;
        await resources.WriteAsync(
            item, SceneSource.Path, "application/json", JsonSerializer.SerializeToUtf8Bytes(source, SceneSourceJson), "private",
            replace: kept, limit: long.MaxValue, cancellation).ConfigureAwait(false);

        // From the feature layer to the scene layer: the Maps SDK 4.29 asks a scene layer's item for its Service2Service
        // relationships going *reverse* (measured 2026-11-05, the request in the server's log), so the feature layer is
        // the origin.
        await services.GetRequiredService<IItemAnnotationStore>()
            .RelateAsync(made.Service.Id, item, PortalEndpoints.Service2Service, cancellation).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions SceneSourceJson = new(JsonSerializerDefaults.Web);

    private static Task<SceneSource?> ReadSourceAsync(HttpContext context, string documentId, CancellationToken cancellation) =>
        ReadSourceAsync(context.RequestServices, documentId, cancellation);

    /// <summary>A scene layer's source, for a caller with no request (ADR-277).</summary>
    internal static async Task<SceneSource?> ReadSourceAsync(IServiceProvider services, string documentId, CancellationToken cancellation)
    {
        if (!Guid.TryParse(documentId, out Guid item)
            || await services.GetRequiredService<IItemResourceStore>()
                .FindAsync(item, SceneSource.Path, cancellation).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SceneSource>(found.Content, SceneSourceJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where a scene layer made here came from — what Studio's page of the scene layer says beside <i>Rebuild</i>. Its
    /// owner's and an administrator's only: the source's name is not the scene layer's readers' to learn. A package that
    /// was uploaded, not made here, has no source: 404.
    /// </summary>
    private static async Task<IResult> SceneSourceAsync(
        HttpContext context, string id, CatalogFallback catalog, IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        if (await ManagedDocumentAsync(context, current, id, "read the source of", documents, audit, cancellation).ConfigureAwait(false)
            is not { } document)
        {
            return Results.Empty;
        }

        if (await MeshSourceAsync(context.RequestServices, id, cancellation).ConfigureAwait(false) is { } meshId)
        {
            Document? mesh = await documents.FindAsync(meshId, cancellation).ConfigureAwait(false);
            return Results.Json(new
            {
                service = $"meshes/{meshId}", layer = 0, exists = mesh is not null, mesh = meshId, built = document.Modified.ToUnixTimeMilliseconds(),
                autoRebuild = false,
            });
        }

        if (await ReadSourceAsync(context, id, cancellation).ConfigureAwait(false) is not { } source)
        {
            return Refuse(404, $"'{document.Title}' was not made by this server from a layer, so it has nothing to be made again from.");
        }

        PublishedService? service = (await catalog.ListServicesAsync(cancellation).ConfigureAwait(false)).Services?
            .FirstOrDefault(s => s.Id == source.ServiceId);
        return Results.Json(new
        {
            service = service?.QualifiedName ?? (string.IsNullOrEmpty(source.Folder) ? source.Service : $"{source.Folder}/{source.Service}"),
            layer = source.Layer, exists = service is not null, heightField = source.HeightField, baseField = source.BaseField,
            heightScale = source.HeightScale, onTheGround = source.OnTheGround, built = source.Built.ToUnixTimeMilliseconds(),
            autoRebuild = source.AutoRebuild == true,
            lastAutomatic = await ReadAutomaticAsync(context.RequestServices, id, cancellation).ConfigureAwait(false),
        });
    }

    /// <summary>What the last rebuild the sweep made said (ADR-277): when, whether it was made, and why not.</summary>
    internal sealed record SceneAutomatic(DateTimeOffset At, bool Made, string? Why, int? Features, string? Partial = null);

    /// <summary>The item resource the sweep's outcome is kept as, beside the source.</summary>
    internal const string AutomaticPath = "graticula/scene-rebuild.json";

    internal static async Task<object?> ReadAutomaticAsync(IServiceProvider services, string documentId, CancellationToken cancellation)
    {
        if (!Guid.TryParse(documentId, out Guid item)
            || await services.GetRequiredService<IItemResourceStore>().FindAsync(item, AutomaticPath, cancellation).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SceneAutomatic>(found.Content, SceneSourceJson) is { } said
                ? new { at = said.At.ToUnixTimeMilliseconds(), made = said.Made, why = said.Why, features = said.Features, partial = said.Partial }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static async Task WriteAutomaticAsync(IServiceProvider services, string documentId, SceneAutomatic said, CancellationToken cancellation)
    {
        Guid item = Guid.Parse(documentId);
        IItemResourceStore resources = services.GetRequiredService<IItemResourceStore>();
        bool kept = await resources.FindAsync(item, AutomaticPath, cancellation).ConfigureAwait(false) is not null;
        await resources.WriteAsync(item, AutomaticPath, "application/json", JsonSerializer.SerializeToUtf8Bytes(said, SceneSourceJson), "private",
            replace: kept, limit: long.MaxValue, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the scene layer's automatic rebuild on or off — <c>{ "autoRebuild": true }</c>; its owner's and an
    /// administrator's, as <i>Rebuild</i> is (ADR-277).
    /// </summary>
    private static async Task<IResult> SetSceneSourceAsync(
        HttpContext context, string id, IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        if (await ManagedDocumentAsync(context, current, id, "change the rebuilding of", documents, audit, cancellation).ConfigureAwait(false)
            is not { } document)
        {
            return Results.Empty;
        }

        if (await ReadSourceAsync(context, id, cancellation).ConfigureAwait(false) is not { } source)
        {
            return Refuse(404, $"'{document.Title}' was not made by this server from a layer, so it has nothing to be made again from.");
        }

        bool on;
        try
        {
            using JsonDocument asked = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellation).ConfigureAwait(false);
            if (!asked.RootElement.TryGetProperty("autoRebuild", out JsonElement flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Refuse(400, "Send { \"autoRebuild\": true } or false.");
            }

            on = flag.GetBoolean();
        }
        catch (JsonException)
        {
            return Refuse(400, "Send { \"autoRebuild\": true } or false.");
        }

        Guid item = Guid.Parse(id);
        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        await resources.WriteAsync(item, SceneSource.Path, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(source with { AutoRebuild = on }, SceneSourceJson), "private",
            replace: true, limit: long.MaxValue, cancellation).ConfigureAwait(false);

        await AuditAsync(context, audit, "document.update", id, Detail(new { autoRebuild = on }), succeeded: true, cancellation).ConfigureAwait(false);
        return Results.Json(new { autoRebuild = on });
    }

    /// <summary>
    /// Makes a scene layer again from its source, in place, with no request — the rebuild sweep's (ADR-277). The same
    /// maker and the same replacement as <i>Rebuild</i>; answers what it did, for the item's record.
    /// </summary>
    internal static async Task<SceneAutomatic> RebuildFromSourceAsync(
        IServiceProvider services, Document document, SceneSource source, PublishedService service, CancellationToken cancellation)
    {
        SceneFromLayerRequest request = source.Request(document.Title);
        IDocumentStore store = services.GetRequiredService<IDocumentStore>();
        byte[]? previous = await store.ContentAsync(document.Id, cancellation).ConfigureAwait(false);
        (SceneMade? made, _, string? why) = await MakeSceneFromAsync(
            request, service, services.GetRequiredService<ServiceContexts>(), cancellation, previous).ConfigureAwait(false);
        if (made is null)
        {
            return new SceneAutomatic(DateTimeOffset.UtcNow, false, why, null);
        }

        long limit = services.GetRequiredService<HostSettings>().DocumentMaximumBytes;
        if (made.Package.Length > limit)
        {
            return new SceneAutomatic(DateTimeOffset.UtcNow, false,
                $"The scene layer came to {made.Package.Length / (1 << 20)} MB, more than the {limit / (1 << 20)} MB a package may be here.", null);
        }

        IDocumentStore documents = services.GetRequiredService<IDocumentStore>();
        if (!await documents.ReplaceAsync(document.Id, document.FileName, document.Kind, made.Package, cancellation).ConfigureAwait(false))
        {
            return new SceneAutomatic(DateTimeOffset.UtcNow, false, "The scene layer no longer exists.", null);
        }

        await RememberSourceAsync(services, document.Id, request with { Title = source.Title }, made, source.AutoRebuild, cancellation).ConfigureAwait(false);
        return new SceneAutomatic(DateTimeOffset.UtcNow, true, null, made.Count, made.Partial);
    }

    /// <summary>
    /// Makes a scene layer again from the layer it was made from — Enterprise's <i>Rebuild cache</i> (ADR-270 §5.4). The
    /// package is replaced in place: its id, its address, its sharing and every scene that uses it stay; each node's copy
    /// is keyed by the document's modified time, so none is read stale.
    /// </summary>
    private static async Task<IResult> RebuildSceneAsync(
        HttpContext context, string id, CatalogFallback catalog, ServiceContexts contexts, IDocumentStore documents, IAuditLog audit,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        if (await ManagedDocumentAsync(context, current, id, "rebuild", documents, audit, cancellation).ConfigureAwait(false)
            is not { } document)
        {
            return Results.Empty;
        }

        // ADR-280: the scene layer of a 3D object layer is made again from its models.
        if (await MeshSourceAsync(context.RequestServices, id, cancellation).ConfigureAwait(false) is { } meshId)
        {
            return await RebuildFromMeshAsync(context, document, meshId, documents, audit, cancellation).ConfigureAwait(false);
        }

        if (await ReadSourceAsync(context, id, cancellation).ConfigureAwait(false) is not { } source)
        {
            return Refuse(409, $"'{document.Title}' was not made by this server from a layer, so it cannot be made again here. Upload a new package instead.");
        }

        // The layer as it is now, by the name it had: a service renamed or removed since is said, not guessed at.
        SceneFromLayerRequest request = source.Request(document.Title);
        // ADR-277 §5.2: the package as it is, so only what changed is written again.
        byte[]? previous = await documents.ContentAsync(id, cancellation).ConfigureAwait(false);
        (SceneMade? made, IResult? refused) = await MakeSceneAsync(context, request, catalog, contexts, cancellation, previous).ConfigureAwait(false);
        if (made is null)
        {
            return refused!;
        }

        if (made.Package.Length > MaximumDocumentBytes(context))
        {
            return TooLarge(context, made);
        }

        if (!await documents.ReplaceAsync(id, document.FileName, document.Kind, made.Package, cancellation).ConfigureAwait(false)
            || await documents.FindAsync(id, cancellation).ConfigureAwait(false) is not { } after)
        {
            return Refuse(404, NoDocument(id));
        }

        await RememberSourceAsync(context, id, request with { Title = source.Title }, made, cancellation, source.AutoRebuild).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "document.replace", id,
            Detail(new { rebuilt = true, from = made.Service.QualifiedName, layer = made.Index, features = made.Count, of = made.Noun, bytes = after.Size }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Json(Answer(context, after, made));
    }

    private static bool Numeric(FieldType type) =>
        type is FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger or FieldType.Single or FieldType.Double;

    private static double Number(object? value) =>
        value is null or DBNull ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture) is var d && double.IsFinite(d) ? d : 0;

    private static IReadOnlyList<IReadOnlyList<(double, double)>> Rings(Polygon polygon) =>
        [Ring(polygon.Shell), .. polygon.Holes.Select(Ring)];

    private static IReadOnlyList<(double, double)> Ring(LinearRing ring)
    {
        XySequence xy = ring.Coordinates;
        (double, double)[] points = new (double, double)[xy.Count];
        for (int i = 0; i < xy.Count; i++)
        {
            points[i] = (xy.X(i), xy.Y(i));
        }

        return points;
    }

    private static string SafeFileName(string title)
    {
        string name = new([.. title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        return name.Length == 0 ? "scene" : name[..Math.Min(name.Length, 80)];
    }
}
