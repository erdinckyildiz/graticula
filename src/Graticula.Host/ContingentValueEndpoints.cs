using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Graticula.Host;

/// <summary>
/// A hosted layer's contingent values — ADR-312: the layer document's flag, ArcGIS's <c>contingentValues</c> resource and
/// <c>queryContingentValues</c> operation, Studio's admin route, and <c>addToDefinition</c>'s
/// <c>contingentValuesDefinition</c>.
/// </summary>
/// <remarks>
/// <b>Nothing here is on the edit path.</b> <c>applyEdits</c> does not read a definition and does not refuse a
/// combination it does not allow, which is ArcGIS's behaviour (ADR-312 §5.7).
/// </remarks>
internal static class ContingentValueEndpoints
{
    /// <summary>Maps the resource and the operation under one service prefix.</summary>
    /// <param name="app">The application.</param>
    /// <param name="prefix">The prefix the feature face is mapped under.</param>
    public static void Map(WebApplication app, string prefix)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet($"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}/contingentValues", LayerAsync)
            .Governed(SharingGovernedExtensions.ByService);
        app.MapMethods($"{prefix}/{{serviceName}}/FeatureServer/queryContingentValues", ["GET", "POST"], QueryAsync)
            .Governed(SharingGovernedExtensions.ByService);
    }

    /// <summary>Maps Studio's admin route.</summary>
    /// <param name="app">The application.</param>
    public static void MapAdmin(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/admin/layers/{name}/contingent-values", GetAdminAsync);
        app.MapPut("/admin/layers/{name}/contingent-values", SetAdminAsync);
    }

    /// <summary>The definition a client is served, narrowed to the columns it can see, or null.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="described">Its columns as a client sees them.</param>
    /// <returns>The served definition, or null when nothing survives.</returns>
    public static ContingentValues? Served(PublishedLayer layer, LayerDescription described)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(described);

        if (layer.ContingentValues is not { IsEmpty: false } held)
        {
            return null;
        }

        ContingentValues served = held.Served(column => described.Fields.Any(f => f.Name == column));
        return served.IsEmpty ? null : served;
    }

    /// <summary>A layer document with <c>hasContingentValuesDefinition</c>, or the document unchanged.</summary>
    /// <param name="context">The request, for the response's serializer options.</param>
    /// <param name="document">The document.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="described">Its columns as a client sees them.</param>
    /// <returns>The document to write.</returns>
    public static object ApplyToLayer(HttpContext context, object document, PublishedLayer layer, LayerDescription described)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(document);

        if (Served(layer, described) is null)
        {
            return document;
        }

        JsonObject? written = document as JsonObject;

        if (written is null)
        {
            JsonSerializerOptions options =
                context.RequestServices?.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
                ?? JsonSerializerOptions.Web;
            written = JsonSerializer.SerializeToNode(document, document.GetType(), options) as JsonObject;
        }

        if (written is null)
        {
            return document;
        }

        written["hasContingentValuesDefinition"] = true;
        return written;
    }

    private static string? Parameter(IFormCollection form, HttpContext context, string name) =>
        form.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : context.Request.Query.TryGetValue(name, out value) && !string.IsNullOrWhiteSpace(value)
                ? value.ToString()
                : null;

    /// <summary>The refusal for a format or option this face does not answer (D-474), or null.</summary>
    private static string? Unanswered(IFormCollection form, HttpContext context)
    {
        if (string.Equals(Parameter(form, context, "f"), "pbf", StringComparison.OrdinalIgnoreCase))
        {
            return "f=pbf is not answered here: contingent values are served as JSON (f=json or pjson). See D-474.";
        }

        if (string.Equals(Parameter(form, context, "compactFormat"), "false", StringComparison.OrdinalIgnoreCase))
        {
            return "compactFormat=false is not answered here: codes are served as they are stored. See D-474.";
        }

        if (Parameter(form, context, "domainDictionaries") is { } dictionaries
            && !string.Equals(dictionaries, "none", StringComparison.OrdinalIgnoreCase))
        {
            return $"domainDictionaries={dictionaries} is not answered here; only none is. See D-474.";
        }

        return null;
    }

    private static Task Refuse(HttpContext context, int code, string message) =>
        Results.Json(new { error = new { code, message, details = Array.Empty<string>() } }, statusCode: code).ExecuteAsync(context);

    private static async Task<IFormCollection> FormAsync(HttpContext context, CancellationToken cancellation) =>
        context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;

    private static string? GeometryTypeOf(PublishedLayer layer) =>
        layer.GeometryType is { } kind ? ArcGisGeometryWriter.TypeName(kind) : null;

    /// <summary><c>…/FeatureServer/{id}/contingentValues</c>.</summary>
    private static async Task LayerAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        IFormCollection form = await FormAsync(context, cancellation).ConfigureAwait(false);

        if (Unanswered(form, context) is { } unanswered)
        {
            await Refuse(context, 400, unanswered).ConfigureAwait(false);
            return;
        }

        if (!ContingentValuesDocument.TryIds(Parameter(form, context, "idSubset"), out Func<int, bool>? ids, out string? wrong))
        {
            await Refuse(context, 400, $"idSubset: {wrong}").ConfigureAwait(false);
            return;
        }

        HashSet<string>? groups = Parameter(form, context, "fieldGroupSubset") is { } subset
            ? new HashSet<string>(
                subset.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(n => n.Trim('"')),
                StringComparer.OrdinalIgnoreCase)
            : null;

        if (await ServiceLookup.LayerAsync(context, catalog, serviceName, layerId, cancellation).ConfigureAwait(false)
            is not { } layer)
        {
            return;
        }

        (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
        ContingentValuesDocument.Dictionaries dictionaries = new();
        Dictionary<string, object?> definition = ContingentValuesDocument.Definition(
            layer.Definition, layer.LayerIndex, layer.DisplayName, GeometryTypeOf(layer), described,
            Served(layer, described) ?? ContingentValues.Empty, dictionaries, groups, ids);

        await Results.Json(new
        {
            typeCodes = ContingentValuesDocument.TypeCodes,
            stringDicts = dictionaries.Written,
            contingentValuesDefinition = definition,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary><c>…/FeatureServer/queryContingentValues</c>.</summary>
    private static async Task QueryAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        IFormCollection form = await FormAsync(context, cancellation).ConfigureAwait(false);

        if (Unanswered(form, context) is { } unanswered)
        {
            await Refuse(context, 400, unanswered).ConfigureAwait(false);
            return;
        }

        if (!ContingentValuesDocument.TryIds(Parameter(form, context, "layers"), out Func<int, bool>? wanted, out string? wrong))
        {
            await Refuse(context, 400, $"layers: {wrong}").ConfigureAwait(false);
            return;
        }

        if (await ServiceLookup.FeatureServiceAsync(context, catalog, serviceName, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        ContingentValuesDocument.Dictionaries dictionaries = new();
        List<object> definitions = [];

        foreach (PublishedLayer each in service.LayersAndTables.OrderBy(l => l.LayerIndex))
        {
            if (wanted is not null && !wanted(each.LayerIndex))
            {
                continue;
            }

            if (each.ContingentValues is not { IsEmpty: false })
            {
                continue;
            }

            if (await ServiceLookup.LayerAsync(context, catalog, serviceName, each.LayerIndex, cancellation).ConfigureAwait(false)
                is not { } layer)
            {
                return;
            }

            (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            if (Served(layer, described) is { } served)
            {
                definitions.Add(ContingentValuesDocument.Definition(
                    layer.Definition, layer.LayerIndex, layer.DisplayName, GeometryTypeOf(layer), described, served, dictionaries));
            }
        }

        await Results.Json(new
        {
            typeCodes = ContingentValuesDocument.TypeCodes,
            stringDicts = dictionaries.Written,
            contingentValuesDefinitions = definitions.ToArray(),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Why a layer's definition may not be written, or null: hosted layers only (ADR-312 §5.5, INFERRED).</summary>
    internal static string? WriteRefusal(PublishedLayer layer) =>
        !layer.Definition.IsHosted
            ? $"'{layer.Definition.Name}' is a registered layer. Contingent values are kept on hosted layers only (ADR-312 §5.5)."
            : layer.ViewOf is not null
                ? $"'{layer.Definition.Name}' is a view; its contingent values are its source's, taken when the view was made (ADR-312 §5.2)."
                : null;

    private static async Task GetAdminAsync(
        HttpContext context,
        string name,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (await AdminEndpoints.ReadableLayerAsync(context, layers, name, cancellation).ConfigureAwait(false) is not { } layer)
        {
            return;
        }

        await WriteAdminAsync(context, layer, layer.ContingentValues ?? ContingentValues.Empty, contexts, cancellation)
            .ConfigureAwait(false);
    }

    private static async Task WriteAdminAsync(
        HttpContext context, PublishedLayer layer, ContingentValues held, ServiceContexts contexts, CancellationToken cancellation)
    {
        (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
        LayerSubtypes? subtypes = described.Subtypes;
        string? globalId = GlobalIds.FieldOf(described.Fields);

        await Results.Json(new
        {
            name = layer.Definition.Name,
            writable = WriteRefusal(layer) is null,
            why = WriteRefusal(layer),
            definition = ContingentValueJson.Write(held),
            inert = held.Inert(column => described.Fields.Any(f => f.Name == column)).Select(g => g.Name).ToArray(),
            fields = described.Fields.Select(f => new
            {
                name = f.Name,
                alias = f.Label,
                type = FeatureServerMetadataWriter.FieldTypeOf(layer.Definition, described, f),
                nullable = f.Nullable,
                domain = f.Domain is null ? null : FeatureServerMetadataWriter.Domain(f.Domain),
                eligible = f.Name != layer.Definition.IntegerIdentityColumn && f.Name != globalId
                    && f.Name != subtypes?.Field && !f.Maintained,
            }).ToArray(),
            subtypes = subtypes is null ? null : new
            {
                field = subtypes.Field,
                types = subtypes.Types.Select(t => new
                {
                    code = t.Code,
                    name = t.Name,
                    domains = t.Domains.ToDictionary(d => d.Key, d => FeatureServerMetadataWriter.Domain(d.Value)),
                }).ToArray(),
            },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task SetAdminAsync(
        HttpContext context,
        string name,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (await AdminEndpoints.ManagedLayerAsync(context, layers, name, "change the contingent values of", cancellation)
            .ConfigureAwait(false) is not { } layer)
        {
            return;
        }

        if (WriteRefusal(layer) is { } refusal)
        {
            await Refuse(context, 409, refusal).ConfigureAwait(false);
            return;
        }

        ContingentValues? wanted;

        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellation)
                .ConfigureAwait(false);
            JsonElement root = body.RootElement;
            JsonElement definition = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("definition", out JsonElement d)
                ? d
                : root;
            wanted = ContingentValueJson.Read(definition, out string? unreadable);

            if (wanted is null)
            {
                await Refuse(context, 400, unreadable!).ConfigureAwait(false);
                return;
            }
        }
        catch (JsonException e)
        {
            await Refuse(context, 400, $"The body is not valid JSON: {e.Message}").ConfigureAwait(false);
            return;
        }

        // A value Studio adds has no id yet; it takes one more than the highest held, as grapy's (ADR-235 §5.8 h).
        int next = wanted.HighestId;
        wanted = wanted with { Values = [.. wanted.Values.Select(v => v.Id > 0 ? v : v with { Id = ++next })] };

        (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        // Inert groups already stored are kept, so a column hidden for a while does not cost its groups; a new group must
        // name columns the layer has (ADR-312 §5.3).
        ContingentValues checkable = wanted.Served(column => described.Fields.Any(f => f.Name == column));
        IReadOnlyList<FieldGroup> inert = wanted.Inert(column => described.Fields.Any(f => f.Name == column));

        foreach (FieldGroup group in inert)
        {
            if (layer.ContingentValues?.Group(group.Name) is not { } stored || !stored.Fields.SequenceEqual(group.Fields))
            {
                string missing = group.Fields.First(f => !described.Fields.Any(c => c.Name == f));
                await Refuse(context, 400,
                    $"The field group '{group.Name}' names '{missing}', which is not a field of this layer.").ConfigureAwait(false);
                return;
            }
        }

        if (checkable.Refusal(described.Fields, described.Subtypes, layer.Definition.IntegerIdentityColumn) is { } wrong)
        {
            await Refuse(context, 400, wrong).ConfigureAwait(false);
            return;
        }

        if (!await catalog.SetContingentValuesAsync(layer.Id, wanted.IsEmpty ? null : wanted, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        contexts.Forget(layer);

        await AdminEndpoints.AuditAsync(
            context, audit, "layer.contingentValues", name,
            AdminEndpoints.Detail(new { groups = wanted.Groups.Count, values = wanted.Values.Count }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await WriteAdminAsync(context, layer, wanted, contexts, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>addToDefinition</c> with a <c>contingentValuesDefinition</c> — ADR-312 §5.5. The layer is resolved, hosted and
    /// managed by the caller already.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="root">The <c>addToDefinition</c> object.</param>
    /// <param name="contexts">The shapes.</param>
    /// <param name="catalog">The admin catalogue.</param>
    /// <param name="audit">The audit log.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>A task.</returns>
    public static async Task AddToDefinitionAsync(
        HttpContext context,
        PublishedLayer layer,
        JsonElement root,
        ServiceContexts contexts,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(layer);

        string[] others = [.. root.EnumerateObject().Select(p => p.Name).Where(n => n != "contingentValuesDefinition")];

        if (others.Length > 0)
        {
            await Refuse(context, 400,
                $"'addToDefinition' carries contingentValuesDefinition with {string.Join(", ", others)}. Nothing was applied: "
                + "send the contingent values on their own (ADR-312 §5.5).").ConfigureAwait(false);
            return;
        }

        if (WriteRefusal(layer) is { } refusal)
        {
            await Refuse(context, 400, refusal).ConfigureAwait(false);
            return;
        }

        (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
        ContingentValues held = layer.ContingentValues ?? ContingentValues.Empty;

        if (!ContingentValuesDocument.TryRead(
                root.GetProperty("contingentValuesDefinition"), described, held,
                out List<FieldGroup> groups, out List<ContingentValue> values, out string? unreadable))
        {
            await Refuse(context, 400, unreadable!).ConfigureAwait(false);
            return;
        }

        if (held.Adding(groups, values, out List<int> added, out string? clash) is not { } wanted)
        {
            await Refuse(context, 400, clash!).ConfigureAwait(false);
            return;
        }

        ContingentValues checkable = wanted.Served(column => described.Fields.Any(f => f.Name == column));
        if (groups.FirstOrDefault(g => !checkable.Groups.Contains(g)) is { } unknown)
        {
            string missing = unknown.Fields.First(f => !described.Fields.Any(c => c.Name == f));
            await Refuse(context, 400,
                $"The field group '{unknown.Name}' names '{missing}', which is not a field of this layer.").ConfigureAwait(false);
            return;
        }

        // The groups as they will be, and only the values this request adds: a value already held is not judged again.
        ContingentValues fresh = new([.. checkable.Groups], [.. wanted.Values.Where(v => added.Contains(v.Id))]);

        if (fresh.Refusal(described.Fields, described.Subtypes, layer.Definition.IntegerIdentityColumn) is { } wrong)
        {
            await Refuse(context, 400, wrong).ConfigureAwait(false);
            return;
        }

        if (!await catalog.SetContingentValuesAsync(layer.Id, wanted, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No layer '{layer.Definition.Name}'.").ConfigureAwait(false);
            return;
        }

        contexts.Forget(layer);

        await AdminEndpoints.AuditAsync(
            context, audit, "layer.contingentValues", layer.Definition.Name,
            AdminEndpoints.Detail(new { addedGroups = groups.Select(g => g.Name).ToArray(), addedValues = added }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { success = true, addedContingentValueIds = added }).ExecuteAsync(context).ConfigureAwait(false);
    }
}
