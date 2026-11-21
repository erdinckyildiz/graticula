using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Api.ArcGis;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Graticula.Host;

/// <summary>
/// What a feature service's own document says about <c>append</c> — <c>supportsAppend</c> and
/// <c>supportedAppendFormats</c>, the two properties ArcGIS's service resource carries — ADR-310.
/// </summary>
/// <remarks>
/// <b>Said only where the route does it.</b> The service-level <c>append</c> writes into hosted layers by the importer and
/// into registered ones through the edit writer, but a registered layer is writable only for a caller who may edit it,
/// which the service's document cannot say once for everybody; so the service says it where a hosted layer is, as the
/// layer's own document has said it since ADR-105, and a registered layer says it in its own.
/// </remarks>
internal static class AppendFlags
{
    /// <summary>The service document with the two append properties, when a layer of it is hosted.</summary>
    /// <param name="context">The request.</param>
    /// <param name="document">The service document.</param>
    /// <param name="service">The service.</param>
    /// <returns>The document to write.</returns>
    public static object Apply(HttpContext context, object document, PublishedService service)
    {
        if (!service.Layers.Any(layer => layer.Definition.IsHosted))
        {
            return document;
        }

        JsonSerializerOptions options =
            context.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
            ?? JsonSerializerOptions.Web;

        if (JsonSerializer.SerializeToNode(document, document.GetType(), options) is not JsonObject written)
        {
            return document;
        }

        written["supportsAppend"] = true;
        written["supportedAppendFormats"] = FeatureServerMetadataWriter.AppendFormats;

        return written;
    }
}
