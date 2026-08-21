using System;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>
/// Where each canvas pixel lands on the ground, for a request in another reference.
/// </summary>
/// <remarks>
/// <para>
/// <b>A grid of control points and bilinear interpolation between them, which is what
/// every raster warp does and is an approximation.</b> Projecting all
/// <c>width × height</c> pixel centres would be exact and would ask the projection
/// engine for a million points per request — and this engine is PostGIS, so that is a
/// round trip carrying a million coordinates.
/// [ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) condition 2
/// asks what the approximation costs, and the answer is
/// [benchmarks/raster-warp/RESULTS.md](../../../benchmarks/raster-warp/RESULTS.md)
/// rather than a claim.
/// </para>
/// <para>
/// <b>The grid is in canvas space, not in ground space.</b> Interpolating between two
/// ground positions assumes the projection is close to linear between them, and how
/// close that is depends on how far apart they are <em>on the screen</em> — which is
/// the thing that stays constant as a client zooms. A ground-spaced grid would be
/// dense where the map is zoomed out and sparse where it is zoomed in, which is
/// backwards.
/// </para>
/// <para>
/// <b>This type does no projecting.</b> It is handed the control points already
/// projected, because the transformation is <see cref="IProjector"/>'s and that port
/// is the tier boundary. What lives here is the choice of how many points, where they
/// go, and what happens between them — the part that decides how the picture looks.
/// </para>
/// </remarks>
public sealed class CoverageWarp
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly int _steps;
    private readonly int _width;
    private readonly int _height;

    /// <summary>Builds a warp from a projected control grid.</summary>
    /// <param name="width">The canvas width in pixels.</param>
    /// <param name="height">The canvas height in pixels.</param>
    /// <param name="steps">How many cells the grid is divided into, per axis.</param>
    /// <param name="groundX">
    /// The eastings of the <c>(steps + 1)²</c> control points, row by row from the top.
    /// </param>
    /// <param name="groundY">The northings of the same points, in the same order.</param>
    public CoverageWarp(int width, int height, int steps, double[] groundX, double[] groundY)
    {
        ArgumentNullException.ThrowIfNull(groundX);
        ArgumentNullException.ThrowIfNull(groundY);

        if (steps < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steps), steps, "A grid has at least one cell per axis.");
        }

        int wanted = (steps + 1) * (steps + 1);

        if (groundX.Length != wanted || groundY.Length != wanted)
        {
            throw new ArgumentException(
                $"A {steps}-step grid needs {wanted} control points and was given "
                + $"{groundX.Length}.",
                nameof(groundX));
        }

        _width = width;
        _height = height;
        _steps = steps;
        _x = groundX;
        _y = groundY;
    }

    /// <summary>
    /// How many cells a canvas of this size is divided into, per axis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A cell about sixty-four pixels across, and the number is measured rather than
    /// chosen.</b> `benchmarks/raster-warp` walks the grid density against the error it
    /// leaves and against what it costs; sixty-four is where the error falls below a
    /// tenth of a pixel for the projections this server sees and the point count is
    /// still in the low hundreds.
    /// </para>
    /// <para>
    /// <b>Bounded at both ends.</b> Fewer than two cells cannot interpolate at all, and
    /// more than sixty-four per axis is 4,225 control points — past the point where the
    /// round trip costs more than it saves.
    /// </para>
    /// </remarks>
    /// <param name="width">Canvas width in pixels.</param>
    /// <param name="height">Canvas height in pixels.</param>
    /// <returns>The number of cells per axis.</returns>
    public static int StepsFor(int width, int height) =>
        Math.Clamp((Math.Max(width, height) + 63) / 64, 2, 64);

    /// <summary>
    /// The control points a canvas of this size needs, in its own reference.
    /// </summary>
    /// <param name="extent">The requested ground extent.</param>
    /// <param name="width">Canvas width in pixels.</param>
    /// <param name="height">Canvas height in pixels.</param>
    /// <param name="steps">The number of cells per axis.</param>
    /// <returns>The points, row by row from the top, to be projected.</returns>
    /// <remarks>
    /// <b>The grid spans the canvas edge to edge, corners included.</b> A grid that
    /// stopped at the last pixel centre would leave the outermost half-pixel to
    /// extrapolation, and extrapolating a projection is where the error stops being
    /// small.
    /// </remarks>
    public static Point[] ControlPoints(Envelope extent, int width, int height, int steps)
    {
        if (steps < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steps), steps, "A grid has at least one cell per axis.");
        }

        Point[] points = new Point[(steps + 1) * (steps + 1)];

        double spanX = extent.MaxX - extent.MinX;
        double spanY = extent.MaxY - extent.MinY;

        for (int row = 0; row <= steps; row++)
        {
            double v = (double)row / steps;

            for (int column = 0; column <= steps; column++)
            {
                double u = (double)column / steps;

                points[(row * (steps + 1)) + column] = new Point(
                    extent.MinX + (u * spanX),
                    extent.MaxY - (v * spanY));
            }
        }

        return points;
    }

    /// <summary>Where a canvas pixel centre lands, in the coverage's reference.</summary>
    /// <param name="pixelX">Column, from the left.</param>
    /// <param name="pixelY">Row, from the top.</param>
    /// <returns>The ground position.</returns>
    public (double X, double Y) Ground(double pixelX, double pixelY)
    {
        double u = _width <= 0 ? 0 : pixelX / _width * _steps;
        double v = _height <= 0 ? 0 : pixelY / _height * _steps;

        int column = Math.Clamp((int)u, 0, _steps - 1);
        int row = Math.Clamp((int)v, 0, _steps - 1);

        double fx = u - column;
        double fy = v - row;

        int stride = _steps + 1;
        int at = (row * stride) + column;

        double x = Bilinear(_x, at, stride, fx, fy);
        double y = Bilinear(_y, at, stride, fx, fy);

        return (x, y);
    }

    /// <summary>
    /// Redraws a window of painted coverage pixels onto the canvas, pixel by pixel.
    /// </summary>
    /// <param name="source">The painted window, row by row.</param>
    /// <param name="sourceWidth">Its width in pixels.</param>
    /// <param name="sourceHeight">Its height in pixels.</param>
    /// <param name="originX">The ground easting of the window's left edge.</param>
    /// <param name="originY">The ground northing of the window's top edge.</param>
    /// <param name="perPixelX">Ground units per window pixel, west to east.</param>
    /// <param name="perPixelY">Ground units per window pixel, north to south.</param>
    /// <returns>A canvas-sized buffer, transparent where nothing was sampled.</returns>
    /// <remarks>
    /// <para>
    /// <b>Nearest neighbour, and that is a decision rather than a shortcut.</b> The
    /// alternative is to interpolate between four coverage pixels, which is right for
    /// continuous data — elevation, temperature — and wrong for anything categorical,
    /// where the average of *forest* and *water* is a class that does not exist. This
    /// server cannot tell the two apart from the file, and inventing values is the worse
    /// of the two errors: a slightly blockier image is visibly approximate, and a
    /// plausible wrong class is not.
    /// </para>
    /// <para>
    /// <b>Outside the window is left transparent rather than clamped to its edge.</b>
    /// Clamping smears the last row of real pixels across the margin, which reads as
    /// data. A client asking for more ground than the coverage has should see where it
    /// stops.
    /// </para>
    /// </remarks>
    public Rgba[] Resample(
        ReadOnlySpan<Rgba> source,
        int sourceWidth,
        int sourceHeight,
        double originX,
        double originY,
        double perPixelX,
        double perPixelY)
    {
        Rgba[] canvas = new Rgba[_width * _height];

        if (sourceWidth <= 0 || sourceHeight <= 0 || perPixelX == 0 || perPixelY == 0)
        {
            return canvas;
        }

        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                // The centre of the pixel, not its corner: sampling at the corner
                // shifts the whole image half a pixel up and to the left, which is the
                // classic off-by-a-half that looks like nothing until two layers are
                // drawn over each other.
                (double groundX, double groundY) = Ground(x + 0.5, y + 0.5);

                int column = (int)Math.Floor((groundX - originX) / perPixelX);
                int row = (int)Math.Floor((originY - groundY) / perPixelY);

                if (column < 0 || column >= sourceWidth || row < 0 || row >= sourceHeight)
                {
                    continue;
                }

                canvas[(y * _width) + x] = source[(row * sourceWidth) + column];
            }
        }

        return canvas;
    }

    private static double Bilinear(double[] values, int at, int stride, double fx, double fy)
    {
        double top = values[at] + ((values[at + 1] - values[at]) * fx);

        double bottom = values[at + stride]
            + ((values[at + stride + 1] - values[at + stride]) * fx);

        return top + ((bottom - top) * fy);
    }
}
