using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// <b>A service's own admin document, and ArcGIS's <c>updateDefinition</c></b> — the ArcGIS gap review of 2026-10-17
/// found <c>GET /rest/admin/services/hosted/{name}/FeatureServer</c> a 404, so the ArcGIS API for Python's
/// <c>manager.update_definition</c> and Pro's service settings had nothing to call.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it changes is what this server keeps, through the writers its own screens use</b>: <c>capabilities</c>
/// is the editing the owner offers (Create, Update, Delete, Extract — Query is always offered) inside the
/// administrator's ceiling; <c>serviceDescription</c> and <c>copyrightText</c> are the description and credits;
/// <c>maxRecordCount</c> is the page size, which only an administrator sets here, as on the Limits page (ADR-084).
/// </para>
/// <para>
/// <b>Since 2026-11-03 also the derived keys and editor tracking</b> — see <see cref="UpdateDefinitionAsync"/>.
/// <b>Every other key is refused by name, and nothing is written</b>: a caller told <c>success</c> about a definition
/// that was half applied builds on the half that is not there (ADR-008 §2). The document is checked whole before any
/// write.
/// </para>
/// </remarks>
internal static partial class ArcGisAdminEndpoints
{
    private static readonly HashSet<string> DefinitionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "capabilities", "serviceDescription", "description", "copyrightText", "maxRecordCount",
        "hasStaticData", "allowGeometryUpdates", "syncEnabled", "name", "editorTrackingInfo",
        "ownershipBasedAccessControlForFeatures",
    };

    /// <summary>Maps the service document and updateDefinition, at the root and under a folder.</summary>
    private static void MapService(WebApplication app)
    {
        foreach (string folder in (string[])["", "/{folder}"])
        {
            foreach (string service in (string[])["{serviceName}/FeatureServer", "{serviceName}.FeatureServer"])
            {
                app.MapMethods($"/rest/admin/services{folder}/{service}", ["GET", "POST"], ServiceDocumentAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapPost($"/rest/admin/services{folder}/{service}/updateDefinition", UpdateDefinitionAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapPost($"/rest/admin/services{folder}/{service}/addToDefinition", AddLayersToDefinitionAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapMethods($"/rest/admin/services{folder}/{service}/{{layerId:int}}", ["GET", "POST"], LayerDocumentAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
            }
        }
    }

    /// <summary>The service as its owner or an administrator manages it.</summary>
    private static async Task ServiceDocumentAsync(
        HttpContext context, string serviceName, PostgresLayerCatalog layers, IAdminCatalog catalog, CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "read the administration of", cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (PublishedService service, string? folder) = found;
        ServiceStewardship? stewardship = await catalog.FindStewardshipAsync(service.Name, folder, cancellation).ConfigureAwait(false);
        ServiceCapabilityLimits? limits = await catalog.FindServiceCapabilitiesAsync(service.Name, folder, cancellation)
            .ConfigureAwait(false);

        await Results.Ok(new
        {
            currentVersion = Graticula.Api.ArcGis.FeatureServerMetadataWriter.CurrentVersion,
            serviceDescription = service.Description ?? string.Empty,
            copyrightText = service.CopyrightText ?? string.Empty,
            capabilities = Capabilities(stewardship),
            maxRecordCount = limits?.Cost.MaximumRecordCount,

            // The portal item this service is, as the REST document names it — the ArcGIS API for Python's create_view
            // reads it from the manager's properties and stops without it (ADR-257 §11).
            serviceItemId = PortalEndpoints.ItemIdOf(service, "FeatureServer"),
            layers = service.Layers.OrderBy(l => l.LayerIndex).Select(l => new { id = l.LayerIndex, name = l.Definition.Name }),
            tables = service.Tables.Select(l => new { id = l.LayerIndex, name = l.Definition.Name }),
            adminServiceInfo = new
            {
                name = service.Name,
                type = "FeatureServer",
                status = new { configuredState = service.Status.ToString().ToUpperInvariant(), realTimeState = service.Status.ToString().ToUpperInvariant() },
                database = new { datasource = new { name = service.LayersAndTables.Count > 0 ? service.LayersAndTables[0].DataSourceName : null } },
            },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// A layer as its service's owner manages it — ADR-257 §11: the ArcGIS API for Python's <c>create_view</c> names each
    /// view layer from <c>layer.manager.properties</c>' <c>id</c> and <c>name</c>, which this address answered 404 for.
    /// The layer's own document is its REST one; this says what the administration adds: what it is a view of.
    /// </summary>
    private static async Task LayerDocumentAsync(
        HttpContext context, string serviceName, int layerId, PostgresLayerCatalog layers, CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "read the administration of", cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (PublishedService service, _) = found;

        if (service.LayersAndTables.FirstOrDefault(l => l.LayerIndex == layerId) is not { } layer)
        {
            await RefuseAsync(context, 404, $"There is no layer {layerId} in '{serviceName}'.").ConfigureAwait(false);
            return;
        }

        PublishedService? source = service.ViewOf is { } of
            ? (await layers.ListServicesAsync(cancellation).ConfigureAwait(false)).FirstOrDefault(s => s.Id == of)
            : null;

        await Results.Ok(new
        {
            currentVersion = Graticula.Api.ArcGis.FeatureServerMetadataWriter.CurrentVersion,
            id = layer.LayerIndex,
            name = layer.Definition.Name,
            type = layer.GeometryType is null ? "Table" : "Feature Layer",
            geometryType = layer.GeometryType is { } shape ? Graticula.Api.ArcGis.MapServerMetadataWriter.GeometryName(shape) : null,
            viewDefinitionQuery = layer.ViewDefinition,
            adminLayerInfo = new
            {
                viewLayerDefinition = source is null ? null : new
                {
                    sourceServiceName = source.Name,
                    sourceLayerId = layer.LayerIndex,
                    sourceLayerFields = "*",
                },
            },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// ArcGIS's <c>updateDefinition</c>, for the keys this server keeps, the keys it derives, and every other documented
    /// key refused by name — ADR-257 §12.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Four keys until 2026-11-03</b>, and any fifth refused the whole request: the typical script
    /// <c>{"capabilities":"Create,Update,Delete,Query,Editing","hasStaticData":false}</c> was refused for
    /// <c>hasStaticData</c> (the ArcGIS veteran's review). ArcGIS's <i>Update Definition (Feature Service)</i> documents
    /// that and <c>allowGeometryUpdates</c>, <c>editorTrackingInfo</c>, <c>syncEnabled</c>, <c>name</c>, <c>units</c>,
    /// <c>xssPreventionInfo</c>, <c>initialExtent</c>, <c>preferredTimeReference</c>,
    /// <c>ownershipBasedAccessControlForFeatures</c> and <c>layerOverridesEnabled</c> beside the four.
    /// </para>
    /// <para>
    /// <b>A derived key is accepted when it says what the rest of the definition implies, and refused with the reason
    /// when it contradicts it</b> — <c>hasStaticData</c> (no edit offered), <c>allowGeometryUpdates</c> (Update offered;
    /// this server has no attribute-only update), <c>syncEnabled</c> false, <c>name</c> as it is. Agreeing with the server
    /// changes nothing and is not an error; contradicting it would be a setting this server cannot hold, and saying
    /// <c>success</c> to it is the silent half-application ADR-008 §2 forbids.
    /// </para>
    /// </remarks>
    private static async Task UpdateDefinitionAsync(
        HttpContext context, string serviceName, PostgresLayerCatalog layers, IAdminCatalog catalog, IAuditLog audit,
        ServiceContexts contexts, PostGisImporter importer, LayerConnections connections, ITileCache tiles,
        CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "update the definition of", cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (PublishedService service, string? folder) = found;
        IFormCollection form = await FormAsync(context, cancellation).ConfigureAwait(false);
        JsonElement definition;

        try
        {
            definition = JsonDocument.Parse(form["updateDefinition"].ToString() is { Length: > 0 } text ? text : "{}").RootElement.Clone();
        }
        catch (JsonException e)
        {
            await RefuseAsync(context, 400, $"'updateDefinition' is not valid JSON: {e.Message}").ConfigureAwait(false);
            return;
        }

        if (definition.ValueKind != JsonValueKind.Object)
        {
            await RefuseAsync(context, 400, "'updateDefinition' is an object of the service properties to change.").ConfigureAwait(false);
            return;
        }

        // Documented keys this server does not keep, each with its reason; then anything else, by name.
        string[] notKept = [.. definition.EnumerateObject().Select(p => p.Name).Where(NotKept.ContainsKey)];
        if (notKept.Length > 0)
        {
            await RefuseAsync(context, 400,
                string.Join(" ", notKept.Select(n => $"'{n}': {NotKept[n]}")) + " " + Accepted).ConfigureAwait(false);
            return;
        }

        string[] unknown = [.. definition.EnumerateObject().Select(p => p.Name).Where(n => !DefinitionKeys.Contains(n))];
        if (unknown.Length > 0)
        {
            await RefuseAsync(context, 400,
                $"{string.Join(", ", unknown)}: not a key this server's updateDefinition knows. " + Accepted).ConfigureAwait(false);
            return;
        }

        // ---- everything is checked before anything is written ----
        ServiceStewardship? now = await catalog.FindStewardshipAsync(service.Name, folder, cancellation).ConfigureAwait(false);
        string[]? offered = null;
        if (definition.TryGetProperty("capabilities", out JsonElement capabilities))
        {
            HashSet<string> asked = new(
                (capabilities.GetString() ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            if (asked.Remove("Editing"))
            {
                if (!asked.Overlaps(["Create", "Update", "Delete"]))
                {
                    asked.UnionWith(["Create", "Update", "Delete"]);
                }
            }

            asked.Remove("Query");
            string[] other = [.. asked.Where(op => !AdminEndpoints.OwnerOperations.Contains(op, StringComparer.OrdinalIgnoreCase))];
            if (other.Length > 0)
            {
                await RefuseAsync(context, 400,
                    $"{string.Join(", ", other)}: not offered here — Sync and ChangeTracking need offline replicas, which this "
                    + "server does not serve (ADR-082). Nothing was changed.").ConfigureAwait(false);
                return;
            }

            offered = [.. AdminEndpoints.OwnerOperations.Where(op => asked.Contains(op))];
            string[] over = now?.Ceiling is { } ceiling ? [.. offered.Where(op => !ceiling.Contains(op))] : [];
            if (over.Length > 0)
            {
                await RefuseAsync(context, 400,
                    $"The server administrator does not allow {string.Join(", ", over)} on this service, so it cannot be offered. "
                    + "Nothing was changed.").ConfigureAwait(false);
                return;
            }
        }

        // The edits offered once this definition is applied — what hasStaticData and allowGeometryUpdates are derived from.
        IReadOnlyList<string> editsAfter = offered ?? now?.EditingOffered ?? now?.Ceiling ?? AdminEndpoints.OwnerOperations;
        bool edited = editsAfter.Any(op => op is "Create" or "Update" or "Delete");

        if (Derived(definition, "hasStaticData", !edited) is { } staticWrong)
        {
            await RefuseAsync(context, 400, staticWrong + (edited
                ? " It is true only where no edit is offered: send capabilities \"Query\" to make the service read-only, and "
                  + "hasStaticData follows."
                : " It is false only where an edit is offered: send the edits in capabilities, and hasStaticData follows.")
                + " Nothing was changed.").ConfigureAwait(false);
            return;
        }

        if (Derived(definition, "allowGeometryUpdates", editsAfter.Contains("Update")) is { } geometryWrong)
        {
            await RefuseAsync(context, 400, geometryWrong
                + " An update here may change a feature's shape and its attributes alike, so geometry updates are allowed "
                + "exactly where Update is offered; change capabilities instead. Nothing was changed.").ConfigureAwait(false);
            return;
        }

        if (Derived(definition, "syncEnabled", false) is { } syncWrong)
        {
            await RefuseAsync(context, 400, syncWrong
                + " Offline sync (createReplica) is not served here (ADR-082). Nothing was changed.").ConfigureAwait(false);
            return;
        }

        if (definition.TryGetProperty("name", out JsonElement named)
            && !string.Equals(named.ValueKind == JsonValueKind.String ? named.GetString() : null, service.Name, StringComparison.OrdinalIgnoreCase))
        {
            await RefuseAsync(context, 400,
                $"'name' is '{service.Name}' and is not changed by updateDefinition here: a service is renamed from its item "
                + "in Studio, which moves its address with it. Nothing was changed.").ConfigureAwait(false);
            return;
        }

        int? pageSize = null;
        if (definition.TryGetProperty("maxRecordCount", out JsonElement records))
        {
            if (records.ValueKind != JsonValueKind.Number || !records.TryGetInt32(out int count) || count < 1)
            {
                await RefuseAsync(context, 400, "'maxRecordCount' is a whole number of at least 1. Nothing was changed.").ConfigureAwait(false);
                return;
            }

            if (!context.Features.Get<RequestPrincipal>()!.Authorization.Allows(Privilege.AdminManageServer))
            {
                await RefuseAsync(context, 403,
                    "A service's page size is the server administrator's to set, as on its Limits page (ADR-084). Nothing was changed.")
                    .ConfigureAwait(false);
                return;
            }

            pageSize = count;
        }

        // editorTrackingInfo, and its layer-level twin: the same two facts per layer, read before either is decided.
        List<(PublishedLayer Layer, LayerDescription Described)> described = [];
        foreach (PublishedLayer layer in service.LayersAndTables)
        {
            (_, LayerDescription about) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
            described.Add((layer, about));
        }

        bool trackedNow = described.Count > 0 && described.All(d => d.Described.Tracking.Any);
        bool ownNow = described.Count > 0 && described.All(d => d.Layer.EditOwnOnly);
        bool? track = null;
        bool? own = null;

        foreach (string key in (string[])["editorTrackingInfo", "ownershipBasedAccessControlForFeatures"])
        {
            if (!definition.TryGetProperty(key, out JsonElement info))
            {
                continue;
            }

            if (Tracking(key, info, ref track, ref own, ownNow) is { } trackingWrong)
            {
                await RefuseAsync(context, 400, trackingWrong + " Nothing was changed.").ConfigureAwait(false);
                return;
            }
        }

        bool trackAfter = track ?? trackedNow;
        bool ownAfter = own ?? ownNow;

        if (ownAfter && !trackAfter)
        {
            await RefuseAsync(context, 400,
                "Holding editors to their own features needs to know who added each one: enableOwnershipAccessControl true "
                + "needs enableEditorTracking true. Nothing was changed.").ConfigureAwait(false);
            return;
        }

        if (track is { } turned && turned != trackedNow && service.ViewOf is not null)
        {
            await RefuseAsync(context, 400,
                "A view records edits in its source's columns: turn editor tracking on or off on the source service. "
                + "Nothing was changed.").ConfigureAwait(false);
            return;
        }

        if (ownAfter && described.FirstOrDefault(d => d.Described.Tracking.Any && d.Described.Tracking.Creator is null) is { Layer: { } noCreator })
        {
            await RefuseAsync(context, 409,
                $"'{noCreator.Definition.Name}' records edits without a creator column, so no feature is anybody's own. Give a "
                + "column the creator's role on its Fields page first. Nothing was changed.").ConfigureAwait(false);
            return;
        }

        string? Text(string name) => definition.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
        bool describes = definition.TryGetProperty("serviceDescription", out _) || definition.TryGetProperty("description", out _);
        string? description = Text("serviceDescription") ?? Text("description");

        // ---- and written ----
        if (offered is not null)
        {
            await catalog.SetEditingOfferedAsync(service.Name, folder, offered, cancellation).ConfigureAwait(false);
        }

        if (describes)
        {
            await catalog.SetServiceDescriptionAsync(service.Name, folder, string.IsNullOrWhiteSpace(description) ? null : description, cancellation)
                .ConfigureAwait(false);
        }

        if (definition.TryGetProperty("copyrightText", out _))
        {
            string? credits = Text("copyrightText");
            await catalog.SetServiceCreditsAsync(service.Name, folder, string.IsNullOrWhiteSpace(credits) ? null : credits, cancellation)
                .ConfigureAwait(false);
        }

        if (pageSize is { } size
            && await catalog.FindServiceCapabilitiesAsync(service.Name, folder, cancellation).ConfigureAwait(false) is { } limits)
        {
            ServiceCostCeilings cost = limits.Cost;
            await catalog.SetServiceCapabilitiesAsync(service.Name, folder, limits.With(new ServiceCostCeilings(
                maximumRecordCount: size,
                maximumResponseBytes: cost.MaximumResponseBytes,
                maximumRequestBytes: cost.MaximumRequestBytes,
                maximumEditsPerTransaction: cost.MaximumEditsPerTransaction,
                requestDeadline: cost.RequestDeadline)), cancellation).ConfigureAwait(false);
        }

        // <b>Editor tracking, layer by layer, through the work Studio's own buttons do</b> (ADR-064, ADR-115, ADR-212).
        // The order keeps every step valid: ownership off before tracking stops, tracking on before ownership starts.
        // Each layer's columns are their own ALTER, so a refusal after the first layer leaves the layers before it
        // changed — said in the refusal, as Studio's button says it, rather than reported as success.
        List<string> done = [];
        foreach ((PublishedLayer layer, LayerDescription about) in described)
        {
            if (own is false && layer.EditOwnOnly)
            {
                await catalog.SetEditOwnOnlyAsync(layer.Id, false, cancellation).ConfigureAwait(false);
            }

            if (track is false && about.Tracking.Any
                && await HostedDataEndpoints.StopTrackingOfAsync(layer, contexts, tiles, catalog, cancellation, ownershipCleared: own is false)
                    .ConfigureAwait(false) is { } stopRefused)
            {
                await RefuseAsync(context, 409, Partly(done, stopRefused)).ConfigureAwait(false);
                return;
            }

            if (track is true && !about.Tracking.Any)
            {
                HostedDataEndpoints.TrackingOutcome outcome = await HostedDataEndpoints
                    .TrackEditsOfAsync(layer, importer, contexts, tiles, catalog, connections, cancellation).ConfigureAwait(false);

                if (outcome.Code != 0)
                {
                    await RefuseAsync(context, outcome.Code, Partly(done, outcome.Message!)).ConfigureAwait(false);
                    return;
                }
            }

            if (own is true && !layer.EditOwnOnly)
            {
                await catalog.SetEditOwnOnlyAsync(layer.Id, true, cancellation).ConfigureAwait(false);
            }

            done.Add(layer.Definition.Name);
        }

        foreach (PublishedLayer layer in service.LayersAndTables)
        {
            contexts.Forget(layer);
        }

        await AdminEndpoints.AuditAsync(context, audit, "service.updateDefinition", service.Name,
            AdminEndpoints.Detail(new { folder, keys = definition.EnumerateObject().Select(p => p.Name).ToArray() }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Ok(new { success = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>What the refusals name as accepted.</summary>
    private const string Accepted =
        "This server's updateDefinition changes capabilities, serviceDescription, copyrightText, maxRecordCount and "
        + "editorTrackingInfo, and accepts hasStaticData, allowGeometryUpdates, syncEnabled, name and "
        + "ownershipBasedAccessControlForFeatures where they agree with the rest. Nothing was changed.";

    /// <summary>The documented keys this server does not keep, each with why.</summary>
    private static readonly Dictionary<string, string> NotKept = new(StringComparer.OrdinalIgnoreCase)
    {
        ["syncCapabilities"] = "offline sync is not served here (ADR-082).",
        ["xssPreventionInfo"] = "not a setting here — attribute values are stored as sent and every face writes them escaped "
            + "for its format.",
        ["initialExtent"] = "not a setting here — a service's extent is computed from its data.",
        ["fullExtent"] = "not a setting here — a service's extent is computed from its data.",
        ["units"] = "not a setting here — the units follow the service's spatial reference.",
        ["spatialReference"] = "not changed by updateDefinition here — a service's reference is set on its Settings page.",
        ["preferredTimeReference"] = "not kept here — dates are stored and answered in UTC.",
        ["layerOverridesEnabled"] = "not kept here — capabilities and editing are the service's, never a layer's.",
    };

    /// <summary>
    /// A derived key's value checked against what the server derives: null when it agrees or is absent, otherwise the
    /// sentence that says so. ArcGIS's own example sends <c>allowGeometryUpdates</c> as the string <c>"true"</c>.
    /// </summary>
    private static string? Derived(JsonElement definition, string key, bool derived)
    {
        if (!definition.TryGetProperty(key, out JsonElement value))
        {
            return null;
        }

        bool? said = Flag(value);
        return said is null
            ? $"'{key}' is true or false."
            : said == derived ? null : $"'{key}' {(said.Value ? "true" : "false")} contradicts this service, where it is {(derived ? "true" : "false")}.";
    }

    /// <summary>A JSON boolean, or the strings "true" and "false", or null.</summary>
    private static bool? Flag(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// <c>editorTrackingInfo</c> or <c>ownershipBasedAccessControlForFeatures</c> read into the two facts this server
    /// keeps — whether edits are recorded and whether editors are held to their own features — or the sentence that
    /// says why it cannot be held. ADR-115: when held, editors may neither update nor delete others' features, and
    /// everybody who may read the layer may query it; a signed-out caller never edits (ADR-075).
    /// </summary>
    private static string? Tracking(string key, JsonElement info, ref bool? track, ref bool? own, bool ownNow)
    {
        if (info.ValueKind != JsonValueKind.Object)
        {
            return $"'{key}' is an object.";
        }

        bool layerForm = key == "ownershipBasedAccessControlForFeatures";
        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase)
        {
            "allowOthersToQuery", "allowOthersToUpdate", "allowOthersToDelete",
            "allowAnonymousToQuery", "allowAnonymousToUpdate", "allowAnonymousToDelete",
        };

        if (!layerForm)
        {
            known.UnionWith(["enableEditorTracking", "enableOwnershipAccessControl"]);
        }

        string[] strange = [.. info.EnumerateObject().Select(p => p.Name).Where(n => !known.Contains(n))];
        if (strange.Length > 0)
        {
            return $"'{key}' has {string.Join(", ", strange)}, which this server does not keep; it reads "
                + $"{string.Join(", ", known.Order(StringComparer.Ordinal))}.";
        }

        Dictionary<string, bool> said = [];
        foreach (JsonProperty property in info.EnumerateObject())
        {
            if (Flag(property.Value) is not { } flag)
            {
                return $"'{key}.{property.Name}' is true or false.";
            }

            said[property.Name.ToLowerInvariant()] = flag;
        }

        if (said.TryGetValue("enableeditortracking", out bool tracking))
        {
            track = tracking;
        }

        if (said.TryGetValue("enableownershipaccesscontrol", out bool owning))
        {
            own = owning;
        }

        // The layer form says ownership by the others-may flags alone: both false is held, both true is not.
        if (layerForm && said.TryGetValue("allowotherstoupdate", out bool othersUpdate)
            && said.TryGetValue("allowotherstodelete", out bool othersDelete) && othersUpdate == othersDelete)
        {
            own = !othersUpdate;
        }

        bool owned = own ?? ownNow;

        foreach (string what in (string[])["Update", "Delete"])
        {
            if (said.TryGetValue("allowothersto" + what.ToLowerInvariant(), out bool others) && others == owned)
            {
                return owned
                    ? $"'{key}.allowOthersTo{what}' true while editors are held to their own features: this server holds "
                      + "them for update and delete together. Send enableOwnershipAccessControl false to let others edit."
                    : $"'{key}.allowOthersTo{what}' false while editors are not held to their own features: send "
                      + "enableOwnershipAccessControl true, which holds them for update and delete together.";
            }
        }

        if (said.TryGetValue("allowotherstoquery", out bool othersQuery) && !othersQuery)
        {
            return $"'{key}.allowOthersToQuery' false is not kept here: whoever may read the layer queries all of it.";
        }

        if (said.TryGetValue("allowanonymoustoquery", out bool anonymousQuery) && !anonymousQuery)
        {
            return $"'{key}.allowAnonymousToQuery' false is not kept here: who may query is the service's sharing.";
        }

        foreach (string what in (string[])["Update", "Delete"])
        {
            if (said.TryGetValue("allowanonymousto" + what.ToLowerInvariant(), out bool anonymous) && anonymous)
            {
                return $"'{key}.allowAnonymousTo{what}' true is not kept here: a signed-out caller never edits (ADR-075).";
            }
        }

        return null;
    }

    /// <summary>A refusal after some layers were changed, which says which.</summary>
    private static string Partly(List<string> done, string why) =>
        done.Count == 0
            ? why
            : $"{why} The definition's other keys were applied, and editor tracking was changed on {string.Join(", ", done)} "
              + "before this one; send it again once the cause is repaired.";

    /// <summary>The service in the route's folder, when the caller owns or administers it; otherwise the refusal is written.</summary>
    private static async Task<(PublishedService Service, string? Folder)?> ManagedServiceAsync(
        HttpContext context, PostgresLayerCatalog layers, string serviceName, string what, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return null;
        }

        string? folder = context.Request.RouteValues.TryGetValue("folder", out object? given) ? given as string : null;

        if (!await AdminEndpoints.ManagesServiceAsync(context, layers, folder, serviceName, what, cancellation).ConfigureAwait(false)
            || await layers.FindServiceAsync(folder, serviceName, cancellation).ConfigureAwait(false) is not { } service)
        {
            return null;
        }

        return (service, folder);
    }

    /// <summary>The capability string ArcGIS writes: Query, and what the owner offers.</summary>
    private static string Capabilities(ServiceStewardship? stewardship)
    {
        IReadOnlyList<string> offered = stewardship?.EditingOffered ?? stewardship?.Ceiling ?? AdminEndpoints.OwnerOperations;
        List<string> all = ["Query", .. offered.Where(op => op != "Query")];
        if (offered.Any(op => op is "Create" or "Update" or "Delete"))
        {
            all.Add("Editing");
        }

        return string.Join(",", all);
    }
}
