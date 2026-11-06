using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Graticula.Scenes;

/// <summary>
/// A partial update of a 3D object scene layer package — ADR-277 §5.2, Enterprise's <i>partial update</i> of a scene
/// layer cache: only the nodes whose buildings changed are written again; every other node is copied from the package
/// as it was.
/// </summary>
/// <remarks>
/// <para>
/// <b>What changed is found by fingerprints, not by a change log.</b> The package keeps, beside its I3S, each
/// building's fingerprint — its footprint, base, height and values, hashed — and each leaf's buildings
/// (<see cref="PlanEntry"/>). An update reads the layer as it is now and compares: a building whose fingerprint differs
/// was edited, one that is new was added, one that is gone was deleted. This finds an edit whichever path made it —
/// applyEdits, append, overwrite, a script in the database — which a log written by this server's edit paths would not.
/// The layer is still read whole; what is saved is writing the city again.
/// </para>
/// <para>
/// <b>The tree keeps its shape.</b> An edited or deleted building stays in, or leaves, its leaf; a new one joins the leaf
/// whose box is nearest it. The leaves touched and every node above them are written again — their geometry, their
/// coarse city, their box. When more than a quarter of the buildings changed, or a leaf would be emptied or grow past
/// twice its size, the shape would no longer suit the city, and the answer is null: make it whole again.
/// </para>
/// </remarks>
public static partial class SceneLayerPackageWriter
{
    /// <summary>The package entry the plan is kept in; outside I3S's own names, so a client never asks for it.</summary>
    public const string PlanEntry = "graticula/plan.json.gz";

    private static JsonObject Plan(IReadOnlyList<(int? Parent, IReadOnlyList<int> Children, IReadOnlyList<long> Ids)> nodes, IEnumerable<SceneBuilding> buildings)
    {
        JsonObject fingerprints = [];
        foreach (SceneBuilding b in buildings)
        {
            fingerprints[b.Id.ToString(CultureInfo.InvariantCulture)] = Fingerprint(b);
        }

        return new JsonObject
        {
            ["nodes"] = new JsonArray([.. nodes.Select(n => (JsonNode)new JsonObject
            {
                ["p"] = n.Parent is { } p ? p : null,
                ["c"] = new JsonArray([.. n.Children.Select(c => (JsonNode)c)]),
                ["ids"] = new JsonArray([.. n.Ids.Select(i => (JsonNode)i)]),
            })]),
            ["fp"] = fingerprints,
        };
    }

    /// <summary>A building's fingerprint: its footprint, base, height and values, hashed.</summary>
    public static string Fingerprint(SceneBuilding building)
    {
        ArgumentNullException.ThrowIfNull(building);
        StringBuilder text = new();
        foreach (var rings in building.Polygons)
        {
            text.Append('P');
            foreach (var ring in rings)
            {
                text.Append('R');
                foreach ((double lon, double lat) in ring)
                {
                    text.Append(lon.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(lat.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                }
            }
        }

        text.Append('B').Append(building.Base.ToString("R", CultureInfo.InvariantCulture))
            .Append('H').Append(building.Height.ToString("R", CultureInfo.InvariantCulture));
        foreach (object? value in building.Values)
        {
            text.Append('|').Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>What a partial update did.</summary>
    /// <param name="Package">The package, updated.</param>
    /// <param name="Rewritten">How many nodes were written again; every other one was copied.</param>
    /// <param name="Changed">How many buildings were edited, added or deleted.</param>
    public sealed record Updated(byte[] Package, int Rewritten, int Changed);

    /// <summary>
    /// Updates a package this writer made from the buildings as they are now, or answers null when it should be made
    /// whole again (no plan, too much changed, or the tree's shape no longer fits).
    /// </summary>
    public static Updated? Update(
        byte[] previous, string name, IReadOnlyList<SceneField> fields, IReadOnlyList<SceneBuilding> buildings, int[] colour,
        DateTimeOffset updated, double wholeAbove = 0.25)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(buildings);

        using ZipArchive old = new(new MemoryStream(previous), ZipArchiveMode.Read);
        if (old.GetEntry(PlanEntry) is not { } planEntry || ReadJson(planEntry) is not JsonObject plan
            || plan["nodes"] is not JsonArray planNodes || plan["fp"] is not JsonObject oldPrints)
        {
            return null;
        }

        int count = planNodes.Count;
        int?[] parents = new int?[count];
        List<int>[] children = new List<int>[count];
        List<long>[] leafIds = new List<long>[count];
        for (int i = 0; i < count; i++)
        {
            JsonObject n = planNodes[i]!.AsObject();
            parents[i] = n["p"] is JsonValue pv ? pv.GetValue<int>() : null;
            children[i] = [.. n["c"]!.AsArray().Select(c => c!.GetValue<int>())];
            leafIds[i] = [.. n["ids"]!.AsArray().Select(c => c!.GetValue<long>())];
        }

        // The old node documents, by index, for the nodes copied as they are and the boxes a new building is placed by.
        Dictionary<int, JsonObject> oldNodes = [];
        foreach (ZipArchiveEntry page in old.Entries.Where(e => e.FullName.StartsWith("nodepages/", StringComparison.Ordinal)))
        {
            foreach (JsonNode? n in ReadJson(page)!["nodes"]!.AsArray())
            {
                oldNodes[n!["index"]!.GetValue<int>()] = n.AsObject();
            }
        }

        Dictionary<long, SceneBuilding> now = buildings.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        Dictionary<long, int> leafOf = [];
        for (int i = 0; i < count; i++)
        {
            foreach (long id in leafIds[i])
            {
                leafOf[id] = i;
            }
        }

        HashSet<int> touched = [];
        int changed = 0;
        foreach ((long id, int leaf) in leafOf.ToList())
        {
            if (!now.TryGetValue(id, out SceneBuilding? b))
            {
                leafIds[leaf].Remove(id);
                touched.Add(leaf);
                changed++;
            }
            else if (oldPrints[id.ToString(CultureInfo.InvariantCulture)]?.GetValue<string>() != Fingerprint(b))
            {
                touched.Add(leaf);
                changed++;
            }
        }

        List<int> leaves = [.. Enumerable.Range(1, count - 1).Where(i => children[i].Count == 0 && leafIds[i].Count + (touched.Contains(i) ? 1 : 0) > 0)];
        foreach (SceneBuilding added in buildings.Where(b => !leafOf.ContainsKey(b.Id)))
        {
            (double lon, double lat) = Centre(added);
            int nearest = leaves.OrderBy(i => Distance(oldNodes, i, lon, lat)).FirstOrDefault(-1);
            if (nearest < 0)
            {
                return null;
            }

            leafIds[nearest].Add(added.Id);
            touched.Add(nearest);
            changed++;
        }

        if (changed == 0)
        {
            return new Updated(previous, 0, 0);
        }

        if (changed > wholeAbove * Math.Max(1, leafOf.Count) || touched.Any(i => leafIds[i].Count == 0 || leafIds[i].Count > 2 * MaximumPerNode))
        {
            return null;
        }

        // The touched leaves and every node above them.
        HashSet<int> rewrite = [];
        foreach (int leaf in touched)
        {
            for (int? at = leaf; at is { } i; at = parents[i])
            {
                rewrite.Add(i);
            }
        }

        List<long> Subtree(int i) => children[i].Count == 0 ? leafIds[i] : [.. children[i].SelectMany(Subtree)];
        List<(SceneField Field, int Index)> stored = [.. fields.Select((f, i) => (f, i)).Where(p => Buffered(p.f.Type))];

        using MemoryStream package = new();
        PackageWriter zip = new(package);
        JsonArray pageNodes = [];
        HashSet<string> copied = [];

        for (int i = 0; i < count; i++)
        {
            JsonObject json;
            if (!rewrite.Contains(i))
            {
                json = oldNodes[i].DeepClone().AsObject();
                foreach (ZipArchiveEntry entry in old.Entries.Where(e => e.FullName.StartsWith($"nodes/{i}/", StringComparison.Ordinal)))
                {
                    zip.Add(entry.FullName, Read(entry));
                    copied.Add(entry.FullName);
                }
            }
            else
            {
                List<SceneBuilding> mine = [.. Subtree(i).Select(id => now[id])];
                List<Mesh> meshes = children[i].Count == 0 ? [.. mine.Select(Build).Where(m => m.Points.Count > 0)] : [];
                Box box = children[i].Count == 0 ? BoxOf(meshes) : BoxOf(mine.SelectMany(Corners));
                json = new JsonObject { ["index"] = i, ["obb"] = box.Json() };
                if (parents[i] is { } parent)
                {
                    json["parentIndex"] = parent;
                }

                if (children[i].Count > 0)
                {
                    json["children"] = new JsonArray([.. children[i].Select(c => (JsonNode)c)]);
                }

                List<Mesh> drawn = i == 0 ? [] : children[i].Count == 0 ? meshes : CoarseOf(mine);
                if (drawn.Count > 0)
                {
                    (byte[] geometry, int vertices) = Geometry(drawn, box, colour);
                    zip.Add($"nodes/{i}/geometries/0.bin.gz", Gzip(geometry));
                    AddAttributes(zip, i, [.. drawn.Select(m => m.Building.Id)], [.. drawn.Select(m => m.Building.Values)], stored);
                    json["lodThreshold"] = children[i].Count == 0 ? 20_000_000.0 : CoarseThreshold;
                    json["mesh"] = new JsonObject
                    {
                        ["material"] = new JsonObject { ["definition"] = 0 },
                        ["geometry"] = new JsonObject
                        {
                            ["definition"] = 0, ["resource"] = i, ["vertexCount"] = vertices, ["featureCount"] = drawn.Count,
                        },
                        ["attribute"] = new JsonObject { ["resource"] = i },
                    };
                }
            }

            pageNodes.Add(json);
            if (pageNodes.Count == NodesPerPage || i == count - 1)
            {
                zip.Add($"nodepages/{i / NodesPerPage}.json.gz", Gzip(Utf8(new JsonObject { ["nodes"] = pageNodes })));
                pageNodes = [];
            }
        }

        List<SceneBuilding> all = [.. leafIds.SelectMany(ids => ids).Select(id => now[id])];
        for (int k = 1; k <= stored.Count; k++)
        {
            zip.Add($"statistics/f_{k}/0.json.gz", Gzip(Utf8(Statistics([.. all.Select(b => b.Values)], stored[k - 1]))));
        }

        zip.Add("3dSceneLayer.json.gz", Gzip(Utf8(Layer(name, stored, BoxOf(all.SelectMany(Corners)), colour, updated))));
        zip.Add("metadata.json", Utf8(new JsonObject { ["I3SVersion"] = "1.7", ["nodeCount"] = count }));
        zip.Add(PlanEntry, Gzip(Utf8(Plan(
            [.. Enumerable.Range(0, count).Select(i => (parents[i], (IReadOnlyList<int>)children[i], (IReadOnlyList<long>)(children[i].Count == 0 && i > 0 ? leafIds[i] : [])))],
            all))));
        zip.Finish();
        return new Updated(package.ToArray(), rewrite.Count(i => i > 0), changed);
    }

    /// <summary>A building's corners at its base and its top, for a box — without making its triangles.</summary>
    private static IEnumerable<(double Lon, double Lat, double Z)> Corners(SceneBuilding b)
    {
        foreach (var rings in b.Polygons)
        {
            foreach ((double lon, double lat) in rings.SelectMany(r => r))
            {
                yield return (lon, lat, b.Base);
                yield return (lon, lat, b.Base + b.Height);
            }
        }
    }

    private static (double Lon, double Lat) Centre(SceneBuilding b)
    {
        (double Lon, double Lat)[] points = [.. b.Polygons.SelectMany(r => r).SelectMany(r => r)];
        return points.Length == 0 ? (0, 0) : (points.Average(p => p.Lon), points.Average(p => p.Lat));
    }

    private static double Distance(Dictionary<int, JsonObject> nodes, int i, double lon, double lat)
    {
        if (!nodes.TryGetValue(i, out JsonObject? node) || node["obb"]?["center"] is not JsonArray c)
        {
            return double.MaxValue;
        }

        double dx = (c[0]!.GetValue<double>() - lon) * Math.Cos(lat * Math.PI / 180), dy = c[1]!.GetValue<double>() - lat;
        return (dx * dx) + (dy * dy);
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static JsonNode? ReadJson(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using GZipStream gz = new(stream, CompressionMode.Decompress);
        return JsonNode.Parse(gz);
    }
}
