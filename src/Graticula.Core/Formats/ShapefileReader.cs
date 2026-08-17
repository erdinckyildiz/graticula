using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Formats;

/// <summary>
/// Reads a shapefile — the <c>.shp</c> geometry and the <c>.dbf</c> attributes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written rather than adopted, for the reason
/// <see cref="GeoJsonFeatures"/> gives.</b> A shapefile library brings its own
/// geometry types, which <see href="../../../docs/build-vs-adopt-policy.md">the
/// build-vs-adopt policy</see> forbids on a Tier 1 path, and its own opinions
/// about the cases below that this refuses. The format is thirty years old,
/// fully published by Esri, and small: a header, a record loop, and a DBF table
/// that predates it.
/// </para>
/// <para>
/// <b>The endianness is mixed and that is not a mistake in this reader.</b> The
/// <c>.shp</c> header's file code and lengths are <em>big</em>-endian while its
/// version, shape type and every coordinate are little-endian, and each record
/// header is big-endian in front of a little-endian body. The specification
/// says so; reading it all one way produces plausible nonsense.
/// </para>
/// <para>
/// <b>Ring winding is the opposite of OGC's and carries the structure.</b> A
/// shapefile polygon record is a flat list of rings with no nesting: a
/// clockwise ring starts a new polygon and a counter-clockwise ring is a hole in
/// the one before it. Ignoring that turns a polygon with a lake into two
/// overlapping polygons, which renders convincingly and is wrong.
/// </para>
/// <para>
/// <b>Encoding is refused rather than guessed.</b> A DBF has no reliable
/// declaration — a <c>.cpg</c> sidecar sometimes, a language-driver byte often
/// wrong. Guessing between UTF-8 and Windows-1254 mangles Turkish names in a way
/// that surfaces months later in somebody's map labels, so the caller states it
/// when the file does not (owner decision, Q-98).
/// </para>
/// </remarks>
public static class ShapefileReader
{
    /// <summary>The file code every shapefile begins with, big-endian.</summary>
    private const int FileCode = 9994;

    /// <summary>The only version the format has ever had.</summary>
    private const int Version = 1000;

    /// <summary>The header both the .shp and .shx carry.</summary>
    private const int HeaderBytes = 100;

    /// <summary>Where a DBF field descriptor array ends.</summary>
    private const byte FieldTerminator = 0x0D;

    /// <summary>A DBF record marked deleted.</summary>
    private const byte Deleted = (byte)'*';

    /// <summary>
    /// Whether the file declares z or m values, which this reader drops.
    /// </summary>
    /// <param name="shp">The geometry file.</param>
    /// <returns>Whether anything is being lost.</returns>
    /// <remarks>
    /// <b>Exposed so the import can say so at the time.</b> The geometry model
    /// is two-dimensional and the layer document reports <c>hasZ: false</c>, so
    /// carrying z would store something no surface can serve — but a loss the
    /// caller is not told about is a loss they find months later, in a file they
    /// no longer have.
    /// </remarks>
    public static bool DropsZOrM(ReadOnlySpan<byte> shp)
    {
        if (shp.Length < HeaderBytes)
        {
            return false;
        }

        // 11/13/15/18 carry z and m; 21/23/25/28 carry m alone.
        return BinaryPrimitives.ReadInt32LittleEndian(shp[32..]) > 10;
    }

    /// <summary>
    /// Reads a shapefile into something publishable.
    /// </summary>
    /// <param name="shp">The geometry file.</param>
    /// <param name="dbf">The attribute table, or null when there is none.</param>
    /// <param name="srid">The spatial reference the caller resolved.</param>
    /// <param name="encoding">How to read DBF text.</param>
    /// <param name="limits">Caps on what one file may contain.</param>
    /// <param name="dataset">What the file turned out to hold.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it was read.</returns>
    public static bool TryRead(
        ReadOnlySpan<byte> shp,
        ReadOnlySpan<byte> dbf,
        int srid,
        Encoding encoding,
        ImportLimits limits,
        out ImportedDataset? dataset,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        dataset = null;
        error = null;

        if (!TryHeader(shp, out GeometryKind declared, out error))
        {
            return false;
        }

        List<InferredColumn> columns = [];
        List<Dictionary<string, JsonElement>> attributes = [];

        if (dbf.Length > 0
            && !TryDbf(dbf, encoding, limits, out columns, out attributes, out error))
        {
            return false;
        }

        List<ImportedFeature> features = [];
        long vertices = 0;
        int at = HeaderBytes;

        while (at + 8 <= shp.Length)
        {
            // Record header: number then content length, both big-endian, the
            // length in 16-bit words because the format predates byte counts
            // being obvious.
            int words = BinaryPrimitives.ReadInt32BigEndian(shp[(at + 4)..]);

            if (words < 0 || at + 8 + (words * 2) > shp.Length)
            {
                error =
                    $"A record at byte {at} claims {words * 2} bytes of content and the file has "
                    + $"{shp.Length - at - 8} left. The .shp is truncated or not a shapefile.";
                return false;
            }

            ReadOnlySpan<byte> body = shp.Slice(at + 8, words * 2);
            at += 8 + (words * 2);

            if (features.Count >= limits.Features)
            {
                error =
                    $"The file holds more than {limits.Features:N0} features, which is the import "
                    + "limit.";
                return false;
            }

            if (!TryShape(body, out Geometry? geometry, out error))
            {
                return false;
            }

            if (geometry is not null)
            {
                vertices += geometry.CoordinateCount;

                if (vertices > limits.Vertices)
                {
                    error =
                        $"The file holds more than {limits.Vertices:N0} vertices, which is the "
                        + "import limit.";
                    return false;
                }
            }

            IReadOnlyDictionary<string, JsonElement> values =
                features.Count < attributes.Count
                    ? attributes[features.Count]
                    : new Dictionary<string, JsonElement>();

            features.Add(new ImportedFeature(geometry, values));
        }

        // <b>Counted rather than assumed equal.</b> The .shp and .dbf are two
        // files that a user may have zipped from different exports, and a
        // mismatch means every attribute after the first missing record belongs
        // to the wrong shape — which is invisible in a map and obvious in a
        // table nobody looks at.
        if (attributes.Count > 0 && attributes.Count != features.Count)
        {
            error =
                $"The .shp holds {features.Count:N0} shapes and the .dbf holds "
                + $"{attributes.Count:N0} records. They must match: attributes are matched to "
                + "shapes by position, so a mismatch silently attaches the wrong values.";
            return false;
        }

        if (features.Count == 0)
        {
            error = "The shapefile has no features in it.";
            return false;
        }

        dataset = new ImportedDataset(features, columns, declared, srid);
        return true;
    }

    /// <summary>
    /// The geometry kind a <c>.prj</c>-less header declares.
    /// </summary>
    /// <remarks>
    /// <b>Declared once for the whole file, which is the format's own rule.</b>
    /// Every record must be that type or Null, so a layer's type is known before
    /// a shape is read — the same property the ArcGIS layer document needs and
    /// GeoJSON does not give.
    /// </remarks>
    private static bool TryHeader(
        ReadOnlySpan<byte> shp, out GeometryKind kind, out string? error)
    {
        kind = GeometryKind.Point;
        error = null;

        if (shp.Length < HeaderBytes)
        {
            error =
                $"The .shp is {shp.Length} bytes and a shapefile header is {HeaderBytes}. This is "
                + "not a shapefile.";
            return false;
        }

        if (BinaryPrimitives.ReadInt32BigEndian(shp) != FileCode)
        {
            error =
                "The .shp does not begin with the shapefile file code (9994). This is not a "
                + "shapefile, whatever it is named.";
            return false;
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(shp[28..]) != Version)
        {
            error = "The .shp declares a version this reader does not know.";
            return false;
        }

        int type = BinaryPrimitives.ReadInt32LittleEndian(shp[32..]);

        kind = type switch
        {
            1 or 11 or 21 => GeometryKind.Point,
            3 or 13 or 23 => GeometryKind.MultiLineString,
            5 or 15 or 25 => GeometryKind.MultiPolygon,
            8 or 18 or 28 => GeometryKind.MultiPoint,
            _ => (GeometryKind)(-1),
        };

        if ((int)kind < 0)
        {
            error =
                $"The .shp declares shape type {type}, which this reader does not handle. Point, "
                + "PolyLine, Polygon and MultiPoint are read, including their Z and M variants — "
                + "MultiPatch is not.";
            return false;
        }

        return true;
    }

    /// <summary>One record's shape.</summary>
    /// <remarks>
    /// <b>Z and M values are read past, not returned.</b> The geometry model is
    /// two-dimensional and the layer document says <c>hasZ: false</c>, so
    /// carrying them would be storing something no surface can serve. Dropping
    /// them is a loss and is stated at import.
    /// </remarks>
    private static bool TryShape(
        ReadOnlySpan<byte> body, out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        if (body.Length < 4)
        {
            error = "A record is too short to carry a shape type.";
            return false;
        }

        int type = BinaryPrimitives.ReadInt32LittleEndian(body);

        // 0 is the Null shape, and it is legal: a feature with attributes and no
        // location. Dropping the row would change the answer to a count.
        if (type == 0)
        {
            return true;
        }

        return type switch
        {
            1 or 11 or 21 => TryPoint(body, out geometry, out error),
            8 or 18 or 28 => TryMultiPoint(body, out geometry, out error),
            3 or 13 or 23 => TryPolyLine(body, out geometry, out error),
            5 or 15 or 25 => TryPolygon(body, out geometry, out error),
            _ => Fail($"A record declares shape type {type}, which this reader does not handle.",
                out error),
        };
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static bool TryPoint(ReadOnlySpan<byte> body, out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        if (body.Length < 20)
        {
            error = "A point record is too short.";
            return false;
        }

        geometry = new Point(
            BinaryPrimitives.ReadDoubleLittleEndian(body[4..]),
            BinaryPrimitives.ReadDoubleLittleEndian(body[12..]));

        return true;
    }

    private static bool TryMultiPoint(
        ReadOnlySpan<byte> body, out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        // 4 type + 32 box + 4 count
        if (body.Length < 40)
        {
            error = "A multipoint record is too short.";
            return false;
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(body[36..]);

        if (count < 0 || 40 + (count * 16) > body.Length)
        {
            error = $"A multipoint record claims {count} points and does not carry them.";
            return false;
        }

        List<Point> points = [];

        for (int i = 0; i < count; i++)
        {
            int at = 40 + (i * 16);

            points.Add(new Point(
                BinaryPrimitives.ReadDoubleLittleEndian(body[at..]),
                BinaryPrimitives.ReadDoubleLittleEndian(body[(at + 8)..])));
        }

        geometry = new MultiPoint(points);
        return true;
    }

    /// <summary>The parts and points every polyline and polygon record carries.</summary>
    private static bool TryParts(
        ReadOnlySpan<byte> body,
        out List<XySequence> parts,
        out string? error)
    {
        parts = [];
        error = null;

        // 4 type + 32 box + 4 numParts + 4 numPoints
        if (body.Length < 44)
        {
            error = "A record with parts is too short to carry its counts.";
            return false;
        }

        int partCount = BinaryPrimitives.ReadInt32LittleEndian(body[36..]);
        int pointCount = BinaryPrimitives.ReadInt32LittleEndian(body[40..]);

        if (partCount < 0 || pointCount < 0)
        {
            error = "A record declares a negative part or point count.";
            return false;
        }

        int indexAt = 44;
        int pointsAt = indexAt + (partCount * 4);

        if (pointsAt + (pointCount * 16) > body.Length)
        {
            error =
                $"A record claims {partCount} parts and {pointCount} points, which is more than it "
                + "carries. The file is truncated.";
            return false;
        }

        for (int part = 0; part < partCount; part++)
        {
            int from = BinaryPrimitives.ReadInt32LittleEndian(body[(indexAt + (part * 4))..]);

            int to = part + 1 < partCount
                ? BinaryPrimitives.ReadInt32LittleEndian(body[(indexAt + ((part + 1) * 4))..])
                : pointCount;

            if (from < 0 || to > pointCount || to < from)
            {
                error = $"Part {part} spans points {from}..{to}, which is not inside the record.";
                return false;
            }

            double[] coordinates = new double[(to - from) * 2];

            for (int i = from; i < to; i++)
            {
                int at = pointsAt + (i * 16);

                coordinates[(i - from) * 2] = BinaryPrimitives.ReadDoubleLittleEndian(body[at..]);
                coordinates[((i - from) * 2) + 1] =
                    BinaryPrimitives.ReadDoubleLittleEndian(body[(at + 8)..]);
            }

            parts.Add(XySequence.Wrap(coordinates));
        }

        return true;
    }

    private static bool TryPolyLine(
        ReadOnlySpan<byte> body, out Geometry? geometry, out string? error)
    {
        geometry = null;

        if (!TryParts(body, out List<XySequence> parts, out error))
        {
            return false;
        }

        List<LineString> lines = [];

        foreach (XySequence part in parts)
        {
            if (part.Count >= 2)
            {
                lines.Add(new LineString(part));
            }
        }

        if (lines.Count == 0)
        {
            error = "A polyline record has no part with two or more points in it.";
            return false;
        }

        geometry = new MultiLineString(lines);
        return true;
    }

    /// <summary>
    /// A polygon record, whose rings are grouped by containment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The specification says winding decides, and real files disagree with
    /// the specification often enough that trusting it produces invalid
    /// geometry.</b> A shapefile polygon record is a flat list of rings; the
    /// spec says an outer ring is clockwise and a hole counter-clockwise. Two of
    /// the fifty real polygons in the test corpus — exported through an ordinary
    /// tool chain — carry a counter-clockwise outer ring and a clockwise hole,
    /// which is the OGC convention and the exact opposite. Read by winding, they
    /// became two overlapping shells and PostGIS reported <em>nested shells</em>
    /// on import.
    /// </para>
    /// <para>
    /// <b>So containment decides and winding is only the tie-breaker.</b> A ring
    /// inside another ring is a hole in it, whichever way round it was written;
    /// two rings side by side are two polygons. That is what the topology
    /// actually is, and it is the same answer as the spec's for a file that
    /// follows the spec.
    /// </para>
    /// <para>
    /// <b>Bounding boxes first.</b> The containment test is quadratic in the
    /// ring count, which matters for a coastline with a thousand islands — but a
    /// box comparison rejects almost every pair, so the point-in-ring test runs
    /// only for pairs that genuinely overlap.
    /// </para>
    /// </remarks>
    private static bool TryPolygon(
        ReadOnlySpan<byte> body, out Geometry? geometry, out string? error)
    {
        geometry = null;

        if (!TryParts(body, out List<XySequence> parts, out error))
        {
            return false;
        }

        List<XySequence> rings = [];

        foreach (XySequence part in parts)
        {
            // A ring needs four points to close. Fewer is a degenerate part some
            // exporters emit; skipping it loses nothing a renderer would draw.
            if (part.Count >= LinearRing.MinimumCoordinates)
            {
                rings.Add(part);
            }
        }

        if (rings.Count == 0)
        {
            error = "A polygon record has no ring with four or more points in it.";
            return false;
        }

        if (rings.Count == 1)
        {
            geometry = new MultiPolygon([new Polygon(new LinearRing(rings[0]))]);
            return true;
        }

        // Which ring, if any, each ring sits inside. The innermost container
        // wins, so a hole inside an island inside a lake lands on the island.
        int[] parent = new int[rings.Count];
        double[] area = new double[rings.Count];

        for (int i = 0; i < rings.Count; i++)
        {
            parent[i] = -1;
            area[i] = Math.Abs(SignedArea(rings[i]));
        }

        for (int i = 0; i < rings.Count; i++)
        {
            for (int j = 0; j < rings.Count; j++)
            {
                if (i == j || !Contains(rings[j], rings[i]))
                {
                    continue;
                }

                // The smallest container is the real parent.
                if (parent[i] < 0 || area[j] < area[parent[i]])
                {
                    parent[i] = j;
                }
            }
        }

        // Depth decides: a ring inside an odd number of rings is a hole, and one
        // inside an even number (including none) is a shell. That is the same
        // rule a renderer applies, and it handles an island in a lake.
        List<Polygon> polygons = [];
        Dictionary<int, List<LinearRing>> holes = [];

        for (int i = 0; i < rings.Count; i++)
        {
            if (Depth(parent, i) % 2 != 0)
            {
                int shell = parent[i];

                if (!holes.TryGetValue(shell, out List<LinearRing>? list))
                {
                    list = [];
                    holes[shell] = list;
                }

                list.Add(new LinearRing(rings[i]));
            }
        }

        for (int i = 0; i < rings.Count; i++)
        {
            if (Depth(parent, i) % 2 == 0)
            {
                polygons.Add(new Polygon(
                    new LinearRing(rings[i]),
                    holes.TryGetValue(i, out List<LinearRing>? mine) ? mine : []));
            }
        }

        geometry = new MultiPolygon(polygons);
        return true;
    }

    /// <summary>How many rings this one sits inside.</summary>
    private static int Depth(int[] parent, int ring)
    {
        int depth = 0;
        int at = parent[ring];

        // Bounded by the ring count: a cycle would be a containment relation
        // that is not a partial order, which cannot happen for real geometry but
        // would hang here if it did.
        while (at >= 0 && depth <= parent.Length)
        {
            depth++;
            at = parent[at];
        }

        return depth;
    }

    /// <summary>Whether the outer ring contains the inner one.</summary>
    /// <remarks>
    /// <b>One point is enough, and it has to be a point on the inner ring.</b>
    /// Shapefile rings from the same record never cross — the format forbids it
    /// — so if any vertex of the inner ring is inside the outer, all of it is.
    /// Testing a centroid instead would be wrong for a crescent, whose centroid
    /// lies outside itself.
    /// </remarks>
    private static bool Contains(XySequence outer, XySequence inner)
    {
        (double minX, double minY, double maxX, double maxY) = Box(outer);
        (double innerMinX, double innerMinY, double innerMaxX, double innerMaxY) = Box(inner);

        // The cheap rejection, which is almost every pair.
        if (innerMinX < minX || innerMaxX > maxX || innerMinY < minY || innerMaxY > maxY)
        {
            return false;
        }

        return InRing(outer, inner.X(0), inner.Y(0));
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Box(XySequence ring)
    {
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        for (int i = 0; i < ring.Count; i++)
        {
            minX = Math.Min(minX, ring.X(i));
            minY = Math.Min(minY, ring.Y(i));
            maxX = Math.Max(maxX, ring.X(i));
            maxY = Math.Max(maxY, ring.Y(i));
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>Crossing number, which does not care which way the ring runs.</summary>
    private static bool InRing(XySequence ring, double x, double y)
    {
        bool inside = false;

        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double xi = ring.X(i);
            double yi = ring.Y(i);
            double xj = ring.X(j);
            double yj = ring.Y(j);

            if (yi > y != yj > y
                && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Twice the signed area, about the ring's first vertex.</summary>
    /// <remarks>
    /// <b>The subtraction is not decoration.</b> The plain shoelace multiplies
    /// coordinates together, and a shapefile in a state-plane or Web Mercator
    /// system has coordinates in the millions: each product lands near 10¹³,
    /// where a double's rounding error is around 0.003, while the answer for a
    /// parcel is a few hundred. <c>GeometryMeasures.SignedArea</c> had the same
    /// defect and it was measured at 1.6 × 10⁻⁵ relative on real polygons
    /// (D-35). Translating a ring cannot change its area, so subtracting the
    /// first vertex costs nothing and removes the cancellation.
    /// </remarks>
    /// <remarks>
    /// <b>Fixed here on the same day and for the same reason, but the
    /// consequence was smaller.</b> This value only ranks rings by size to
    /// decide which contains which, and an error of one part in a hundred
    /// thousand does not reorder rings that differ by orders of magnitude. It
    /// is corrected because the correction is free and because leaving one
    /// known-wrong shoelace in the tree invites the next one.
    /// </remarks>
    private static double SignedArea(XySequence ring)
    {
        int n = ring.Count - 1;

        if (n < 3)
        {
            return 0;
        }

        double originX = ring.X(0);
        double originY = ring.Y(0);

        double sum = 0;

        // From 1: with the origin at vertex zero the first and last terms are
        // identically zero.
        for (int i = 1; i < n; i++)
        {
            sum += ((ring.X(i) - originX) * (ring.Y(i + 1) - originY))
                 - ((ring.X(i + 1) - originX) * (ring.Y(i) - originY));
        }

        return sum;
    }

    /// <summary>The dBASE III table beside the shapes.</summary>
    /// <remarks>
    /// <b>Read in full rather than streamed, because it is bounded first.</b>
    /// The caller has already capped the uncompressed size of every member, so
    /// the table cannot be larger than that cap — and matching attributes to
    /// shapes by position needs both in hand anyway.
    /// </remarks>
    private static bool TryDbf(
        ReadOnlySpan<byte> dbf,
        Encoding encoding,
        ImportLimits limits,
        out List<InferredColumn> columns,
        out List<Dictionary<string, JsonElement>> rows,
        out string? error)
    {
        columns = [];
        rows = [];
        error = null;

        if (dbf.Length < 32)
        {
            error = "The .dbf is too short to carry a header.";
            return false;
        }

        int records = BinaryPrimitives.ReadInt32LittleEndian(dbf[4..]);
        int headerBytes = BinaryPrimitives.ReadInt16LittleEndian(dbf[8..]);
        int recordBytes = BinaryPrimitives.ReadInt16LittleEndian(dbf[10..]);

        if (records < 0 || headerBytes < 33 || recordBytes < 1 || headerBytes > dbf.Length)
        {
            error = "The .dbf header is not self-consistent.";
            return false;
        }

        List<(string Name, char Type, int Length)> fields = [];

        for (int at = 32; at + 32 <= headerBytes; at += 32)
        {
            if (dbf[at] == FieldTerminator)
            {
                break;
            }

            string name = encoding.GetString(dbf.Slice(at, 11)).TrimEnd('\0', ' ');

            if (name.Length == 0)
            {
                break;
            }

            fields.Add((name, (char)dbf[at + 11], dbf[at + 16]));
        }

        if (fields.Count > limits.Attributes)
        {
            error =
                $"The .dbf has {fields.Count} fields and the import limit is {limits.Attributes}.";
            return false;
        }

        // <b>Observed rather than mapped from the DBF type.</b> Both import
        // paths then produce columns the same way, and the observation is
        // strictly better information: a DBF numeric column is one type for
        // integers and decimals alike, and watching the values tells us which
        // this file actually holds.
        foreach ((string name, _, _) in fields)
        {
            columns.Add(new InferredColumn { Name = name });
        }

        for (int record = 0; record < records; record++)
        {
            int at = headerBytes + (record * recordBytes);

            if (at + recordBytes > dbf.Length)
            {
                // Truncated tail. Said rather than silently returning the rows
                // that did arrive, because the count is what pairs them with
                // shapes.
                error =
                    $"The .dbf declares {records:N0} records and holds {record:N0}. It is "
                    + "truncated.";
                return false;
            }

            if (dbf[at] == Deleted)
            {
                continue;
            }

            Dictionary<string, JsonElement> values = [];
            int field = at + 1;

            foreach ((string name, char type, int length) in fields)
            {
                if (field + length > dbf.Length)
                {
                    error = $"Record {record} is shorter than its field descriptors promise.";
                    return false;
                }

                string raw = encoding.GetString(dbf.Slice(field, length)).Trim();
                field += length;

                JsonElement value = Value(raw, type);

                values[name] = value;
                columns[values.Count - 1].Observe(value);
            }

            rows.Add(values);
        }

        return true;
    }

    private static JsonElement Value(string raw, char type)
    {
        if (raw.Length == 0)
        {
            return Json("null");
        }

        switch (type)
        {
            case 'L':
                return Json(raw is "Y" or "y" or "T" or "t" ? "true" : "false");

            case 'N' or 'F' or 'I' or 'O' or 'B' or '+':
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    ? Json(raw)
                    : Json(JsonSerializer.Serialize(raw));

            case 'D':
                // yyyyMMdd, which is the one date shape DBF actually uses.
                return raw.Length == 8
                    ? Json(JsonSerializer.Serialize($"{raw[..4]}-{raw[4..6]}-{raw[6..]}"))
                    : Json(JsonSerializer.Serialize(raw));

            default:
                return Json(JsonSerializer.Serialize(raw));
        }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
