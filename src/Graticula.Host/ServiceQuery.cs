using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// <c>FeatureServer/query</c>: several layers of one service queried in one request, answered as
/// <c>{"layers":[{"id":0,…},…]}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>V-82, the owner's decision of 2026-09-23.</b> ArcGIS offers the operation on the service as well as
/// on each layer — <c>layerDefs</c> says which layers, each with its own <c>where</c> and, in the array
/// form, its own <c>outFields</c>; the spatial filter, <c>outSR</c> and the return flags are shared — and
/// some Dashboards and Experience Builder widgets call it. It was a 404.
/// </para>
/// <para>
/// <b>Each layer is answered by the layer's own query, not by a second implementation of it.</b> The
/// handler runs <c>FeatureServer/{id}/query</c> for every layer named, on a request of its own that
/// carries the caller's identity and the shared parameters, and puts each answer under its layer id. So
/// sharing, the capability ceiling, the service's cost ceilings, the where grammar, the parameters it
/// refuses and the shape of every answer — features, ids only, count only — are the layer query's by
/// construction, and nothing here can drift from it.
/// </para>
/// <para>
/// <b>All or nothing.</b> A layer that refuses — a clause that does not parse, a ceiling that refuses
/// <c>Query</c>, an unknown parameter — refuses the request with that layer's answer and status, and names
/// the layer, as ArcGIS does rather than returning the layers that worked.
/// </para>
/// <para>
/// <b>The cost that is accepted.</b> Each layer's answer is held in memory until every layer has answered,
/// because they are one JSON document; each is still bounded by the service's own ceilings, which is what
/// bounds a single layer's query. The answer is <c>no-store</c>: it is several layers' data under one
/// address, and the per-layer lifetimes and validators do not combine into one that would be true.
/// </para>
/// </remarks>
internal static class ServiceQuery
{
    /// <summary>The parameters the service-level query reads itself rather than passing to each layer.</summary>
    private static readonly HashSet<string> Own = new(StringComparer.OrdinalIgnoreCase)
    {
        "layerDefs", "f", "where", "outFields",
    };

    /// <summary>Answers the request.</summary>
    /// <param name="context">The request.</param>
    /// <param name="serviceName">The service name from the route.</param>
    /// <param name="catalog">The catalogue.</param>
    /// <param name="queryLayer">Runs one layer's own query against a request built for it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task RunAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        Func<HttpContext, int, Task> queryLayer,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(queryLayer);

        PublishedService? service = await ServiceLookup
            .FeatureServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        ArcGisParameters parameters = await ArcGisParameters.ReadAsync(context, cancellation).ConfigureAwait(false);

        // <b>Each value, not their join.</b> A client that repeats `f=json` is asking for JSON twice, and the
        // layer query takes it; reading the collection as one string made that `json,json` and a refusal.
        if (parameters["f"].FirstOrDefault(value =>
                !string.IsNullOrEmpty(value)
                && !string.Equals(value, "json", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "pjson", StringComparison.OrdinalIgnoreCase)) is { } format)
        {
            await RefuseAsync(
                context,
                400,
                $"A service-level query answers f=json only; '{format}' is answered by each layer's own "
                + $"query at /rest/services/{service.QualifiedName}/FeatureServer/{{id}}/query.")
                .ConfigureAwait(false);
            return;
        }

        if (!TryReadDefinitions(parameters["layerDefs"].ToString(), service, out List<Definition> definitions, out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        JsonArray layers = [];

        foreach (Definition definition in definitions)
        {
            (int status, byte[] body) = await RunLayerAsync(context, parameters, definition, queryLayer)
                .ConfigureAwait(false);

            JsonNode? answer = Parse(body);

            if (status != StatusCodes.Status200OK || answer is not JsonObject found || found["error"] is not null)
            {
                await PassOnRefusalAsync(context, definition.LayerId, status, answer).ConfigureAwait(false);
                return;
            }

            JsonObject layer = new() { ["id"] = definition.LayerId };

            foreach ((string name, JsonNode? value) in found.ToList())
            {
                found.Remove(name);
                layer[name] = value;
            }

            layers.Add(layer);
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response
            .WriteAsync(new JsonObject { ["layers"] = layers }.ToJsonString(), cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>One layer the caller named, with its clause and fields.</summary>
    /// <param name="LayerId">The layer's id in the service.</param>
    /// <param name="Where">Its clause; null for every feature.</param>
    /// <param name="OutFields">Its fields; null for the layer query's default.</param>
    internal sealed record Definition(int LayerId, string? Where, string? OutFields);

    /// <summary>
    /// Reads <c>layerDefs</c> in its three ArcGIS spellings: an array of
    /// <c>{layerId, where, outFields}</c>, an object keyed by layer id, and <c>0:clause;1:clause</c>.
    /// </summary>
    /// <param name="raw">The parameter.</param>
    /// <param name="service">The service, for which layers exist.</param>
    /// <param name="definitions">The layers named, in the order named.</param>
    /// <param name="error">Why not.</param>
    /// <returns>Whether it could be read.</returns>
    internal static bool TryReadDefinitions(
        string? raw, PublishedService service, out List<Definition> definitions, out string? error)
    {
        ArgumentNullException.ThrowIfNull(service);

        definitions = [];
        error = null;

        string text = (raw ?? string.Empty).Trim();

        if (text.Length == 0 || text is "{}" or "[]")
        {
            error = "A service-level query needs `layerDefs`, which says which layers to query: "
                + "[{\"layerId\":0,\"where\":\"1=1\",\"outFields\":\"*\"}] or {\"0\":\"1=1\"}.";
            return false;
        }

        if (text[0] == '[')
        {
            try
            {
                foreach (JsonElement entry in JsonDocument.Parse(text).RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || Property(entry, "layerId") is not { } id
                        || !id.TryGetInt32(out int layerId))
                    {
                        error = "Each entry of a `layerDefs` array needs a numeric `layerId`.";
                        return false;
                    }

                    string? Text(string name)
                    {
                        if (Property(entry, name) is not { } value || value.ValueKind == JsonValueKind.Null)
                        {
                            return null;
                        }

                        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                    }

                    definitions.Add(new Definition(layerId, Text("where"), Text("outFields")));
                }
            }
            catch (JsonException)
            {
                error = "`layerDefs` starts like JSON and is not JSON.";
                return false;
            }
        }
        else if (LayerDefinitions.TryRead(text, service.Layers, out Dictionary<int, string> clauses, out error))
        {
            definitions.AddRange(clauses.Select(clause => new Definition(clause.Key, clause.Value, null)));
        }
        else
        {
            return false;
        }

        foreach (Definition definition in definitions)
        {
            if (service.Layer(definition.LayerId) is null)
            {
                error = $"`layerDefs` names layer {definition.LayerId}, which is not a layer of this service "
                    + "that holds features.";
                return false;
            }
        }

        if (definitions.GroupBy(d => d.LayerId).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            error = $"`layerDefs` names layer {twice.Key} twice.";
            return false;
        }

        return true;
    }

    private static JsonElement? Property(JsonElement entry, string name)
    {
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs one layer's query on a request of its own, and returns its status and body.
    /// </summary>
    /// <remarks>
    /// <b>Its own request, response, query and form features, and the caller's everything else.</b>
    /// The feature collection falls back to the caller's for what is not set here — the principal, the
    /// services, the connection, cancellation — which is what makes it the same caller. Setting the four
    /// here is what keeps it from being the same <i>request</i>: without them, writing this query string
    /// would rewrite the caller's.
    /// </remarks>
    private static async Task<(int Status, byte[] Body)> RunLayerAsync(
        HttpContext context,
        ArcGisParameters parameters,
        Definition definition,
        Func<HttpContext, int, Task> queryLayer)
    {
        Dictionary<string, StringValues> shared = new(StringComparer.OrdinalIgnoreCase);

        foreach (string key in parameters.Keys.Where(key => !Own.Contains(key)))
        {
            shared[key] = parameters[key];
        }

        shared["f"] = "json";
        shared["where"] = string.IsNullOrWhiteSpace(definition.Where) ? "1=1" : definition.Where;

        if (!string.IsNullOrWhiteSpace(definition.OutFields))
        {
            shared["outFields"] = definition.OutFields;
        }

        HeaderDictionary headers = [];

        foreach ((string name, StringValues value) in context.Request.Headers)
        {
            // Not the body's headers — the layer's request has none — and not a validator the caller
            // sent for this document, which would make a layer answer 304 inside a 200.
            if (!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("If-", StringComparison.OrdinalIgnoreCase))
            {
                headers[name] = value;
            }
        }

        string path = context.Request.Path.Value ?? string.Empty;
        string layerPath = path.EndsWith("/query", StringComparison.OrdinalIgnoreCase)
            ? $"{path[..^"/query".Length]}/{definition.LayerId}/query"
            : path;

        FeatureCollection features = new(context.Features);

        features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Method = HttpMethods.Get,
            Scheme = context.Request.Scheme,
            Protocol = context.Request.Protocol,
            PathBase = context.Request.PathBase.Value ?? string.Empty,
            Path = layerPath,
            QueryString = QueryString.Create(shared).Value ?? string.Empty,
            Headers = headers,
            Body = Stream.Null,
        });

        using MemoryStream body = new();

        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        StreamResponseBodyFeature written = new(body);

        features.Set<IHttpResponseBodyFeature>(written);
        features.Set<IItemsFeature>(new ItemsFeature());

        DefaultHttpContext layer = new(features);

        features.Set<IQueryFeature>(new QueryFeature(layer.Features));
        features.Set<IFormFeature>(new FormFeature(layer.Request));

        RouteValueDictionary routes = new(context.Request.RouteValues)
        {
            ["layerId"] = definition.LayerId,
        };

        features.Set<IRouteValuesFeature>(new RouteValuesFeature { RouteValues = routes });

        await queryLayer(layer, definition.LayerId).ConfigureAwait(false);
        await written.CompleteAsync().ConfigureAwait(false);

        return (layer.Response.StatusCode, body.ToArray());
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

    /// <summary>A layer's refusal, as the service's, with the layer named.</summary>
    private static async Task PassOnRefusalAsync(HttpContext context, int layerId, int status, JsonNode? answer)
    {
        string said = answer?["error"]?["message"] is JsonValue message && message.TryGetValue(out string? text)
            ? text
            : $"it answered {status} with no explanation";

        int code = status == StatusCodes.Status200OK ? StatusCodes.Status400BadRequest : status;

        await RefuseAsync(context, code, $"Layer {layerId} could not be queried: {said}").ConfigureAwait(false);
    }

    private static Task RefuseAsync(HttpContext context, int status, string message) =>
        Results.Json(
            new { error = new { code = status, message, details = Array.Empty<string>() } },
            statusCode: status)
            .ExecuteAsync(context);
}
