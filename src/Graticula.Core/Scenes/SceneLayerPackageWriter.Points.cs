using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Graticula.Scenes;

/// <summary>One point: where it is in WGS 84, its height, and its values.</summary>
/// <param name="Id">Its object id; I3S keeps a point's id in 32 bits.</param>
/// <param name="Lon">Its longitude.</param>
/// <param name="Lat">Its latitude.</param>
/// <param name="Z">Its height in metres, read as the layer's elevation mode says.</param>
/// <param name="Values">Its values, in the order of the fields.</param>
public sealed record ScenePoint(long Id, double Lon, double Lat, double Z, IReadOnlyList<object?> Values);

/// <summary>Where a point scene layer's heights stand.</summary>
/// <remarks>
/// There is no <c>onTheGround</c>: in it the Maps SDK drapes an icon onto the terrain, where a tilted view flattens it
/// to a sliver (measured 2026-11-05). A point without a height is written at zero above the ground instead, and stands
/// up as a symbol facing the viewer.
/// </remarks>
public enum SceneElevation
{
    /// <summary>The height is above the ground: a field of heights, or zero for a point that has none.</summary>
    RelativeToGround,

    /// <summary>The height is above the vertical datum: a point's own z.</summary>
    AbsoluteHeight,
}

public static partial class SceneLayerPackageWriter
{
    /// <summary>The most points a node of a point layer holds before it is split.</summary>
    public const int MaximumPointsPerNode = 2048;

    // A node above the leaves keeps one point in each cell of this many by this many across its box.
    private const int ThinningGrid = 32;

    /// <summary>Writes a point scene layer package — ADR-269 §5.3, D-386.</summary>
    /// <remarks>
    /// <para>
    /// <b>Every node draws something.</b> I3S point layers are <c>AutoThinning</c>: a node above the leaves carries a
    /// thinned sample of the points below it, so a city's worth of points shows as an even scatter from far away and
    /// fills in as the view comes closer, rather than loading every leaf in view. The sample keeps the first point in
    /// each cell of a 32 × 32 grid across the node's box — at most 1,024 points, spread over the ground rather than
    /// bunched where the points are dense — and every sampled point is in a leaf as well, which node switching needs: a
    /// node's children replace it, so nothing it showed may disappear when they do.
    /// </para>
    /// <para>
    /// <b>The geometry is Draco</b>, which I3S 1.7 and later require of a point layer; see
    /// <see cref="DracoPointCloud"/>. The rest — node pages, attribute buffers, statistics, the zip and its hash table —
    /// is what the 3D object writer writes, and the layer document follows what ArcGIS Pro 3.7 writes for a point layer.
    /// </para>
    /// </remarks>
    /// <param name="name">The layer's name.</param>
    /// <param name="fields">The fields after the object id.</param>
    /// <param name="points">The points.</param>
    /// <param name="colour">The symbol's colour, [r, g, b].</param>
    /// <param name="size">The symbol's size in points.</param>
    /// <param name="elevation">What the points' heights stand on.</param>
    /// <param name="updated">When the content was made, for the layer's update stamp.</param>
    /// <returns>The .slpk bytes.</returns>
    public static byte[] WritePoints(
        string name, IReadOnlyList<SceneField> fields, IReadOnlyList<ScenePoint> points, int[] colour, double size,
        SceneElevation elevation, DateTimeOffset updated)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(colour);

        if (points.FirstOrDefault(p => p.Id is < int.MinValue or > int.MaxValue) is { } wide)
        {
            throw new ArgumentException($"Point {wide.Id} has an id I3S cannot hold: a point layer's ids are 32-bit.", nameof(points));
        }

        List<(SceneField Field, int Index)> stored = [.. fields.Select((f, i) => (f, i)).Where(p => Buffered(p.f.Type))];
        List<ScenePoint> all = [.. points.Where(p => double.IsFinite(p.Lon) && double.IsFinite(p.Lat))];
        if (all.Count == 0)
        {
            throw new ArgumentException("A point scene layer has at least one point.", nameof(points));
        }

        List<PointNode> nodes = [new PointNode(all, null)];
        for (int i = 0; i < nodes.Count; i++)
        {
            PointNode node = nodes[i];
            if (node.Points.Count <= MaximumPointsPerNode)
            {
                continue;
            }

            foreach (List<ScenePoint> quarter in SplitPoints(node.Points))
            {
                if (quarter.Count > 0)
                {
                    node.Children.Add(nodes.Count);
                    nodes.Add(new PointNode(quarter, i));
                }
            }
        }

        using System.IO.MemoryStream package = new();
        PackageWriter zip = new(package);
        JsonArray pageNodes = [];

        for (int i = 0; i < nodes.Count; i++)
        {
            PointNode node = nodes[i];
            List<ScenePoint> shown = node.Children.Count > 0 ? Thin(node.Points) : node.Points;
            Box box = BoxOf(node.Points.Select(Place));

            // Positions in metres from the box's centre, which the scale factors turn back into degrees.
            double kx = Math.PI / 180 * EarthRadius * Math.Cos(box.Lat * Math.PI / 180), ky = Math.PI / 180 * EarthRadius;
            byte[] geometry = DracoPointCloud.Encode(
                [.. shown.Select(p => ((float)((p.Lon - box.Lon) * kx), (float)((p.Lat - box.Lat) * ky), (float)(p.Z - box.Z)))],
                [.. shown.Select(p => (int)p.Id)],
                1 / kx, 1 / ky);
            zip.Add($"nodes/{i}/geometries/0.bin.gz", Gzip(geometry));
            AddAttributes(zip, i, [.. shown.Select(p => p.Id)], [.. shown.Select(p => p.Values)], stored);

            JsonObject json = new()
            {
                ["index"] = i,
                ["obb"] = box.Json(),
                // Pixels across the node on screen before its children take over: a sample of n points is about √n
                // across, so this switches when they would stand more than 16 pixels apart.
                ["lodThreshold"] = 16 * Math.Sqrt(shown.Count),
                ["mesh"] = new JsonObject
                {
                    ["geometry"] = new JsonObject
                    {
                        ["definition"] = 0, ["resource"] = i, ["vertexCount"] = shown.Count, ["featureCount"] = shown.Count,
                    },
                    ["attribute"] = new JsonObject { ["resource"] = i },
                },
            };

            if (node.Parent is { } parent)
            {
                json["parentIndex"] = parent;
            }

            if (node.Children.Count > 0)
            {
                json["children"] = new JsonArray([.. node.Children.Select(c => (JsonNode)c)]);
            }

            pageNodes.Add(json);
            if (pageNodes.Count == NodesPerPage || i == nodes.Count - 1)
            {
                zip.Add($"nodepages/{i / NodesPerPage}.json.gz", Gzip(Utf8(new JsonObject { ["nodes"] = pageNodes })));
                pageNodes = [];
            }
        }

        IReadOnlyList<IReadOnlyList<object?>> rows = [.. all.Select(p => p.Values)];
        for (int k = 1; k <= stored.Count; k++)
        {
            zip.Add($"statistics/f_{k}/0.json.gz", Gzip(Utf8(Statistics(rows, stored[k - 1]))));
        }

        zip.Add("3dSceneLayer.json.gz", Gzip(Utf8(PointLayer(name, stored, BoxOf(all.Select(Place)), colour, size, elevation, updated))));
        zip.Add("metadata.json", Utf8(new JsonObject { ["I3SVersion"] = "1.7", ["nodeCount"] = nodes.Count }));
        zip.Finish();
        return package.ToArray();
    }

    private sealed class PointNode(List<ScenePoint> points, int? parent)
    {
        public List<ScenePoint> Points { get; } = points;
        public int? Parent { get; } = parent;
        public List<int> Children { get; } = [];
    }

    private static (double Lon, double Lat, double Z) Place(ScenePoint p) => (p.Lon, p.Lat, p.Z);

    private static List<ScenePoint>[] SplitPoints(List<ScenePoint> points)
    {
        double midLon = Median(points.Select(p => p.Lon)), midLat = Median(points.Select(p => p.Lat));
        List<ScenePoint>[] quarters = [[], [], [], []];

        foreach (ScenePoint p in points)
        {
            quarters[(p.Lon < midLon ? 0 : 1) + (p.Lat < midLat ? 0 : 2)].Add(p);
        }

        // Many points at one place would split forever; such a node is halved by order instead.
        if (quarters.Count(q => q.Count > 0) == 1)
        {
            int half = points.Count / 2;
            return [points.GetRange(0, half), points.GetRange(half, points.Count - half)];
        }

        return quarters;
    }

    /// <summary>The first point in each cell of a grid across the points' extent.</summary>
    private static List<ScenePoint> Thin(List<ScenePoint> points)
    {
        double minLon = points.Min(p => p.Lon), maxLon = points.Max(p => p.Lon);
        double minLat = points.Min(p => p.Lat), maxLat = points.Max(p => p.Lat);
        double width = Math.Max(maxLon - minLon, 1e-12), height = Math.Max(maxLat - minLat, 1e-12);
        HashSet<int> taken = [];
        List<ScenePoint> sample = [];

        foreach (ScenePoint p in points)
        {
            int column = Math.Min(ThinningGrid - 1, (int)((p.Lon - minLon) / width * ThinningGrid));
            int row = Math.Min(ThinningGrid - 1, (int)((p.Lat - minLat) / height * ThinningGrid));
            if (taken.Add((row * ThinningGrid) + column))
            {
                sample.Add(p);
            }
        }

        return sample;
    }

    private static JsonObject PointLayer(
        string name, List<(SceneField Field, int Index)> stored, Box all, int[] colour, double size, SceneElevation elevation,
        DateTimeOffset updated)
    {
        (JsonArray fields, JsonArray storage, JsonArray statistics) = Schema(stored);
        JsonObject geometry() => new()
        {
            ["geometryBuffers"] = new JsonArray(new JsonObject
            {
                ["compressedAttributes"] = new JsonObject
                {
                    ["encoding"] = "draco", ["attributes"] = new JsonArray("position", "feature-index"),
                },
            }),
        };

        JsonObject layer = new()
        {
            ["id"] = 0,
            ["version"] = Guid.NewGuid().ToString("D").ToUpperInvariant(),
            ["name"] = name,
            ["alias"] = name,
            ["layerType"] = "Point",
            ["serviceUpdateTimeStamp"] = new JsonObject { ["lastUpdate"] = updated.ToUnixTimeMilliseconds() },
            ["capabilities"] = new JsonArray("View", "Query"),
            ["spatialReference"] = Reference(),
            ["heightModelInfo"] = new JsonObject { ["heightModel"] = "gravity_related_height", ["vertCRS"] = "EGM96_height", ["heightUnit"] = "meter" },
            ["store"] = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("D").ToUpperInvariant(),
                ["profile"] = "points",
                ["version"] = "1.7",
                ["resourcePattern"] = new JsonArray("Geometry", "Attributes"),
                ["rootNode"] = "./nodes/root",
                ["extent"] = new JsonArray(all.MinLon, all.MinLat, all.MaxLon, all.MaxLat),
                ["indexCRS"] = "http://www.opengis.net/def/crs/EPSG/0/4326",
                ["vertexCRS"] = "http://www.opengis.net/def/crs/EPSG/0/4326",
                ["lodType"] = "AutoThinning",
                ["lodModel"] = "node-switching",
            },
            ["pointNodePages"] = new JsonObject { ["nodesPerPage"] = NodesPerPage, ["lodSelectionMetricType"] = "maxScreenThreshold" },
            // The specification names the point profile's definition in the singular; ArcGIS Pro writes the plural
            // list the other profiles use. Both are written, with the same content.
            ["geometryDefinition"] = geometry(),
            ["geometryDefinitions"] = new JsonArray(geometry()),
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
                        ["type"] = "PointSymbol3D",
                        ["symbolLayers"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "Icon",
                            ["resource"] = new JsonObject { ["primitive"] = "circle" },
                            ["size"] = size,
                            ["material"] = new JsonObject { ["color"] = new JsonArray(colour[0], colour[1], colour[2]) },
                            ["outline"] = new JsonObject { ["color"] = new JsonArray(255, 255, 255), ["size"] = 1 },
                        }),
                    },
                },
            },
            ["elevationInfo"] = new JsonObject
            {
                ["mode"] = elevation == SceneElevation.AbsoluteHeight ? "absoluteHeight" : "relativeToGround",
            },
            ["popupInfo"] = Popup(name, stored, fields),
            ["fullExtent"] = FullExtent(all),
        };

        return layer;
    }
}
