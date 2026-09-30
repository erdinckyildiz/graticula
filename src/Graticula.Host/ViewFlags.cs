using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Graticula.Host;

/// <summary>
/// What a FeatureServer or layer document says about views — ADR-113 §5.5: <c>isView</c> and <c>isUpdatableView</c> on
/// a view, <c>hasViews</c> on its source.
/// </summary>
/// <remarks>
/// <b>Added to the document only when it is one or the other</b>, through the same serializer options the response
/// uses, so every other document is written exactly as before.
/// </remarks>
internal static class ViewFlags
{
    /// <summary>The document with its view flags, or the document unchanged.</summary>
    /// <param name="context">The request, for the response's serializer options.</param>
    /// <param name="document">The document.</param>
    /// <param name="isView">Whether it is a view's.</param>
    /// <param name="hasViews">Whether it is a source's with views.</param>
    /// <param name="capabilities">What it offers, which says whether a view takes edits.</param>
    /// <returns>The document to write.</returns>
    public static object Apply(HttpContext context, object document, bool isView, bool hasViews, string capabilities)
    {
        if (!isView && !hasViews)
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

        if (isView)
        {
            written["isView"] = true;
            written["isUpdatableView"] = capabilities.Contains("Create", System.StringComparison.Ordinal)
                || capabilities.Contains("Update", System.StringComparison.Ordinal)
                || capabilities.Contains("Delete", System.StringComparison.Ordinal);
        }

        if (hasViews)
        {
            written["hasViews"] = true;
        }

        return written;
    }
}
