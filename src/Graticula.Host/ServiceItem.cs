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

    /// <summary>
    /// A layer document with its service's <c>serviceItemId</c> and, for a hosted layer whose last change this server
    /// recorded, ArcGIS's <c>editingInfo</c> — the ArcGIS veteran's review of 2026-11-03.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ArcGIS's <i>Layer (Feature Service)</i> names both on the layer</b>: <c>serviceItemId</c> as the service does,
    /// and <c>editingInfo</c> with <c>lastEditDate</c>, <c>schemaLastEditDate</c> and <c>dataLastEditDate</c> in
    /// milliseconds since the epoch. The Maps SDK's <c>FeatureLayer.refresh()</c> reads <c>lastEditDate</c> to tell
    /// whether the data changed.
    /// </para>
    /// <para>
    /// <b>Hosted layers only, and only from what this server recorded</b> (migration 133): every edit face, append,
    /// overwrite, truncate, restore, analysis replacement and field change stamps it. A registered layer's table is also
    /// written in its own database by whoever owns it, which this server never sees — a date there would tell
    /// <c>refresh()</c> nothing changed when something had, so none is said. A hosted layer from before migration 133
    /// that has not changed since has no date either, rather than an invented one.
    /// </para>
    /// </remarks>
    public static object ApplyToLayer(HttpContext context, object document, string itemId, Graticula.Platform.Catalog.PublishedLayer layer)
    {
        if (Apply(context, document, itemId) is not JsonObject written)
        {
            return document;
        }

        if (layer.Definition.IsHosted && (layer.DataChangedAt ?? layer.SchemaChangedAt) is not null)
        {
            long? data = layer.DataChangedAt?.ToUnixTimeMilliseconds();
            long? schema = layer.SchemaChangedAt?.ToUnixTimeMilliseconds();

            JsonObject info = new() { ["lastEditDate"] = System.Math.Max(data ?? 0, schema ?? 0) };

            if (schema is { } fields)
            {
                info["schemaLastEditDate"] = fields;
            }

            if (data is { } rows)
            {
                info["dataLastEditDate"] = rows;
            }

            written["editingInfo"] = info;
        }

        return written;
    }
}
