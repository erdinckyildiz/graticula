using System;
using System.Collections.Generic;

namespace Graticula.Cartography;

/// <summary>
/// A pie: several numbers turned into wedges, and each wedge into a ring the canvas can fill.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-052](../../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md) §3.16.</b>
/// A chart renderer draws a small chart at each feature instead of a symbol. It is the last of
/// the three that were called blocked and were not, and like the other two it needed no new
/// primitive: an arc tessellated into a ring is a polygon, and <see cref="IMapCanvas.FillArea"/>
/// fills polygons.
/// </para>
/// <para>
/// <b>A wedge is a triangle fan, and the number of steps comes from the radius.</b> A pie eight
/// pixels across needs a handful of segments to look round and one two hundred across needs
/// dozens; fixing the count either wastes work on the small ones or shows the corners on the
/// large. The rule below keeps every chord under half a pixel of its arc, which is below what
/// antialiasing resolves.
/// </para>
/// <para>
/// <b>It starts at twelve o'clock and goes clockwise</b>, which is what every pie chart anybody
/// has read does. Screen y grows downward, so clockwise on screen is the direction a
/// mathematician would call negative, and getting that backwards mirrors the chart — a mistake
/// that looks like a plausible chart of a different arrangement.
/// </para>
/// </remarks>
public static class PieSlices
{
    /// <summary>The most segments one wedge is drawn with.</summary>
    public const int MostSegments = 180;

    /// <summary>Turns values into wedges and adds each as a closed ring.</summary>
    /// <param name="path">The path to add to. It is reset first.</param>
    /// <param name="values">One per slice. Negatives and non-numbers count as nothing.</param>
    /// <param name="x">The pie's centre, pixel x.</param>
    /// <param name="y">Its centre, pixel y.</param>
    /// <param name="radius">Its radius in pixels.</param>
    /// <param name="hole">
    /// The hole in the middle as a fraction of the radius, 0 for a pie and above 0 for a
    /// doughnut.
    /// </param>
    /// <returns>
    /// One entry per value, saying which figure of the path is that slice — or -1 where the
    /// value contributed nothing, so a caller can keep its colours lined up with its fields.
    /// </returns>
    public static IReadOnlyList<int> Wedges(
        PixelPath path,
        IReadOnlyList<double> values,
        double x,
        double y,
        double radius,
        double hole = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(values);

        path.Reset();

        int[] figures = new int[values.Count];

        Array.Fill(figures, -1);

        if (radius <= 0)
        {
            return figures;
        }

        double total = 0;

        foreach (double value in values)
        {
            if (double.IsFinite(value) && value > 0)
            {
                total += value;
            }
        }

        if (total <= 0)
        {
            return figures;
        }

        double inner = Math.Clamp(hole, 0, 0.95) * radius;

        // <b>Half a pixel of sagitta.</b> The distance from a chord to its arc is
        // r(1 - cos(θ/2)); solving for a twentieth of a pixel gives the step below, and the
        // result is a circle whose corners are under what antialiasing can show.
        int steps = (int)Math.Clamp(
            Math.Ceiling(Math.PI / Math.Acos(Math.Max(1 - (0.05 / radius), -1))), 12, MostSegments);

        double from = -Math.PI / 2;

        for (int i = 0; i < values.Count; i++)
        {
            double value = double.IsFinite(values[i]) && values[i] > 0 ? values[i] : 0;

            if (value <= 0)
            {
                continue;
            }

            double sweep = value / total * (Math.PI * 2);
            int segments = Math.Max(2, (int)Math.Ceiling(sweep / (Math.PI * 2) * steps));

            figures[i] = path.Figures.Count;

            path.Begin(closed: true);

            // The outer arc, clockwise on screen: y grows downward, so the angle grows too.
            for (int s = 0; s <= segments; s++)
            {
                double at = from + (sweep * s / segments);

                path.Add(x + (Math.Cos(at) * radius), y + (Math.Sin(at) * radius));
            }

            if (inner > 0)
            {
                // Back along the inner arc, so the ring encloses the doughnut's band.
                for (int s = segments; s >= 0; s--)
                {
                    double at = from + (sweep * s / segments);

                    path.Add(x + (Math.Cos(at) * inner), y + (Math.Sin(at) * inner));
                }
            }
            else
            {
                path.Add(x, y);
            }

            path.End();

            from += sweep;
        }

        return figures;
    }

    /// <summary>
    /// The radius of a pie whose area stands for a total.
    /// </summary>
    /// <remarks>
    /// <b>The same curve as the proportional renderer's, and deliberately the same code
    /// shape.</b> A chart sized by its sum is a proportional symbol whose symbol happens to be a
    /// chart, so it uses area proportionality and Flannery's correction for the same published
    /// reasons — see <c>Cim.AreaExponent</c>. A second sizing law here would mean two renderers
    /// answering *how big is this* differently.
    /// </remarks>
    /// <param name="total">This feature's sum.</param>
    /// <param name="smallestValue">The value that draws at the smallest size.</param>
    /// <param name="smallestSize">That size, as a diameter.</param>
    /// <param name="flannery">Whether to apply Flannery's correction.</param>
    /// <returns>The diameter to draw, never below the smallest.</returns>
    public static double SizeFor(
        double total, double smallestValue, double smallestSize, bool flannery)
    {
        if (!double.IsFinite(total) || total <= 0
            || !double.IsFinite(smallestValue) || smallestValue <= 0
            || !double.IsFinite(smallestSize) || smallestSize <= 0)
        {
            return Math.Max(smallestSize, 0);
        }

        double power = flannery ? 0.5716 : 0.5;

        return smallestSize * Math.Pow(Math.Max(total / smallestValue, 1), power);
    }
}
