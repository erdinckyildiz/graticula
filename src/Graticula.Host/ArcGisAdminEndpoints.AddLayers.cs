using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// <b>A hosted service's <c>addToDefinition</c></b> — ADR-257 §11, owner decision 2026-11-02: the second half of the
/// ArcGIS API for Python's <c>create_service</c> and <c>create_view</c>, which make the service empty and then send its
/// layers here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Doors, as the rest of this class is.</b> A layer with a geometry type and fields is Studio's
/// <c>POST /admin/hosted/define</c> into this service (<see cref="PortalEndpoints.DefineLayersAsync"/>); a layer whose
/// <c>adminLayerInfo.viewLayerDefinition</c> names a source is Studio's view (<see cref="AdminEndpoints.AddViewLayersAsync"/>),
/// which makes the service a view of that source if it was empty. Nothing is written a second way.
/// </para>
/// <para>
/// <b>Layers and tables only.</b> A service property is changed by <c>updateDefinition</c>, and a key this server does
/// not make a layer from is refused by name, with nothing made: a client told <c>success</c> about a definition that was
/// half applied builds on the half that is not there (ADR-008 §2).
/// </para>
/// </remarks>
internal static partial class ArcGisAdminEndpoints
{
    /// <summary>The keys of a view layer's definition this server reads or passes over.</summary>
    private static readonly HashSet<string> ViewLayerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "name", "type", "adminLayerInfo", "viewDefinitionQuery", "description", "displayField", "minScale",
        "maxScale", "defaultVisibility", "geometryType", "fields", "objectIdField",
    };

    /// <summary>ArcGIS's service-level <c>addToDefinition</c>, for layers and tables.</summary>
    private static async Task AddLayersToDefinitionAsync(
        HttpContext context, string serviceName, PostgresLayerCatalog layers, CancellationToken cancellation)
    {
        if (await ManagedServiceAsync(context, layers, serviceName, "add layers to", cancellation).ConfigureAwait(false)
            is not { } found)
        {
            return;
        }

        (PublishedService service, string? folder) = found;
        IFormCollection form = await FormAsync(context, cancellation).ConfigureAwait(false);

        if (IsTrue(form["async"].ToString()))
        {
            // A client that asked for async waits for a statusURL; answering success with none leaves it polling nothing.
            await RefuseAsync(context, 400,
                "'async=true' is not offered: addToDefinition answers here when the layers exist. Send async=false.").ConfigureAwait(false);
            return;
        }

        JsonElement definition;

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(form["addToDefinition"].ToString() is { Length: > 0 } text ? text : "{}");
            definition = parsed.RootElement.Clone();
        }
        catch (JsonException e)
        {
            await RefuseAsync(context, 400, $"'addToDefinition' is not valid JSON: {e.Message}").ConfigureAwait(false);
            return;
        }

        if (definition.ValueKind != JsonValueKind.Object)
        {
            await RefuseAsync(context, 400, "'addToDefinition' is an object naming the layers and tables to add.").ConfigureAwait(false);
            return;
        }

        string[] others = [.. definition.EnumerateObject().Select(p => p.Name).Where(n => n is not ("layers" or "tables"))];

        if (others.Length > 0)
        {
            await RefuseAsync(context, 400,
                $"{string.Join(", ", others)}: a service's addToDefinition adds layers and tables here, and its own properties "
                + "are changed by updateDefinition. Nothing was added.").ConfigureAwait(false);
            return;
        }

        List<JsonElement> entries = [];
        foreach (string list in (string[])["layers", "tables"])
        {
            if (definition.TryGetProperty(list, out JsonElement each) && each.ValueKind == JsonValueKind.Array)
            {
                entries.AddRange(each.EnumerateArray());
            }
        }

        if (entries.Count == 0)
        {
            await RefuseAsync(context, 400, "'addToDefinition' names no layers or tables. Nothing was added.").ConfigureAwait(false);
            return;
        }

        int viewing = entries.Count(IsViewLayer);

        if (viewing > 0 && viewing < entries.Count)
        {
            await RefuseAsync(context, 400,
                "Some of these layers name a source in adminLayerInfo.viewLayerDefinition and some do not. A view's layers are "
                + "its source's, and a service here is a view or holds its own layers, not both. Nothing was added.").ConfigureAwait(false);
            return;
        }

        if (!string.Equals(service.Folder ?? folder, Graticula.Api.ArcGis.FeatureServerMetadataWriter.HostedFolder, StringComparison.OrdinalIgnoreCase))
        {
            await RefuseAsync(context, 400,
                $"'{serviceName}' is not a hosted service. Layers are added by addToDefinition to a hosted service only; a "
                + "registered table is published from Studio's Publish screen.").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<(int Id, string Name)>? added = viewing > 0
            ? await AddViewLayersAsync(context, service, entries, cancellation).ConfigureAwait(false)
            : await AddDesignedLayersAsync(context, service, entries, layers, cancellation).ConfigureAwait(false);

        if (added is null)
        {
            return;
        }

        // ADR-266 condition 1: new layers change the service's definition.
        await context.RequestServices.GetRequiredService<WebhookRaiser>().RaiseChangeAsync(
            service.Id, service.Name, service.Folder ?? folder, null, "FeatureServiceDefinitionChanged",
            new System.Text.Json.Nodes.JsonObject { ["layersAdded"] = new System.Text.Json.Nodes.JsonArray([.. added.Select(l => (System.Text.Json.Nodes.JsonNode?)l.Id)]) },
            cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            success = true,
            layers = added.Select(l => new { id = l.Id, name = l.Name }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static bool IsViewLayer(JsonElement layer) =>
        layer.ValueKind == JsonValueKind.Object
        && layer.TryGetProperty("adminLayerInfo", out JsonElement info) && info.ValueKind == JsonValueKind.Object
        && info.TryGetProperty("viewLayerDefinition", out JsonElement definition) && definition.ValueKind == JsonValueKind.Object;

    /// <summary>Designed layers, through Studio's define, into a hosted service that is not a view.</summary>
    private static async Task<IReadOnlyList<(int Id, string Name)>?> AddDesignedLayersAsync(
        HttpContext context, PublishedService service, List<JsonElement> entries, PostgresLayerCatalog layers,
        CancellationToken cancellation)
    {
        if (service.ViewOf is not null)
        {
            await RefuseAsync(context, 400,
                $"'{service.Name}' is a view: its layers are its source's, named in adminLayerInfo.viewLayerDefinition. "
                + "Nothing was added.").ConfigureAwait(false);
            return null;
        }

        (int status, string? message, IReadOnlyList<string> made) = await PortalEndpoints.DefineLayersAsync(
            context, service.Name, null, entries, lone: string.Empty, cancellation).ConfigureAwait(false);

        if (status is < 200 or >= 300)
        {
            await RefuseAsync(context, status, message ?? "The layers were not added.").ConfigureAwait(false);
            return null;
        }

        PublishedService now = await layers.FindServiceAsync(service.Folder, service.Name, cancellation).ConfigureAwait(false) ?? service;

        return [.. made.Select(name => (
            now.LayersAndTables.FirstOrDefault(l => string.Equals(l.Definition.Name, name, StringComparison.Ordinal))?.LayerIndex ?? -1,
            name))];
    }

    /// <summary>View layers, through Studio's view, into an empty service or a view of the same source.</summary>
    private static async Task<IReadOnlyList<(int Id, string Name)>?> AddViewLayersAsync(
        HttpContext context, PublishedService service, List<JsonElement> entries, CancellationToken cancellation)
    {
        HashSet<string> sources = new(StringComparer.OrdinalIgnoreCase);
        List<int> indexes = [];
        Dictionary<string, string?> filters = [];

        foreach (JsonElement layer in entries)
        {
            string? stray = layer.EnumerateObject()
                .Where(p => !ViewLayerKeys.Contains(p.Name) && p.Value.ValueKind != JsonValueKind.Null)
                .Select(p => p.Name)
                .FirstOrDefault();

            if (stray is not null)
            {
                await RefuseAsync(context, 400,
                    $"A view layer here carries '{stray}', which this server does not take: a view shows its source layer, "
                    + "filtered by viewDefinitionQuery. Nothing was added.").ConfigureAwait(false);
                return null;
            }

            JsonElement view = layer.GetProperty("adminLayerInfo").GetProperty("viewLayerDefinition");

            if (!view.TryGetProperty("sourceServiceName", out JsonElement source) || source.GetString() is not { Length: > 0 } sourceName
                || !view.TryGetProperty("sourceLayerId", out JsonElement index) || !index.TryGetInt32(out int sourceLayer))
            {
                await RefuseAsync(context, 400,
                    "Each view layer names its source in adminLayerInfo.viewLayerDefinition: sourceServiceName and sourceLayerId.")
                    .ConfigureAwait(false);
                return null;
            }

            if (view.TryGetProperty("sourceLayerFields", out JsonElement fields)
                && !(fields.ValueKind == JsonValueKind.Null || (fields.ValueKind == JsonValueKind.String && fields.GetString() == "*")))
            {
                await RefuseAsync(context, 400,
                    "A view here shows every field of its source layer, so sourceLayerFields is '*'. Hide fields afterwards on the "
                    + "view's Fields page in Studio. Nothing was added.").ConfigureAwait(false);
                return null;
            }

            if (view.TryGetProperty("filter", out JsonElement spatial) && spatial.ValueKind != JsonValueKind.Null)
            {
                await RefuseAsync(context, 400,
                    "A view here is filtered by its rows (viewDefinitionQuery), not by an area; viewLayerDefinition's filter is "
                    + "not taken. Nothing was added.").ConfigureAwait(false);
                return null;
            }

            sources.Add(sourceName);
            indexes.Add(sourceLayer);

            if (layer.TryGetProperty("viewDefinitionQuery", out JsonElement query) && query.ValueKind == JsonValueKind.String)
            {
                filters[sourceLayer.ToString(CultureInfo.InvariantCulture)] = query.GetString();
            }
        }

        if (sources.Count != 1)
        {
            await RefuseAsync(context, 400, "A view here shows one source service; these layers name "
                + string.Join(", ", sources) + ". Nothing was added.").ConfigureAwait(false);
            return null;
        }

        IServiceProvider services = context.RequestServices;
        IReadOnlyList<(int Id, string Name)>? placed = null;

        (int status, string? message) = await PortalEndpoints.InnerStatusAsync(context, async () =>
            placed = await AdminEndpoints.AddViewLayersAsync(
                context, service, sources.First(), [.. indexes], filters,
                services.GetRequiredService<IAdminCatalog>(), services.GetRequiredService<PostgresLayerCatalog>(),
                services.GetRequiredService<PostGisImporter>(), services.GetRequiredService<ServiceContexts>(),
                services.GetRequiredService<IAuditLog>(), cancellation).ConfigureAwait(false)).ConfigureAwait(false);

        if (placed is null)
        {
            await RefuseAsync(context, status is >= 400 ? status : 400, message ?? "The view layers were not added.").ConfigureAwait(false);
            return null;
        }

        return placed;
    }
}
