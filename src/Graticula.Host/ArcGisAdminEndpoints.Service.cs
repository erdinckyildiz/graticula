using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
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
            layers = service.Layers.OrderBy(l => l.LayerIndex).Select(l => new { id = l.LayerIndex, name = l.Definition.Name }),
            tables = Array.Empty<object>(),
            adminServiceInfo = new
            {
                name = service.Name,
                type = "FeatureServer",
                status = new { configuredState = service.Status.ToString().ToUpperInvariant(), realTimeState = service.Status.ToString().ToUpperInvariant() },
                database = new { datasource = new { name = service.Layers.Count > 0 ? service.Layers[0].DataSourceName : null } },
            },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>ArcGIS's <c>updateDefinition</c>, for the keys this server keeps.</summary>
    private static async Task UpdateDefinitionAsync(
        HttpContext context, string serviceName, PostgresLayerCatalog layers, IAdminCatalog catalog, IAuditLog audit,
        ServiceContexts contexts, CancellationToken cancellation)
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

        string[] unknown = [.. definition.EnumerateObject().Select(p => p.Name).Where(n => !DefinitionKeys.Contains(n))];
        if (unknown.Length > 0)
        {
            await RefuseAsync(context, 400,
                $"{string.Join(", ", unknown)}: not changed by updateDefinition here, so nothing was. This server changes "
                + "capabilities, serviceDescription, copyrightText and maxRecordCount.").ConfigureAwait(false);
            return;
        }

        // ---- everything is checked before anything is written ----
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

            ServiceStewardship? now = await catalog.FindStewardshipAsync(service.Name, folder, cancellation).ConfigureAwait(false);
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

        foreach (PublishedLayer layer in service.Layers)
        {
            contexts.Forget(layer);
        }

        await AdminEndpoints.AuditAsync(context, audit, "service.updateDefinition", service.Name,
            AdminEndpoints.Detail(new { folder, keys = definition.EnumerateObject().Select(p => p.Name).ToArray() }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Ok(new { success = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

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
