using System;
using System.Collections.Generic;

namespace Graticula.Cartography;

/// <summary>
/// What one style layer resolves to for one feature: a symbol, in pixels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four kinds, because [ADR-033](../../../docs/adr/ADR-033-symbology.md) stores
/// four.</b> The canonical document keeps <c>fill</c>, <c>line</c>, <c>circle</c>
/// and <c>symbol</c> layers and refuses the rest at write time, so a fifth kind
/// here would be a symbol nothing can produce.
/// </para>
/// <para>
/// <b>Everything is already resolved.</b> Expressions have been evaluated against
/// the feature, opacity has been folded into the alpha channel, and every length is
/// in pixels. The port receives a value, never a rule — which is what keeps the
/// cartography in Tier 1 and the rasteriser ignorant of styles.
/// </para>
/// </remarks>
public abstract record MapSymbol
{
    private MapSymbol()
    {
    }

    /// <summary>A filled area, optionally outlined.</summary>
    /// <param name="Colour">The fill.</param>
    /// <param name="OutlineColour">The outline, or transparent for none.</param>
    /// <param name="OutlineWidth">Outline width in pixels.</param>
    public sealed record Area(Rgba Colour, Rgba OutlineColour, double OutlineWidth) : MapSymbol;

    /// <summary>A stroked line.</summary>
    /// <param name="Colour">The stroke.</param>
    /// <param name="Width">Width in pixels.</param>
    /// <param name="Dash">
    /// Dash lengths in pixels, alternating on and off, or null for a solid line.
    /// </param>
    public sealed record Stroke(
        Rgba Colour, double Width, IReadOnlyList<double>? Dash) : MapSymbol;

    /// <summary>A circular marker.</summary>
    /// <param name="Colour">The fill.</param>
    /// <param name="Radius">Radius in pixels.</param>
    /// <param name="OutlineColour">The outline, or transparent for none.</param>
    /// <param name="OutlineWidth">Outline width in pixels.</param>
    public sealed record Marker(
        Rgba Colour, double Radius, Rgba OutlineColour, double OutlineWidth) : MapSymbol;

    /// <summary>Text drawn beside a feature.</summary>
    /// <remarks>
    /// <b>The halo is not decoration.</b> Unhaloed text over a busy map is
    /// unreadable wherever it crosses a line of similar value, and every
    /// cartographic renderer draws one by default for that reason.
    /// </remarks>
    /// <param name="Colour">The text colour.</param>
    /// <param name="Size">Em size in pixels.</param>
    /// <param name="HaloColour">The halo, or transparent for none.</param>
    /// <param name="HaloWidth">Halo width in pixels.</param>
    public sealed record Label(
        Rgba Colour, double Size, Rgba HaloColour, double HaloWidth) : MapSymbol;
}

/// <summary>
/// A label waiting to be placed: its text, its symbol and where it wants to go.
/// </summary>
/// <remarks>
/// <b>Collected during the geometry pass and drawn after it</b>, so that a label is
/// never painted under a polygon drawn later. That ordering is not an optimisation;
/// it is the difference between a legible map and one where half the names are
/// buried.
/// </remarks>
/// <param name="Text">What it says.</param>
/// <param name="Symbol">How it is drawn.</param>
/// <param name="X">Anchor, pixel x.</param>
/// <param name="Y">Anchor, pixel y.</param>
public readonly record struct LabelCandidate(string Text, MapSymbol.Label Symbol, double X, double Y);

/// <summary>A rectangle in pixels, used to keep labels off each other.</summary>
/// <param name="MinX">Left.</param>
/// <param name="MinY">Top.</param>
/// <param name="MaxX">Right.</param>
/// <param name="MaxY">Bottom.</param>
public readonly record struct PixelBox(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>Whether two boxes touch.</summary>
    /// <param name="other">The other box.</param>
    /// <returns>Whether they overlap.</returns>
    public bool Intersects(PixelBox other) =>
        MinX < other.MaxX && MaxX > other.MinX && MinY < other.MaxY && MaxY > other.MinY;

    /// <summary>The box grown on every side.</summary>
    /// <param name="padding">How far, in pixels.</param>
    /// <returns>The grown box.</returns>
    public PixelBox Padded(double padding) =>
        new(MinX - padding, MinY - padding, MaxX + padding, MaxY + padding);

    /// <summary>Whether the box lies wholly outside an image of this size.</summary>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <returns>Whether it is off the image.</returns>
    public bool IsOutside(int width, int height) =>
        MaxX <= 0 || MaxY <= 0 || MinX >= width || MinY >= height;
}

/// <summary>The image formats this server will encode a map into.</summary>
/// <remarks>
/// <b>Two, and the omission is the interesting one.</b> GIF and TIFF appear in WMS
/// capabilities documents across the estate and neither is worth an encoder: GIF
/// cannot carry the colour a map needs and TIFF is not a browser format. A client
/// asking for either is refused rather than answered in PNG, because a client that
/// receives a format it did not ask for cannot tell that from a server that ignored
/// the parameter.
/// </remarks>
public enum MapImageFormat
{
    /// <summary>PNG, which carries transparency and is the WMS default here.</summary>
    Png = 1,

    /// <summary>JPEG, which does not carry transparency and is smaller.</summary>
    Jpeg = 2,
}

/// <summary>Raised when a map cannot be drawn, as opposed to drawing empty.</summary>
/// <remarks>
/// <b>[ADR-041](../../../docs/adr/ADR-041-the-map-renderer.md) condition 5.</b> A
/// request that matched no features returns a transparent image and 200; a request
/// that failed raises this and becomes a service exception. Answering both with an
/// empty image is the single most common way a broken map server looks healthy.
/// </remarks>
public sealed class RenderException : Exception
{
    /// <summary>A failure with a message a client can act on.</summary>
    /// <param name="message">What went wrong.</param>
    public RenderException(string message)
        : base(message)
    {
    }

    /// <summary>A failure with a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">The cause.</param>
    public RenderException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>A failure with no message, which should not be used.</summary>
    public RenderException()
        : base("The map could not be drawn.")
    {
    }
}
