using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Graticula.Host;

/// <summary>
/// A service document's <c>serviceItemId</c>: the portal item this face of the service is, as ArcGIS's service
/// documents name it.
/// </summary>
/// <remarks>
/// <b>Asked for 2026-10-18</b> (owner, through the session building Studio's item page): the link between a service
/// and its item both ways. The item already named its service by <c>url</c>; the four service documents — FeatureServer,
/// MapServer, VectorTileServer, ImageServer — named no item. The id is the one the portal gives the same face
/// (<c>PortalEndpoints.ItemIdOf</c>, <c>CoverageItemId</c>), so the two cannot disagree. The writers live in
/// <c>Graticula.Api.ArcGis</c>, which knows nothing of the portal, so it is added here, as <see cref="ViewFlags"/> adds
/// its fields.
/// </remarks>
internal static class ServiceItem
{
    /// <summary>The document with <c>serviceItemId</c> written into it.</summary>
    public static object Apply(HttpContext context, object document, string itemId)
    {
        JsonSerializerOptions options =
            context.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
            ?? JsonSerializerOptions.Web;

        if (document is not JsonObject written)
        {
            if (JsonSerializer.SerializeToNode(document, document.GetType(), options) is not JsonObject node)
            {
                return document;
            }

            written = node;
        }

        written["serviceItemId"] = itemId;
        return written;
    }
}
