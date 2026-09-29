using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.Tiles;

/// <summary>One source layer of a vector tile, as TileJSON 3.0.0 lists it under <c>vector_layers</c>.</summary>
/// <param name="Id">The layer's name in the tile — the name the tile path encodes it under.</param>
/// <param name="Fields">The attributes the tile carries for it, in order.</param>
/// <param name="MinZoom">The first level it is in the tile at.</param>
/// <param name="MaxZoom">The last level it is in the tile at.</param>
public sealed record TileJsonLayer(string Id, IReadOnlyList<FieldDescription> Fields, int MinZoom, int MaxZoom);

/// <summary>
/// A TileJSON 3.0.0 document for a Web Mercator vector tile service — ADR-097 §5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Web Mercator only, and refused rather than approximated for any other grid.</b> TileJSON has no
/// field for a reference, an origin or a resolution: its <c>tiles</c> template is an XYZ address on the
/// Web Mercator pyramid and nothing else, and OGC API Tiles says as much (<i>use of the TileJSON
/// specification usually implies a WebMercatorQuad TileMatrixSet</i>). A TileJSON for a TUREF service
/// would send a MapLibre client TM30 tiles to draw as Mercator ones — in the wrong place, silently.
/// </para>
/// <para>
/// <b><c>fields</c> is what the tile carries, not what the table has.</b> The tile path keeps at most a
/// dozen attribute columns of a tag-able type (<c>VectorTileEndpoints.AttributesOf</c>); a TileJSON that
/// listed the table would promise a client properties no tile holds. The caller passes that list.
/// </para>
/// </remarks>
public static class TileJson
{
    /// <summary>The version this writes.</summary>
    public const string Version = "3.0.0";

    /// <summary>Writes the document.</summary>
    /// <param name="name">The service's name.</param>
    /// <param name="tiles">The tile URL template, with <c>{z}</c>, <c>{x}</c> and <c>{y}</c>.</param>
    /// <param name="layers">The source layers.</param>
    /// <param name="minZoom">The first level any layer draws at.</param>
    /// <param name="maxZoom">The last level any layer draws at.</param>
    /// <param name="bounds">The data's extent in WGS 84 longitude and latitude, or null for the whole world.</param>
    /// <param name="description">A description, or null.</param>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] Write(
        string name,
        string tiles,
        IReadOnlyList<TileJsonLayer> layers,
        int minZoom,
        int maxZoom,
        Envelope? bounds,
        string? description)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(layers);

        // <b>Clamped to the Mercator latitude band</b>: TileJSON's own default bounds are ±85.0511, and
        // a centre outside it has no tile.
        (double west, double south, double east, double north) = bounds is { IsEmpty: false } b
            ? (Math.Max(b.MinX, -180), Math.Max(b.MinY, -MercatorLatitude), Math.Min(b.MaxX, 180), Math.Min(b.MaxY, MercatorLatitude))
            : (-180, -MercatorLatitude, 180, MercatorLatitude);

        if (west > east || south > north)
        {
            (west, south, east, north) = (-180, -MercatorLatitude, 180, MercatorLatitude);
        }

        using MemoryStream stream = new();

        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("tilejson", Version);
            json.WriteString("name", name);

            if (description is { Length: > 0 })
            {
                json.WriteString("description", description);
            }

            json.WriteString("scheme", "xyz");
            json.WriteStartArray("tiles");
            json.WriteStringValue(tiles);
            json.WriteEndArray();
            json.WriteNumber("minzoom", minZoom);
            json.WriteNumber("maxzoom", maxZoom);
            json.WriteStartArray("bounds");
            json.WriteNumberValue(Math.Round(west, 7));
            json.WriteNumberValue(Math.Round(south, 7));
            json.WriteNumberValue(Math.Round(east, 7));
            json.WriteNumberValue(Math.Round(north, 7));
            json.WriteEndArray();

            // <b>The centre is the middle of the data, at the coarsest level that holds all of it</b> —
            // the level whose one tile is at least as wide as the data, never outside the range the
            // service draws at, so a client that opens at the centre shows something.
            double width = Math.Max(east - west, 1e-9);
            int fits = (int)Math.Floor(Math.Log2(360.0 / width));
            int zoom = Math.Clamp(fits, minZoom, maxZoom);

            json.WriteStartArray("center");
            json.WriteNumberValue(Math.Round((west + east) / 2, 7));
            json.WriteNumberValue(Math.Round((south + north) / 2, 7));
            json.WriteNumberValue(zoom);
            json.WriteEndArray();

            json.WriteStartArray("vector_layers");

            foreach (TileJsonLayer layer in layers)
            {
                json.WriteStartObject();
                json.WriteString("id", layer.Id);
                json.WriteStartObject("fields");

                foreach (FieldDescription field in layer.Fields)
                {
                    json.WriteString(field.Name, KindOf(field.Type));
                }

                json.WriteEndObject();
                json.WriteNumber("minzoom", layer.MinZoom);
                json.WriteNumber("maxzoom", layer.MaxZoom);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>The Web Mercator latitude limit, 85.0511287798°.</summary>
    public const double MercatorLatitude = 85.0511287798066;

    /// <summary>
    /// How a field's values are described in <c>fields</c> — TileJSON 3.0.0 §3.3.2 leaves the text to the
    /// author; these are the words the MVT value union makes true.
    /// </summary>
    /// <param name="type">The column's type.</param>
    /// <returns>A short description.</returns>
    public static string KindOf(FieldType type) => type switch
    {
        FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger
            or FieldType.Single or FieldType.Double => "Number",
        FieldType.Boolean => "Boolean",
        FieldType.Date => "String (date)",
        _ => "String",
    };

    /// <summary>The document as a string, for a test.</summary>
    /// <param name="bytes">What <see cref="Write"/> returned.</param>
    /// <returns>The text.</returns>
    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
