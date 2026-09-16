using System;

namespace Graticula.Geometries;

/// <summary>
/// An ordered sequence of 2D coordinates held as one flat, interleaved
/// <c>double[]</c> — <c>x0, y0, x1, y1, …</c> — with zero-copy slicing.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because of a measurement, not a preference.
/// <c>benchmarks/mvt-generation/RESULTS.md</c> found that the adopted geometry
/// library represents a coordinate as a <c>class</c>, so a 556,728-vertex tile
/// becomes 556,728 heap objects before the first distance calculation. Moving
/// the two hot-path primitives onto flat arrays took a z12 tile from
/// <b>404 MB allocated to 204 MB</b> and halved gen0 collections.
/// </para>
/// <para>
/// That matters more than either primitive did, because <c>A-037</c> established
/// that <b>allocation, not CPU, is the binding constraint</b> — 80.9% GC pause at
/// 18% CPU utilisation under concurrency. A profiler showing only CPU reports an
/// idle worker and explains nothing.
/// </para>
/// <para>
/// <b>Slicing does not copy.</b> A polygon's shell and holes are slices of one
/// buffer, so walking a multi-ring geometry allocates nothing.
/// </para>
/// <para>
/// <b>Z and M ride beside x and y, not inside them — ADR-077.</b> A sequence may carry an elevation
/// and a measure per coordinate, in their own arrays sharing this view's offset and count. The x/y
/// buffer is untouched by that: <see cref="AsSpan"/> is still <c>x0, y0, x1, y1, …</c>, which is what
/// every hot loop in this repository reads, so a two-dimensional sequence costs one null reference
/// more than it did and nothing that reads it had to change. Interleaving <c>x, y, z</c> instead was
/// the alternative, and it would have turned every one of those loops into a silent misreading of a
/// three-dimensional shape.
/// </para>
/// <para>
/// This is Tier 1 (<c>docs/build-vs-adopt-policy.md</c> §4) and
/// <c>ADR-003</c> §6a tier 2 — ours, on flat arrays. It is deliberately not a
/// geometry: it carries no ring semantics, no validity notion and no coordinate
/// reference system. Those belong to the types built on it.
/// </para>
/// </remarks>
public readonly struct XySequence : IEquatable<XySequence>
{
    /// <summary>
    /// The x/y buffer itself for a flat sequence, or a <see cref="Beside"/> holding it with Z and M.
    /// </summary>
    /// <remarks>
    /// <b>One field, so a flat sequence is exactly the size it was.</b> The first version added a second
    /// reference beside the buffer, which grew this struct from 16 bytes to 24 and every ring and line
    /// that holds one by eight: measured on 50,000 two-ring polygons decoded from WKB and written as
    /// ArcGIS JSON, 53.8 MB became 54.6 MB, for a capability no surface serves yet. A type test on a sealed
    /// class is what reading the buffer costs instead.
    /// </remarks>
    private readonly object? _data;
    private readonly int _offset;

    /// <summary>The x/y buffer and, beside it, the ordinates beyond x and y.</summary>
    private sealed class Beside(double[] xy, double[]? z, double[]? m)
    {
        public double[] Xy { get; } = xy;

        public double[]? Z { get; } = z;

        public double[]? M { get; } = m;
    }

    private double[]? Buffer => _data is Beside beside ? beside.Xy : System.Runtime.CompilerServices.Unsafe.As<double[]>(_data);

    private Beside? Extra => _data as Beside;

    /// <summary>An empty sequence. Allocates nothing.</summary>
    public static XySequence Empty => default;

    private XySequence(object? data, int offset, int count)
    {
        _data = data;
        _offset = offset;
        Count = count;
    }

    /// <summary>Number of coordinates — half the number of doubles.</summary>
    public int Count { get; }

    /// <summary><see langword="true"/> when the sequence holds no coordinates.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Wraps an interleaved <c>x, y</c> buffer without copying it.
    /// </summary>
    /// <param name="interleaved">
    /// Coordinates as <c>x0, y0, x1, y1, …</c>. The array is referenced, not
    /// copied, and must not be mutated afterwards — this type presents itself as
    /// immutable and cannot enforce that on a caller who keeps the array.
    /// </param>
    /// <exception cref="ArgumentException">The length is odd.</exception>
    public static XySequence Wrap(double[] interleaved)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if ((interleaved.Length & 1) != 0)
        {
            throw new ArgumentException(
                $"An interleaved coordinate buffer must have even length; got {interleaved.Length}.",
                nameof(interleaved));
        }

        return new XySequence(interleaved, 0, interleaved.Length / 2);
    }

    /// <summary>
    /// Wraps an interleaved <c>x, y</c> buffer and, beside it, an elevation and a measure per coordinate
    /// — ADR-077. Nothing is copied.
    /// </summary>
    /// <param name="interleaved">Coordinates as <c>x0, y0, x1, y1, …</c>.</param>
    /// <param name="z">One elevation per coordinate, or null for none.</param>
    /// <param name="m">One measure per coordinate, or null for none.</param>
    /// <exception cref="ArgumentException">A length does not match the coordinate count.</exception>
    public static XySequence Wrap(double[] interleaved, double[]? z, double[]? m)
    {
        XySequence xy = Wrap(interleaved);

        if (z is not null && z.Length != xy.Count)
        {
            throw new ArgumentException(
                $"There are {xy.Count} coordinates and {z.Length} elevations; there must be one per coordinate.", nameof(z));
        }

        if (m is not null && m.Length != xy.Count)
        {
            throw new ArgumentException(
                $"There are {xy.Count} coordinates and {m.Length} measures; there must be one per coordinate.", nameof(m));
        }

        return z is null && m is null ? xy : new XySequence(new Beside(interleaved, z, m), 0, xy.Count);
    }

    /// <summary>Which ordinates beyond x and y this sequence carries.</summary>
    public GeometryOrdinates Ordinates =>
        (Extra?.Z is null ? GeometryOrdinates.None : GeometryOrdinates.Z)
        | (Extra?.M is null ? GeometryOrdinates.None : GeometryOrdinates.M);

    /// <summary>Whether each coordinate has an elevation.</summary>
    public bool HasZ => Extra?.Z is not null;

    /// <summary>Whether each coordinate has a measure.</summary>
    public bool HasM => Extra?.M is not null;

    /// <summary>The elevation at <paramref name="index"/>.</summary>
    /// <exception cref="InvalidOperationException">The sequence carries no elevation.</exception>
    public double Z(int index)
    {
        ThrowIfOutOfRange(index);
        return (Extra?.Z ?? throw new InvalidOperationException("This sequence carries no Z ordinate."))[_offset + index];
    }

    /// <summary>The measure at <paramref name="index"/>.</summary>
    /// <exception cref="InvalidOperationException">The sequence carries no measure.</exception>
    public double M(int index)
    {
        ThrowIfOutOfRange(index);
        return (Extra?.M ?? throw new InvalidOperationException("This sequence carries no M ordinate."))[_offset + index];
    }

    /// <summary>This view's elevations, one per coordinate, or empty when it carries none.</summary>
    public ReadOnlySpan<double> ZSpan() =>
        Extra?.Z is { } z ? z.AsSpan(_offset, Count) : ReadOnlySpan<double>.Empty;

    /// <summary>This view's measures, one per coordinate, or empty when it carries none.</summary>
    public ReadOnlySpan<double> MSpan() =>
        Extra?.M is { } m ? m.AsSpan(_offset, Count) : ReadOnlySpan<double>.Empty;

    /// <summary>The x ordinate at <paramref name="index"/>.</summary>
    public double X(int index)
    {
        ThrowIfOutOfRange(index);
        return Buffer![((_offset + index) * 2)];
    }

    /// <summary>The y ordinate at <paramref name="index"/>.</summary>
    public double Y(int index)
    {
        ThrowIfOutOfRange(index);
        return Buffer![((_offset + index) * 2) + 1];
    }

    /// <summary>
    /// A view of <paramref name="count"/> coordinates starting at
    /// <paramref name="start"/>. <b>No copy is made</b> — this is how a polygon's
    /// rings share one buffer.
    /// </summary>
    public XySequence Slice(int start, int count)
    {
        if ((uint)start > (uint)Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start), start, $"Start must be in [0, {Count}].");
        }

        if ((uint)count > (uint)(Count - start))
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, $"Count must be in [0, {Count - start}] for start {start}.");
        }

        return new XySequence(_data, _offset + start, count);
    }

    /// <summary>
    /// The underlying interleaved ordinates for this view, for hot loops that
    /// want to avoid per-coordinate calls. Length is <see cref="Count"/> × 2.
    /// </summary>
    public ReadOnlySpan<double> AsSpan() =>
        Buffer is null ? ReadOnlySpan<double>.Empty : Buffer.AsSpan(_offset * 2, Count * 2);

    /// <summary>
    /// Copies this view into a fresh buffer. Named to make the allocation
    /// obvious at the call site, because <see cref="Slice"/> not copying is the
    /// point of this type.
    /// </summary>
    public double[] ToInterleavedArray() => AsSpan().ToArray();

    private void ThrowIfOutOfRange(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"Index must be in [0, {Count - 1}]; the sequence holds {Count}.");
        }
    }

    /// <summary>
    /// Compares by coordinate value, not by buffer identity — two sequences over
    /// different arrays holding the same numbers are equal.
    /// </summary>
    public bool Equals(XySequence other) =>
        AsSpan().SequenceEqual(other.AsSpan())
        && Ordinates == other.Ordinates
        && ZSpan().SequenceEqual(other.ZSpan())
        && MSpan().SequenceEqual(other.MSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is XySequence other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Endpoints and length, not every ordinate: this exists so the type can
        // sit in a dictionary, and hashing a 200,000-vertex ring to do it would
        // reintroduce the cost the type was written to remove.
        if (Count == 0)
        {
            return 0;
        }

        return HashCode.Combine(Count, X(0), Y(0), X(Count - 1), Y(Count - 1));
    }

    /// <summary>Value equality. See <see cref="Equals(XySequence)"/>.</summary>
    public static bool operator ==(XySequence left, XySequence right) => left.Equals(right);

    /// <summary>Value inequality. See <see cref="Equals(XySequence)"/>.</summary>
    public static bool operator !=(XySequence left, XySequence right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => Ordinates == GeometryOrdinates.None
        ? $"XySequence[{Count}]"
        : $"XySequence[{Count}, {Ordinates}]";
}
