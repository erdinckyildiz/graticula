using System;
using System.Collections.Generic;

namespace Graticula.Cartography;

/// <summary>
/// A density surface: weights splatted into a buffer, then coloured through a ramp.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-052](../../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md) §3.14.</b>
/// A heat map is the one renderer whose answer does not depend on a feature alone — every point
/// contributes to its neighbours' pixels, so it cannot be drawn in the per-feature pass every
/// other renderer uses. It accumulates while the features go past and is composited once at the
/// end, through <see cref="IMapCanvas.DrawImage"/>.
/// </para>
/// <para>
/// <b>It needed no new drawing primitive, and saying so corrected a claim.</b> `DrawImage` has
/// been on the canvas and implemented in the Skia renderer since before this; the refusal message
/// that said a heat map <i>needs a drawing primitive this server does not have</i> was wrong, and
/// was corrected on the day this was written.
/// </para>
/// <para>
/// <b>The kernel is Epanechnikov's</b> — <c>(1 - t²)</c> for <c>t</c> in 0..1, zero beyond — which
/// is the standard kernel for density estimation and is what makes the surface smooth rather than
/// a pile of discs. It is published statistics: V. A. Epanechnikov, <i>Non-parametric estimation
/// of a multivariate probability density</i>, Theory of Probability and its Applications 14
/// (1969). Nothing here is read out of an implementation.
/// </para>
/// </remarks>
public sealed class HeatField
{
    private readonly float[] _weights;
    private readonly int _width;
    private readonly int _height;

    /// <summary>Creates an empty field the size of the image.</summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    public HeatField(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        _width = width;
        _height = height;
        _weights = new float[width * height];
    }

    /// <summary>Whether anything has been added.</summary>
    public bool IsEmpty { get; private set; } = true;

    /// <summary>The largest accumulated weight, which is what a surface is normalised by.</summary>
    public double Peak { get; private set; }

    /// <summary>Adds one point's contribution.</summary>
    /// <remarks>
    /// <b>Every point contributes outside the image as well as inside it, and that is the whole
    /// of why tiles join.</b> A point half a radius beyond the left edge still lights pixels
    /// inside it; dropping it because its centre is outside would put a visible seam down every
    /// tile boundary. The caller is expected to read features from a slightly larger extent than
    /// it draws — the loop below clips to the image and does not care where the centre is.
    /// </remarks>
    /// <param name="x">Pixel x of the point.</param>
    /// <param name="y">Pixel y.</param>
    /// <param name="radius">How far the heat spreads, in pixels.</param>
    /// <param name="weight">What this feature counts for. One is one feature.</param>
    public void Add(double x, double y, double radius, double weight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(radius) || radius <= 0
            || !double.IsFinite(weight) || weight == 0)
        {
            return;
        }

        int left = Math.Max(0, (int)Math.Floor(x - radius));
        int right = Math.Min(_width - 1, (int)Math.Ceiling(x + radius));
        int top = Math.Max(0, (int)Math.Floor(y - radius));
        int bottom = Math.Min(_height - 1, (int)Math.Ceiling(y + radius));

        if (left > right || top > bottom)
        {
            return;
        }

        double squared = radius * radius;

        for (int row = top; row <= bottom; row++)
        {
            double dy = row + 0.5 - y;
            int at = (row * _width) + left;

            for (int column = left; column <= right; column++, at++)
            {
                double dx = column + 0.5 - x;
                double distance = (dx * dx) + (dy * dy);

                if (distance >= squared)
                {
                    continue;
                }

                // Epanechnikov: one at the centre, zero at the radius, and no cliff at either.
                double kernel = 1 - (distance / squared);
                float total = _weights[at] + (float)(kernel * weight);

                _weights[at] = total;

                if (total > Peak)
                {
                    Peak = total;
                }
            }
        }

        IsEmpty = false;
    }

    /// <summary>
    /// Colours the surface and hands it to the canvas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Transparent where there is nothing, and that is not the ramp's first colour.</b> A ramp
    /// runs from its coolest colour to its warmest; painting the coolest across the whole image
    /// would tint every empty pixel and turn a map of three hot spots into a coloured rectangle.
    /// The alpha rises with the density over the first fifth of the range, so the surface fades
    /// in from nothing rather than starting as a wash.
    /// </para>
    /// <para>
    /// <b>The peak decides the scale unless the document says otherwise.</b> CIM's
    /// <c>maxPixelIntensity</c> fixes it, which is what makes two tiles of the same layer
    /// comparable; without one, each image normalises to its own peak, which is what
    /// <c>autoAdjustPixelIntensity</c> means and is right for a single picture and wrong for a
    /// tiled map. The caller chooses; this reports which it used.
    /// </para>
    /// </remarks>
    /// <param name="canvas">Where to composite it.</param>
    /// <param name="ramp">Two or more colours, coolest first.</param>
    /// <param name="ceiling">The density that maps to the ramp's last colour, or null for the peak.</param>
    /// <param name="opacity">How opaque the whole surface is, 0 to 1.</param>
    /// <returns>The density the surface was scaled against.</returns>
    public double Paint(
        IMapCanvas canvas, IReadOnlyList<Rgba> ramp, double? ceiling, double opacity)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(ramp);

        if (ramp.Count < 2)
        {
            throw new RenderException(
                $"A heat map needs at least two colours to run between and was given {ramp.Count}. "
                + "One colour is a stencil, not a surface.");
        }

        double top = ceiling is { } fixedTop && double.IsFinite(fixedTop) && fixedTop > 0
            ? fixedTop
            : Peak;

        if (IsEmpty || top <= 0)
        {
            return top;
        }

        double alpha = Math.Clamp(opacity, 0, 1);
        Rgba[] pixels = new Rgba[_weights.Length];

        for (int i = 0; i < _weights.Length; i++)
        {
            double density = Math.Clamp(_weights[i] / top, 0, 1);

            if (density <= 0)
            {
                continue;
            }

            Rgba colour = Along(ramp, density);

            // <b>The fade is over the bottom fifth.</b> Below that the surface is barely
            // supported by any feature, and a hard edge there is the artefact that makes a heat
            // map look like a set of overlapping circles.
            double fade = Math.Min(1, density / 0.2);

            pixels[i] = colour with
            {
                A = (byte)Math.Clamp(Math.Round(colour.A * fade * alpha), 0, 255),
            };
        }

        canvas.DrawImage(pixels, _width, _height, new PixelBox(0, 0, _width, _height));

        return top;
    }

    /// <summary>The ramp's colour at a fraction of the way along it.</summary>
    private static Rgba Along(IReadOnlyList<Rgba> ramp, double at)
    {
        double scaled = Math.Clamp(at, 0, 1) * (ramp.Count - 1);
        int low = (int)Math.Floor(scaled);
        int high = Math.Min(low + 1, ramp.Count - 1);
        double part = scaled - low;

        Rgba from = ramp[low];
        Rgba to = ramp[high];

        return new Rgba(
            Between(from.R, to.R, part),
            Between(from.G, to.G, part),
            Between(from.B, to.B, part),
            Between(from.A, to.A, part));

        static byte Between(byte a, byte b, double at) =>
            (byte)Math.Clamp(Math.Round(a + ((b - a) * at)), 0, 255);
    }
}
