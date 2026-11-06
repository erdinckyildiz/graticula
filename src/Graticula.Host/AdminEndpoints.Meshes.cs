using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Scenes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// ADR-280 §5.2: a 3D object layer — models placed in a scene and edited there — and the scene layer that draws it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own store and its own small FeatureServer</b>, apart from the hosted layers' pipeline, which this does not
/// touch. The layer is a document of kind <c>meshlayer</c>; its features, their tombstones and their asset maps are one
/// JSON resource of that item, and each model is a resource of its own, by its SHA-256. It is served at
/// <c>/rest/services/meshes/{id}/FeatureServer</c>, whose literal folder outranks the catalog's <c>{folder}</c> route,
/// so no hosted service is shadowed.
/// </para>
/// <para>
/// <b>What it answers is what Maps SDK 5.2 was measured asking</b> (experiments/mesh-editing):
/// <list type="bullet">
/// <item>a multipatch layer with <c>infoFor3D</c> in the public sample's shape and no tables listed;</item>
/// <item><c>uploadAssets</c> of base64 glb;</item>
/// <item><c>applyEdits</c> by GlobalID, the transform as <c>ESRI3DO_*</c> attributes and an asset map naming the hash;</item>
/// <item><c>extractChanges</c> from a server generation, which is the time of an edit in milliseconds, so the scene
/// layer's <c>serviceUpdateTimeStamp</c> is one.</item>
/// </list>
/// </para>
/// <para>
/// <b>One writer at a time per layer, per node</b> (a lock in this process). Two nodes editing one layer at the same
/// moment can lose an edit: D-390.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    private const string MeshRoot = "/rest/services/meshes/{id}/FeatureServer";
    private const string MeshStatePath = "graticula/mesh/features.json";
    private const string MeshAssetPrefix = "graticula/mesh/assets/";
    private const string MeshSourcePath = "graticula/mesh-source.json";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MeshLocks = new(StringComparer.Ordinal);

    /// <summary>The attributes a 3D object feature keeps, after its object id and GlobalID.</summary>
    private static readonly string[] MeshNumbers =
        ["ESRI3DO_OX", "ESRI3DO_OY", "ESRI3DO_OZ", "ESRI3DO_TX", "ESRI3DO_TY", "ESRI3DO_TZ", "ESRI3DO_SX", "ESRI3DO_SY", "ESRI3DO_SZ",
         "ESRI3DO_RX", "ESRI3DO_RY", "ESRI3DO_RZ", "ESRI3DO_RDEG"];

    private static readonly string[] MeshTexts = ["name", "ESRI3DO_TYPE", "ESRI3DO_SHASH", "ESRI3DO_PID"];

    private static void MapMeshes(WebApplication app)
    {
        app.MapPost("/content/meshes", CreateMeshLayerAsync);

        app.MapMethods(MeshRoot, ["GET", "POST"], MeshServiceAsync);
        app.MapMethods(MeshRoot + "/0", ["GET", "POST"], MeshLayerAsync);
        app.MapMethods(MeshRoot + "/layers", ["GET", "POST"], MeshLayersAsync);
        app.MapMethods(MeshRoot + "/uploads/info", ["GET", "POST"], MeshUploadsInfoAsync);
        app.MapPost(MeshRoot + "/0/uploadAssets", MeshUploadAssetsAsync).DisableAntiforgery();
        app.MapPost(MeshRoot + "/0/applyEdits", MeshApplyEditsAsync).DisableAntiforgery();
        app.MapPost(MeshRoot + "/applyEdits", MeshServiceApplyEditsAsync).DisableAntiforgery();
        app.MapMethods(MeshRoot + "/extractChanges", ["GET", "POST"], MeshExtractChangesAsync).DisableAntiforgery();
        app.MapMethods(MeshRoot + "/0/query", ["GET", "POST"], (HttpContext c, string id, IDocumentStore d, CancellationToken t) => MeshQueryAsync(c, id, d, false, t)).DisableAntiforgery();
        app.MapMethods(MeshRoot + "/0/query3D", ["GET", "POST"], (HttpContext c, string id, IDocumentStore d, CancellationToken t) => MeshQueryAsync(c, id, d, true, t)).DisableAntiforgery();
        app.MapMethods(MeshRoot + "/0/queryAssets", ["GET", "POST"], MeshQueryAssetsAsync).DisableAntiforgery();
        app.MapGet(MeshRoot + "/0/assets/{hash}", MeshAssetAsync);
    }

    // ------------------------------------------------------------------------------------------------ the layer's state

    /// <summary>A layer's features, tombstones and asset maps, as kept in its item.</summary>
    private sealed class MeshState
    {
        public long Gen { get; set; }

        public long Next { get; set; } = 1;

        public List<MeshFeature> Features { get; set; } = [];

        public List<MeshTombstone> Deleted { get; set; } = [];

        public List<MeshMap> Maps { get; set; } = [];
    }

    private sealed class MeshFeature
    {
        public long Oid { get; set; }

        public string Gid { get; set; } = string.Empty;

        public long Created { get; set; }

        public long Gen { get; set; }

        public Dictionary<string, JsonElement> A { get; set; } = [];
    }

    private sealed record MeshTombstone(long Oid, string Gid, long Gen);

    private sealed record MeshMap(string Gid, string Parent, string Name, string Hash, long Gen);

    private static readonly JsonSerializerOptions MeshJson = new(JsonSerializerDefaults.Web);

    private static async Task<MeshState> ReadMeshAsync(IServiceProvider services, string id, CancellationToken cancellation)
    {
        IItemResourceStore resources = services.GetRequiredService<IItemResourceStore>();
        return await resources.FindAsync(Guid.Parse(id), MeshStatePath, cancellation).ConfigureAwait(false) is { } found
            ? JsonSerializer.Deserialize<MeshState>(found.Content, MeshJson) ?? new MeshState()
            : new MeshState();
    }

    private static async Task WriteMeshAsync(IServiceProvider services, string id, MeshState state, CancellationToken cancellation)
    {
        IItemResourceStore resources = services.GetRequiredService<IItemResourceStore>();
        bool kept = await resources.FindAsync(Guid.Parse(id), MeshStatePath, cancellation).ConfigureAwait(false) is not null;
        await resources.WriteAsync(
            Guid.Parse(id), MeshStatePath, "application/json", JsonSerializer.SerializeToUtf8Bytes(state, MeshJson), "private",
            replace: kept, limit: long.MaxValue, cancellation).ConfigureAwait(false);
    }

    /// <summary>A generation after every one before it: the time in milliseconds, or one more than the last.</summary>
    private static long NextGen(MeshState state) => state.Gen = Math.Max(state.Gen + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>A GlobalID as ArcGIS writes one: upper case, in braces.</summary>
    private static string? Gid(string? text) =>
        Guid.TryParse(text?.Trim().Trim('{', '}'), out Guid g) ? "{" + g.ToString("D").ToUpperInvariant() + "}" : null;

    // -------------------------------------------------------------------------------------------------------- access

    /// <summary>The layer, when this caller may read it; otherwise null and the answer already given.</summary>
    private static async Task<Document?> ReadableMeshAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation)
    {
        string lower = (id ?? string.Empty).ToLowerInvariant();
        Document? document = WebMaps.IsId(lower) ? await documents.FindAsync(lower, cancellation).ConfigureAwait(false) : null;
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        if (document is null || !Documents.IsMeshLayer(document.Kind)
            || !LayerAccess.Evaluate(document.Sharing, document.Owner, current.Principal, current.Authorization, document.SharedWith).IsAllowed())
        {
            // The same answer for a layer that is private and one that does not exist.
            await (TokenRequired.Applies(context) ? TokenRequired.Result(context) : MeshError(404, "Service not found.")).ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }

        return document;
    }

    /// <summary>The layer, when this caller may edit it — its owner or an administrator.</summary>
    private static async Task<Document?> EditableMeshAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation)
    {
        if (await ReadableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is not { } document)
        {
            return null;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        if (!LayerAccess.MayManage(document.Owner, current.Principal, current.Authorization))
        {
            await MeshError(403, $"Only the layer's owner, {document.OwnerName}, or an administrator can edit '{document.Title}'.").ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }

        return document;
    }

    private static IResult MeshError(int code, string message) =>
        Results.Json(new { error = new { code, message, details = Array.Empty<string>() } }, statusCode: code);

    /// <summary>A parameter from the query string or the form, as ArcGIS clients send either.</summary>
    private static async Task<Dictionary<string, string>> MeshParamsAsync(HttpContext context)
    {
        Dictionary<string, string> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in context.Request.Query)
        {
            found[key] = value.ToString();
        }

        if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            foreach (var (key, value) in form)
            {
                found[key] = value.ToString();
            }
        }

        return found;
    }

    // -------------------------------------------------------------------------------------------- making the layer

    /// <summary>What making a 3D object layer asks for.</summary>
    internal sealed record MeshLayerRequest(string? Title);

    /// <summary>
    /// Makes an empty 3D object layer and its scene layer, related as ArcGIS relates a hosted 3D object layer to its scene
    /// layer, both private to the caller (ADR-280 §5.2).
    /// </summary>
    private static async Task<IResult> CreateMeshLayerAsync(
        HttpContext context, MeshLayerRequest request, IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentCreate).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        string title = string.IsNullOrWhiteSpace(request.Title) ? "3D objects" : request.Title.Trim();
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        Document mesh = await documents.CreateAsync(
            title, "3D objects placed and edited in a scene.", current.Principal.Id, SharingScope.Private, SafeFileName(title) + ".json",
            Documents.MeshLayerKind, Encoding.UTF8.GetBytes("{\"version\":1}"), cancellation).ConfigureAwait(false);
        await WriteMeshAsync(context.RequestServices, mesh.Id, new MeshState { Gen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, cancellation).ConfigureAwait(false);

        byte[] package = await MeshPackageAsync(context.RequestServices, mesh, null, cancellation).ConfigureAwait(false) is { } made ? made.Package : [];
        Document scene = await documents.CreateAsync(
            $"{title} (scene layer)", $"The scene layer of {title}.", current.Principal.Id, SharingScope.Private, SafeFileName(title) + ".slpk",
            Documents.Named("slpk")!, package, cancellation).ConfigureAwait(false);

        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        await resources.WriteAsync(Guid.Parse(scene.Id), MeshSourcePath, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { mesh = mesh.Id }), "private", replace: false, limit: long.MaxValue, cancellation).ConfigureAwait(false);
        // From the 3D object layer to its scene layer, as from a feature layer to the scene layer made from it (ADR-270 §5.4).
        await context.RequestServices.GetRequiredService<IItemAnnotationStore>()
            .RelateAsync(Guid.Parse(mesh.Id), Guid.Parse(scene.Id), PortalEndpoints.Service2Service, cancellation).ConfigureAwait(false);

        await AuditAsync(context, audit, "document.create", mesh.Id, Detail(new { title, kind = "meshlayer", scene = scene.Id }), succeeded: true, cancellation).ConfigureAwait(false);
        return Results.Json(new
        {
            id = mesh.Id, title, url = MeshAddressOf(context.Request, mesh.Id),
            scene = new { id = scene.Id, url = SceneServerEndpoints.AddressOf(context.Request, scene.Id) },
        }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The address a 3D object layer's FeatureServer answers at.</summary>
    internal static string MeshAddressOf(HttpRequest request, string id) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}/rest/services/meshes/{id}/FeatureServer";

    /// <summary>The 3D object layer a scene layer was made from, or null.</summary>
    internal static async Task<string?> MeshSourceAsync(IServiceProvider services, string sceneId, CancellationToken cancellation)
    {
        if (!Guid.TryParse(sceneId, out Guid item)
            || await services.GetRequiredService<IItemResourceStore>().FindAsync(item, MeshSourcePath, cancellation).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        return JsonNode.Parse(found.Content)?["mesh"]?.GetValue<string>();
    }

    private static readonly SceneField[] MeshSceneFields = [new("name", FieldType.Text), new("globalid", FieldType.Guid, "GlobalID", GlobalId: true)];

    /// <summary>
    /// The scene layer of a 3D object layer: each model placed by its transform (ADR-280 condition 2), in part where the
    /// previous package allows (ADR-277 §4b). Its update stamp is the layer's generation, which <c>extractChanges</c>
    /// counts from.
    /// </summary>
    internal static async Task<SceneMade?> MeshPackageAsync(IServiceProvider services, Document mesh, byte[]? previous, CancellationToken cancellation)
    {
        MeshState state = await ReadMeshAsync(services, mesh.Id, cancellation).ConfigureAwait(false);
        IItemResourceStore resources = services.GetRequiredService<IItemResourceStore>();
        Dictionary<string, MeshMap> latest = state.Maps.GroupBy(m => m.Parent).ToDictionary(g => g.Key, g => g.OrderBy(m => m.Gen).Last());
        Dictionary<string, byte[]> assets = new(StringComparer.Ordinal);
        List<SceneBuilding> buildings = [];
        foreach (MeshFeature feature in state.Features)
        {
            if (!latest.TryGetValue(feature.Gid, out MeshMap? map))
            {
                continue;
            }

            if (!assets.TryGetValue(map.Hash, out byte[]? glb))
            {
                glb = (await resources.FindAsync(Guid.Parse(mesh.Id), MeshAssetPrefix + map.Hash, cancellation).ConfigureAwait(false))?.Content;
                if (glb is null)
                {
                    continue;
                }

                assets[map.Hash] = glb;
            }

            buildings.Add(new SceneBuilding(feature.Oid, [], 0, 0, [Text(feature, "name"), feature.Gid], ModelOf(feature, glb, map.Hash)));
        }

        DateTimeOffset stamp = DateTimeOffset.FromUnixTimeMilliseconds(state.Gen);
        string partial = string.Empty;
        byte[]? package = null;
        if (previous is { Length: > 0 } && SceneLayerPackageWriter.Update(previous, mesh.Title, MeshSceneFields, buildings, MeshColour, stamp) is { } updated)
        {
            package = updated.Package;
            partial = updated.Changed == 0 ? "nothing had changed" : $"{updated.Changed} changed, {updated.Rewritten} nodes written again";
        }

        package ??= SceneLayerPackageWriter.Write(mesh.Title, MeshSceneFields, buildings, MeshColour, stamp);
        return new SceneMade(package, mesh.Title, $"{buildings.Count:N0} 3D objects", "3D objects", buildings.Count, null!, 0, partial.Length > 0 ? partial : null);
    }

    private static readonly int[] MeshColour = [220, 220, 215];

    private static string? Text(MeshFeature feature, string name) =>
        feature.A.TryGetValue(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Number(MeshFeature feature, string name, double fallback) =>
        feature.A.TryGetValue(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    /// <summary>
    /// A feature's model and transform. The axis and angle are read as the SDK writes them; with a zero angle — every model
    /// the SDK was measured placing — the axis does not matter, and which frame it is given in is not yet measured.
    /// </summary>
    private static SceneModel ModelOf(MeshFeature f, byte[] glb, string hash) => new(
        glb, hash, Number(f, "ESRI3DO_OX", 0), Number(f, "ESRI3DO_OY", 0), Number(f, "ESRI3DO_OZ", 0),
        (Number(f, "ESRI3DO_TX", 0), Number(f, "ESRI3DO_TY", 0), Number(f, "ESRI3DO_TZ", 0)),
        (Number(f, "ESRI3DO_SX", 1), Number(f, "ESRI3DO_SY", 1), Number(f, "ESRI3DO_SZ", 1)),
        (Number(f, "ESRI3DO_RX", 0), Number(f, "ESRI3DO_RY", 0), Number(f, "ESRI3DO_RZ", 1)), Number(f, "ESRI3DO_RDEG", 0));

    // ------------------------------------------------------------------------------------------------------ metadata

    private static object[] MeshFields() =>
        new object[]
        {
            new { name = "OBJECTID", type = "esriFieldTypeOID", alias = "OBJECTID", editable = false, nullable = false },
            new { name = "globalid", type = "esriFieldTypeGlobalID", alias = "GlobalID", length = 38, editable = false, nullable = false },
            new { name = "name", type = "esriFieldTypeString", alias = "Name", length = 256, editable = true, nullable = true },
        }
        .Concat(MeshNumbers.Select(n => (object)new { name = n, type = "esriFieldTypeDouble", alias = n, editable = true, nullable = true }))
        .Concat([
            new { name = "ESRI3DO_TYPE", type = "esriFieldTypeString", alias = "ESRI3DO_TYPE", length = 64, editable = true, nullable = true },
            new { name = "ESRI3DO_SHASH", type = "esriFieldTypeString", alias = "ESRI3DO_SHASH", length = 80, editable = true, nullable = true },
            new { name = "ESRI3DO_PID", type = "esriFieldTypeGUID", alias = "ESRI3DO_PID", length = 38, editable = true, nullable = true },
        ])
        .ToArray();

    private static async Task<IResult> MeshServiceAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation)
    {
        if (await ReadableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is not { } mesh)
        {
            return Results.Empty;
        }

        // No tables listed: with its asset tables among them, SDK 5.2 loaded the service as a group layer (ADR-280 §4).
        return Results.Json(new
        {
            currentVersion = 11.3, serviceDescription = mesh.Title, serviceItemId = mesh.Id, hasVersionedData = false,
            supportsApplyEditsWithGlobalIds = true, capabilities = "Create,Delete,Query,Update,Editing,ChangeTracking,Uploads",
            spatialReference = new { wkid = 4326, latestWkid = 4326 },
            layers = new[] { new { id = 0, name = mesh.Title, geometryType = "esriGeometryMultiPatch" } },
            tables = Array.Empty<object>(), syncEnabled = false,
            extractChangesCapabilities = new { supportsReturnIdsOnly = true, supportsReturnExtentOnly = false, supportsReturnFeature = true, supportsServerGens = true },
        });
    }

    private static async Task<IResult> MeshLayersAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation) =>
        await ReadableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is { } mesh
            ? Results.Json(new { layers = new[] { await MeshLayerJsonAsync(context, mesh, cancellation).ConfigureAwait(false) }, tables = Array.Empty<object>() })
            : Results.Empty;

    private static async Task<IResult> MeshLayerAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation) =>
        await ReadableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is { } mesh
            ? Results.Json(await MeshLayerJsonAsync(context, mesh, cancellation).ConfigureAwait(false))
            : Results.Empty;

    private static async Task<JsonObject> MeshLayerJsonAsync(HttpContext context, Document mesh, CancellationToken cancellation)
    {
        MeshState state = await ReadMeshAsync(context.RequestServices, mesh.Id, cancellation).ConfigureAwait(false);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool editor = LayerAccess.MayManage(mesh.Owner, current.Principal, current.Authorization);
        IModelConverter converter = context.RequestServices.GetRequiredService<IModelConverter>();
        string[] roles = ["globalID", "originX", "originY", "originZ", "translationX", "translationY", "translationZ", "scaleX", "scaleY", "scaleZ",
            "rotationX", "rotationY", "rotationZ", "rotationDeg"];
        JsonObject transformRoles = new() { ["globalID"] = "globalid" };
        for (int i = 1; i < roles.Length; i++)
        {
            transformRoles[roles[i]] = MeshNumbers[i - 1];
        }

        (string Name, string Type)[] mapFields =
        [
            ("ESRI3DO_PID", "esriFieldTypeGUID"), ("ESRI3DO_FLAGS", "esriFieldTypeInteger"), ("ESRI3DO_STATUS", "esriFieldTypeInteger"),
            ("ESRI3DO_NAME", "esriFieldTypeString"), ("ESRI3DO_TYPE", "esriFieldTypeString"), ("ESRI3DO_SIZE", "esriFieldTypeInteger"),
            ("ESRI3DO_SEQNO", "esriFieldTypeInteger"), ("ESRI3DO_CPLX", "esriFieldTypeInteger"), ("ESRI3DO_AHASH", "esriFieldTypeString"),
            ("ESRI3DO_SHASH", "esriFieldTypeString"), ("GlobalID", "esriFieldTypeGlobalID"),
        ];

        JsonObject layer = new()
        {
            ["currentVersion"] = 11.3, ["id"] = 0, ["name"] = mesh.Title, ["type"] = "Feature Layer", ["serviceItemId"] = mesh.Id,
            ["geometryType"] = "esriGeometryMultiPatch", ["hasZ"] = true, ["hasM"] = false,
            ["objectIdField"] = "OBJECTID", ["globalIdField"] = "globalid", ["displayField"] = "name",
            ["uniqueIdField"] = new JsonObject { ["name"] = "OBJECTID", ["isSystemMaintained"] = true },
            ["fields"] = JsonSerializer.SerializeToNode(MeshFields()),
            // A reader is told Query only; the owner and an administrator are told what they may do (ADR-023 §4c decides).
            ["capabilities"] = editor ? "Create,Delete,Query,Update,Editing,ChangeTracking,Uploads" : "Query,ChangeTracking",
            ["supportsApplyEditsWithGlobalIds"] = true, ["supportsRollbackOnFailureParameter"] = true, ["hasAttachments"] = false,
            ["supportedQueryFormats"] = "JSON", ["maxRecordCount"] = 2000, ["allowGeometryUpdates"] = true,
            ["spatialReference"] = new JsonObject { ["wkid"] = 4326, ["latestWkid"] = 4326 },
            ["serverGens"] = new JsonObject { ["minServerGen"] = 0, ["serverGen"] = state.Gen },
            ["editingInfo"] = new JsonObject { ["lastEditDate"] = state.Gen, ["dataLastEditDate"] = state.Gen, ["schemaLastEditDate"] = state.Gen },
            ["advancedQueryCapabilities"] = new JsonObject
            {
                ["supportsPagination"] = true, ["supportsReturningQueryExtent"] = true, ["supportsOrderBy"] = false, ["supportsDistinct"] = false,
                ["supportedMultipatchOptions"] = new JsonArray("extent"),
            },
            ["advancedEditingCapabilities"] = new JsonObject
            {
                ["supportsAsyncApplyEdits"] = false, ["supportsReturnEditResults"] = true, ["supportsApplyEditsbyUploadID"] = false,
            },
            ["infoFor3D"] = new JsonObject
            {
                ["supportedFormats"] = new JsonArray([new JsonArray("3D_glb", "model/gltf-binary", "glb", "Khronos Group glTF binary"),
                    .. converter.Formats.Select(f => (JsonNode)new JsonArray(f, "application/octet-stream", f[3..], f))]),
                ["queryFormats"] = new JsonArray("3D_glb"),
                ["editFormats"] = new JsonArray([(JsonNode)"3D_glb", .. converter.Formats.Select(f => (JsonNode)f)]),
                ["transformFieldRoles"] = transformRoles,
                ["assetTypeField"] = "ESRI3DO_TYPE", ["sourceHashField"] = "ESRI3DO_SHASH", ["parentFeatureField"] = "ESRI3DO_PID",
                ["assetHashAlgorithm"] = "SHA256",
                ["assetMapFields"] = new JsonArray([.. mapFields.Select(f => (JsonNode)new JsonObject
                {
                    ["name"] = f.Name, ["type"] = f.Type, ["alias"] = f.Name, ["nullable"] = f.Name != "ESRI3DO_PID", ["editable"] = f.Name != "GlobalID",
                    ["required"] = true,
                })]),
                ["assetMapFieldRoles"] = new JsonObject
                {
                    ["globalID"] = "GlobalID", ["parentGlobalId"] = "ESRI3DO_PID", ["assetName"] = "ESRI3DO_NAME", ["assetType"] = "ESRI3DO_TYPE",
                    ["conversionStatus"] = "ESRI3DO_STATUS", ["flags"] = "ESRI3DO_FLAGS", ["size"] = "ESRI3DO_SIZE", ["complexity"] = "ESRI3DO_CPLX",
                    ["sourceHash"] = "ESRI3DO_SHASH", ["assetHash"] = "ESRI3DO_AHASH", ["seqNo"] = "ESRI3DO_SEQNO",
                },
                ["tables"] = new JsonArray(
                    new JsonObject { ["id"] = 1, ["name"] = "assetMaps", ["role"] = "assetMaps" },
                    new JsonObject { ["id"] = 2, ["name"] = "assets", ["role"] = "assets" }),
            },
            ["supportsAsyncConvert3D"] = false,
            // The Editor builds its Create list from the layer's templates: without one it offered nothing to place
            // (measured on SDK 5.2, ADR-280 condition 5). One template, the transform at rest, as a 3D object layer has.
            ["templates"] = new JsonArray(new JsonObject
            {
                ["name"] = mesh.Title, ["description"] = string.Empty, ["drawingTool"] = "esriFeatureEditToolNone",
                ["prototype"] = new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["name"] = null, ["ESRI3DO_TYPE"] = null, ["ESRI3DO_OX"] = 0, ["ESRI3DO_OY"] = 0, ["ESRI3DO_OZ"] = 0,
                        ["ESRI3DO_TX"] = 0, ["ESRI3DO_TY"] = 0, ["ESRI3DO_TZ"] = 0, ["ESRI3DO_SX"] = 1, ["ESRI3DO_SY"] = 1, ["ESRI3DO_SZ"] = 1,
                        ["ESRI3DO_RX"] = 0, ["ESRI3DO_RY"] = 1, ["ESRI3DO_RZ"] = 0, ["ESRI3DO_RDEG"] = 0,
                        ["ESRI3DO_PID"] = "{00000000-0000-0000-0000-000000000000}",
                    },
                },
            }),
            ["types"] = new JsonArray(),
        };
        return layer;
    }

    private static async Task<IResult> MeshUploadsInfoAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation) =>
        await ReadableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is not null
            ? Results.Json(new { maxUploadFileSize = MaximumDocumentBytes(context) / (1 << 20) })
            : Results.Empty;

    // -------------------------------------------------------------------------------------------------------- writes

    /// <summary>Keeps glb models by their SHA-256. Another format is refused until the converter (ADR-282) is built.</summary>
    private static async Task<IResult> MeshUploadAssetsAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation)
    {
        if (await EditableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is not { } mesh)
        {
            return Results.Empty;
        }

        Dictionary<string, string> p = await MeshParamsAsync(context).ConfigureAwait(false);
        JsonArray? assets;
        try
        {
            assets = JsonNode.Parse(p.GetValueOrDefault("assets") ?? "[]") as JsonArray;
        }
        catch (JsonException)
        {
            return MeshError(400, "assets is not JSON.");
        }

        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        long limit = MaximumDocumentBytes(context);
        List<object> results = [];
        foreach (JsonNode? asset in assets ?? [])
        {
            string? type = asset?["assetType"]?.GetValue<string>();
            string? data = asset?["assetData"]?.GetValue<string>();
            IModelConverter converter = context.RequestServices.GetRequiredService<IModelConverter>();
            bool glbAsset = string.Equals(type, "3D_glb", StringComparison.OrdinalIgnoreCase);
            if (data is null || (!glbAsset && !converter.Formats.Contains(type ?? string.Empty, StringComparer.OrdinalIgnoreCase)))
            {
                string offered = string.Join(", ", converter.Formats.Prepend("3D_glb"));
                results.Add(new { success = false, error = new { code = 400, description = $"{type ?? "This asset"} is not taken here, as base64 assetData; these are: {offered}." } });
                continue;
            }

            byte[] glb;
            try
            {
                glb = Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                results.Add(new { success = false, error = new { code = 400, description = "assetData is not base64." } });
                continue;
            }

            if (glb.Length > limit)
            {
                results.Add(new { success = false, error = new { code = 413, description = $"The model is {glb.Length / (1 << 20)} MB, more than the {limit / (1 << 20)} MB a model may be here." } });
                continue;
            }

            // ADR-282: another format is converted to glb, and kept under the hash of what was uploaded, which is the hash
            // the client names in its asset map.
            string hash = Convert.ToHexStringLower(SHA256.HashData(glb));
            if (!glbAsset)
            {
                ModelConversion converted = await converter.ConvertAsync(glb, type!, cancellation).ConfigureAwait(false);
                if (converted.Glb is null)
                {
                    results.Add(new { success = false, error = new { code = 400, description = converted.Refusal } });
                    continue;
                }

                glb = converted.Glb;
            }

            try
            {
                if (GltfTriangles.Read(glb).Points.Count == 0)
                {
                    results.Add(new { success = false, error = new { code = 400, description = "The model has no triangles this server reads (no textures, Draco or quantized positions yet)." } });
                    continue;
                }
            }
            catch (FormatException e)
            {
                results.Add(new { success = false, error = new { code = 400, description = e.Message } });
                continue;
            }

            if (await resources.FindAsync(Guid.Parse(mesh.Id), MeshAssetPrefix + hash, cancellation).ConfigureAwait(false) is null)
            {
                await resources.WriteAsync(Guid.Parse(mesh.Id), MeshAssetPrefix + hash, "model/gltf-binary", glb, "private", replace: false, limit: long.MaxValue, cancellation).ConfigureAwait(false);
            }

            results.Add(new { success = true, assetHash = hash });
        }

        return Results.Json(new { uploadResults = results });
    }

    private static Task<IResult> MeshServiceApplyEditsAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation) =>
        MeshApplyEditsAsync(context, id, documents, cancellation);

    /// <summary>
    /// Adds, updates and deletes features by GlobalID or object id, with the asset maps that name their models. An asset
    /// map's hash must be a model this layer holds. The edits are applied together or not at all.
    /// </summary>
    private static async Task<IResult> MeshApplyEditsAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation)
    {
        if (await EditableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is not { } mesh)
        {
            return Results.Empty;
        }

        Dictionary<string, string> p = await MeshParamsAsync(context).ConfigureAwait(false);
        SemaphoreSlim gate = MeshLocks.GetOrAdd(mesh.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            MeshState state = await ReadMeshAsync(context.RequestServices, mesh.Id, cancellation).ConfigureAwait(false);
            IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
            long gen = NextGen(state);
            List<object> adds = [], updates = [], deletes = [], mapAdds = [], mapUpdates = [];

            JsonArray Array(string key)
            {
                string text = p.GetValueOrDefault(key) ?? string.Empty;
                return text.TrimStart().StartsWith('[') ? JsonNode.Parse(text) as JsonArray ?? [] : [];
            }

            foreach (JsonNode? add in Array("adds"))
            {
                JsonObject attributes = add?["attributes"] as JsonObject ?? [];
                string gid = Gid(attributes["globalid"]?.ToString() ?? attributes["GlobalID"]?.ToString()) ?? "{" + Guid.NewGuid().ToString("D").ToUpperInvariant() + "}";
                if (state.Features.Any(f => f.Gid == gid))
                {
                    adds.Add(new { objectId = -1, globalId = gid, success = false, error = new { code = 1000, description = "A feature with this GlobalID exists." } });
                    continue;
                }

                MeshFeature feature = new() { Oid = state.Next++, Gid = gid, Created = gen, Gen = gen, A = Kept(attributes) };
                state.Features.Add(feature);
                adds.Add(new { objectId = feature.Oid, globalId = gid, success = true });
            }

            foreach (JsonNode? update in Array("updates"))
            {
                JsonObject attributes = update?["attributes"] as JsonObject ?? [];
                string? gid = Gid(attributes["globalid"]?.ToString() ?? attributes["GlobalID"]?.ToString());
                long? oid = attributes["OBJECTID"] is JsonValue o && o.TryGetValue(out long l) ? l : null;
                MeshFeature? feature = state.Features.FirstOrDefault(f => (gid is not null && f.Gid == gid) || (gid is null && f.Oid == oid));
                if (feature is null)
                {
                    updates.Add(new { objectId = oid ?? -1, globalId = gid, success = false, error = new { code = 1019, description = "No feature with this id." } });
                    continue;
                }

                foreach (var (key, value) in Kept(attributes))
                {
                    feature.A[key] = value;
                }

                feature.Gen = gen;
                updates.Add(new { objectId = feature.Oid, globalId = feature.Gid, success = true });
            }

            string deleted = p.GetValueOrDefault("deletes") ?? string.Empty;
            IEnumerable<string> deleteIds = deleted.TrimStart().StartsWith('[')
                ? (JsonNode.Parse(deleted) as JsonArray ?? []).Select(n => n?.ToString() ?? string.Empty)
                : deleted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string key in deleteIds)
            {
                string? gid = Gid(key);
                long? oid = long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : null;
                MeshFeature? feature = state.Features.FirstOrDefault(f => (gid is not null && f.Gid == gid) || (oid is not null && f.Oid == oid));
                if (feature is null)
                {
                    deletes.Add(new { objectId = oid ?? -1, globalId = gid, success = false, error = new { code = 1019, description = "No feature with this id." } });
                    continue;
                }

                state.Features.Remove(feature);
                state.Maps.RemoveAll(m => m.Parent == feature.Gid);
                state.Deleted.Add(new MeshTombstone(feature.Oid, feature.Gid, gen));
                deletes.Add(new { objectId = feature.Oid, globalId = feature.Gid, success = true });
            }

            if (p.GetValueOrDefault("assetMaps") is { Length: > 0 } mapsText && JsonNode.Parse(mapsText) is JsonObject maps)
            {
                foreach ((string part, List<object> results) in new[] { ("adds", mapAdds), ("updates", mapUpdates) })
                {
                    foreach (JsonNode? map in maps[part] as JsonArray ?? [])
                    {
                        string? gid = Gid(map?["globalId"]?.ToString());
                        string? parent = Gid(map?["parentGlobalId"]?.ToString());
                        string? hash = map?["assetHash"]?.ToString()?.ToLowerInvariant();
                        bool held = hash is not null && await resources.FindAsync(Guid.Parse(mesh.Id), MeshAssetPrefix + hash, cancellation).ConfigureAwait(false) is not null;
                        if (gid is null || parent is null || !held || !state.Features.Any(f => f.Gid == parent))
                        {
                            return MeshError(400, held || hash is null
                                ? "An asset map names a feature this edit does not hold."
                                : "An asset map names a model that was not uploaded to this layer. Nothing was changed.");
                        }

                        state.Maps.RemoveAll(m => m.Gid == gid);
                        state.Maps.Add(new MeshMap(gid, parent, map?["assetName"]?.ToString() ?? "geometry.glb", hash!, gen));
                        state.Features.First(f => f.Gid == parent).Gen = gen;
                        results.Add(new { globalId = gid, success = true });
                    }
                }
            }

            await WriteMeshAsync(context.RequestServices, mesh.Id, state, cancellation).ConfigureAwait(false);
            return Results.Json(new
            {
                addResults = adds, updateResults = updates, deleteResults = deletes,
                assetMaps = new { addResults = mapAdds, updateResults = mapUpdates, deleteResults = System.Array.Empty<object>() },
                editMoment = gen,
            });
        }
        catch (JsonException)
        {
            return MeshError(400, "An edit is not JSON. Nothing was changed.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The attributes a feature keeps from an edit: the layer's own, numbers as numbers and texts as texts.</summary>
    private static Dictionary<string, JsonElement> Kept(JsonObject attributes)
    {
        Dictionary<string, JsonElement> kept = [];
        foreach (var (key, value) in attributes)
        {
            string? name = MeshNumbers.Concat(MeshTexts).FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));
            if (name is null || value is null)
            {
                continue;
            }

            JsonElement element = JsonSerializer.SerializeToElement(value);
            if (MeshNumbers.Contains(name) ? element.ValueKind == JsonValueKind.Number : element.ValueKind == JsonValueKind.String)
            {
                kept[name] = element;
            }
        }

        return kept;
    }

    // --------------------------------------------------------------------------------------------------------- reads

    /// <summary>
    /// What changed since a server generation: the ids added, updated and deleted. Maps SDK 5.2 asks with the scene layer's
    /// update stamp, so it learns which features the cache does not have (ADR-280 §4).
    /// </summary>
    private static async Task<IResult> MeshExtractChangesAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation)
    {
        if (await ReadableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is not { } mesh)
        {
            return Results.Empty;
        }

        Dictionary<string, string> p = await MeshParamsAsync(context).ConfigureAwait(false);
        long since = 0;
        try
        {
            if (JsonNode.Parse(p.GetValueOrDefault("layerServerGens") ?? "[]") is JsonArray gens && gens.FirstOrDefault()?["serverGen"] is JsonValue g)
            {
                since = g.GetValue<long>();
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return MeshError(400, "layerServerGens is not a list of {id, serverGen}.");
        }

        MeshState state = await ReadMeshAsync(context.RequestServices, mesh.Id, cancellation).ConfigureAwait(false);
        long[] added = [.. state.Features.Where(f => f.Created > since).Select(f => f.Oid)];
        long[] updated = [.. state.Features.Where(f => f.Created <= since && f.Gen > since).Select(f => f.Oid)];
        long[] gone = [.. state.Deleted.Where(d => d.Gen > since).Select(d => d.Oid)];
        bool idsOnly = !string.Equals(p.GetValueOrDefault("returnIdsOnly"), "false", StringComparison.OrdinalIgnoreCase);
        object edits = idsOnly
            ? new { id = 0, objectIds = new { adds = added, updates = updated, deletes = gone } }
            : new { id = 0, features = new
            {
                adds = state.Features.Where(f => added.Contains(f.Oid)).Select(f => MeshFeatureJson(f, false, context, mesh.Id, state)).ToArray(),
                updates = state.Features.Where(f => updated.Contains(f.Oid)).Select(f => MeshFeatureJson(f, false, context, mesh.Id, state)).ToArray(),
                deleteIds = gone,
            } };
        return Results.Json(new { layerServerGens = new[] { new { id = 0, serverGen = state.Gen } }, edits = new[] { edits } });
    }

    /// <summary>
    /// The layer's features: every one (<c>1=1</c>) or those named by object id, with their extent as geometry. query3D
    /// adds each feature's asset maps with the address of its model.
    /// </summary>
    private static async Task<IResult> MeshQueryAsync(HttpContext context, string id, IDocumentStore documents, bool threeD, CancellationToken cancellation)
    {
        if (await ReadableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is not { } mesh)
        {
            return Results.Empty;
        }

        Dictionary<string, string> p = await MeshParamsAsync(context).ConfigureAwait(false);
        MeshState state = await ReadMeshAsync(context.RequestServices, mesh.Id, cancellation).ConfigureAwait(false);
        IEnumerable<MeshFeature> features = state.Features;
        string where = (p.GetValueOrDefault("where") ?? "1=1").Trim();
        if (where.Length > 0 && where.Replace(" ", string.Empty, StringComparison.Ordinal) != "1=1")
        {
            return MeshError(400, "This 3D object layer answers where=1=1 and objectIds; another where clause is not read here yet.");
        }

        if (p.GetValueOrDefault("objectIds") is { Length: > 0 } named)
        {
            HashSet<long> wanted = [.. named.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : -1)];
            features = features.Where(f => wanted.Contains(f.Oid));
        }

        if (string.Equals(p.GetValueOrDefault("returnCountOnly"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { count = features.Count() });
        }

        if (string.Equals(p.GetValueOrDefault("returnIdsOnly"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { objectIdFieldName = "OBJECTID", objectIds = features.Select(f => f.Oid).ToArray() });
        }

        bool geometry = !string.Equals(p.GetValueOrDefault("returnGeometry"), "false", StringComparison.OrdinalIgnoreCase);
        List<object> answered = [];
        foreach (MeshFeature feature in features.Take(2000))
        {
            answered.Add(MeshFeatureJson(feature, threeD, context, mesh.Id, state, geometry
                ? await MeshExtentAsync(context.RequestServices, mesh.Id, state, feature, cancellation).ConfigureAwait(false)
                : null));
        }

        return Results.Json(new
        {
            objectIdFieldName = "OBJECTID", globalIdFieldName = "globalid", geometryType = "esriGeometryMultiPatch", hasZ = true,
            spatialReference = new { wkid = 4326, latestWkid = 4326 }, fields = MeshFields(), features = answered,
        });
    }

    private static async Task<object?> MeshExtentAsync(IServiceProvider services, string id, MeshState state, MeshFeature feature, CancellationToken cancellation)
    {
        if (state.Maps.Where(m => m.Parent == feature.Gid).OrderBy(m => m.Gen).LastOrDefault() is not { } map
            || await services.GetRequiredService<IItemResourceStore>().FindAsync(Guid.Parse(id), MeshAssetPrefix + map.Hash, cancellation).ConfigureAwait(false) is not { } asset
            || SceneLayerPackageWriter.ExtentOf(ModelOf(feature, asset.Content, map.Hash)) is not { } e)
        {
            return null;
        }

        return new { xmin = e.West, ymin = e.South, xmax = e.East, ymax = e.North, zmin = e.Low, zmax = e.High, spatialReference = new { wkid = 4326 } };
    }

    private static object MeshFeatureJson(MeshFeature feature, bool threeD, HttpContext context, string id, MeshState state, object? geometry = null)
    {
        Dictionary<string, object?> attributes = new(StringComparer.Ordinal) { ["OBJECTID"] = feature.Oid, ["globalid"] = feature.Gid };
        foreach (var (key, value) in feature.A)
        {
            attributes[key] = value;
        }

        if (!threeD)
        {
            return new { attributes, geometry };
        }

        string root = MeshAddressOf(context.Request, id);
        return new
        {
            attributes, geometry,
            assetMaps = state.Maps.Where(m => m.Parent == feature.Gid).Select(m => new
            {
                globalId = m.Gid, parentGlobalId = m.Parent, assetName = m.Name, assetHash = m.Hash, assetType = "3D_glb",
                conversionStatus = "COMPLETED", flags = System.Array.Empty<string>(), assetURL = $"{root}/0/assets/{m.Hash}",
            }).ToArray(),
        };
    }

    private static async Task<IResult> MeshQueryAssetsAsync(HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation)
    {
        if (await ReadableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is not { } mesh)
        {
            return Results.Empty;
        }

        Dictionary<string, string> p = await MeshParamsAsync(context).ConfigureAwait(false);
        string[] hashes;
        try
        {
            hashes = [.. (JsonNode.Parse(p.GetValueOrDefault("assetHashes") ?? "[]") as JsonArray ?? []).Select(n => n?.ToString()?.ToLowerInvariant() ?? string.Empty)];
        }
        catch (JsonException)
        {
            return MeshError(400, "assetHashes is not a JSON list.");
        }

        IItemResourceStore resources = context.RequestServices.GetRequiredService<IItemResourceStore>();
        string root = MeshAddressOf(context.Request, mesh.Id);
        List<object> found = [];
        foreach (string hash in hashes.Distinct())
        {
            if (await resources.FindAsync(Guid.Parse(mesh.Id), MeshAssetPrefix + hash, cancellation).ConfigureAwait(false) is not null)
            {
                found.Add(new { assetHash = hash, contentType = "model/gltf-binary", assetName = "geometry.glb", assetURL = $"{root}/0/assets/{hash}" });
            }
        }

        return Results.Json(new { assets = found });
    }

    private static async Task<IResult> MeshAssetAsync(HttpContext context, string id, string hash, IDocumentStore documents, CancellationToken cancellation)
    {
        if (await ReadableMeshAsync(context, id, documents, cancellation).ConfigureAwait(false) is not { } mesh)
        {
            return Results.Empty;
        }

        return await context.RequestServices.GetRequiredService<IItemResourceStore>()
            .FindAsync(Guid.Parse(mesh.Id), MeshAssetPrefix + (hash ?? string.Empty).ToLowerInvariant(), cancellation).ConfigureAwait(false) is { } asset
            ? Results.Bytes(asset.Content, "model/gltf-binary")
            : MeshError(404, "No such model in this layer.");
    }
}
