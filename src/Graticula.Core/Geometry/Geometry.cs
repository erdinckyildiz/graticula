namespace Graticula.Geometries;

/// <summary>
/// A geometry, in OGC Simple Features terms. See <see cref="GeometryKind"/> for
/// why those names and not ArcGIS's.
/// </summary>
/// <remarks>
/// <para>
/// <b>One object per geometry, never one per coordinate.</b> That distinction is
/// the whole lesson of <c>benchmarks/mvt-generation</c>: the adopted library
/// models a coordinate as a class, so a 556,728-vertex tile became 556,728 heap
/// objects and A-037 measured the result at 80.9% GC pause on 18% CPU. Here a
/// 200,000-vertex polygon is one <see cref="Polygon"/>, a few ring objects, and
/// one <c>double[]</c>.
/// </para>
/// <para>
/// <b>No coordinate reference system on the geometry.</b> PostGIS carries SRID on
/// the column and so do we — CRS is a property of a layer, not of every shape in
/// it. Putting it here would mean carrying and comparing it a few million times
/// per tile to answer a question the layer already answered once.
/// </para>
/// <para>
/// The hierarchy is closed: <see cref="Geometry"/> cannot be derived from outside
/// this assembly, so exhaustive handling of <see cref="Kind"/> stays exhaustive.
/// </para>
/// </remarks>
public abstract class Geometry
{
    private Envelope _envelope;
    private bool _envelopeComputed;

    /// <summary>Closed hierarchy: only this assembly may add geometry types.</summary>
    private protected Geometry()
    {
    }

    /// <summary>Which geometry this is, without a type test.</summary>
    public abstract GeometryKind Kind { get; }

    /// <summary><see langword="true"/> when the geometry holds no coordinates.</summary>
    public abstract bool IsEmpty { get; }

    /// <summary>Total number of coordinates across every part and ring.</summary>
    public abstract int CoordinateCount { get; }

    /// <summary>
    /// The bounding rectangle, computed once and cached. Geometries are
    /// immutable, so the cache cannot go stale.
    /// </summary>
    public Envelope Envelope
    {
        get
        {
            if (!_envelopeComputed)
            {
                _envelope = ComputeEnvelope();
                _envelopeComputed = true;
            }

            return _envelope;
        }
    }

    /// <summary>Computes the bounding rectangle. Called at most once.</summary>
    protected abstract Envelope ComputeEnvelope();
}
