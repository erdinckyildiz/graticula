using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Graticula.Api.OgcFeatures;

/// <summary>
/// The OpenAPI 3.0 definition at <c>/api</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written by hand, not generated from the routes.</b> A generated document
/// describes what the code happens to expose; this one describes what the
/// specification requires, which is the thing a conformance test checks. The two
/// disagreeing is a finding, and generating it would hide the finding by
/// construction.
/// </para>
/// <para>
/// <b>The <c>oas30</c> conformance class is claimed, so this has to be real.</b> A
/// server that lists the class and serves a stub has told every validator to check
/// something that is not there. What is here covers the five operations Part 1
/// defines and the parameters this server honours — nothing that is not implemented
/// appears.
/// </para>
/// <para>
/// <b>Collections are not enumerated as paths.</b> Part 1 §7.3 allows either;
/// listing a path per collection makes the document grow with the catalogue, and at
/// the stated scale of 100–1,000 services that is a document nobody reads and every
/// client downloads.
/// </para>
/// </remarks>
public static class OpenApiDocument
{
    /// <summary>Writes the definition.</summary>
    /// <param name="origin">This server's absolute address.</param>
    /// <param name="limits">The bounds the <c>limit</c> parameter carries.</param>
    /// <returns>The JSON.</returns>
    public static string Write(string origin, OgcLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        using System.IO.MemoryStream stream = new();

        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("openapi", "3.0.3");

            json.WriteStartObject("info");
            json.WriteString("title", "Graticula — OGC API Features");
            json.WriteString(
                "description",
                "Features from this server's published layers, read-only.");
            json.WriteString("version", "1.0.0");

            json.WriteStartObject("license");
            json.WriteString("name", "AGPL-3.0-or-later");
            json.WriteEndObject();

            json.WriteEndObject();

            json.WriteStartArray("servers");
            json.WriteStartObject();
            json.WriteString("url", origin + OgcNames.Base);
            json.WriteString("description", "This server");
            json.WriteEndObject();
            json.WriteEndArray();

            json.WriteStartObject("paths");

            Path(json, "/", "getLandingPage", "The landing page",
                "Links to the API definition, the conformance declaration and the collections.",
                [], "application/json");

            Path(json, "/conformance", "getConformanceDeclaration",
                "The conformance classes this server implements",
                "A list of URIs, one per class.", [], "application/json");

            Path(json, "/api", "getApiDefinition", "This document",
                "The OpenAPI 3.0 definition of this API.", [], "application/json");

            Path(json, "/collections", "getCollections", "The collections",
                "Every collection this caller may see.", [], "application/json");

            Path(json, "/collections/{collectionId}", "describeCollection", "One collection",
                "Its extent, its reference systems and links to its features.",
                ["collectionId"], "application/json");

            Path(json, "/collections/{collectionId}/items", "getFeatures", "Features",
                "A page of features, filtered by extent, time and attribute.",
                ["collectionId", "limit", "offset", "bbox", "bbox-crs", "crs", "datetime"],
                "application/geo+json");

            Path(json, "/collections/{collectionId}/items/{featureId}", "getFeature", "One feature",
                "A single feature by its identifier.",
                ["collectionId", "featureId", "crs"], "application/geo+json");

            json.WriteEndObject();

            json.WriteStartObject("components");
            json.WriteStartObject("parameters");

            Parameter(json, "collectionId", "path",
                "The collection's identifier, which is the layer's name.", "string");

            Parameter(json, "featureId", "path",
                "The feature's identifier within its collection.", "string");

            Parameter(json, "limit", "query",
                $"How many features to return. At most {limits.MaximumLimit}; a larger value is "
                + $"reduced to it rather than refused. Default {limits.DefaultLimit}.",
                "integer");

            Parameter(json, "offset", "query", "How many features to skip.", "integer");

            // <b>An array, not a string, and the CITE suite is what said so.</b> Part 1
            // §7.15.3 types `bbox` as an array of four or six numbers, and a
            // generated client built from a `string` schema sends the whole box as
            // one opaque value. It was declared as a string here until 2026-08-20.
            ArrayParameter(json, "bbox",
                "Four numbers, or six with minimum and maximum elevation between them, in the "
                + "order the `bbox-crs` reference system defines.", 4, 6);

            Parameter(json, "bbox-crs", "query",
                "The reference system the bounding box is written in. Default CRS84.", "string");

            Parameter(json, "crs", "query",
                "The reference system to answer in. Default CRS84.", "string");

            Parameter(json, "datetime", "query",
                "An RFC 3339 instant, or an interval with `..` for an open end.", "string");

            json.WriteEndObject();
            json.WriteEndObject();

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Path(
        Utf8JsonWriter json,
        string path,
        string operationId,
        string summary,
        string description,
        IReadOnlyList<string> parameters,
        string mediaType)
    {
        json.WriteStartObject(path);
        json.WriteStartObject("get");
        json.WriteString("operationId", operationId);
        json.WriteString("summary", summary);
        json.WriteString("description", description);

        json.WriteStartArray("tags");
        json.WriteStringValue("Features");
        json.WriteEndArray();

        json.WriteStartArray("parameters");

        foreach (string parameter in parameters)
        {
            json.WriteStartObject();
            json.WriteString("$ref", "#/components/parameters/" + parameter);
            json.WriteEndObject();
        }

        json.WriteEndArray();

        json.WriteStartObject("responses");

        json.WriteStartObject("200");
        json.WriteString("description", summary);
        json.WriteStartObject("content");
        json.WriteStartObject(mediaType);
        json.WriteStartObject("schema");
        json.WriteString("type", "object");
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();

        // <b>The refusals are described because they are part of the contract.</b>
        // A document that lists only 200 tells a generated client that nothing can
        // go wrong, and the generated client then has no branch for what does.
        foreach ((string code, string why) in
            ((string, string)[])
            [
                ("400", "A parameter this server cannot honour."),
                ("404", "No such resource, or none this caller may see."),
                ("406", "A representation this server does not write."),
            ])
        {
            json.WriteStartObject(code);
            json.WriteString("description", why);
            json.WriteStartObject("content");
            json.WriteStartObject(OgcNames.Problem);
            json.WriteStartObject("schema");
            json.WriteString("type", "object");
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>A query parameter that is an array of numbers.</summary>
    private static void ArrayParameter(
        Utf8JsonWriter json, string name, string description, int minimum, int maximum)
    {
        json.WriteStartObject(name);
        json.WriteString("name", name);
        json.WriteString("in", "query");
        json.WriteString("description", description);
        json.WriteBoolean("required", false);
        json.WriteString("style", "form");
        json.WriteBoolean("explode", false);

        json.WriteStartObject("schema");
        json.WriteString("type", "array");
        json.WriteNumber("minItems", minimum);
        json.WriteNumber("maxItems", maximum);

        json.WriteStartObject("items");
        json.WriteString("type", "number");
        json.WriteEndObject();

        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void Parameter(
        Utf8JsonWriter json, string name, string place, string description, string type)
    {
        json.WriteStartObject(name);
        json.WriteString("name", name);
        json.WriteString("in", place);
        json.WriteString("description", description);
        json.WriteBoolean("required", string.Equals(place, "path", StringComparison.Ordinal));
        json.WriteString("style", "form");
        json.WriteBoolean("explode", false);

        json.WriteStartObject("schema");
        json.WriteString("type", type);
        json.WriteEndObject();

        json.WriteEndObject();
    }
}
