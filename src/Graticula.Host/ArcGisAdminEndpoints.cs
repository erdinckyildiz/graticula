using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Tiles;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// ArcGIS's layer administration operations on a hosted feature layer: <c>addToDefinition</c>,
/// <c>deleteFromDefinition</c> and <c>truncate</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>At the addresses ArcGIS clients compute.</b> The ArcGIS API for Python and ArcGIS Pro reach a
/// hosted layer's administration by rewriting <c>/rest/services/</c> to <c>/rest/admin/services/</c>
/// — ArcGIS Online keeps <c>/FeatureServer/</c> as a segment, ArcGIS Enterprise writes
/// <c>{service}.FeatureServer</c> — so both spellings answer, with or without a folder.
/// </para>
/// <para>
/// <b>Doors onto what already exists, not a second implementation.</b> Each operation resolves the
/// layer and hands it to <see cref="HostedDataEndpoints"/>, whose checks — the privilege, hosted
/// only, the table this server created, the columns something depends on, the two-second lock wait —
/// are the ones <c>/admin/hosted/{layer}/fields</c> applies. Only the envelope differs: ArcGIS's
/// <c>{"success": true}</c>, and its <c>{"error": {...}}</c> for a refusal.
/// </para>
/// <para>
/// <b>Fields only.</b> A definition can also carry indexes, types, templates, relationships and
/// more; each is refused by name rather than ignored, because a caller told <c>success</c> about a
/// definition that was half applied will build on the half that is not there (ADR-008 §2).
/// </para>
/// </remarks>
internal static class ArcGisAdminEndpoints
{
    /// <summary>Maps the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The literal `/rest/admin/` stays in each route, because that is how
        // `No_protocol_face_takes_the_catalogue_without_the_fallback` knows these are administrative:
        // an admin action during a catalogue outage fails rather than acting on remembered state.
        foreach (string folder in (string[])["", "/{folder}"])
        {
            foreach (string service in (string[])["{serviceName}/FeatureServer", "{serviceName}.FeatureServer"])
            {
                app.MapPost($"/rest/admin/services{folder}/{service}/{{layerId:int}}/addToDefinition", AddToDefinitionAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapPost($"/rest/admin/services{folder}/{service}/{{layerId:int}}/deleteFromDefinition", DeleteFromDefinitionAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
                app.MapPost($"/rest/admin/services{folder}/{service}/{{layerId:int}}/truncate", TruncateAsync)
                    .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
            }
        }
    }

    /// <summary>ArcGIS field types this server can add a column for, as the hosted field types.</summary>
    internal static readonly IReadOnlyDictionary<string, string> AddableTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["esriFieldTypeSmallInteger"] = "SmallInteger",
            ["esriFieldTypeInteger"] = "Integer",
            ["esriFieldTypeBigInteger"] = "BigInteger",
            ["esriFieldTypeSingle"] = "Single",
            ["esriFieldTypeDouble"] = "Double",
            ["esriFieldTypeString"] = "Text",
            ["esriFieldTypeDate"] = "Date",
            ["esriFieldTypeGUID"] = "Guid",
        };

    /// <summary>
    /// Reads the <c>fields</c> of an <c>addToDefinition</c> or <c>deleteFromDefinition</c> document.
    /// </summary>
    /// <param name="json">The parameter's value.</param>
    /// <param name="adding">Whether types are needed.</param>
    /// <param name="fields">The fields, when this returns true.</param>
    /// <param name="error">Why not, when it returns false.</param>
    /// <returns>Whether the document can be applied.</returns>
    internal static bool TryDefinition(
        string? json, bool adding, out List<HostedDataEndpoints.FieldDesign> fields, out string? error)
    {
        fields = [];
        error = null;
        string parameter = adding ? "addToDefinition" : "deleteFromDefinition";

        if (string.IsNullOrWhiteSpace(json))
        {
            error = $"'{parameter}' is required.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"'{parameter}' must be a JSON object.";
                return false;
            }

            string[] others = [.. root.EnumerateObject().Select(p => p.Name).Where(n => n != "fields")];

            if (others.Length > 0)
            {
                error =
                    $"'{parameter}' carries {string.Join(", ", others)}, and this server changes a hosted "
                    + "layer's fields only. Nothing was applied: send the fields on their own.";
                return false;
            }

            if (!root.TryGetProperty("fields", out JsonElement list)
                || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() == 0)
            {
                error = $"'{parameter}' names no fields.";
                return false;
            }

            foreach (JsonElement field in list.EnumerateArray())
            {
                string? name = field.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "Every field needs a 'name'.";
                    return false;
                }

                if (!adding)
                {
                    fields.Add(new HostedDataEndpoints.FieldDesign(name, null, null));
                    continue;
                }

                string? type = field.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;

                if (type is null || !AddableTypes.TryGetValue(type, out string? hosted))
                {
                    error = type switch
                    {
                        null => $"Field '{name}' has no 'type'.",
                        _ when type.Equals("esriFieldTypeGlobalID", StringComparison.OrdinalIgnoreCase) =>
                            $"Field '{name}' is a GlobalID, which is added with POST /admin/hosted/{{layer}}/global-ids "
                            + "so that every existing row is given one.",
                        _ => $"Field '{name}' has type '{type}', and a hosted layer's column is one of "
                            + string.Join(", ", AddableTypes.Keys) + ".",
                    };
                    return false;
                }

                int? length = type.Equals("esriFieldTypeString", StringComparison.OrdinalIgnoreCase)
                    && field.TryGetProperty("length", out JsonElement l) && l.TryGetInt32(out int characters)
                        ? characters
                        : null;

                // A domain or a default would be a second change folded into this one, and dropping
                // either silently is the half-applied definition this class refuses. An alias is a
                // label (ADR-063) and is set on the layer's Fields page, not in the table.
                foreach (string unsupported in (string[])["domain", "defaultValue"])
                {
                    if (field.TryGetProperty(unsupported, out JsonElement carried) && carried.ValueKind != JsonValueKind.Null)
                    {
                        error =
                            $"Field '{name}' carries a {unsupported}, which addToDefinition does not set here. "
                            + "Add the field without it, then give it a domain or a default through the layer's field settings (ADR-065).";
                        return false;
                    }
                }

                // Every added column accepts null: rows already in the table have no value for it,
                // which AddFieldToAsync says in its own answer. `nullable: false` is refused
                // rather than silently made nullable.
                if (field.TryGetProperty("nullable", out JsonElement nullable) && nullable.ValueKind == JsonValueKind.False)
                {
                    error =
                        $"Field '{name}' asks for nullable false. A column added to a layer that already has "
                        + "features cannot be required without a default, and this server does not invent one.";
                    return false;
                }

                fields.Add(new HostedDataEndpoints.FieldDesign(name, hosted, true, length));
            }

            return true;
        }
        catch (JsonException)
        {
            error = $"'{parameter}' is not valid JSON.";
            return false;
        }
    }

    private static async Task AddToDefinitionAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback services,
        PostgresLayerCatalog layers,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await LayerAsync(context, services, layers, serviceName, layerId, "add a field to", cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        IFormCollection form = await FormAsync(context, cancellation).ConfigureAwait(false);

        if (!TryDefinition(form["addToDefinition"].ToString(), adding: true, out List<HostedDataEndpoints.FieldDesign> fields, out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        foreach (HostedDataEndpoints.FieldDesign field in fields)
        {
            if (await HostedDataEndpoints
                .AddFieldToAsync(context, found, field, importer, contexts, tiles, catalog, audit, cancellation)
                .ConfigureAwait(false) is null)
            {
                // The refusal is written. Fields before this one were added — each is its own ALTER,
                // and the answer for that field already says why this one was not.
                return;
            }
        }

        await Results.Json(new { success = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteFromDefinitionAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback services,
        PostgresLayerCatalog layers,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await LayerAsync(context, services, layers, serviceName, layerId, "drop a field from", cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        IFormCollection form = await FormAsync(context, cancellation).ConfigureAwait(false);

        if (!TryDefinition(form["deleteFromDefinition"].ToString(), adding: false, out List<HostedDataEndpoints.FieldDesign> fields, out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        foreach (HostedDataEndpoints.FieldDesign field in fields)
        {
            if (await HostedDataEndpoints
                .DropFieldFromAsync(context, found, field.Name!, importer, contexts, tiles, catalog, audit, cancellation)
                .ConfigureAwait(false) is null)
            {
                return;
            }
        }

        await Results.Json(new { success = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task TruncateAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback services,
        PostgresLayerCatalog layers,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await LayerAsync(context, services, layers, serviceName, layerId, "empty", cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        IFormCollection form = await FormAsync(context, cancellation).ConfigureAwait(false);

        if (IsTrue(form["async"].ToString()))
        {
            // A client that asked for async waits for a statusURL; answering success with none would
            // leave it polling nothing.
            await RefuseAsync(
                context, 400,
                "'async=true' is not offered: truncate is one statement here and answers when it is done. "
                + "Send async=false.").ConfigureAwait(false);
            return;
        }

        if (await HostedDataEndpoints
            .TruncateHostedAsync(context, found, IsTrue(form["attachmentOnly"].ToString()), importer, contexts, tiles, catalog, audit, cancellation)
            .ConfigureAwait(false))
        {
            await Results.Json(new { success = true }).ExecuteAsync(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The layer at an admin address, after the privilege — so a caller without it learns nothing
    /// about which services exist.
    /// </summary>
    private static async Task<PublishedLayer?> LayerAsync(
        HttpContext context,
        CatalogFallback services,
        PostgresLayerCatalog layers,
        string serviceName,
        int layerId,
        string what,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return null;
        }

        string? folder = context.Request.RouteValues.TryGetValue("folder", out object? given) ? given as string : null;

        CatalogAnswer answer = await services.FindServiceAsync(folder, serviceName, cancellation).ConfigureAwait(false);

        if (answer.Service?.Layer(layerId) is not { } layer)
        {
            await RefuseAsync(
                context, 404,
                $"There is no layer {layerId} in a feature service '{(folder is null ? serviceName : folder + "/" + serviceName)}'.")
                .ConfigureAwait(false);
            return null;
        }

        return await HostedDataEndpoints
            .HostedLayerAsync(context, layers, layer.Definition.Name, what, cancellation)
            .ConfigureAwait(false);
    }

    private static async Task<IFormCollection> FormAsync(HttpContext context, CancellationToken cancellation) =>
        context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : new FormCollection(context.Request.Query.ToDictionary(q => q.Key, q => q.Value));

    private static bool IsTrue(string value) => value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static Task RefuseAsync(HttpContext context, int code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: code).ExecuteAsync(context);
}
