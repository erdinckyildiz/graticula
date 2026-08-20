using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Graticula.Geometries;

namespace Graticula.Api.OgcFeatures;

/// <summary>
/// The metadata documents: landing page, conformance, collections and one collection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Links are the protocol, not decoration.</b> OGC API Features is discoverable
/// by following <c>rel</c> values from the landing page, and a client that cannot
/// find <c>data</c> cannot find the collections at all. Every document here carries
/// <c>self</c> and an <c>alternate</c> for its other representation, which is what
/// §7.2 requires and what makes the HTML face reachable from the JSON one.
/// </para>
/// <para>
/// <b>Absolute URLs, built from the request.</b> A relative link is ambiguous the
/// moment a client stores a document and resolves it later, and this server sits
/// behind whatever host an operator gives it.
/// </para>
/// </remarks>
public static class OgcDocuments
{
    /// <summary>One link in a document.</summary>
    /// <param name="Href">Where it goes.</param>
    /// <param name="Rel">What it is.</param>
    /// <param name="Type">The media type behind it, or null.</param>
    /// <param name="Title">Something for a person to read, or null.</param>
    public readonly record struct Link(string Href, string Rel, string? Type = null, string? Title = null);

    /// <summary>The landing page at the API's root.</summary>
    /// <param name="origin">This server's absolute address, with no trailing slash.</param>
    /// <returns>The JSON.</returns>
    public static string Landing(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        string root = origin + OgcNames.Base;

        return Write(json =>
        {
            json.WriteString("title", "Graticula");
            json.WriteString(
                "description",
                "Features from this server's published layers, read-only. The same layers are "
                + "served through WFS 2.0, WMS 1.3.0 and the ArcGIS REST API.");

            WriteLinks(json,
            [
                new Link(root, "self", OgcNames.Json, "This document"),
                new Link(root + "?f=html", "alternate", OgcNames.Html, "This document as HTML"),
                new Link(root + "/api", "service-desc", OgcNames.OpenApi, "The API definition"),
                new Link(root + "/api?f=html", "service-doc", OgcNames.Html, "The API documentation"),
                new Link(root + "/conformance", "conformance", OgcNames.Json, "Conformance classes"),
                new Link(root + "/collections", "data", OgcNames.Json, "Collections"),
            ]);
        });
    }

    /// <summary>The conformance document.</summary>
    /// <returns>The JSON.</returns>
    public static string Conformance() => Write(json =>
    {
        json.WriteStartArray("conformsTo");

        foreach (string uri in OgcNames.ConformsTo)
        {
            json.WriteStringValue(uri);
        }

        json.WriteEndArray();
    });

    /// <summary>The list of collections.</summary>
    /// <param name="origin">This server's absolute address.</param>
    /// <param name="collections">The collections the caller may see.</param>
    /// <returns>The JSON.</returns>
    public static string Collections(string origin, IReadOnlyList<CollectionMetadata> collections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentNullException.ThrowIfNull(collections);

        string root = origin + OgcNames.Base;

        return Write(json =>
        {
            WriteLinks(json,
            [
                new Link(root + "/collections", "self", OgcNames.Json, "This document"),
                new Link(
                    root + "/collections?f=html", "alternate", OgcNames.Html, "This document as HTML"),
            ]);

            json.WriteStartArray("collections");

            foreach (CollectionMetadata collection in collections)
            {
                json.WriteStartObject();
                WriteCollection(json, root, collection);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        });
    }

    /// <summary>One collection's document.</summary>
    /// <param name="origin">This server's absolute address.</param>
    /// <param name="collection">The collection.</param>
    /// <returns>The JSON.</returns>
    public static string Collection(string origin, CollectionMetadata collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentNullException.ThrowIfNull(collection);

        return Write(json => WriteCollection(json, origin + OgcNames.Base, collection));
    }

    private static void WriteCollection(Utf8JsonWriter json, string root, CollectionMetadata collection)
    {
        string self = $"{root}/collections/{Uri.EscapeDataString(collection.Id)}";

        json.WriteString("id", collection.Id);
        json.WriteString("title", collection.Title);

        if (collection.Description is { Length: > 0 } description)
        {
            json.WriteString("description", description);
        }

        // <b>`itemType` defaults to "feature" and is written anyway.</b> OGC API
        // Records and EDR use the same collection shape with other item types, so a
        // client reading a mixed catalogue tells them apart by this member alone.
        json.WriteString("itemType", "feature");

        WriteLinks(json,
        [
            new Link(self, "self", OgcNames.Json, collection.Title),
            new Link(self + "?f=html", "alternate", OgcNames.Html, "This document as HTML"),
            new Link(self + "/items", "items", OgcNames.GeoJson, collection.Title + " — features"),
            new Link(
                self + "/items?f=html", "items", OgcNames.Html, collection.Title + " — as HTML"),
        ]);

        json.WriteStartObject("extent");

        json.WriteStartObject("spatial");
        json.WriteStartArray("bbox");
        json.WriteStartArray();

        // <b>The whole world when the extent is unknown, and that is the honest
        // answer.</b> `extent` is required; omitting the bbox would say the
        // collection has no extent, which is a different claim from not knowing it.
        Envelope box = collection.Extent ?? new Envelope(-180, -90, 180, 90);

        json.WriteNumberValue(box.MinX);
        json.WriteNumberValue(box.MinY);
        json.WriteNumberValue(box.MaxX);
        json.WriteNumberValue(box.MaxY);

        json.WriteEndArray();
        json.WriteEndArray();
        json.WriteString("crs", OgcNames.Crs84);
        json.WriteEndObject();

        json.WriteStartObject("temporal");
        json.WriteStartArray("interval");
        json.WriteStartArray();

        // <b>null is how an open end is written here</b>, not `..`. The string form
        // belongs to the `datetime` parameter; the document uses JSON's own null,
        // and mixing them produces a document that validates nowhere.
        WriteMoment(json, collection.From);
        WriteMoment(json, collection.Until);

        json.WriteEndArray();
        json.WriteEndArray();
        json.WriteString("trs", "http://www.opengis.net/def/uom/ISO-8601/0/Gregorian");
        json.WriteEndObject();

        json.WriteEndObject();

        json.WriteStartArray("crs");

        foreach (string crs in collection.CoordinateSystems)
        {
            json.WriteStringValue(crs);
        }

        json.WriteEndArray();

        // Part 2 §6.3: a collection says which reference system its data is held in,
        // so a client can ask for that one and get no transformation at all.
        json.WriteString("storageCrs", collection.StorageCrs);
    }

    private static void WriteMoment(Utf8JsonWriter json, DateTimeOffset? moment)
    {
        if (moment is { } at)
        {
            json.WriteStringValue(at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            json.WriteNullValue();
        }
    }

    /// <summary>Writes a <c>links</c> array.</summary>
    /// <param name="json">Where to write.</param>
    /// <param name="links">The links.</param>
    public static void WriteLinks(Utf8JsonWriter json, IReadOnlyList<Link> links)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(links);

        json.WriteStartArray("links");

        foreach (Link link in links)
        {
            json.WriteStartObject();
            json.WriteString("href", link.Href);
            json.WriteString("rel", link.Rel);

            if (link.Type is { Length: > 0 } type)
            {
                json.WriteString("type", type);
            }

            if (link.Title is { Length: > 0 } title)
            {
                json.WriteString("title", title);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using System.IO.MemoryStream stream = new();

        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            body(json);
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
