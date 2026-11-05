using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Features;

namespace Graticula.Scenes;

/// <summary>One field a generated scene layer carries, after its object id.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Type">Its type; a type I3S has no buffer for is left out by the writer.</param>
/// <param name="Alias">What it is called on screen.</param>
public sealed record SceneField(string Name, FieldType Type, string? Alias = null);

/// <summary>One building: its footprint in WGS 84, where it stands and how tall it is.</summary>
/// <param name="Id">Its object id.</param>
/// <param name="Polygons">Each part's rings in (longitude, latitude), the outer ring first, then its holes.</param>
/// <param name="Base">The height its floor stands at, in metres.</param>
/// <param name="Height">How tall it is, in metres; zero or less draws the footprint flat.</param>
/// <param name="Values">Its values, in the order of the fields.</param>
public sealed record SceneBuilding(
    long Id, IReadOnlyList<IReadOnlyList<IReadOnlyList<(double Lon, double Lat)>>> Polygons, double Base, double Height,
    IReadOnlyList<object?> Values);

/// <summary>
/// An I3S 3D object scene layer package of extruded footprints — ADR-269 §5.3, written from the public I3S specification.
/// </summary>
/// <remarks>
/// <para>
/// <b>A global layer in WGS 84, untextured, with an uncompressed geometry buffer.</b> The buffer is the 1.7 layout every
/// ArcGIS client reads, the one ArcGIS Pro writes as its first buffer: an 8-byte header (vertex count, feature count),
/// then positions, vertex colours, feature ids and face ranges, with no normals — the client computes them, as it does
/// for Pro's. Positions are offsets from the node's box centre, in degrees for longitude and latitude and metres for
/// height, which is the I3S rule for a global layer.
/// </para>
/// <para>
/// <b>A quadtree whose leaves carry every building at full detail, and whose inner nodes carry a coarser city</b> —
/// D-386, 2026-11-05. The root carries no geometry, as I3S requires of a 3D object layer, and has one child holding
/// every building; a node of more than <see cref="MaximumPerNode"/> is split into four by the centres of its buildings,
/// and keeps for itself the <see cref="MaximumPerNode"/> largest of them by volume, each as the prism of its footprint's
/// convex hull. A client far away draws that node and loads no leaf; closer, the node's projected size passes its
/// <see cref="CoarseThreshold"/> and its children replace it (I3S's mesh pyramid, by <c>maxScreenThresholdSQ</c>). The
/// small buildings come in with the leaves — at the distance a node is drawn, they are a few pixels.
/// </para>
/// <para>
/// <b>Each node's box is aligned to east, north and up at its centre</b>, its half sizes in metres and its rotation the
/// quaternion of the matrix whose columns are those three directions in earth-centred coordinates — the orientation
/// ArcGIS Pro writes for a root node (measured: its quaternion at 29°E 41°N matches this one to four places).
/// </para>
/// </remarks>
public static partial class SceneLayerPackageWriter
{
    /// <summary>The most buildings one node holds before it is split.</summary>
    public const int MaximumPerNode = 512;

    /// <summary>
    /// The projected area, in square pixels, past which an inner node gives way to its children: a box about 300 pixels
    /// across. Leaves never give way, so theirs is far above anything a screen draws.
    /// </summary>
    public const double CoarseThreshold = 90_000.0;

    private const double EarthRadius = 6_378_137.0;
    private const int NodesPerPage = 64;

    /// <summary>Writes the package.</summary>
    /// <param name="name">The layer's name.</param>
    /// <param name="fields">The fields after the object id.</param>
    /// <param name="buildings">The buildings.</param>
    /// <param name="colour">The buildings' colour, [r, g, b].</param>
    /// <param name="updated">When the content was made, for the layer's update stamp.</param>
    /// <returns>The .slpk bytes.</returns>
    public static byte[] Write(
        string name, IReadOnlyList<SceneField> fields, IReadOnlyList<SceneBuilding> buildings, int[] colour, DateTimeOffset updated)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(buildings);

        List<(SceneField Field, int Index)> stored = [.. fields.Select((f, i) => (f, i)).Where(p => Buffered(p.f.Type))];
        List<Mesh> meshes = [.. buildings.Select(Build).Where(m => m.Points.Count > 0)];

        // The tree: the root, its one child holding everything, then each split's four quarters, breadth first, so a node's
        // index is its order here.
        List<Node> nodes = [new Node(meshes, null)];
        for (int i = 0; i < nodes.Count; i++)
        {
            Node node = nodes[i];
            if (node.Meshes.Count > MaximumPerNode || (i == 0 && node.Meshes.Count > 0))
            {
                foreach (List<Mesh> quarter in i == 0 ? [node.Meshes] : Split(node.Meshes))
                {
                    if (quarter.Count == 0)
                    {
                        continue;
                    }

                    node.Children.Add(nodes.Count);
                    nodes.Add(new Node(quarter, i));
                }
            }
        }

        using MemoryStream package = new();
        PackageWriter zip = new(package);
        JsonArray pageNodes = [];
        int leafCount = 0;

        for (int i = 0; i < nodes.Count; i++)
        {
            Node node = nodes[i];
            Box box = BoxOf(node.Meshes);
            JsonObject json = new()
            {
                ["index"] = i,
                ["obb"] = box.Json(),
            };

            if (node.Parent is { } parent)
            {
                json["parentIndex"] = parent;
            }

            if (node.Children.Count > 0)
            {
                json["children"] = new JsonArray([.. node.Children.Select(c => (JsonNode)c)]);
            }

            // A leaf draws its buildings; an inner node below the root draws the coarse city of its largest ones.
            List<Mesh> drawn = i == 0 || node.Meshes.Count == 0 ? []
                : node.Children.Count == 0 ? node.Meshes
                : Coarse(node.Meshes);

            if (drawn.Count > 0)
            {
                (byte[] geometry, int vertices) = Geometry(drawn, box, colour);
                zip.Add($"nodes/{i}/geometries/0.bin.gz", Gzip(geometry));
                AddAttributes(zip, i, [.. drawn.Select(m => m.Building.Id)], [.. drawn.Select(m => m.Building.Values)], stored);

                json["lodThreshold"] = node.Children.Count == 0 ? 20_000_000.0 : CoarseThreshold;
                json["mesh"] = new JsonObject
                {
                    ["material"] = new JsonObject { ["definition"] = 0 },
                    ["geometry"] = new JsonObject
                    {
                        ["definition"] = 0, ["resource"] = i, ["vertexCount"] = vertices, ["featureCount"] = drawn.Count,
                    },
                    ["attribute"] = new JsonObject { ["resource"] = i },
                };
                leafCount += node.Children.Count == 0 ? 1 : 0;
            }

            pageNodes.Add(json);

            if (pageNodes.Count == NodesPerPage || i == nodes.Count - 1)
            {
                zip.Add($"nodepages/{i / NodesPerPage}.json.gz", Gzip(Utf8(new JsonObject { ["nodes"] = pageNodes })));
                pageNodes = [];
            }
        }

        // Statistics for every field after the object id.
        for (int k = 1; k <= stored.Count; k++)
        {
            zip.Add($"statistics/f_{k}/0.json.gz", Gzip(Utf8(Statistics([.. meshes.Select(m => m.Building.Values)], stored[k - 1]))));
        }

        Box all = BoxOf(meshes);
        zip.Add("3dSceneLayer.json.gz", Gzip(Utf8(Layer(name, stored, all, colour, updated))));
        zip.Add("metadata.json", Utf8(new JsonObject { ["I3SVersion"] = "1.7", ["nodeCount"] = nodes.Count }));
        zip.Finish();
        return package.ToArray();
    }

    private static bool Buffered(FieldType type) =>
        type is FieldType.SmallInteger or FieldType.Integer or FieldType.Single or FieldType.Double or FieldType.Text
            or FieldType.Guid or FieldType.Date or FieldType.BigInteger;

    // ------------------------------------------------------------------------------------------------- the meshes

    /// <summary>One building as triangles: positions in degrees and metres, three to a triangle.</summary>
    private sealed record Mesh(SceneBuilding Building, List<(double Lon, double Lat, double Z)> Points, double CenterLon, double CenterLat);

    private static Mesh Build(SceneBuilding building)
    {
        List<(double Lon, double Lat, double Z)> points = [];
        double sumLon = 0, sumLat = 0;
        int n = 0;

        foreach (IReadOnlyList<IReadOnlyList<(double Lon, double Lat)>> rings in building.Polygons)
        {
            foreach ((double lon, double lat) in rings.SelectMany(r => r))
            {
                sumLon += lon;
                sumLat += lat;
                n++;
            }
        }

        if (n == 0)
        {
            return new Mesh(building, points, 0, 0);
        }

        double lon0 = sumLon / n, lat0 = sumLat / n;
        double kx = Math.PI / 180 * EarthRadius * Math.Cos(lat0 * Math.PI / 180), ky = Math.PI / 180 * EarthRadius;
        double bottom = building.Base, top = building.Base + Math.Max(0, building.Height);
        bool solid = building.Height > 0;

        (double X, double Y) Plane((double Lon, double Lat) p) => ((p.Lon - lon0) * kx, (p.Lat - lat0) * ky);
        (double Lon, double Lat, double Z) Earth((double X, double Y) p, double z) => (lon0 + (p.X / kx), lat0 + (p.Y / ky), z);

        foreach (IReadOnlyList<IReadOnlyList<(double Lon, double Lat)>> rings in building.Polygons)
        {
            if (rings.Count == 0)
            {
                continue;
            }

            (List<(double X, double Y)> vertices, List<int> triangles) = FootprintTriangulator.Triangulate(
                [.. rings[0].Select(Plane)], [.. rings.Skip(1).Select(r => (IReadOnlyList<(double X, double Y)>)[.. r.Select(Plane)])]);

            // The roof, counter-clockwise from above; and the floor, reversed, so a closed prism faces outward.
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                points.Add(Earth(vertices[triangles[t]], top));
                points.Add(Earth(vertices[triangles[t + 1]], top));
                points.Add(Earth(vertices[triangles[t + 2]], top));
            }

            if (!solid)
            {
                continue;
            }

            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                points.Add(Earth(vertices[triangles[t]], bottom));
                points.Add(Earth(vertices[triangles[t + 2]], bottom));
                points.Add(Earth(vertices[triangles[t + 1]], bottom));
            }

            // The walls: each ring edge a quad of two triangles facing out — the outer ring counter-clockwise, a hole
            // clockwise, so the outward side of every edge is its right-hand side.
            for (int r = 0; r < rings.Count; r++)
            {
                List<(double X, double Y)> ring = FootprintTriangulator.Clean([.. rings[r].Select(Plane)]);
                if (ring.Count < 3)
                {
                    continue;
                }

                bool counterClockwise = FootprintTriangulator.SignedArea(ring) > 0;
                if ((r == 0) != counterClockwise)
                {
                    ring.Reverse();
                }

                for (int i = 0; i < ring.Count; i++)
                {
                    (double X, double Y) a = ring[i], b = ring[(i + 1) % ring.Count];
                    points.Add(Earth(a, bottom));
                    points.Add(Earth(b, bottom));
                    points.Add(Earth(b, top));
                    points.Add(Earth(a, bottom));
                    points.Add(Earth(b, top));
                    points.Add(Earth(a, top));
                }
            }
        }

        return new Mesh(building, points, lon0, lat0);
    }

    /// <summary>
    /// The coarse city of an inner node (D-386): its <see cref="MaximumPerNode"/> largest buildings by volume, each as the
    /// prism of its footprint's convex hull — a few walls instead of every corner, the same ids and values, so a feature
    /// picked far away is the feature picked close by.
    /// </summary>
    private static List<Mesh> Coarse(List<Mesh> meshes) =>
        [.. meshes
            .OrderByDescending(m => Volume(m.Building))
            .ThenBy(m => m.Building.Id)
            .Take(MaximumPerNode)
            .Select(m => Build(m.Building with { Polygons = [[Hull(m.Building)]] }))
            .Where(m => m.Points.Count > 0)];

    private static double Volume(SceneBuilding building)
    {
        double area = 0;
        foreach (IReadOnlyList<IReadOnlyList<(double Lon, double Lat)>> rings in building.Polygons)
        {
            if (rings.Count == 0)
            {
                continue;
            }

            IReadOnlyList<(double Lon, double Lat)> outer = rings[0];
            double lat = outer.Count > 0 ? outer[0].Lat : 0;
            double kx = Math.PI / 180 * EarthRadius * Math.Cos(lat * Math.PI / 180), ky = Math.PI / 180 * EarthRadius;
            double twice = 0;
            for (int i = 0; i < outer.Count; i++)
            {
                (double Lon, double Lat) a = outer[i], b = outer[(i + 1) % outer.Count];
                twice += (a.Lon * kx * b.Lat * ky) - (b.Lon * kx * a.Lat * ky);
            }

            area += Math.Abs(twice) / 2;
        }

        return area * Math.Max(1, building.Height);
    }

    /// <summary>The convex hull of a building's outer rings, counter-clockwise, by Andrew's monotone chain.</summary>
    private static IReadOnlyList<(double Lon, double Lat)> Hull(SceneBuilding building)
    {
        (double Lon, double Lat)[] points = [.. building.Polygons
            .Where(rings => rings.Count > 0)
            .SelectMany(rings => rings[0])
            .Distinct()
            .OrderBy(p => p.Lon).ThenBy(p => p.Lat)];

        if (points.Length < 3)
        {
            return points;
        }

        static double Cross((double Lon, double Lat) o, (double Lon, double Lat) a, (double Lon, double Lat) b) =>
            ((a.Lon - o.Lon) * (b.Lat - o.Lat)) - ((a.Lat - o.Lat) * (b.Lon - o.Lon));

        List<(double Lon, double Lat)> hull = [];
        foreach ((double Lon, double Lat) p in points)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        int lower = hull.Count + 1;
        for (int i = points.Length - 2; i >= 0; i--)
        {
            (double Lon, double Lat) p = points[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static List<Mesh>[] Split(List<Mesh> meshes)
    {
        double midLon = Median(meshes.Select(m => m.CenterLon)), midLat = Median(meshes.Select(m => m.CenterLat));
        List<Mesh>[] quarters = [[], [], [], []];

        foreach (Mesh m in meshes)
        {
            quarters[(m.CenterLon < midLon ? 0 : 1) + (m.CenterLat < midLat ? 0 : 2)].Add(m);
        }

        // Many buildings at one centre would split forever; such a node is halved by order instead.
        if (quarters.Count(q => q.Count > 0) == 1)
        {
            int half = meshes.Count / 2;
            return [meshes.GetRange(0, half), meshes.GetRange(half, meshes.Count - half)];
        }

        return quarters;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    // ---------------------------------------------------------------------------------------------- the boxes

    private sealed class Node(List<Mesh> meshes, int? parent)
    {
        public List<Mesh> Meshes { get; } = meshes;
        public int? Parent { get; } = parent;
        public List<int> Children { get; } = [];
    }

    /// <summary>A box aligned to east, north and up at its centre: centre in degrees and metres, half sizes in metres.</summary>
    private sealed record Box(double Lon, double Lat, double Z, double HalfX, double HalfY, double HalfZ, double MinLon, double MinLat, double MaxLon, double MaxLat, double MinZ, double MaxZ)
    {
        public JsonObject Json()
        {
            (double x, double y, double z, double w) = Quaternion(Lon, Lat);
            return new JsonObject
            {
                ["center"] = new JsonArray(Lon, Lat, Z),
                ["halfSize"] = new JsonArray(Math.Max(HalfX, 0.01), Math.Max(HalfY, 0.01), Math.Max(HalfZ, 0.01)),
                ["quaternion"] = new JsonArray(x, y, z, w),
            };
        }
    }

    private static Box BoxOf(IReadOnlyList<Mesh> meshes) => BoxOf(meshes.SelectMany(m => m.Points));

    private static Box BoxOf(IEnumerable<(double Lon, double Lat, double Z)> points)
    {
        double minLon = double.MaxValue, minLat = double.MaxValue, maxLon = double.MinValue, maxLat = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        foreach ((double pointLon, double pointLat, double pointZ) in points)
        {
            minLon = Math.Min(minLon, pointLon); maxLon = Math.Max(maxLon, pointLon);
            minLat = Math.Min(minLat, pointLat); maxLat = Math.Max(maxLat, pointLat);
            minZ = Math.Min(minZ, pointZ); maxZ = Math.Max(maxZ, pointZ);
        }

        if (minLon > maxLon)
        {
            return new Box(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        double lon = (minLon + maxLon) / 2, lat = (minLat + maxLat) / 2, height = (minZ + maxZ) / 2;
        double kx = Math.PI / 180 * EarthRadius * Math.Cos(lat * Math.PI / 180), ky = Math.PI / 180 * EarthRadius;

        // A little room for the earth's curvature across the box, which an east-north-up box does not follow.
        double spanX = (maxLon - minLon) * kx, spanY = (maxLat - minLat) * ky;
        double sag = ((spanX * spanX) + (spanY * spanY)) / (8 * EarthRadius);
        return new Box(lon, lat, height, (spanX / 2) + 0.5, (spanY / 2) + 0.5, ((maxZ - minZ) / 2) + sag + 0.5,
            minLon, minLat, maxLon, maxLat, minZ, maxZ);
    }

    /// <summary>The rotation from east-north-up axes at a place to earth-centred axes, as (x, y, z, w).</summary>
    public static (double X, double Y, double Z, double W) Quaternion(double lonDegrees, double latDegrees)
    {
        double l = lonDegrees * Math.PI / 180, p = latDegrees * Math.PI / 180;
        // Columns: east, north, up.
        double m00 = -Math.Sin(l), m01 = -Math.Sin(p) * Math.Cos(l), m02 = Math.Cos(p) * Math.Cos(l);
        double m10 = Math.Cos(l), m11 = -Math.Sin(p) * Math.Sin(l), m12 = Math.Cos(p) * Math.Sin(l);
        double m20 = 0, m21 = Math.Cos(p), m22 = Math.Sin(p);
        double trace = m00 + m11 + m22;
        double x, y, z, w;

        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1.0) * 2;
            w = 0.25 * s; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            double s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
            w = (m21 - m12) / s; x = 0.25 * s; y = (m01 + m10) / s; z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            double s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
            w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25 * s; z = (m12 + m21) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
            w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25 * s;
        }

        return w < 0 ? (-x, -y, -z, -w) : (x, y, z, w);
    }

    // ------------------------------------------------------------------------------------------ the buffers

    /// <summary>The node's geometry buffer: header, positions, colours, feature ids, face ranges.</summary>
    private static (byte[] Bytes, int Vertices) Geometry(IReadOnlyList<Mesh> meshes, Box box, int[] colour)
    {
        int v = meshes.Sum(m => m.Points.Count), f = meshes.Count;
        byte[] buffer = new byte[8 + (12 * v) + (4 * v) + (8 * f) + (8 * f)];
        Span<byte> span = buffer;
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)v);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)f);

        int position = 8, colours = 8 + (12 * v), ids = colours + (4 * v), ranges = ids + (8 * f), triangle = 0;

        foreach (Mesh m in meshes)
        {
            foreach ((double lon, double lat, double z) in m.Points)
            {
                BinaryPrimitives.WriteSingleLittleEndian(span[position..], (float)(lon - box.Lon));
                BinaryPrimitives.WriteSingleLittleEndian(span[(position + 4)..], (float)(lat - box.Lat));
                BinaryPrimitives.WriteSingleLittleEndian(span[(position + 8)..], (float)(z - box.Z));
                position += 12;
                span[colours] = (byte)colour[0];
                span[colours + 1] = (byte)colour[1];
                span[colours + 2] = (byte)colour[2];
                span[colours + 3] = 255;
                colours += 4;
            }

            int count = m.Points.Count / 3;
            BinaryPrimitives.WriteUInt64LittleEndian(span[ids..], (ulong)m.Building.Id);
            ids += 8;
            BinaryPrimitives.WriteUInt32LittleEndian(span[ranges..], (uint)triangle);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(ranges + 4)..], (uint)(triangle + count - 1));
            ranges += 8;
            triangle += count;
        }

        return (buffer, v);
    }

    /// <summary>A node's attribute buffers: the object ids, then each stored field, in the order of its features.</summary>
    private static void AddAttributes(
        PackageWriter zip, int node, IReadOnlyList<long> ids, IReadOnlyList<IReadOnlyList<object?>> rows, List<(SceneField Field, int Index)> stored)
    {
        for (int k = 0; k <= stored.Count; k++)
        {
            byte[] attribute = k == 0 ? Oids(ids) : Attribute(rows, stored[k - 1].Field, stored[k - 1].Index);
            zip.Add($"nodes/{node}/attributes/f_{k}/0.bin.gz", Gzip(attribute));
        }
    }

    private static byte[] Oids(IReadOnlyList<long> ids)
    {
        byte[] buffer = new byte[4 + (4 * ids.Count)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4 + (4 * i)), (uint)ids[i]);
        }

        return buffer;
    }

    private static byte[] Attribute(IReadOnlyList<IReadOnlyList<object?>> rows, SceneField field, int index)
    {
        object?[] values = [.. rows.Select(r => index < r.Count ? r[index] : null)];
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((uint)values.Length);

        switch (field.Type)
        {
            case FieldType.SmallInteger:
                foreach (object? value in values) writer.Write(value is null ? short.MinValue : Convert.ToInt16(value, CultureInfo.InvariantCulture));
                break;
            case FieldType.Integer:
                foreach (object? value in values) writer.Write(value is null ? int.MinValue : Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case FieldType.Single:
                foreach (object? value in values) writer.Write(value is null ? float.NaN : Convert.ToSingle(value, CultureInfo.InvariantCulture));
                break;
            case FieldType.Double:
            case FieldType.BigInteger:
                writer.Write(0u); // The doubles start eight-byte aligned (measured in Pro's packages).
                foreach (object? value in values) writer.Write(value is null ? double.NaN : Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            default:
                byte[][] strings = [.. values.Select(v => v is null ? Array.Empty<byte>() : (byte[])[.. Encoding.UTF8.GetBytes(Text(v)), (byte)0])];
                writer.Write((uint)strings.Sum(s => s.Length));
                foreach (byte[] s in strings) writer.Write((uint)s.Length);
                foreach (byte[] s in strings) writer.Write(s);
                break;
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static string Text(object value) => value switch
    {
        DateTime d => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    // ----------------------------------------------------------------------------------------- the documents

    private static (string Type, string? Value) I3sType(FieldType type) => type switch
    {
        FieldType.SmallInteger => ("esriFieldTypeSmallInteger", "Int16"),
        FieldType.Integer => ("esriFieldTypeInteger", "Int32"),
        FieldType.Single => ("esriFieldTypeSingle", "Float32"),
        FieldType.Double or FieldType.BigInteger => ("esriFieldTypeDouble", "Float64"),
        FieldType.Date => ("esriFieldTypeDate", "String"),
        FieldType.Guid => ("esriFieldTypeGUID", "String"),
        _ => ("esriFieldTypeString", "String"),
    };

    /// <summary>The layer's fields, how each is stored and where its statistics are — the same for every profile.</summary>
    private static (JsonArray Fields, JsonArray Storage, JsonArray Statistics) Schema(List<(SceneField Field, int Index)> stored)
    {
        JsonArray fields = [new JsonObject { ["name"] = "OBJECTID", ["type"] = "esriFieldTypeOID", ["alias"] = "OBJECTID" }];
        JsonArray storage =
        [
            new JsonObject
            {
                ["key"] = "f_0", ["name"] = "OBJECTID",
                ["header"] = new JsonArray(new JsonObject { ["property"] = "count", ["valueType"] = "UInt32" }),
                ["ordering"] = new JsonArray("attributeValues"),
                ["attributeValues"] = new JsonObject { ["valueType"] = "Oid32", ["valuesPerElement"] = 1 },
            },
        ];
        JsonArray statistics = [];

        for (int k = 1; k <= stored.Count; k++)
        {
            SceneField field = stored[k - 1].Field;
            (string type, string? value) = I3sType(field.Type);
            fields.Add(new JsonObject { ["name"] = field.Name, ["type"] = type, ["alias"] = field.Alias ?? field.Name });
            JsonObject entry = new() { ["key"] = $"f_{k}", ["name"] = field.Name };

            if (value == "String")
            {
                entry["header"] = new JsonArray(
                    new JsonObject { ["property"] = "count", ["valueType"] = "UInt32" },
                    new JsonObject { ["property"] = "attributeValuesByteCount", ["valueType"] = "UInt32" });
                entry["ordering"] = new JsonArray("attributeByteCounts", "attributeValues");
                entry["attributeByteCounts"] = new JsonObject { ["valueType"] = "UInt32", ["valuesPerElement"] = 1 };
                JsonObject values = new() { ["valueType"] = "String", ["valuesPerElement"] = 1, ["encoding"] = "UTF-8" };
                if (field.Type == FieldType.Date)
                {
                    values["timeEncoding"] = "ECMA_ISO8601";
                }

                entry["attributeValues"] = values;
            }
            else
            {
                entry["header"] = new JsonArray(new JsonObject { ["property"] = "count", ["valueType"] = "UInt32" });
                entry["ordering"] = new JsonArray("attributeValues");
                entry["attributeValues"] = new JsonObject { ["valueType"] = value, ["valuesPerElement"] = 1 };
            }

            storage.Add(entry);
            statistics.Add(new JsonObject { ["key"] = $"f_{k}", ["name"] = field.Name, ["href"] = $"./statistics/f_{k}/0" });
        }

        return (fields, storage, statistics);
    }

    private static JsonObject Reference() => new() { ["wkid"] = 4326, ["latestWkid"] = 4326 };

    private static JsonObject Popup(string name, List<(SceneField Field, int Index)> stored, JsonArray fields) => new()
    {
        ["title"] = stored.Count > 0 && stored[0].Field.Type == FieldType.Text ? $"{{{stored[0].Field.Name}}}" : name,
        ["popupElements"] = new JsonArray(new JsonObject { ["type"] = "fields" }),
        ["fieldInfos"] = new JsonArray([.. fields.Select(f => (JsonNode)new JsonObject
        {
            ["fieldName"] = f!["name"]!.GetValue<string>(), ["label"] = f["alias"]!.GetValue<string>(), ["visible"] = true,
        })]),
    };

    private static JsonObject FullExtent(Box all) => new()
    {
        ["xmin"] = all.MinLon, ["ymin"] = all.MinLat, ["xmax"] = all.MaxLon, ["ymax"] = all.MaxLat,
        ["zmin"] = all.MinZ, ["zmax"] = all.MaxZ, ["spatialReference"] = Reference(),
    };

    private static JsonObject Layer(
        string name, List<(SceneField Field, int Index)> stored, Box all, int[] colour, DateTimeOffset updated)
    {
        (JsonArray fields, JsonArray storage, JsonArray statistics) = Schema(stored);

        return new JsonObject
        {
            ["id"] = 0,
            ["version"] = Guid.NewGuid().ToString("D").ToUpperInvariant(),
            ["name"] = name,
            ["alias"] = name,
            ["layerType"] = "3DObject",
            ["serviceUpdateTimeStamp"] = new JsonObject { ["lastUpdate"] = updated.ToUnixTimeMilliseconds() },
            ["capabilities"] = new JsonArray("View", "Query"),
            ["spatialReference"] = Reference(),
            ["heightModelInfo"] = new JsonObject { ["heightModel"] = "gravity_related_height", ["vertCRS"] = "EGM96_height", ["heightUnit"] = "meter" },
            ["store"] = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("D").ToUpperInvariant(),
                ["profile"] = "meshpyramids",
                ["version"] = "1.7",
                ["resourcePattern"] = new JsonArray("Geometry", "Attributes"),
                ["rootNode"] = "./nodes/root",
                ["extent"] = new JsonArray(all.MinLon, all.MinLat, all.MaxLon, all.MaxLat),
                ["indexCRS"] = "http://www.opengis.net/def/crs/EPSG/0/4326",
                ["vertexCRS"] = "http://www.opengis.net/def/crs/EPSG/0/4326",
                ["normalReferenceFrame"] = "east-north-up",
                ["lodType"] = "MeshPyramid",
                ["lodModel"] = "node-switching",
                ["defaultGeometrySchema"] = new JsonObject
                {
                    ["geometryType"] = "triangles",
                    ["header"] = new JsonArray(
                        new JsonObject { ["property"] = "vertexCount", ["type"] = "UInt32" },
                        new JsonObject { ["property"] = "featureCount", ["type"] = "UInt32" }),
                    ["topology"] = "PerAttributeArray",
                    ["ordering"] = new JsonArray("position", "color"),
                    ["vertexAttributes"] = new JsonObject
                    {
                        ["position"] = new JsonObject { ["valueType"] = "Float32", ["valuesPerElement"] = 3 },
                        ["color"] = new JsonObject { ["valueType"] = "UInt8", ["valuesPerElement"] = 4 },
                    },
                    ["featureAttributeOrder"] = new JsonArray("id", "faceRange"),
                    ["featureAttributes"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["valueType"] = "UInt64", ["valuesPerElement"] = 1 },
                        ["faceRange"] = new JsonObject { ["valueType"] = "UInt32", ["valuesPerElement"] = 2 },
                    },
                },
            },
            ["nodePages"] = new JsonObject { ["nodesPerPage"] = NodesPerPage, ["lodSelectionMetricType"] = "maxScreenThresholdSQ" },
            ["geometryDefinitions"] = new JsonArray(new JsonObject
            {
                ["geometryBuffers"] = new JsonArray(new JsonObject
                {
                    ["offset"] = 8,
                    ["position"] = new JsonObject { ["type"] = "Float32", ["component"] = 3 },
                    ["color"] = new JsonObject { ["type"] = "UInt8", ["component"] = 4 },
                    ["featureId"] = new JsonObject { ["type"] = "UInt64", ["component"] = 1, ["binding"] = "per-feature" },
                    ["faceRange"] = new JsonObject { ["type"] = "UInt32", ["component"] = 2, ["binding"] = "per-feature" },
                }),
            }),
            ["materialDefinitions"] = new JsonArray(new JsonObject
            {
                ["doubleSided"] = true,
                ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(1, 1, 1, 1), ["metallicFactor"] = 0, ["roughnessFactor"] = 1 },
            }),
            ["fields"] = fields,
            ["attributeStorageInfo"] = storage,
            ["statisticsInfo"] = statistics,
            ["drawingInfo"] = new JsonObject
            {
                ["renderer"] = new JsonObject
                {
                    ["type"] = "simple",
                    ["symbol"] = new JsonObject
                    {
                        ["type"] = "MeshSymbol3D",
                        ["symbolLayers"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "Fill",
                            ["material"] = new JsonObject { ["color"] = new JsonArray(colour[0], colour[1], colour[2]), ["colorMixMode"] = "replace" },
                        }),
                    },
                },
            },
            ["popupInfo"] = Popup(name, stored, fields),
            ["fullExtent"] = FullExtent(all),
        };
    }

    private static JsonObject Statistics(IReadOnlyList<IReadOnlyList<object?>> rows, (SceneField Field, int Index) stored)
    {
        object?[] values = [.. rows.Select(r => stored.Index < r.Count ? r[stored.Index] : null)];
        JsonObject stats = new() { ["totalValuesCount"] = values.Length };

        if (stored.Field.Type is FieldType.SmallInteger or FieldType.Integer or FieldType.Single or FieldType.Double or FieldType.BigInteger)
        {
            double[] numbers = [.. values.Where(v => v is not null).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).Where(d => !double.IsNaN(d))];
            if (numbers.Length > 0)
            {
                stats["min"] = numbers.Min();
                stats["max"] = numbers.Max();
                stats["count"] = numbers.Length;
                stats["sum"] = numbers.Sum();
                stats["avg"] = numbers.Average();
            }
        }
        else
        {
            stats["mostFrequentValues"] = new JsonArray([.. values.Where(v => v is not null).Select(v => Text(v!))
                .GroupBy(v => v).OrderByDescending(g => g.Count()).Take(256)
                .Select(g => (JsonNode)new JsonObject { ["value"] = g.Key, ["count"] = g.Count() })]);
        }

        return new JsonObject { ["stats"] = stats };
    }

    // ------------------------------------------------------------------------------------------------ the zip

    private static byte[] Utf8(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static byte[] Gzip(byte[] content)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    /// <summary>
    /// A zip of stored entries, as I3S requires of a package, ending with the hash table of entry offsets.
    /// </summary>
    /// <remarks>
    /// Written here rather than through <see cref="ZipArchive"/>, which does not say where it put an entry's local
    /// header — and the hash table I3S recommends (<c>@specialIndexFileHASH128@</c>, last in the archive) is a sorted list
    /// of each entry's path digest and that offset. No ZIP64: a generated package stays far below 4 GB, and the
    /// document store's limit is smaller still.
    /// </remarks>
    private sealed class PackageWriter(Stream output)
    {
        private readonly List<(string Name, uint Crc, uint Size, uint Offset)> _entries = [];

        public void Add(string name, byte[] content)
        {
            uint offset = (uint)output.Position;
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            uint crc = Crc32(content);
            Span<byte> header = stackalloc byte[30];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034b50);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0x0800); // UTF-8 names
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 0); // stored
            BinaryPrimitives.WriteUInt32LittleEndian(header[14..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header[18..], (uint)content.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[22..], (uint)content.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)nameBytes.Length);
            output.Write(header);
            output.Write(nameBytes);
            output.Write(content);
            _entries.Add((name, crc, (uint)content.Length, offset));
        }

        public void Finish()
        {
            // The hash table: MD5 of each lower-case path and the offset of its local header, sorted by the digest read
            // as two little-endian 64-bit words.
            // MD5 is the format's choice of an index key here, not a security measure (I3S slpk_hashtable).
#pragma warning disable CA5351
            List<(byte[] Digest, ulong Offset)> table = [.. _entries.Select(e => (MD5.HashData(Encoding.UTF8.GetBytes(e.Name.ToLowerInvariant())), (ulong)e.Offset))];
#pragma warning restore CA5351
            table.Sort((a, b) =>
            {
                int first = BinaryPrimitives.ReadUInt64LittleEndian(a.Digest).CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(b.Digest));
                return first != 0 ? first : BinaryPrimitives.ReadUInt64LittleEndian(a.Digest.AsSpan(8)).CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(b.Digest.AsSpan(8)));
            });
            byte[] hash = new byte[24 * table.Count];
            for (int i = 0; i < table.Count; i++)
            {
                table[i].Digest.CopyTo(hash, 24 * i);
                BinaryPrimitives.WriteUInt64LittleEndian(hash.AsSpan((24 * i) + 16), table[i].Offset);
            }

            Add("@specialIndexFileHASH128@", hash);

            uint directory = (uint)output.Position;
            Span<byte> central = stackalloc byte[46];
            foreach ((string name, uint crc, uint size, uint offset) in _entries)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                central.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(central, 0x02014b50);
                BinaryPrimitives.WriteUInt16LittleEndian(central[4..], 20);
                BinaryPrimitives.WriteUInt16LittleEndian(central[6..], 20);
                BinaryPrimitives.WriteUInt16LittleEndian(central[8..], 0x0800);
                BinaryPrimitives.WriteUInt32LittleEndian(central[16..], crc);
                BinaryPrimitives.WriteUInt32LittleEndian(central[20..], size);
                BinaryPrimitives.WriteUInt32LittleEndian(central[24..], size);
                BinaryPrimitives.WriteUInt16LittleEndian(central[28..], (ushort)nameBytes.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(central[42..], offset);
                output.Write(central);
                output.Write(nameBytes);
            }

            uint directorySize = (uint)output.Position - directory;
            Span<byte> end = stackalloc byte[22];
            end.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
            BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)_entries.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)_entries.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(end[12..], directorySize);
            BinaryPrimitives.WriteUInt32LittleEndian(end[16..], directory);
            output.Write(end);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }
}
