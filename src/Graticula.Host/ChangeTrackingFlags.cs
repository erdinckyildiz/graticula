using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Graticula.Host;

/// <summary>
/// A hosted feature service's change tracking — ADR-311 §5.1: the owner's switch, the history it needs, and what the
/// documents say while it is honoured (<c>ChangeTracking</c> in capabilities, <c>extractChangesCapabilities</c>,
/// <c>changeTrackingInfo</c>).
/// </summary>
/// <remarks>
/// <b>The switch is a wish; the history is the fact.</b> A service says <c>ChangeTracking</c> only while its owner has the
/// switch on <i>and</i> every layer and table still keeps ADR-078's history, so a trigger dropped in <c>psql</c> takes the
/// capability away rather than leaving an <c>extractChanges</c> that answers from a history that stopped.
/// </remarks>
internal static class ChangeTrackingFlags
{
    /// <summary>The capability's name in a capabilities string.</summary>
    public const string Capability = "ChangeTracking";

    /// <summary>
    /// Why a service cannot track its changes, or null when it can — ADR-311 §5.3: where sync could not be had either,
    /// because both stand on the history only a hosted layer keeps.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <param name="needsLayers">False when a service still empty may take the switch (the portal's createService).</param>
    /// <returns>The reason, or null.</returns>
    public static string? Refusal(PublishedService service, bool needsLayers = true)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (needsLayers && service.LayersAndTables.Count == 0)
        {
            return "The service has no layers whose changes could be tracked.";
        }

        if (service.ViewOf is not null)
        {
            return "A view's rows and their history are its source's; track changes on the source service (ADR-311 §5.3).";
        }

        if (service.LayersAndTables.FirstOrDefault(l => !l.Definition.IsHosted) is { } registered)
        {
            return $"Layer '{registered.Definition.Name}' is not hosted. Change tracking is offered on hosted feature services "
                + "only, because extractChanges is read from the history a hosted layer keeps (ADR-078, ADR-311 §5.3).";
        }

        if (service.LayersAndTables.FirstOrDefault(l => l.Definition.IntegerIdentityColumn is null) is { } unnamed)
        {
            return $"Layer '{unnamed.Definition.Name}' has no integer object id, so its changes cannot be named (ADR-078).";
        }

        return null;
    }

    /// <summary>
    /// Why a service's change tracking is not honoured now, or null when it is: the switch is off, the service cannot have
    /// it, or a layer no longer keeps its history.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <param name="described">Each of its layers and tables, described.</param>
    /// <returns>The reason, or null.</returns>
    public static string? NotHonoured(PublishedService service, IReadOnlyList<(PublishedLayer Layer, LayerDescription Described)> described)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(described);

        if (!service.ChangeTracking)
        {
            return $"Change tracking is not enabled on '{service.QualifiedName}', so extractChanges is not offered. Its owner "
                + "enables it with ChangeTracking in updateDefinition's capabilities (ADR-311 §5.2).";
        }

        if (Refusal(service) is { } refused)
        {
            return refused;
        }

        if (described.FirstOrDefault(d => !d.Described.Archived) is { Layer: { } stopped })
        {
            return $"Layer '{stopped.Definition.Name}' no longer keeps its history, which extractChanges is read from. Turn its "
                + "history on again, or send ChangeTracking in updateDefinition's capabilities, which turns it on (ADR-311 §5.1).";
        }

        return null;
    }

    /// <summary>Whether a service's change tracking is honoured now.</summary>
    public static bool IsOn(PublishedService service, IReadOnlyList<LayerDescription> described) =>
        service.ChangeTracking && Refusal(service) is null
        && described.Count == service.LayersAndTables.Count && described.All(d => d.Archived);

    /// <summary>Each layer and table of a service, described through the cache.</summary>
    public static async Task<List<(PublishedLayer Layer, LayerDescription Described)>> DescribeAsync(
        PublishedService service, ServiceContexts contexts, CancellationToken cancellation)
    {
        List<(PublishedLayer, LayerDescription)> described = [];

        foreach (PublishedLayer layer in service.LayersAndTables)
        {
            (_, LayerDescription about) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
            described.Add((layer, about));
        }

        return described;
    }

    /// <summary>A capabilities string with <c>ChangeTracking</c> added where its caller may query.</summary>
    /// <param name="capabilities">The capabilities.</param>
    /// <returns>The capabilities to write.</returns>
    public static string WithChangeTracking(string capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        string[] parts = capabilities.Split(',', StringSplitOptions.TrimEntries);

        return !parts.Contains("Query", StringComparer.Ordinal) || parts.Contains(Capability, StringComparer.Ordinal)
            ? capabilities
            : capabilities + "," + Capability;
    }

    /// <summary>
    /// The <c>extractChangesCapabilities</c> object, a new instance each time — every member true is served (ADR-311
    /// §5.4), and nothing that is refused is said to be.
    /// </summary>
    /// <returns>The object.</returns>
    public static JsonObject ExtractChangesCapabilities() => new()
    {
        ["supportsReturnIdsOnly"] = true,
        ["supportsReturnExtentOnly"] = true,
        ["supportsReturnAttachments"] = true,
        ["supportsLayerQueries"] = true,
        ["supportsGeometry"] = true,
        ["supportsReturnFeature"] = true,
        ["supportsReturnHasGeometryUpdates"] = true,
        ["supportsFieldsToCompare"] = true,
        ["supportsServerGens"] = true,
        ["supportsReturnDeletedFeatures"] = true,
    };

    /// <summary>
    /// The service document with its change tracking, or the document unchanged where it is not honoured: <c>ChangeTracking</c>
    /// in capabilities, <c>extractChangesCapabilities</c>, and <c>changeTrackingInfo</c> with each layer's generations.
    /// </summary>
    /// <param name="context">The request, for the response's serializer options.</param>
    /// <param name="document">The document.</param>
    /// <param name="service">The service.</param>
    /// <param name="described">Its layers and tables, each with its description.</param>
    /// <param name="now">The moment the generations are of.</param>
    /// <returns>The document to write.</returns>
    public static object ApplyToService(
        HttpContext context, object document, PublishedService service,
        IReadOnlyList<(PublishedLayer Layer, LayerDescription Described)> described, DateTimeOffset now)
    {
        if (!IsOn(service, [.. described.Select(d => d.Described)]) || Node(context, document) is not { } written)
        {
            return document;
        }

        written["capabilities"] = WithChangeTracking(written["capabilities"]?.GetValue<string>() ?? string.Empty);
        written["extractChangesCapabilities"] = ExtractChangesCapabilities();

        long serverGen = now.ToUnixTimeMilliseconds();
        JsonArray gens = [];

        foreach ((PublishedLayer layer, LayerDescription about) in described.OrderBy(d => d.Layer.LayerIndex))
        {
            gens.Add(new JsonObject
            {
                ["id"] = layer.LayerIndex,
                ["minServerGen"] = about.ArchivedSince?.ToUnixTimeMilliseconds() ?? 0,
                ["serverGen"] = serverGen,
            });
        }

        written["changeTrackingInfo"] = new JsonObject { ["lastSyncDate"] = serverGen, ["layerServerGens"] = gens };
        return written;
    }

    /// <summary>A layer document with <c>ChangeTracking</c> in its capabilities where its service tracks changes.</summary>
    /// <param name="context">The request.</param>
    /// <param name="document">The document.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="described">The layer, described.</param>
    /// <returns>The document to write.</returns>
    /// <remarks>
    /// Judged from the layer alone — its service's switch, hosted, not a view, and its own history — because its document
    /// is answered without its service's other layers; the service document is the one that checks them all.
    /// </remarks>
    public static object ApplyToLayer(HttpContext context, object document, PublishedLayer layer, LayerDescription described)
    {
        if (!layer.ServiceChangeTracking || !layer.Definition.IsHosted || layer.ViewOf is not null || !described.Archived
            || Node(context, document) is not { } written)
        {
            return document;
        }

        written["capabilities"] = WithChangeTracking(written["capabilities"]?.GetValue<string>() ?? string.Empty);
        return written;
    }

    /// <summary>
    /// Turns a service's change tracking on or off — <c>updateDefinition</c>'s capabilities and the portal's
    /// <c>createService</c>. Turning on starts each layer's history first; turning off keeps it (ADR-311 §5.1, §5.2). The
    /// caller has already been checked to manage the service.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="service">The service.</param>
    /// <param name="folder">Its folder.</param>
    /// <param name="enabled">On or off.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>Null when done, or the status and why not; nothing is switched on a refusal.</returns>
    public static async Task<(int Status, string Message)?> SwitchAsync(
        HttpContext context, PublishedService service, string? folder, bool enabled, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        IServiceProvider services = context.RequestServices;

        if (enabled && Refusal(service, needsLayers: false) is { } refusal)
        {
            return (409, refusal);
        }

        List<string> started = [];

        if (enabled && await StartHistoryAsync(services, service, started, cancellation).ConfigureAwait(false) is { } failed)
        {
            return (409, failed + " Change tracking is still off.");
        }

        await services.GetRequiredService<IOfflineSyncStore>().SetChangeTrackingAsync(service.Id, enabled, cancellation)
            .ConfigureAwait(false);

        ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();
        foreach (PublishedLayer layer in service.LayersAndTables)
        {
            contexts.Forget(layer);
        }

        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "service.changeTracking", service.Name,
            AdminEndpoints.Detail(new { folder, enabled, historyStarted = started }), succeeded: true, cancellation)
            .ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Starts the history of every layer and table of a service that keeps none — when change tracking is turned on, and
    /// when a layer is added to a service that has it on (ADR-311 §5.1). Answers null when done, or why not.
    /// </summary>
    /// <param name="services">The request's services.</param>
    /// <param name="service">The service, as it is now.</param>
    /// <param name="started">The layers whose history this started.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>Null, or the reason it stopped.</returns>
    public static async Task<string?> StartHistoryAsync(
        IServiceProvider services, PublishedService service, List<string> started, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(started);

        LayerConnections connections = services.GetRequiredService<LayerConnections>();
        ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();

        foreach (PublishedLayer layer in service.LayersAndTables)
        {
            if (!layer.Definition.IsHosted || layer.Definition.IntegerIdentityColumn is null)
            {
                continue;
            }

            IFeatureHistory history = connections.HistoryFor(layer);

            try
            {
                if (!await history.IsOnAsync(cancellation).ConfigureAwait(false))
                {
                    await history.EnableAsync(cancellation).ConfigureAwait(false);
                    started.Add(layer.Definition.Name);
                }
            }
            catch (Exception e) when (e is DbException or InvalidOperationException)
            {
                return $"Change tracking needs the history of '{layer.Definition.Name}', and it could not be turned on: {e.Message}"
                    + (started.Count > 0 ? $" History was turned on for {string.Join(", ", started)} before this." : string.Empty);
            }

            contexts.Forget(layer);
        }

        return null;
    }

    private static JsonObject? Node(HttpContext context, object document)
    {
        if (document is JsonObject already)
        {
            return already;
        }

        JsonSerializerOptions options =
            context.RequestServices?.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
            ?? JsonSerializerOptions.Web;

        return JsonSerializer.SerializeToNode(document, document.GetType(), options) as JsonObject;
    }
}
