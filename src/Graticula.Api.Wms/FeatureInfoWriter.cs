using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.Wms;

/// <summary>
/// What is at a pixel, in the three formats a client may ask for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three, because clients differ and none of them is wrong.</b> A browser popup
/// wants HTML, a script wants JSON, and the oldest desktop tools ask for plain text
/// and will accept nothing else. The specification names no required format at all,
/// which is why every server publishes a different set and a client that guesses is
/// refused.
/// </para>
/// <para>
/// <b>The geometry is not returned.</b> <c>GetFeatureInfo</c> answers *what is
/// here*, and a client wanting the shape has WFS or the FeatureServer for it.
/// Returning geometry through a picture protocol makes an unbounded response out of
/// a request whose only bound is <c>FEATURE_COUNT</c>.
/// </para>
/// </remarks>
public static class FeatureInfoWriter
{
    /// <summary>One layer's hits.</summary>
    /// <param name="Layer">The layer name.</param>
    /// <param name="Features">What was found there.</param>
    public sealed record Hits(string Layer, IReadOnlyList<Feature> Features);

    /// <summary>
    /// The format a request asked for, or null when it is not one this writes.
    /// </summary>
    /// <remarks>
    /// <b><c>application/vnd.ogc.gml</c> is deliberately absent.</b> It is what
    /// several clients ask for first, and answering it would mean a GML writer whose
    /// only consumer is this operation while WFS already publishes the same features
    /// properly. A client that asks for it falls back to text/plain, which every one
    /// of them supports.
    /// </remarks>
    /// <param name="infoFormat">The <c>INFO_FORMAT</c> parameter.</param>
    /// <returns>The media type this will write, or null.</returns>
    public static string? Resolve(string? infoFormat)
    {
        if (string.IsNullOrWhiteSpace(infoFormat))
        {
            return "text/plain";
        }

        string value = infoFormat.Trim();

        foreach (string known in CapabilitiesDocument.InfoFormats)
        {
            if (string.Equals(value, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        // text/xml is what several clients send meaning "anything structured".
        return string.Equals(value, "text/xml", StringComparison.OrdinalIgnoreCase)
            ? "application/json"
            : null;
    }

    /// <summary>Writes the answer.</summary>
    /// <param name="mediaType">One of <see cref="CapabilitiesDocument.InfoFormats"/>.</param>
    /// <param name="hits">What was found, by layer.</param>
    /// <param name="at">Where the client asked, in the map's CRS.</param>
    /// <param name="srid">The map's CRS.</param>
    /// <returns>The document.</returns>
    public static string Write(
        string mediaType, IReadOnlyList<Hits> hits, (double X, double Y) at, int srid)
    {
        ArgumentNullException.ThrowIfNull(hits);

        return mediaType switch
        {
            "application/json" => Json(hits, at, srid),
            "text/html" => Html(hits),
            _ => Plain(hits),
        };
    }

    /// <summary>
    /// Plain text, in the shape the oldest clients parse.
    /// </summary>
    /// <remarks>
    /// <b>An empty result is a sentence rather than an empty body.</b> A client
    /// showing a blank popup has told its user nothing; *nothing here* is an answer.
    /// </remarks>
    private static string Plain(IReadOnlyList<Hits> hits)
    {
        StringBuilder text = new();
        int found = 0;

        foreach (Hits layer in hits)
        {
            if (layer.Features.Count == 0)
            {
                continue;
            }

            text.Append(CultureInfo.InvariantCulture, $"Layer: {layer.Layer}\n");

            foreach (Feature feature in layer.Features)
            {
                found++;
                text.Append(CultureInfo.InvariantCulture, $"  Feature: {feature.Id}\n");

                foreach (string name in feature.Schema.Names)
                {
                    text.Append(
                        CultureInfo.InvariantCulture, $"    {name} = {Text(feature[name])}\n");
                }
            }
        }

        return found == 0 ? "Nothing at that point.\n" : text.ToString();
    }

    private static string Json(IReadOnlyList<Hits> hits, (double X, double Y) at, int srid)
    {
        using System.IO.MemoryStream stream = new();

        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            // <b>A GeoJSON FeatureCollection with null geometries.</b> The shape is
            // what every JavaScript client already knows how to read; the null is
            // this operation refusing to return geometry, said in the document's own
            // vocabulary rather than by omitting a required member.
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");

            writer.WriteStartArray("features");

            foreach (Hits layer in hits)
            {
                foreach (Feature feature in layer.Features)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "Feature");
                    writer.WriteString("id", feature.Id);
                    writer.WriteNull("geometry");

                    writer.WriteStartObject("properties");
                    writer.WriteString("__layer", layer.Layer);

                    foreach (string name in feature.Schema.Names)
                    {
                        Value(writer, name, feature[name]);
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();

            writer.WriteStartObject("queriedAt");
            writer.WriteNumber("x", at.X);
            writer.WriteNumber("y", at.Y);
            writer.WriteString("crs", $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}");
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Value(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(name);
                break;

            case bool flag:
                writer.WriteBoolean(name, flag);
                break;

            case double or float or decimal or int or long or short or byte:
                writer.WriteNumber(
                    name, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;

            case DateTimeOffset moment:
                writer.WriteString(name, moment.ToString("O", CultureInfo.InvariantCulture));
                break;

            case DateTime moment:
                writer.WriteString(name, moment.ToString("O", CultureInfo.InvariantCulture));
                break;

            default:
                writer.WriteString(name, Text(value));
                break;
        }
    }

    /// <summary>
    /// HTML, for the popup a browser client puts it in.
    /// </summary>
    /// <remarks>
    /// <b>A fragment, not a page, and every value is escaped.</b> This text is
    /// inserted into somebody else's document by a client that trusts us, and the
    /// values are attribute data a user uploaded. Escaping is the whole of the
    /// safety here.
    /// </remarks>
    private static string Html(IReadOnlyList<Hits> hits)
    {
        StringBuilder text = new();
        int found = 0;

        foreach (Hits layer in hits)
        {
            if (layer.Features.Count == 0)
            {
                continue;
            }

            text.Append(CultureInfo.InvariantCulture, $"<h4>{Escape(layer.Layer)}</h4>");

            foreach (Feature feature in layer.Features)
            {
                found++;
                text.Append("<table class=\"featureInfo\">");

                foreach (string name in feature.Schema.Names)
                {
                    text.Append(
                        CultureInfo.InvariantCulture,
                        $"<tr><th>{Escape(name)}</th><td>{Escape(Text(feature[name]))}</td></tr>");
                }

                text.Append("</table>");
            }
        }

        return found == 0
            ? "<p>Nothing at that point.</p>"
            : text.ToString();
    }

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// The map extent to query for a pixel, as a tiny box around it.
    /// </summary>
    /// <remarks>
    /// <b>A box rather than a point, because a click never lands on a line.</b> A
    /// point-in-geometry test against a one-pixel-wide road returns nothing for every
    /// click a person can make. Five pixels is the tolerance most WMS
    /// implementations use and is about the accuracy of a mouse.
    /// </remarks>
    /// <param name="transform">The map's transform.</param>
    /// <param name="x">Pixel column.</param>
    /// <param name="y">Pixel row.</param>
    /// <param name="tolerance">How many pixels either side.</param>
    /// <returns>The extent to query.</returns>
    public static Envelope Around(
        Graticula.Cartography.PixelTransform transform, int x, int y, double tolerance = 5)
    {
        double minX = transform.MapX(x - tolerance);
        double maxX = transform.MapX(x + tolerance);

        // Pixel rows grow downward and map y grows upward, so the top pixel is the
        // larger coordinate. Written the other way round the envelope is inverted
        // and matches nothing, which looks like a layer with no data.
        double maxY = transform.MapY(y - tolerance);
        double minY = transform.MapY(y + tolerance);

        return new Envelope(minX, minY, maxX, maxY);
    }
}
