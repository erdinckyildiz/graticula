using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// <c>FeatureServer/{layer}/{objectId}</c>: one feature by its id, answered as <c>{"feature":{…}}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>D-298, 2026-10-16.</b> ArcGIS documents this as the <i>Feature</i> resource, and Esri's server answers it
/// with every attribute and the geometry whatever <c>outFields</c> says, and with a 404 envelope for an id that
/// is not there (measured on sampleserver6 the same day). It was a 404 here for every id.
/// </para>
/// <para>
/// <b>Answered by the layer's own query, as <see cref="ServiceQuery"/> is.</b> The handler runs
/// <c>query?objectIds={id}&amp;outFields=*</c> on a request of its own that is still the caller's, so sharing,
/// the feature face's door, the capability ceiling and the shape of a feature are the query's by construction.
/// A refusal from the query is passed on as it was given.
/// </para>
/// </remarks>
internal static class FeatureResource
{
    /// <summary>The parameters passed on to the query: who is asking and where the geometry is wanted.</summary>
    private static readonly string[] Passed = ["token", "outSR"];

    /// <summary>Answers the request.</summary>
    /// <param name="context">The request.</param>
    /// <param name="layerId">The layer's id in the service.</param>
    /// <param name="objectId">The feature's object id.</param>
    /// <param name="queryLayer">Runs the layer's own query against a request built for it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task RunAsync(
        HttpContext context,
        int layerId,
        long objectId,
        Func<HttpContext, int, Task> queryLayer,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(queryLayer);

        ArcGisParameters parameters = await ArcGisParameters.ReadAsync(context, cancellation).ConfigureAwait(false);

        if (parameters["f"].FirstOrDefault(value =>
                !string.IsNullOrEmpty(value)
                && !string.Equals(value, "json", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "pjson", StringComparison.OrdinalIgnoreCase)) is { } format)
        {
            await RefuseAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"A feature is answered as f=json only; '{format}' is answered by the layer's query with objectIds={objectId}.")
                .ConfigureAwait(false);
            return;
        }

        Dictionary<string, StringValues> query = new(StringComparer.OrdinalIgnoreCase)
        {
            ["objectIds"] = objectId.ToString(CultureInfo.InvariantCulture),
            ["outFields"] = "*",
            ["returnGeometry"] = "true",
            ["f"] = "json",
        };

        foreach (string name in Passed)
        {
            if (parameters[name] is { Count: > 0 } value && !string.IsNullOrEmpty(value.ToString()))
            {
                query[name] = value;
            }
        }

        string path = context.Request.Path.Value ?? string.Empty;
        string queryPath = $"{path[..path.LastIndexOf('/')]}/query";

        (int status, byte[] body) = await ServiceQuery
            .InnerQueryAsync(context, queryPath, query, layerId, queryLayer)
            .ConfigureAwait(false);

        JsonNode? answer = Parse(body);

        if (status != StatusCodes.Status200OK || answer is not JsonObject found || found["error"] is not null)
        {
            context.Response.StatusCode = status == StatusCodes.Status200OK ? StatusCodes.Status400BadRequest : status;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.Body.WriteAsync(body, cancellation).ConfigureAwait(false);
            return;
        }

        if (found["features"] is not JsonArray { Count: > 0 } features)
        {
            await RefuseAsync(
                context,
                StatusCodes.Status404NotFound,
                $"Layer {layerId} has no feature {objectId}, or none this caller can see.")
                .ConfigureAwait(false);
            return;
        }

        JsonNode? feature = features[0];
        features.Clear();

        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response
            .WriteAsync(new JsonObject { ["feature"] = feature }.ToJsonString(), cancellation)
            .ConfigureAwait(false);
    }

    private static JsonNode? Parse(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task RefuseAsync(HttpContext context, int status, string message) =>
        Results.Json(
            new { error = new { code = status, message, details = Array.Empty<string>() } },
            statusCode: status)
            .ExecuteAsync(context);
}
