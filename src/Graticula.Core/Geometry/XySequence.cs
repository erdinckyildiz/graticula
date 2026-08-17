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
/// This is Tier 1 (<c>docs/build-vs-adopt-policy.md</c> §4) and
/// <c>ADR-003</c> §6a tier 2 — ours, on flat arrays. It is deliberately not a
/// geometry: it carries no ring semantics, no validity notion and no coordinate
/// reference system. Those belong to the types built on it.
/// </para>
/// </remarks>
public readonly struct XySequence : IEquatable<XySequence>
{
    private readonly double[]? _xy;
    private readonly int _offset;

    /// <summary>An empty sequence. Allocates nothing.</summary>
    public static XySequence Empty => default;

    private XySequence(double[]? xy, int offset, int count)
    {
        _xy = xy;
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

    /// <summary>The x ordinate at <paramref name="index"/>.</summary>
    public double X(int index)
    {
        ThrowIfOutOfRange(index);
        return _xy![((_offset + index) * 2)];
    }

    /// <summary>The y ordinate at <paramref name="index"/>.</summary>
    public double Y(int index)
    {
        ThrowIfOutOfRange(index);
        return _xy![((_offset + index) * 2) + 1];
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

        return new XySequence(_xy, _offset + start, count);
    }

    /// <summary>
    /// The underlying interleaved ordinates for this view, for hot loops that
    /// want to avoid per-coordinate calls. Length is <see cref="Count"/> × 2.
    /// </summary>
    public ReadOnlySpan<double> AsSpan() =>
        _xy is null ? ReadOnlySpan<double>.Empty : _xy.AsSpan(_offset * 2, Count * 2);

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
    public bool Equals(XySequence other) => AsSpan().SequenceEqual(other.AsSpan());

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
    public override string ToString() => $"XySequence[{Count}]";
}
