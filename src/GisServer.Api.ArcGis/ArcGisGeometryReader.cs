using System;
using System.Collections.Generic;
using System.Text.Json;
using GisServer.Geometries;

namespace GisServer.Api.ArcGis;

/// <summary>
/// Reads ArcGIS JSON geometry into our model.
/// </summary>
/// <remarks>
/// <para>
/// The inverse of <see cref="ArcGisGeometryWriter"/>, and the harder direction.
/// ADR-005 §3.3c: ArcGIS carries a polygon as a <b>flat bag of rings</b> with the
/// part structure encoded in winding order — clockwise is a shell, counter-clockwise
/// is a hole in the shell that precedes it. Our model has that structure
/// explicitly, so reading means <em>reconstructing</em> it, and reconstructing it
/// wrongly merges two polygons into one or turns a hole into an island.
/// </para>
/// <para>
/// <b>Errors are returned, never thrown.</b> <c>applyEdits</c> reports a result
/// per feature, so one unreadable geometry in a batch of five hundred must
/// produce one failure and four hundred and ninety-nine successes — not an
/// exception that loses the other results.
/// </para>
/// <para>
/// <b>Nothing here is lenient about losing data.</b> Z and M are refused rather
/// than dropped (ADR-008 §4.5a), and a spatial reference that disagrees with the
/// layer is refused rather than assumed. The one leniency is closing an unclosed
/// ring, which adds the vertex the format already implies and loses nothing.
/// </para>
/// </remarks>
public static class ArcGisGeometryReader
{
    /// <summary>Reads a geometry, or explains why it could not be read.</summary>
    /// <param name="json">The <c>geometry</c> member of an ArcGIS feature.</param>
    /// <param name="layerSrid">The layer's SRID, which the geometry must match.</param>
    /// <param name="geometry">The geometry, on success.</param>
    /// <param name="error">Why not, on failure.</param>
    public static bool TryRead(
        JsonElement json, int layerSrid, out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        if (json.ValueKind != JsonValueKind.Object)
        {
            error = "The geometry must be a JSON object.";
            return false;
        }

        if (!SpatialReferenceMatches(json, layerSrid, out error))
        {
            return false;
        }

        // <b>Refused, not flattened.</b> Our model is two-dimensional, so
        // accepting a geometry that declares Z or M would silently discard an
        // ordinate the client believes it stored. ADR-008 §4.5a states the rule
        // for the read direction; this is the same rule on the way in.
        if (Declares(json, "hasZ") || Declares(json, "hasM"))
        {
            error =
                "This geometry declares Z or M ordinates, and this server stores two dimensions. "
                + "Accepting it would silently discard the third, so it is refused instead. "
                + "Send 2D geometry, or use a layer whose provider carries Z.";
            return false;
        }

        if (json.TryGetProperty("rings", out JsonElement rings))
        {
            return TryReadPolygon(rings, out geometry, out error);
        }

        if (json.TryGetProperty("paths", out JsonElement paths))
        {
            return TryReadPaths(paths, out geometry, out error);
        }

        if (json.TryGetProperty("points", out JsonElement points))
        {
            return TryReadMultipoint(points, out geometry, out error);
        }

        if (json.TryGetProperty("x", out JsonElement x))
        {
            return TryReadPoint(x, json, out geometry, out error);
        }

        error =
            "The geometry has none of 'rings', 'paths', 'points' or 'x', so its type cannot be "
            + "determined. Envelopes are accepted as a query filter and are not a feature "
            + "geometry.";
        return false;
    }

    private static bool TryReadPoint(
        JsonElement x, JsonElement json, out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        // ArcGIS spells an empty point as a null x. It is a legitimate value —
        // a feature may exist with attributes and no location.
        if (x.ValueKind == JsonValueKind.Null)
        {
            geometry = Point.Empty;
            return true;
        }

        if (!json.TryGetProperty("y", out JsonElement y))
        {
            error = "A point has 'x' and no 'y'.";
            return false;
        }

        if (!TryNumber(x, out double xv) || !TryNumber(y, out double yv))
        {
            error = "A point's 'x' and 'y' must be numbers.";
            return false;
        }

        geometry = new Point(xv, yv);
        return true;
    }

    private static bool TryReadMultipoint(
        JsonElement points, out Geometry? geometry, out string? error)
    {
        geometry = null;

        if (!TryReadPositions(points, out List<Point>? parts, out error))
        {
            return false;
        }

        geometry = new MultiPoint(parts!);
        return true;
    }

    private static bool TryReadPaths(JsonElement paths, out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() == 0)
        {
            error = "'paths' must be a non-empty array.";
            return false;
        }

        List<LineString> lines = [];

        foreach (JsonElement path in paths.EnumerateArray())
        {
            if (!TryReadSequence(path, minimum: 2, out XySequence coordinates, out error))
            {
                return false;
            }

            lines.Add(new LineString(coordinates));
        }

        // One path is a LineString, several a MultiLineString. ArcGIS calls both
        // a Polyline; the distinction is ours and is what ADR-005 §3.3c warns
        // must not be lost on the way back out.
        geometry = lines.Count == 1 ? lines[0] : new MultiLineString(lines);
        return true;
    }

    /// <summary>
    /// Rebuilds shells and holes from a flat ring list, using winding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule: clockwise starts a new polygon, counter-clockwise is a hole
    /// in the one before it.</b> That is what ArcGIS means, and it is why a
    /// polygon and its reversal are different geometries here rather than the
    /// same one drawn backwards.
    /// </para>
    /// <para>
    /// <b>A hole before any shell is refused</b> rather than promoted to a
    /// shell. It is a genuinely malformed geometry — a hole belongs to
    /// something — and guessing would produce a feature the client did not send.
    /// </para>
    /// </remarks>
    private static bool TryReadPolygon(JsonElement rings, out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        if (rings.ValueKind != JsonValueKind.Array || rings.GetArrayLength() == 0)
        {
            error = "'rings' must be a non-empty array.";
            return false;
        }

        List<(LinearRing Shell, List<LinearRing> Holes)> polygons = [];

        foreach (JsonElement ring in rings.EnumerateArray())
        {
            if (!TryReadSequence(ring, minimum: 4, out XySequence coordinates, out error))
            {
                return false;
            }

            LinearRing linear = new(Close(coordinates));

            if (!linear.IsCounterClockwise)
            {
                polygons.Add((linear, []));
                continue;
            }

            if (polygons.Count == 0)
            {
                error =
                    "The first ring is counter-clockwise, which ArcGIS reads as a hole — and a "
                    + "hole cannot come before the shell it belongs to. Order the rings shell "
                    + "first, clockwise, with its holes after it.";
                return false;
            }

            polygons[^1].Holes.Add(linear);
        }

        geometry = polygons.Count == 1
            ? new Polygon(polygons[0].Shell, polygons[0].Holes)
            : new MultiPolygon([.. polygons.ConvertAll(p => new Polygon(p.Shell, p.Holes))]);

        return true;
    }

    /// <summary>
    /// Closes a ring whose last position is not its first.
    /// </summary>
    /// <remarks>
    /// The one leniency in this reader, and it is safe because it is not a
    /// guess: a ring is closed by definition, so the missing vertex is already
    /// implied by the format. Real clients do send unclosed rings, and refusing
    /// them would fail an edit for a reason the user cannot act on.
    /// </remarks>
    private static XySequence Close(XySequence coordinates)
    {
        ReadOnlySpan<double> xy = coordinates.AsSpan();

        if (xy.Length >= 4 && xy[0] == xy[^2] && xy[1] == xy[^1])
        {
            return coordinates;
        }

        double[] closed = new double[xy.Length + 2];
        xy.CopyTo(closed);
        closed[^2] = xy[0];
        closed[^1] = xy[1];

        return XySequence.Wrap(closed);
    }

    private static bool TryReadPositions(
        JsonElement array, out List<Point>? points, out string? error)
    {
        points = null;
        error = null;

        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            error = "'points' must be a non-empty array.";
            return false;
        }

        List<Point> read = [];

        foreach (JsonElement position in array.EnumerateArray())
        {
            if (!TryReadPosition(position, out double x, out double y, out error))
            {
                return false;
            }

            read.Add(new Point(x, y));
        }

        points = read;
        return true;
    }

    private static bool TryReadSequence(
        JsonElement array, int minimum, out XySequence coordinates, out string? error)
    {
        coordinates = XySequence.Empty;
        error = null;

        if (array.ValueKind != JsonValueKind.Array)
        {
            error = "Each ring or path must be an array of positions.";
            return false;
        }

        int count = array.GetArrayLength();

        if (count < minimum)
        {
            error = $"A ring or path needs at least {minimum} positions; this one has {count}.";
            return false;
        }

        double[] xy = new double[count * 2];
        int at = 0;

        foreach (JsonElement position in array.EnumerateArray())
        {
            if (!TryReadPosition(position, out double x, out double y, out error))
            {
                return false;
            }

            xy[at++] = x;
            xy[at++] = y;
        }

        coordinates = XySequence.Wrap(xy);
        return true;
    }

    private static bool TryReadPosition(
        JsonElement position, out double x, out double y, out string? error)
    {
        x = 0;
        y = 0;
        error = null;

        if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2)
        {
            error = "A position must be an array of at least two numbers.";
            return false;
        }

        // A third element is Z, and reaching here means hasZ was not declared —
        // so the client is sending an ordinate it did not admit to. Refused for
        // the same reason as a declared Z: accepting it drops data silently.
        if (position.GetArrayLength() > 2)
        {
            error =
                "A position carries more than two numbers, so it has a Z or M ordinate that "
                + "'hasZ' and 'hasM' did not declare. This server stores two dimensions and will "
                + "not silently discard the rest.";
            return false;
        }

        if (!TryNumber(position[0], out x) || !TryNumber(position[1], out y))
        {
            error = "A position's values must be numbers.";
            return false;
        }

        return true;
    }

    private static bool SpatialReferenceMatches(JsonElement json, int layerSrid, out string? error)
    {
        error = null;

        if (!json.TryGetProperty("spatialReference", out JsonElement reference)
            || reference.ValueKind != JsonValueKind.Object)
        {
            // Absent means "the layer's", which is what ArcGIS clients assume
            // and what every ArcGIS server does.
            return true;
        }

        int? wkid = reference.TryGetProperty("latestWkid", out JsonElement latest)
                && latest.TryGetInt32(out int latestValue)
            ? latestValue
            : reference.TryGetProperty("wkid", out JsonElement plain) && plain.TryGetInt32(out int value)
                ? value
                : null;

        if (wkid is null || wkid == layerSrid)
        {
            return true;
        }

        // <b>Refused, not reprojected.</b> Reprojecting on write would move
        // somebody's geometry as a side effect of saving it, and the client
        // would have no way to know it happened.
        error =
            $"The geometry declares spatial reference {wkid} and the layer is {layerSrid}. This "
            + "server does not reproject on write, because moving geometry as a side effect of "
            + "saving it is not something a client can detect. Send it in the layer's own "
            + "spatial reference.";
        return false;
    }

    private static bool Declares(JsonElement json, string property) =>
        json.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;

    private static bool TryNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number);
    }
}
