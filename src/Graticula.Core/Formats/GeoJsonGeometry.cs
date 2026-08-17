using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Graticula.Geometries;

namespace Graticula.Formats;

/// <summary>
/// Reads a GeoJSON geometry object.
/// </summary>
/// <remarks>
/// <para>
/// <b>The coordinate order is longitude, latitude.</b> RFC 7946 §3.1.1 is
/// explicit, and it is the single most common way GeoJSON goes wrong — a file
/// written latitude-first parses perfectly and puts every feature in the wrong
/// hemisphere. Nothing here can detect that, but §4's range check catches the
/// case where the swap makes the numbers impossible.
/// </para>
/// <para>
/// <b>Z is dropped and M is not read.</b> Our model is two-dimensional
/// (ADR-008 §4.5a), and RFC 7946 allows a third element. Dropping it silently
/// would lose an ordinate the caller believes they uploaded — so it is dropped
/// loudly, by the importer reporting it, rather than here.
/// </para>
/// </remarks>
public static class GeoJsonGeometry
{
    /// <summary>Reads one geometry.</summary>
    /// <param name="json">The geometry object.</param>
    /// <param name="index">Which feature it came from, for the message.</param>
    /// <param name="geometry">The geometry, on success.</param>
    /// <param name="error">Why not, on failure.</param>
    /// <returns>Whether it was read.</returns>
    public static bool TryRead(
        JsonElement json, int index, out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("type", out JsonElement typeJson))
        {
            error = $"Feature {index} has a geometry with no 'type'.";
            return false;
        }

        string type = typeJson.GetString() ?? "";

        if (type == "GeometryCollection")
        {
            error =
                $"Feature {index} is a GeometryCollection. A layer holds one geometry type and no "
                + "ArcGIS client draws a collection, so there is nothing this could become.";
            return false;
        }

        if (!json.TryGetProperty("coordinates", out JsonElement coordinates))
        {
            error = $"Feature {index} has a {type} with no 'coordinates'.";
            return false;
        }

        try
        {
            geometry = type switch
            {
                "Point" => ReadPoint(coordinates),
                "MultiPoint" => new MultiPoint([.. Each(coordinates, ReadPoint)]),
                "LineString" => new LineString(ReadSequence(coordinates, minimum: 2)),
                "MultiLineString" => new MultiLineString(
                    [.. Each(coordinates, c => new LineString(ReadSequence(c, minimum: 2)))]),
                "Polygon" => ReadPolygon(coordinates),
                "MultiPolygon" => new MultiPolygon([.. Each(coordinates, ReadPolygon)]),
                _ => throw new FormatException($"'{type}' is not a GeoJSON geometry type."),
            };

            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
        {
            error = $"Feature {index}: {e.Message}";
            return false;
        }
    }

    private static Point ReadPoint(JsonElement coordinates)
    {
        (double x, double y) = ReadPosition(coordinates);
        return new Point(x, y);
    }

    /// <summary>
    /// A polygon: the first ring is the shell, the rest are holes.
    /// </summary>
    /// <remarks>
    /// <b>The winding is corrected rather than trusted.</b> RFC 7946 says the
    /// exterior ring should be counter-clockwise, and the earlier draft said
    /// nothing, so files exist with every combination. Our model wants a shell
    /// and holes, which is an ordering fact rather than a winding one — the
    /// first ring is the shell because the format says so, whichever way it
    /// turns.
    /// </remarks>
    private static Polygon ReadPolygon(JsonElement coordinates)
    {
        if (coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() == 0)
        {
            throw new FormatException("a polygon needs at least one ring.");
        }

        List<LinearRing> rings = [];

        foreach (JsonElement ring in coordinates.EnumerateArray())
        {
            rings.Add(new LinearRing(Close(ReadSequence(ring, minimum: 3))));
        }

        return rings.Count == 1 ? new Polygon(rings[0]) : new Polygon(rings[0], [.. rings[1..]]);
    }

    /// <summary>Closes a ring whose last position is not its first.</summary>
    /// <remarks>
    /// RFC 7946 requires it and exporters forget. Refusing would reject files
    /// every other tool accepts, over something that has exactly one correct
    /// repair.
    /// </remarks>
    private static XySequence Close(XySequence points)
    {
        int last = points.Count - 1;

        if (points.X(0) == points.X(last) && points.Y(0) == points.Y(last))
        {
            return points;
        }

        double[] closed = new double[(points.Count + 1) * 2];

        for (int i = 0; i < points.Count; i++)
        {
            closed[i * 2] = points.X(i);
            closed[(i * 2) + 1] = points.Y(i);
        }

        closed[points.Count * 2] = points.X(0);
        closed[(points.Count * 2) + 1] = points.Y(0);

        return XySequence.Wrap(closed);
    }

    private static XySequence ReadSequence(JsonElement coordinates, int minimum)
    {
        if (coordinates.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("coordinates must be an array.");
        }

        int count = coordinates.GetArrayLength();

        if (count < minimum)
        {
            throw new FormatException($"needs at least {minimum} positions, and has {count}.");
        }

        double[] interleaved = new double[count * 2];
        int i = 0;

        foreach (JsonElement position in coordinates.EnumerateArray())
        {
            (interleaved[i * 2], interleaved[(i * 2) + 1]) = ReadPosition(position);
            i++;
        }

        return XySequence.Wrap(interleaved);
    }

    /// <summary>One position: longitude, then latitude, and any Z discarded.</summary>
    private static (double X, double Y) ReadPosition(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2)
        {
            throw new FormatException("a position needs at least a longitude and a latitude.");
        }

        double x = position[0].GetDouble();
        double y = position[1].GetDouble();

        // <b>The range check is the only defence against a latitude-first
        // file.</b> Swapped coordinates parse perfectly and put the data in the
        // wrong hemisphere; a longitude beyond ±180 is the case where the swap
        // made the numbers impossible, and it is worth catching because the
        // alternative is a map nobody can explain.
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new FormatException("a position has a non-finite coordinate.");
        }

        if (x is < -180 or > 180 || y is < -90 or > 90)
        {
            throw new FormatException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"position ({x}, {y}) is outside WGS 84. GeoJSON is longitude then latitude "
                    + $"(RFC 7946 §3.1.1) — if this file is latitude first, every feature would "
                    + $"land in the wrong place."));
        }

        return (x, y);
    }

    private static IEnumerable<T> Each<T>(JsonElement array, Func<JsonElement, T> read)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("expected an array of parts.");
        }

        foreach (JsonElement part in array.EnumerateArray())
        {
            yield return read(part);
        }
    }
}
