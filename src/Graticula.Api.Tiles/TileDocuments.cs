using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.Tiles;

/// <summary>One source layer of a tile, as the OGC tileset metadata and TileJSON describe it.</summary>
/// <param name="Id">Its name in the tile.</param>
/// <param name="GeometryDimension">0 for points, 1 for lines, 2 for polygons, or null when unknown.</param>
/// <param name="Fields">The attributes the tile carries for it.</param>
/// <param name="MinLevel">The first level it is in the tile at, or null when it is in none.</param>
/// <param name="MaxLevel">The last level it is in the tile at, or null when it is in none.</param>
public sealed record TileLayerInfo(
    string Id, int? GeometryDimension, IReadOnlyList<FieldDescription> Fields, int? MinLevel, int? MaxLevel);

/// <summary>One vector tile service, as the OGC face lists it.</summary>
/// <param name="Id">The collection id — <see cref="TileDocuments.CollectionId"/> of its folder and name.</param>
/// <param name="Title">What a person reads: its qualified name.</param>
/// <param name="Description">Its description, or null.</param>
/// <param name="Set">The tile matrix set its grid is.</param>
/// <param name="Wgs84">Its extent in CRS84 longitude and latitude, or null when unknown.</param>
/// <param name="InSet">Its extent in the set's own reference, easting first, or null when unknown.</param>
/// <param name="Layers">Its source layers, in tile order.</param>
public sealed record TiledService(
    string Id,
    string Title,
    string? Description,
    TileMatrixSet Set,
    Envelope? Wgs84,
    Envelope? InSet,
    IReadOnlyList<TileLayerInfo> Layers);

/// <summary>
/// The OGC API Tiles documents — landing page, conformance, collections, tilesets and tile matrix
/// sets — ADR-097 §5.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>A collection is a vector tile service, not a layer.</b> OGC API Features' collection is a layer
/// (ADR-042 §5.2), and the tiles cannot follow it there: a tile carries every layer of its service,
/// concatenated (<c>VectorTileEndpoints.Concatenate</c>), and is cached per layer but served per
/// service. A per-layer collection would be a tile nobody builds. So this face has its own collections
/// under its own base, and each tileset's <c>layers</c> names the service's layers, which is the shape
/// 17-083r4's <c>TileSetMetadata</c> gives a multi-layer vector tileset.
/// </para>
/// <para>
/// <b>Written against 20-057 and 17-083r4's JSON, and nothing else.</b> Links carry the full OGC
/// relation URIs the requirements name (<c>/req/tileset/description</c> D, <c>/req/tilesets-list/
/// tileset-links</c> A), templated links say <c>templated: true</c> (E, G), and a tileset on a
/// registered set gives its <c>tileMatrixSetURI</c> (C).
/// </para>
/// </remarks>
public static class TileDocuments
{
    /// <summary>
    /// The collection id of a service: its name at the root, or <c>folder.name</c> in a folder.
    /// </summary>
    /// <param name="folder">Its folder, or null.</param>
    /// <param name="name">Its name.</param>
    /// <returns>The id.</returns>
    /// <remarks>
    /// <para>
    /// <b>One path segment, because the standard's paths put the id in one.</b> The qualified name
    /// <c>folder/name</c> is the ArcGIS address, and a slash inside a segment has to travel as
    /// <c>%2F</c>, which Apache refuses by default and several proxies decode on the way through — a
    /// collection reachable from one network and not another. Neither a folder nor a service name may
    /// contain <c>/ \ ? # %</c> (<c>AdminEndpoints.TryReadFolderName</c>, <c>HostedDataEndpoints</c>),
    /// so no other character is reserved, and the dot is the one a person reads as <i>inside</i>.
    /// </para>
    /// <para>
    /// <b>Not collision-free, and the collision is refused rather than guessed.</b> A root service called
    /// <c>hosted.parcels</c> and a service <c>parcels</c> in folder <c>hosted</c> share an id. The face
    /// that finds two answers for one id serves neither under it (<c>TileFaces</c>) and says so, so a
    /// client is never handed the other service's tiles. *INFERRED* acceptable: no deployment is known
    /// to name a root service with a folder's name and a dot.
    /// </para>
    /// </remarks>
    public static string CollectionId(string? folder, string name) =>
        folder is { Length: > 0 } ? folder + "." + name : name;

    /// <summary>The landing page.</summary>
    /// <param name="root">The face's absolute base URL, <c>…/ogc/tiles/v1</c>.</param>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] Landing(string root) => Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("title", "Graticula — OGC API Tiles");
        json.WriteString(
            "description",
            "The vector tiles of this server's VectorTileServer services, addressed by the OGC API Tiles "
            + "tile matrix sets. The tiles are the same bytes the ArcGIS face serves.");
        WriteLinks(json,
        [
            ("self", TileNames.Json, root, "This document"),
            ("alternate", "text/html", root + "?f=html", "This document as HTML"),
            ("conformance", TileNames.Json, root + "/conformance", "Conformance classes"),
            (TileNames.RelConformance, TileNames.Json, root + "/conformance", "Conformance classes"),
            ("data", TileNames.Json, root + "/collections", "The vector tile services"),
            (TileNames.RelData, TileNames.Json, root + "/collections", "The vector tile services"),
            (TileNames.RelTilingSchemes, TileNames.Json, root + "/tileMatrixSets", "Tile matrix sets"),
        ]);
        json.WriteEndObject();
    });

    /// <summary>The conformance declaration.</summary>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] Conformance() => Write(json =>
    {
        json.WriteStartObject();
        json.WriteStartArray("conformsTo");

        foreach (string uri in TileNames.ConformsTo)
        {
            json.WriteStringValue(uri);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    });

    /// <summary>The collections.</summary>
    /// <param name="root">The face's base URL.</param>
    /// <param name="collections">What the caller may see.</param>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] Collections(string root, IReadOnlyList<TiledService> collections) => Write(json =>
    {
        ArgumentNullException.ThrowIfNull(collections);

        json.WriteStartObject();
        WriteLinks(json, [("self", TileNames.Json, root + "/collections", "The vector tile services")]);
        json.WriteStartArray("collections");

        foreach (TiledService collection in collections)
        {
            WriteCollection(json, root, collection);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    });

    /// <summary>One collection.</summary>
    /// <param name="root">The face's base URL.</param>
    /// <param name="collection">The collection.</param>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] Collection(string root, TiledService collection) =>
        Write(json => WriteCollection(json, root, collection));

    /// <summary>A collection's tilesets — one, on its service's grid.</summary>
    /// <param name="root">The face's base URL.</param>
    /// <param name="collection">The collection.</param>
    /// <returns>UTF-8 JSON.</returns>
    /// <remarks>
    /// <b>One tileset, because a service is cut on one grid</b> (ADR-096). Offering Web Mercator beside a
    /// TUREF grid would be offering tiles this server does not cut; the tileset list says what exists.
    /// </remarks>
    public static byte[] Tilesets(string root, TiledService collection) => Write(json =>
    {
        ArgumentNullException.ThrowIfNull(collection);
        string tiles = TilesUrl(root, collection.Id);

        json.WriteStartObject();
        WriteLinks(json, [("self", TileNames.Json, tiles, "Tilesets of " + collection.Title)]);
        json.WriteStartArray("tilesets");
        json.WriteStartObject();
        json.WriteString("title", collection.Title);
        json.WriteString("dataType", "vector");
        json.WriteString("crs", collection.Set.Crs);

        if (collection.Set.Uri is { } registered)
        {
            json.WriteString("tileMatrixSetURI", registered);
        }

        WriteLinks(json,
        [
            ("self", TileNames.Json, tiles + "/" + Uri.EscapeDataString(collection.Set.Id), "The tileset"),
            (TileNames.RelTilingScheme, TileNames.Json, SetUrl(root, collection.Set), "Its tile matrix set"),
        ]);
        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteEndObject();
    });

    /// <summary>A tileset's metadata — 17-083r4's <c>TileSetMetadata</c>.</summary>
    /// <param name="root">The face's base URL.</param>
    /// <param name="collection">The collection.</param>
    /// <returns>UTF-8 JSON.</returns>
    /// <remarks>
    /// <b>No <c>tileMatrixSetLimits</c>, deliberately</b> — ADR-097 §5.2. 20-057 <c>/req/core/tc-error</c>
    /// makes a tile outside a tileset's limits a 404 or 400, and the ArcGIS face answers the same address
    /// with an empty 204. Publishing limits would make one tile two answers; its extent is in
    /// <c>boundingBox</c> instead, and the ArcGIS face's tile map says which tiles are empty.
    /// </remarks>
    public static byte[] Tileset(string root, TiledService collection) => Write(json =>
    {
        ArgumentNullException.ThrowIfNull(collection);
        TileMatrixSet set = collection.Set;
        string tileset = TilesUrl(root, collection.Id) + "/" + Uri.EscapeDataString(set.Id);

        json.WriteStartObject();
        json.WriteString("title", collection.Title);

        if (collection.Description is { Length: > 0 } description)
        {
            json.WriteString("description", description);
        }

        json.WriteString("dataType", "vector");
        json.WriteString("crs", set.Crs);

        if (set.Uri is { } registered)
        {
            json.WriteString("tileMatrixSetURI", registered);
        }

        List<(string, string, string, string?)> links =
        [
            ("self", TileNames.Json, tileset, "This tileset"),
            (TileNames.RelTilingScheme, TileNames.Json, SetUrl(root, set), "Its tile matrix set"),
            (TileNames.RelGeodata, TileNames.Json, CollectionUrl(root, collection.Id), "The service"),
        ];

        // TileJSON is XYZ Web Mercator and nothing else (TileJson's remarks), so it is offered only there.
        if (set.Scheme.IsWebMercator)
        {
            links.Add(("alternate", TileNames.TileJsonLink, tileset + "?f=" + TileNames.TileJsonFormat, "This tileset as TileJSON"));
        }

        // <b>The tile template is a link like the others, with `templated: true`</b> — /req/tileset/
        // description E and G — and its media type, because the template names one format (F).
        WriteLinks(json, links, (TileTemplate(root, collection), TileNames.Mvt, "Tiles, as Mapbox Vector Tiles"));

        if (collection.InSet is { } box)
        {
            json.WriteStartObject("boundingBox");
            json.WriteString("crs", set.Crs);
            (double lowerFirst, double lowerSecond) = set.NorthFirst ? (box.MinY, box.MinX) : (box.MinX, box.MinY);
            (double upperFirst, double upperSecond) = set.NorthFirst ? (box.MaxY, box.MaxX) : (box.MaxX, box.MaxY);
            json.WriteStartArray("lowerLeft");
            json.WriteNumberValue(lowerFirst);
            json.WriteNumberValue(lowerSecond);
            json.WriteEndArray();
            json.WriteStartArray("upperRight");
            json.WriteNumberValue(upperFirst);
            json.WriteNumberValue(upperSecond);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteStartArray("layers");

        foreach (TileLayerInfo layer in collection.Layers)
        {
            json.WriteStartObject();
            json.WriteString("id", layer.Id);
            json.WriteString("dataType", "vector");

            if (layer.GeometryDimension is { } dimension)
            {
                json.WriteNumber("geometryDimension", dimension);
            }

            if (layer.MinLevel is { } min && layer.MaxLevel is { } max)
            {
                json.WriteString("minTileMatrix", min.ToString(CultureInfo.InvariantCulture));
                json.WriteString("maxTileMatrix", max.ToString(CultureInfo.InvariantCulture));
            }

            json.WriteStartObject("propertiesSchema");
            json.WriteString("type", "object");
            json.WriteStartObject("properties");

            foreach (FieldDescription field in layer.Fields)
            {
                json.WriteStartObject(field.Name);
                json.WriteString("type", SchemaTypeOf(field.Type));

                if (field.Type == FieldType.Date)
                {
                    json.WriteString("format", "date-time");
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    });

    /// <summary>The tile matrix sets this face offers the caller.</summary>
    /// <param name="root">The face's base URL.</param>
    /// <param name="sets">The sets.</param>
    /// <returns>UTF-8 JSON.</returns>
    /// <remarks>
    /// <b>Each entry is the whole definition</b> — /rec/tileset/tmxslink C asks for <i>an array of
    /// TileMatrixSet objects</i> — with a <c>self</c> link to its own address as well.
    /// </remarks>
    public static byte[] TileMatrixSets(string root, IReadOnlyList<TileMatrixSet> sets) => Write(json =>
    {
        ArgumentNullException.ThrowIfNull(sets);

        json.WriteStartObject();
        WriteLinks(json, [("self", TileNames.Json, root + "/tileMatrixSets", "Tile matrix sets")]);
        json.WriteStartArray("tileMatrixSets");

        foreach (TileMatrixSet set in sets)
        {
            set.WriteJson(json, [("self", TileNames.Json, SetUrl(root, set), set.Title)]);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    });

    /// <summary>One tile matrix set's definition.</summary>
    /// <param name="root">The face's base URL.</param>
    /// <param name="set">The set.</param>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] TileMatrixSetDocument(string root, TileMatrixSet set) => Write(json =>
    {
        ArgumentNullException.ThrowIfNull(set);
        set.WriteJson(json, [("self", TileNames.Json, SetUrl(root, set), set.Title)]);
    });

    /// <summary>The address of a collection.</summary>
    /// <param name="root">The face's base URL.</param>
    /// <param name="id">The collection id.</param>
    /// <returns>The URL.</returns>
    public static string CollectionUrl(string root, string id) => root + "/collections/" + Uri.EscapeDataString(id);

    /// <summary>The address of a collection's tilesets.</summary>
    /// <param name="root">The face's base URL.</param>
    /// <param name="id">The collection id.</param>
    /// <returns>The URL.</returns>
    public static string TilesUrl(string root, string id) => CollectionUrl(root, id) + "/tiles";

    /// <summary>The address of a tile matrix set.</summary>
    /// <param name="root">The face's base URL.</param>
    /// <param name="set">The set.</param>
    /// <returns>The URL.</returns>
    public static string SetUrl(string root, TileMatrixSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return root + "/tileMatrixSets/" + Uri.EscapeDataString(set.Id);
    }

    /// <summary>
    /// The tile template of a collection's tileset, in OGC API Tiles' variables.
    /// </summary>
    /// <param name="root">The face's base URL.</param>
    /// <param name="collection">The collection.</param>
    /// <returns>The template.</returns>
    public static string TileTemplate(string root, TiledService collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return TilesUrl(root, collection.Id) + "/" + Uri.EscapeDataString(collection.Set.Id) + "/{tileMatrix}/{tileRow}/{tileCol}";
    }

    /// <summary>Writes a <c>links</c> array.</summary>
    /// <param name="json">The writer.</param>
    /// <param name="links">The links: relation, media type, address and title.</param>
    public static void WriteLinks(Utf8JsonWriter json, IEnumerable<(string Rel, string Type, string Href, string? Title)> links) =>
        WriteLinks(json, links, item: null);

    /// <summary>Writes a <c>links</c> array ending with a templated <c>item</c> link.</summary>
    /// <param name="json">The writer.</param>
    /// <param name="links">The plain links.</param>
    /// <param name="item">The template, its media type and title, or null for none.</param>
    public static void WriteLinks(
        Utf8JsonWriter json,
        IEnumerable<(string Rel, string Type, string Href, string? Title)> links,
        (string Href, string Type, string Title)? item)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(links);

        json.WriteStartArray("links");

        foreach ((string rel, string type, string href, string? title) in links)
        {
            json.WriteStartObject();
            json.WriteString("rel", rel);
            json.WriteString("type", type);
            json.WriteString("href", href);

            if (title is not null)
            {
                json.WriteString("title", title);
            }

            json.WriteEndObject();
        }

        if (item is { } template)
        {
            json.WriteStartObject();
            json.WriteString("rel", "item");
            json.WriteString("type", template.Type);
            json.WriteString("href", template.Href);
            json.WriteString("title", template.Title);
            json.WriteBoolean("templated", true);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>The JSON Schema type of a field's values in a tile.</summary>
    /// <param name="type">The field's type.</param>
    /// <returns><c>integer</c>, <c>number</c>, <c>boolean</c> or <c>string</c>.</returns>
    public static string SchemaTypeOf(FieldType type) => type switch
    {
        FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger => "integer",
        FieldType.Single or FieldType.Double => "number",
        FieldType.Boolean => "boolean",
        _ => "string",
    };

    private static void WriteCollection(Utf8JsonWriter json, string root, TiledService collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        json.WriteStartObject();
        json.WriteString("id", collection.Id);
        json.WriteString("title", collection.Title);

        if (collection.Description is { Length: > 0 } description)
        {
            json.WriteString("description", description);
        }

        if (collection.Wgs84 is { } box)
        {
            json.WriteStartObject("extent");
            json.WriteStartObject("spatial");
            json.WriteStartArray("bbox");
            json.WriteStartArray();
            json.WriteNumberValue(box.MinX);
            json.WriteNumberValue(box.MinY);
            json.WriteNumberValue(box.MaxX);
            json.WriteNumberValue(box.MaxY);
            json.WriteEndArray();
            json.WriteEndArray();
            json.WriteString("crs", "http://www.opengis.net/def/crs/OGC/1.3/CRS84");
            json.WriteEndObject();
            json.WriteEndObject();
        }

        WriteLinks(json,
        [
            ("self", TileNames.Json, CollectionUrl(root, collection.Id), collection.Title),
            (TileNames.RelTilesetsVector, TileNames.Json, TilesUrl(root, collection.Id), "Vector tilesets"),
        ]);
        json.WriteEndObject();
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using MemoryStream stream = new();

        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            body(json);
        }

        return stream.ToArray();
    }
}
