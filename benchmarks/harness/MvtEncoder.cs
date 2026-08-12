using System.Buffers.Binary;
using NetTopologySuite.Geometries;

namespace GisBench;

/// <summary>
/// Minimal Mapbox Vector Tile encoder, written rather than adopted.
///
/// MVT encoding sits in Tier 1 (docs/build-vs-adopt-policy.md §2) because the
/// format is small and fully specified, and because it is the hottest path in
/// the system. A library here would mean A-019 measured the library rather than
/// the thing we would ship.
///
/// Implements only what the tile path needs: one layer, points/lines/polygons,
/// string and numeric attributes. No feature ids beyond uint64, no unknown
/// geometry, no nested value types.
///
/// Protobuf is hand-written. The MVT schema is small enough that a code
/// generator would add a dependency and a build step for perhaps eighty lines
/// of output.
/// </summary>
public sealed class MvtEncoder
{
    // Tile.layers = 3
    private const byte TileLayers = (3 << 3) | 2;

    // Layer fields
    private const byte LayerName = (1 << 3) | 2;
    private const byte LayerFeatures = (2 << 3) | 2;
    private const byte LayerKeys = (3 << 3) | 2;
    private const byte LayerValues = (4 << 3) | 2;
    private const byte LayerExtent = (5 << 3) | 0;
    private const byte LayerVersion = (15 << 3) | 0;

    // Feature fields
    private const byte FeatureId = (1 << 3) | 0;
    private const byte FeatureTags = (2 << 3) | 2;
    private const byte FeatureType = (3 << 3) | 0;
    private const byte FeatureGeometry = (4 << 3) | 2;

    // Value fields
    private const byte ValueString = (1 << 3) | 2;
    private const byte ValueDouble = (3 << 3) | 1;
    private const byte ValueInt = (4 << 3) | 0;

    private enum GeomType { Unknown = 0, Point = 1, LineString = 2, Polygon = 3 }

    private readonly string _layerName;
    private readonly List<string> _keys = new();
    private readonly Dictionary<string, int> _keyIndex = new(StringComparer.Ordinal);
    private readonly List<object> _values = new();
    private readonly Dictionary<object, int> _valueIndex = new();
    private readonly MemoryStream _features = new();

    public int FeatureCount { get; private set; }

    public MvtEncoder(string layerName) => _layerName = layerName;

    /// <summary>
    /// Add one feature. <paramref name="geometry"/> must already be in tile
    /// space — clipped, quantised, y-down. Returns false when the geometry
    /// produced no drawable commands, which is common after clipping and is not
    /// an error.
    /// </summary>
    public bool Add(long id, Geometry geometry, IReadOnlyList<KeyValuePair<string, object?>> attrs)
    {
        var geomBytes = EncodeGeometry(geometry, out var type);
        if (geomBytes.Length == 0) return false;

        using var f = new MemoryStream();

        WriteTag(f, FeatureId);
        WriteVarint(f, (ulong)id);

        if (attrs.Count > 0)
        {
            using var tags = new MemoryStream();
            foreach (var kv in attrs)
            {
                if (kv.Value is null) continue;
                WriteVarint(tags, (ulong)KeyIdx(kv.Key));
                WriteVarint(tags, (ulong)ValueIdx(kv.Value));
            }
            if (tags.Length > 0)
            {
                WriteTag(f, FeatureTags);
                WriteBytes(f, tags.ToArray());
            }
        }

        WriteTag(f, FeatureType);
        WriteVarint(f, (ulong)type);

        WriteTag(f, FeatureGeometry);
        WriteBytes(f, geomBytes);

        WriteTag(_features, LayerFeatures);
        WriteBytes(_features, f.ToArray());
        FeatureCount++;
        return true;
    }

    public byte[] Finish()
    {
        using var layer = new MemoryStream();

        WriteTag(layer, LayerVersion);
        WriteVarint(layer, 2);

        WriteTag(layer, LayerName);
        WriteBytes(layer, System.Text.Encoding.UTF8.GetBytes(_layerName));

        var fb = _features.ToArray();
        layer.Write(fb, 0, fb.Length);

        foreach (var k in _keys)
        {
            WriteTag(layer, LayerKeys);
            WriteBytes(layer, System.Text.Encoding.UTF8.GetBytes(k));
        }

        Span<byte> dbuf = stackalloc byte[8];
        foreach (var v in _values)
        {
            using var val = new MemoryStream();
            switch (v)
            {
                case string s:
                    WriteTag(val, ValueString);
                    WriteBytes(val, System.Text.Encoding.UTF8.GetBytes(s));
                    break;
                case double d:
                    WriteTag(val, ValueDouble);
                    BinaryPrimitives.WriteDoubleLittleEndian(dbuf, d);
                    val.Write(dbuf);
                    break;
                case long l:
                    WriteTag(val, ValueInt);
                    WriteVarint(val, (ulong)l);
                    break;
                default:
                    WriteTag(val, ValueString);
                    WriteBytes(val, System.Text.Encoding.UTF8.GetBytes(v.ToString() ?? ""));
                    break;
            }
            WriteTag(layer, LayerValues);
            WriteBytes(layer, val.ToArray());
        }

        WriteTag(layer, LayerExtent);
        WriteVarint(layer, TileMath.Extent);

        using var tile = new MemoryStream();
        WriteTag(tile, TileLayers);
        WriteBytes(tile, layer.ToArray());
        return tile.ToArray();
    }

    // ---- geometry command encoding -------------------------------------

    private static byte[] EncodeGeometry(Geometry g, out GeomType type)
    {
        using var ms = new MemoryStream();
        int cx = 0, cy = 0;

        switch (g)
        {
            case Point p:
                type = GeomType.Point;
                WriteCommand(ms, 1, 1);
                WriteZigZagPair(ms, (int)p.X, (int)p.Y, ref cx, ref cy);
                break;

            case MultiPoint mp:
                type = GeomType.Point;
                if (mp.NumGeometries == 0) return [];
                WriteCommand(ms, 1, mp.NumGeometries);
                foreach (var geom in mp.Geometries)
                {
                    var pt = (Point)geom;
                    WriteZigZagPair(ms, (int)pt.X, (int)pt.Y, ref cx, ref cy);
                }
                break;

            case LineString ls:
                type = GeomType.LineString;
                if (!WriteLine(ms, ls.Coordinates, ref cx, ref cy)) return [];
                break;

            case MultiLineString mls:
                type = GeomType.LineString;
                foreach (var geom in mls.Geometries)
                    WriteLine(ms, geom.Coordinates, ref cx, ref cy);
                break;

            case Polygon poly:
                type = GeomType.Polygon;
                if (!WriteRing(ms, poly, ref cx, ref cy)) return [];
                break;

            case MultiPolygon mpoly:
                type = GeomType.Polygon;
                foreach (var geom in mpoly.Geometries)
                    WriteRing(ms, (Polygon)geom, ref cx, ref cy);
                break;

            default:
                type = GeomType.Unknown;
                return [];
        }

        return ms.ToArray();
    }

    private static bool WriteLine(MemoryStream ms, Coordinate[] coords, ref int cx, ref int cy)
    {
        if (coords.Length < 2) return false;
        WriteCommand(ms, 1, 1);
        WriteZigZagPair(ms, (int)coords[0].X, (int)coords[0].Y, ref cx, ref cy);
        WriteCommand(ms, 2, coords.Length - 1);
        for (int i = 1; i < coords.Length; i++)
            WriteZigZagPair(ms, (int)coords[i].X, (int)coords[i].Y, ref cx, ref cy);
        return true;
    }

    private static bool WriteRing(MemoryStream ms, Polygon poly, ref int cx, ref int cy)
    {
        // MVT winding: exterior clockwise, interior counter-clockwise, in a
        // y-down coordinate system. NTS uses y-up conventions, so the sense is
        // inverted relative to what NTS calls CW.
        if (!WriteSingleRing(ms, poly.ExteriorRing.Coordinates, exterior: true, ref cx, ref cy))
            return false;
        foreach (var hole in poly.InteriorRings)
            WriteSingleRing(ms, hole.Coordinates, exterior: false, ref cx, ref cy);
        return true;
    }

    private static bool WriteSingleRing(MemoryStream ms, Coordinate[] coords, bool exterior, ref int cx, ref int cy)
    {
        if (coords.Length < 4) return false;

        // Drop the closing coordinate; ClosePath implies it.
        int n = coords.Length - 1;

        double area = 0;
        for (int i = 0; i < n; i++)
        {
            var a = coords[i];
            var b = coords[(i + 1) % n];
            area += (a.X * b.Y) - (b.X * a.Y);
        }
        // In tile space y is down, so a positive shoelace area is a
        // counter-clockwise ring on screen.
        bool needReverse = exterior ? area > 0 : area < 0;

        WriteCommand(ms, 1, 1);
        int first = needReverse ? n - 1 : 0;
        WriteZigZagPair(ms, (int)coords[first].X, (int)coords[first].Y, ref cx, ref cy);

        WriteCommand(ms, 2, n - 1);
        for (int i = 1; i < n; i++)
        {
            int idx = needReverse ? n - 1 - i : i;
            WriteZigZagPair(ms, (int)coords[idx].X, (int)coords[idx].Y, ref cx, ref cy);
        }

        WriteCommand(ms, 7, 1); // ClosePath
        return true;
    }

    private static void WriteCommand(MemoryStream ms, int id, int count)
        => WriteVarint(ms, (ulong)((id & 0x7) | (count << 3)));

    private static void WriteZigZagPair(MemoryStream ms, int x, int y, ref int cx, ref int cy)
    {
        int dx = x - cx, dy = y - cy;
        WriteVarint(ms, ZigZag(dx));
        WriteVarint(ms, ZigZag(dy));
        cx = x; cy = y;
    }

    private static ulong ZigZag(int v) => (ulong)((v << 1) ^ (v >> 31));

    // ---- protobuf primitives -------------------------------------------

    private static void WriteTag(Stream s, byte tag) => s.WriteByte(tag);

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static void WriteBytes(Stream s, byte[] b)
    {
        WriteVarint(s, (ulong)b.Length);
        s.Write(b, 0, b.Length);
    }

    private int KeyIdx(string k)
    {
        if (_keyIndex.TryGetValue(k, out var i)) return i;
        i = _keys.Count;
        _keys.Add(k);
        _keyIndex[k] = i;
        return i;
    }

    private int ValueIdx(object v)
    {
        if (_valueIndex.TryGetValue(v, out var i)) return i;
        i = _values.Count;
        _values.Add(v);
        _valueIndex[v] = i;
        return i;
    }
}
