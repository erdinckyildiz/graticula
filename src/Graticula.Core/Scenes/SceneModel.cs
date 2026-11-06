using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Graticula.Scenes;

/// <summary>
/// A 3D object's model as a 3D object feature layer keeps it (ADR-280): a glTF binary and the transform that places it.
/// The transform is the one the layer's <c>ESRI3DO_*</c> fields hold, and the one Maps SDK 5.2 was measured writing:
/// an origin in WGS 84 and metres, a translation, a scale and a rotation about an axis, all in metres east, north and up
/// from the origin.
/// </summary>
/// <param name="Glb">The model, a glTF 2.0 binary.</param>
/// <param name="Hash">Its SHA-256, as the layer's asset map names it.</param>
/// <param name="OriginLon">The origin's longitude.</param>
/// <param name="OriginLat">The origin's latitude.</param>
/// <param name="OriginZ">The origin's height, in metres.</param>
/// <param name="Translation">Metres east, north and up added after scaling and rotating.</param>
/// <param name="Scale">The scale on each axis.</param>
/// <param name="Axis">The rotation's axis, east, north and up.</param>
/// <param name="Degrees">The rotation about it, counter-clockwise looking down the axis.</param>
public sealed record SceneModel(
    byte[] Glb, string Hash, double OriginLon, double OriginLat, double OriginZ,
    (double X, double Y, double Z) Translation, (double X, double Y, double Z) Scale, (double X, double Y, double Z) Axis, double Degrees)
{
    /// <summary>A model at an origin, with no translation, scale or rotation.</summary>
    public static SceneModel At(byte[] glb, string hash, double lon, double lat, double z) =>
        new(glb, hash, lon, lat, z, (0, 0, 0), (1, 1, 1), (0, 0, 1), 0);
}

/// <summary>
/// The triangles of a glTF 2.0 binary with each vertex's colour (ADR-280 §5.2, condition 2). Read from the glTF
/// specification: every mesh a scene's nodes instantiate, by each node's matrix or translation, rotation and scale, in
/// triangle primitives with float positions and, if any, 8-, 16- or 32-bit indices. A vertex's colour is its
/// material's base colour factor. <b>Not read</b>: textures, vertex colours, Draco or quantized positions, morph targets,
/// skins and strips; a primitive that needs one of them is left out, and <see cref="Skipped"/> says how many were.
/// </summary>
public sealed class GltfTriangles
{
    private GltfTriangles(List<(double X, double Y, double Z)> points, List<(byte R, byte G, byte B, byte A)> colours, int skipped)
    {
        Points = points;
        Colours = colours;
        Skipped = skipped;
    }

    /// <summary>Three points a triangle, in the model's own metres, glTF's axes (Y up).</summary>
    public IReadOnlyList<(double X, double Y, double Z)> Points { get; }

    /// <summary>Each point's colour, sRGB.</summary>
    public IReadOnlyList<(byte R, byte G, byte B, byte A)> Colours { get; }

    /// <summary>Primitives that were not read, for want of something this reader does not do.</summary>
    public int Skipped { get; }

    /// <summary>Reads a glTF binary.</summary>
    /// <exception cref="FormatException">It is not a glTF 2.0 binary.</exception>
    public static GltfTriangles Read(byte[] glb)
    {
        ArgumentNullException.ThrowIfNull(glb);
        ReadOnlySpan<byte> span = glb;
        if (glb.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x46546C67 || BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) != 2)
        {
            throw new FormatException("This is not a glTF 2.0 binary (glb).");
        }

        int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[16..]) != 0x4E4F534A || 20 + jsonLength > glb.Length)
        {
            throw new FormatException("The glb's first chunk is not its JSON.");
        }

        JsonNode doc = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength))
            ?? throw new FormatException("The glb's JSON is empty.");
        int binStart = 20 + jsonLength;
        byte[] bin = [];
        if (binStart + 8 <= glb.Length && BinaryPrimitives.ReadUInt32LittleEndian(span[(binStart + 4)..]) == 0x004E4942)
        {
            int binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[binStart..]);
            bin = glb.AsSpan(binStart + 8, Math.Min(binLength, glb.Length - binStart - 8)).ToArray();
        }

        List<(double, double, double)> points = [];
        List<(byte, byte, byte, byte)> colours = [];
        int skipped = 0;
        JsonArray nodes = doc["nodes"] as JsonArray ?? [];
        int sceneIndex = (int?)doc["scene"] ?? 0;
        JsonArray roots = doc["scenes"]?[sceneIndex]?["nodes"] as JsonArray
            ?? [.. Enumerable.Range(0, nodes.Count).Select(i => (JsonNode)i)];

        void Walk(int index, double[] parent, int depth)
        {
            if (depth > 64 || index < 0 || index >= nodes.Count || nodes[index] is not JsonObject node)
            {
                return;
            }

            double[] world = Multiply(parent, Local(node));
            if (node["mesh"] is JsonValue m && doc["meshes"]?[(int)m] is JsonObject mesh)
            {
                foreach (JsonNode? primitive in mesh["primitives"] as JsonArray ?? [])
                {
                    if (!Primitive(doc, bin, primitive as JsonObject, world, points, colours))
                    {
                        skipped++;
                    }
                }
            }

            foreach (JsonNode? child in node["children"] as JsonArray ?? [])
            {
                Walk((int)child!, world, depth + 1);
            }
        }

        foreach (JsonNode? root in roots)
        {
            Walk((int)root!, Identity(), 0);
        }

        return new GltfTriangles(points, colours, skipped);
    }

    private static bool Primitive(
        JsonNode doc, byte[] bin, JsonObject? primitive, double[] world,
        List<(double, double, double)> points, List<(byte, byte, byte, byte)> colours)
    {
        if (primitive is null || ((int?)primitive["mode"] ?? 4) != 4 || primitive["extensions"] is JsonObject { Count: > 0 }
            || primitive["targets"] is not null || primitive["attributes"]?["POSITION"] is not JsonValue p)
        {
            return false;
        }

        double[]? positions = Floats(doc, bin, (int)p, 3);
        if (positions is null)
        {
            return false;
        }

        int[] indices;
        if (primitive["indices"] is JsonValue ix)
        {
            int[]? read = Indices(doc, bin, (int)ix);
            if (read is null)
            {
                return false;
            }

            indices = read;
        }
        else
        {
            indices = [.. Enumerable.Range(0, positions.Length / 3)];
        }

        (byte, byte, byte, byte) colour = (200, 200, 200, 255);
        if (primitive["material"] is JsonValue mi && doc["materials"]?[(int)mi]?["pbrMetallicRoughness"]?["baseColorFactor"] is JsonArray f && f.Count >= 3)
        {
            static byte Srgb(double linear)
            {
                double c = Math.Clamp(linear, 0, 1);
                double s = c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055;
                return (byte)Math.Round(s * 255);
            }

            colour = (Srgb((double)f[0]!), Srgb((double)f[1]!), Srgb((double)f[2]!), (byte)Math.Round(Math.Clamp(f.Count > 3 ? (double)f[3]! : 1, 0, 1) * 255));
        }

        int vertexCount = positions.Length / 3;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            if (indices[t] >= vertexCount || indices[t + 1] >= vertexCount || indices[t + 2] >= vertexCount)
            {
                return false;
            }
        }

        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                int i = indices[t + k] * 3;
                double x = positions[i], y = positions[i + 1], z = positions[i + 2];
                points.Add((
                    (world[0] * x) + (world[4] * y) + (world[8] * z) + world[12],
                    (world[1] * x) + (world[5] * y) + (world[9] * z) + world[13],
                    (world[2] * x) + (world[6] * y) + (world[10] * z) + world[14]));
                colours.Add(colour);
            }
        }

        return true;
    }

    /// <summary>A float accessor's values, or null when it is not one this reader takes.</summary>
    private static double[]? Floats(JsonNode doc, byte[] bin, int accessor, int width)
    {
        if (doc["accessors"]?[accessor] is not JsonObject a || ((int?)a["componentType"] ?? 0) != 5126 || a["sparse"] is not null
            || View(doc, a) is not (int offset, int stride))
        {
            return null;
        }

        int count = (int?)a["count"] ?? 0;
        stride = stride == 0 ? width * 4 : stride;
        if (count < 0 || offset < 0 || (count > 0 && offset + ((count - 1) * stride) + (width * 4) > bin.Length))
        {
            return null;
        }

        double[] values = new double[count * width];
        for (int i = 0; i < count; i++)
        {
            for (int k = 0; k < width; k++)
            {
                values[(i * width) + k] = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(offset + (i * stride) + (k * 4)));
            }
        }

        return values;
    }

    private static int[]? Indices(JsonNode doc, byte[] bin, int accessor)
    {
        if (doc["accessors"]?[accessor] is not JsonObject a || a["sparse"] is not null || View(doc, a) is not (int offset, int stride))
        {
            return null;
        }

        int count = (int?)a["count"] ?? 0;
        int size = ((int?)a["componentType"] ?? 0) switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => 0 };
        stride = stride == 0 ? size : stride;
        if (size == 0 || count < 0 || (count > 0 && offset + ((count - 1) * stride) + size > bin.Length))
        {
            return null;
        }

        int[] values = new int[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> at = bin.AsSpan(offset + (i * stride));
            values[i] = size == 1 ? at[0] : size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(at) : (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(at));
        }

        return values;
    }

    /// <summary>An accessor's start in the binary chunk and its stride; null when its buffer is not the glb's own.</summary>
    private static (int Offset, int Stride)? View(JsonNode doc, JsonObject accessor)
    {
        if (accessor["bufferView"] is not JsonValue v || doc["bufferViews"]?[(int)v] is not JsonObject view || ((int?)view["buffer"] ?? 0) != 0
            || doc["buffers"]?[0]?["uri"] is not null)
        {
            return null;
        }

        return (((int?)view["byteOffset"] ?? 0) + ((int?)accessor["byteOffset"] ?? 0), (int?)view["byteStride"] ?? 0);
    }

    private static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary>A node's own matrix, column-major: its <c>matrix</c>, or translation × rotation × scale.</summary>
    private static double[] Local(JsonObject node)
    {
        if (node["matrix"] is JsonArray m && m.Count == 16)
        {
            return [.. m.Select(v => (double)v!)];
        }

        double[] t = node["translation"] is JsonArray ta && ta.Count == 3 ? [.. ta.Select(v => (double)v!)] : [0, 0, 0];
        double[] q = node["rotation"] is JsonArray ra && ra.Count == 4 ? [.. ra.Select(v => (double)v!)] : [0, 0, 0, 1];
        double[] s = node["scale"] is JsonArray sa && sa.Count == 3 ? [.. sa.Select(v => (double)v!)] : [1, 1, 1];
        (double x, double y, double z, double w) = (q[0], q[1], q[2], q[3]);
        return
        [
            (1 - (2 * ((y * y) + (z * z)))) * s[0], 2 * ((x * y) + (z * w)) * s[0], 2 * ((x * z) - (y * w)) * s[0], 0,
            2 * ((x * y) - (z * w)) * s[1], (1 - (2 * ((x * x) + (z * z)))) * s[1], 2 * ((y * z) + (x * w)) * s[1], 0,
            2 * ((x * z) + (y * w)) * s[2], 2 * ((y * z) - (x * w)) * s[2], (1 - (2 * ((x * x) + (y * y)))) * s[2], 0,
            t[0], t[1], t[2], 1,
        ];
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        double[] r = new double[16];
        for (int col = 0; col < 4; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += a[(k * 4) + row] * b[(col * 4) + k];
                }

                r[(col * 4) + row] = sum;
            }
        }

        return r;
    }
}
