using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Testing;

namespace Graticula.Providers.DuckDb.Tests;

/// <summary>A temporary folder of GeoParquet files, deleted afterwards.</summary>
internal sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "graticula-geoparquet-" + Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A native handle still closing on Windows; the temporary directory is the OS's to clear.
        }
    }
}

/// <summary>
/// A projector that moves x by a fixed amount per reference, so a test can see which reference a
/// geometry was moved into and that it was moved at all.
/// </summary>
internal sealed class ShiftingProjector : IProjector
{
    public List<(int From, int To, int Count)> Calls { get; } = [];

    public static double Offset(int srid) => srid == 3857 ? 0 : 1_000_000;

    public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
        IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((fromSrid, toSrid, geometries.Count));
        }

        double dx = Offset(toSrid) - Offset(fromSrid);
        List<Geometry> moved = [];

        foreach (Geometry geometry in geometries)
        {
            moved.Add(Shift(geometry, dx));
        }

        return Task.FromResult<(IReadOnlyList<Geometry>, ProjectionProvenance)>((moved, new ProjectionProvenance("test", null)));
    }

    public Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
        IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Geometry>?>(null);

    public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) => Task.FromResult<Envelope?>(null);

    public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<KnownReference>>([]);

    public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
        Task.FromResult(new ProjectionProvenance("test", null));

    private static Geometry Shift(Geometry geometry, double dx)
    {
        XySequence Move(XySequence s)
        {
            double[] values = s.ToInterleavedArray();

            for (int i = 0; i < values.Length; i += 2)
            {
                values[i] += dx;
            }

            return XySequence.Wrap(values);
        }

        Polygon Area(Polygon p) => new(new LinearRing(Move(p.Shell.Coordinates)), [.. System.Linq.Enumerable.Select(p.Holes, h => new LinearRing(Move(h.Coordinates)))]);

        return geometry switch
        {
            Point p => new Point(p.X + dx, p.Y),
            Polygon p => Area(p),
            LineString l => new LineString(Move(l.Coordinates)),
            MultiPolygon m => new MultiPolygon([.. System.Linq.Enumerable.Select(m.Parts, Area)]),
            _ => throw new NotSupportedException(geometry.GetType().Name),
        };
    }
}

/// <summary>Shapes for fixtures.</summary>
internal static class Shapes
{
    public static Polygon Square(double minX, double minY, double maxX, double maxY) =>
        new(new LinearRing(XySequence.Wrap([minX, minY, maxX, minY, maxX, maxY, minX, maxY, minX, minY])));

    public static Polygon Holed(double minX, double minY, double maxX, double maxY, double inset) =>
        new(
            new LinearRing(XySequence.Wrap([minX, minY, maxX, minY, maxX, maxY, minX, maxY, minX, minY])),
            [new LinearRing(XySequence.Wrap([
                minX + inset, minY + inset, maxX - inset, minY + inset, maxX - inset, maxY - inset,
                minX + inset, maxY - inset, minX + inset, minY + inset]))]);

    public static IEnumerable<(object?[] Values, Geometry? Shape)> Grid(int side)
    {
        // `side` × `side` unit squares a unit apart, ids from 1, and three attributes that make
        // filters and groups interesting: a name, a kind that repeats, and a date.
        long id = 1;

        for (int row = 0; row < side; row++)
        {
            for (int column = 0; column < side; column++)
            {
                double x = column * 2, y = row * 2;

                yield return (
                    [id, $"p{id}", (id % 3) switch { 0 => "field", 1 => "forest", _ => "water" }, (double)id * 1.5,
                        new DateOnly(2026, 1, 1).AddDays((int)id), (int)(id % 7)],
                    Square(x, y, x + 1, y + 1));

                id++;
            }
        }
    }

    public static readonly GeoParquetFixture.Column[] GridColumns =
    [
        new("objectid", "BIGINT"),
        new("name", "VARCHAR"),
        new("kind", "VARCHAR"),
        new("area", "DOUBLE"),
        new("day", "DATE"),
        new("score", "INTEGER"),
    ];
}
