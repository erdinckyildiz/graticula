using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Graticula.Geometries;

/// <summary>One datum transformation in the register — ADR-160.</summary>
/// <param name="Authority">EPSG or ESRI.</param>
/// <param name="Code">Its code, which ArcGIS calls its wkid.</param>
/// <param name="Name">Its name in the register.</param>
/// <param name="Source">The geographic reference it starts from.</param>
/// <param name="Target">The geographic reference it ends in.</param>
/// <param name="Accuracy">Its accuracy in metres, when the register gives one.</param>
/// <param name="Area">Where it applies, in degrees.</param>
/// <param name="NeedsGrid">Whether applying it reads a grid file.</param>
public sealed record DatumTransformation(
    string Authority, int Code, string Name, int Source, int Target, double? Accuracy, Envelope Area, bool NeedsGrid)
{
    /// <summary>How PostGIS names it to <c>ST_TransformPipeline</c>.</summary>
    public string Urn => $"urn:ogc:def:coordinateOperation:{Authority}::{Code.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>A transformation used one way or the other.</summary>
/// <param name="Transformation">The transformation.</param>
/// <param name="Forward">Whether it is applied from its source to its target.</param>
public sealed record TransformationStep(DatumTransformation Transformation, bool Forward);

/// <summary>
/// A way from one geographic reference to another: one transformation, or two through WGS 84, as ArcGIS's
/// <c>geoTransforms</c> — ADR-160.
/// </summary>
/// <param name="Steps">The transformations, in the order applied.</param>
public sealed record TransformationPath(IReadOnlyList<TransformationStep> Steps)
{
    /// <summary>The sum of the steps' accuracies, or null when any is unknown.</summary>
    public double? Accuracy => Steps.All(s => s.Transformation.Accuracy is not null)
        ? Steps.Sum(s => s.Transformation.Accuracy!.Value)
        : null;

    /// <summary>Whether any step reads a grid.</summary>
    public bool NeedsGrid => Steps.Any(s => s.Transformation.NeedsGrid);
}

/// <summary>The register read, and the ways between two references ranked — ADR-160.</summary>
public static partial class TransformationRegister
{
    /// <summary>WGS 84, through which two references without a transformation of their own are joined.</summary>
    public const int Wgs84 = 4326;

    private static readonly Lazy<DatumTransformation[]> All = new(Read);

    private static readonly Lazy<Dictionary<int, DatumTransformation[]>> ByReference = new(() =>
        All.Value.SelectMany(t => new[] { (t.Source, t), (t.Target, t) })
            .GroupBy(p => p.Item1)
            .ToDictionary(g => g.Key, g => g.Select(p => p.t).Distinct().ToArray()));

    /// <summary>How many transformations the register holds.</summary>
    public static int Count => All.Value.Length;

    /// <summary>A transformation by its code, or null.</summary>
    /// <param name="code">The code — ArcGIS's wkid.</param>
    /// <returns>The transformation.</returns>
    public static DatumTransformation? Find(int code) => All.Value.FirstOrDefault(t => t.Code == code);

    /// <summary>
    /// The ways from one geographic reference to another: each transformation between them, either way round,
    /// and — when neither is WGS 84 — each pair through it. Given an area, those that apply there, most accurate
    /// first; without one, the widest area of use first, then the most accurate. Unknown accuracy ranks last;
    /// ties put one step before two, then the lower code first.
    /// </summary>
    /// <param name="from">The geographic reference the data is in.</param>
    /// <param name="to">The geographic reference it is going to.</param>
    /// <param name="area">Where the data is, in degrees, to keep only transformations that apply there; null for anywhere.</param>
    /// <returns>The ways, ranked; empty when the two are the same or nothing joins them.</returns>
    public static List<TransformationPath> Paths(int from, int to, Envelope? area)
    {
        if (from == to)
        {
            return [];
        }

        List<TransformationPath> paths = [.. Between(from, to, area).Select(s => new TransformationPath([s]))];

        if (from != Wgs84 && to != Wgs84)
        {
            // Two legs through WGS 84: the best few of each, so a pair of well-served references does not
            // become hundreds of combinations.
            List<TransformationStep> first = [.. Between(from, Wgs84, area).Take(5)];
            List<TransformationStep> second = [.. Between(Wgs84, to, area).Take(5)];
            paths.AddRange(first.SelectMany(a => second.Select(b => new TransformationPath([a, b]))));
        }

        // Without a place, the transformation meant for the whole of the references' ground comes first — a grid
        // for one region is the most accurate there and wrong anywhere else; with one, the most accurate there.
        IOrderedEnumerable<TransformationPath> ordered = area is null
            ? paths.OrderByDescending(p => p.Steps.Min(s => s.Transformation.Area.Width * s.Transformation.Area.Height))
                .ThenBy(p => p.Accuracy ?? double.MaxValue)
            : paths.OrderBy(p => p.Accuracy ?? double.MaxValue);

        return [.. ordered.ThenBy(p => p.Steps.Count).ThenBy(p => p.Steps[0].Transformation.Code)];
    }

    /// <summary>The transformations joining two references directly, ranked, each used the way it goes.</summary>
    private static IEnumerable<TransformationStep> Between(int from, int to, Envelope? area) =>
        (ByReference.Value.TryGetValue(from, out DatumTransformation[]? touching) ? touching : [])
            .Where(t => (t.Source == from && t.Target == to) || (t.Source == to && t.Target == from))
            .Where(t => area is not { } a || (t.Area.MinX <= a.MaxX && t.Area.MaxX >= a.MinX && t.Area.MinY <= a.MaxY && t.Area.MaxY >= a.MinY))
            .OrderBy(t => t.Accuracy ?? double.MaxValue)
            .ThenBy(t => t.Code)
            .Select(t => new TransformationStep(t, t.Source == from));

    private static DatumTransformation[] Read()
    {
        List<DatumTransformation> read = [];

        foreach (string line in Lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] f = line.Split('|', 11);
            double D(int i) => double.Parse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture);
            read.Add(new DatumTransformation(
                f[0] == "E" ? "EPSG" : "ESRI",
                int.Parse(f[1], CultureInfo.InvariantCulture),
                f[10],
                int.Parse(f[2], CultureInfo.InvariantCulture),
                int.Parse(f[3], CultureInfo.InvariantCulture),
                f[4].Length == 0 ? null : D(4),
                new Envelope(D(5), D(6), D(7), D(8)),
                f[9] == "g"));
        }

        return [.. read];
    }
}
