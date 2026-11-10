using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Api.ArcGis.Offline;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Graticula.Host;

/// <summary>
/// What a FeatureServer or layer document says about offline sync — ADR-288 §5.1: <c>syncEnabled</c>,
/// <c>syncCapabilities</c> and <c>Sync</c> where the service is sync-enabled, through <see cref="SyncDocument"/>.
/// </summary>
/// <remarks>
/// <b>Added only to a sync-enabled hosted service's documents</b>, through the response's own serializer options as
/// <see cref="ViewFlags"/> adds a view's, so every other document is written exactly as before.
/// </remarks>
internal static class SyncFlags
{
    /// <summary>
    /// Why a service cannot be sync-enabled, or null when it can — ADR-288 §5.8 (<c>INFERRED</c>): hosted layers only,
    /// as ADR-078's history is, and not a view, whose rows and history are its source's.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <returns>The reason, or null.</returns>
    public static string? Refusal(PublishedService service)
    {
        System.ArgumentNullException.ThrowIfNull(service);

        if (service.LayersAndTables.Count == 0)
        {
            return "The service has no layers to take offline.";
        }

        if (service.ViewOf is not null)
        {
            return "A view's rows and their history are its source's; enable sync on the source service (ADR-288 §5.8).";
        }

        if (service.LayersAndTables.FirstOrDefault(l => !l.Definition.IsHosted) is { } registered)
        {
            return $"Layer '{registered.Definition.Name}' is not hosted. Offline sync is offered on hosted feature services "
                + "only, because it is built on the history a hosted layer keeps (ADR-288 §5.8).";
        }

        if (service.LayersAndTables.FirstOrDefault(l => l.Definition.IntegerIdentityColumn is null) is { } unnamed)
        {
            return $"Layer '{unnamed.Definition.Name}' has no integer object id, so its changes cannot be named (ADR-078).";
        }

        return null;
    }

    /// <summary>Whether a service's documents say it is sync-enabled.</summary>
    /// <param name="service">The service.</param>
    /// <returns>True when its owner turned sync on and it may have it.</returns>
    public static bool IsOn(PublishedService service) => service.SyncEnabled && Refusal(service) is null;

    /// <summary>The service document with its sync flags, or the document unchanged.</summary>
    /// <param name="context">The request, for the response's serializer options.</param>
    /// <param name="document">The document.</param>
    /// <param name="service">The service.</param>
    /// <returns>The document to write.</returns>
    public static object ApplyToService(HttpContext context, object document, PublishedService service)
    {
        if (!IsOn(service) || Node(context, document) is not { } written)
        {
            return document;
        }

        SyncDocument.ApplyToService(written);
        return written;
    }

    /// <summary>A layer document with its sync flags, or the document unchanged.</summary>
    /// <param name="context">The request, for the response's serializer options.</param>
    /// <param name="document">The document.</param>
    /// <param name="layer">The layer.</param>
    /// <returns>The document to write.</returns>
    public static object ApplyToLayer(HttpContext context, object document, PublishedLayer layer)
    {
        if (!layer.ServiceSyncEnabled || !layer.Definition.IsHosted || layer.ViewOf is not null
            || Node(context, document) is not { } written)
        {
            return document;
        }

        SyncDocument.ApplyToLayer(written);
        return written;
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
