using System;
using System.Collections.Generic;

namespace Graticula.Cartography;

/// <summary>
/// One feature's geometry in pixel coordinates, in a buffer that is reused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reused, and that is the whole design.</b>
/// [ADR-004](../../../docs/adr/ADR-004-rendering-engine.md) §0's surviving
/// objection to server-side rendering is an allocation measurement —
/// <c>benchmarks/mvt-generation</c> run 3 found 80.9% GC pause at 18% CPU on a
/// lighter workload. A path type that allocated an array per ring per feature
/// would make that objection right on the first map. This one allocates two lists,
/// grows them to the largest feature in the request, and is cleared between
/// features.
/// </para>
/// <para>
/// <b>Flat interleaved coordinates rather than a point type</b>, for the same
/// reason and because it is what the port hands the rasteriser: a span of doubles
/// crosses the boundary without a copy.
/// </para>
/// </remarks>
public sealed class PixelPath
{
    private readonly List<double> _xy = [];
    private readonly List<Figure> _figures = [];

    private int _start = -1;

    /// <summary>One ring or line within the path.</summary>
    /// <param name="Start">Index of its first coordinate pair.</param>
    /// <param name="Count">How many coordinate pairs it has.</param>
    /// <param name="Closed">Whether it is a ring rather than an open line.</param>
    public readonly record struct Figure(int Start, int Count, bool Closed);

    /// <summary>The figures, in the order they were added.</summary>
    public IReadOnlyList<Figure> Figures => _figures;

    /// <summary>Whether nothing has been added.</summary>
    public bool IsEmpty => _figures.Count == 0;

    /// <summary>
    /// Every coordinate, interleaved as x, y, x, y.
    /// </summary>
    /// <remarks>
    /// A figure's coordinates start at <c>Figure.Start * 2</c> and run for
    /// <c>Figure.Count * 2</c> doubles.
    /// </remarks>
    public ReadOnlySpan<double> Coordinates => System.Runtime.InteropServices.CollectionsMarshal
        .AsSpan(_xy);

    /// <summary>Empties the path without giving up the buffers it grew.</summary>
    public void Reset()
    {
        _xy.Clear();
        _figures.Clear();
        _start = -1;
    }

    /// <summary>Starts a figure.</summary>
    /// <param name="closed">Whether it is a ring.</param>
    public void Begin(bool closed)
    {
        End();

        _start = _xy.Count / 2;
        _figures.Add(new Figure(_start, 0, closed));
    }

    /// <summary>Adds a coordinate to the open figure.</summary>
    /// <param name="x">Pixel x.</param>
    /// <param name="y">Pixel y.</param>
    /// <exception cref="InvalidOperationException">No figure has been begun.</exception>
    public void Add(double x, double y)
    {
        if (_start < 0)
        {
            throw new InvalidOperationException(
                "Begin a figure before adding coordinates to it.");
        }

        _xy.Add(x);
        _xy.Add(y);

        Figure current = _figures[^1];
        _figures[^1] = current with { Count = current.Count + 1 };
    }

    /// <summary>
    /// Closes the open figure, discarding it if it has too few coordinates to draw.
    /// </summary>
    /// <remarks>
    /// <b>Discarded rather than kept, because a degenerate figure is a rasteriser
    /// bug waiting for the right input.</b> A ring of two points and a line of one
    /// are both producible by clipping, and what they draw is undefined rather than
    /// nothing.
    /// </remarks>
    public void End()
    {
        if (_start < 0)
        {
            return;
        }

        Figure current = _figures[^1];
        int minimum = current.Closed ? 3 : 2;

        if (current.Count < minimum)
        {
            _xy.RemoveRange(current.Start * 2, current.Count * 2);
            _figures.RemoveAt(_figures.Count - 1);
        }

        _start = -1;
    }
}
