using System;

namespace Graticula.Cartography;

/// <summary>
/// The port a rasteriser implements: draw these shapes, give me those bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This interface is the tier boundary</b>
/// ([build-vs-adopt-policy.md](../../../docs/build-vs-adopt-policy.md) §4,
/// [ADR-041](../../../docs/adr/ADR-041-the-map-renderer.md) §5.1). Everything it
/// mentions is a type this repository owns; the rasteriser's own vocabulary —
/// surfaces, paints, typefaces, colour spaces — stops here and does not appear in
/// any Tier 1 signature. An architecture test confines the library to one project,
/// and this file is the reason that is possible.
/// </para>
/// <para>
/// <b>It takes resolved symbols, never style rules.</b> The decision of which
/// colour a feature gets is cartography and stays in Tier 1; the decision of how to
/// fill a polygon with that colour is rasterisation and belongs to whoever
/// implements this. Push a style expression across this boundary once and the
/// boundary has stopped meaning anything.
/// </para>
/// <para>
/// <b>Deliberately not a general drawing API.</b> There is no arc, no gradient, no
/// clip stack, no transform stack — five methods, each of which a map needs. A port
/// that mirrors its library's surface area is a port that can only ever have one
/// implementation.
/// </para>
/// </remarks>
public interface IMapCanvas : IDisposable
{
    /// <summary>Image width in pixels.</summary>
    int Width { get; }

    /// <summary>Image height in pixels.</summary>
    int Height { get; }

    /// <summary>Paints the whole image one colour, including transparent.</summary>
    /// <param name="colour">The colour.</param>
    void Clear(Rgba colour);

    /// <summary>Fills a path's rings, then outlines them.</summary>
    /// <remarks>
    /// <b>Rings marked closed are holes or shells by the even-odd rule</b>, which is
    /// what a polygon means: an inner ring inside an outer one is a hole whichever
    /// direction it winds. Non-zero winding would fill the holes of any producer
    /// that emits both rings the same way, and several do.
    /// </remarks>
    /// <param name="path">The rings, in pixels.</param>
    /// <param name="symbol">The fill and outline.</param>
    void FillArea(PixelPath path, MapSymbol.Area symbol);

    /// <summary>Strokes a path's figures.</summary>
    /// <param name="path">The lines, in pixels.</param>
    /// <param name="symbol">The stroke.</param>
    void StrokeLine(PixelPath path, MapSymbol.Stroke symbol);

    /// <summary>Draws a circular marker centred on a point.</summary>
    /// <param name="x">Pixel x.</param>
    /// <param name="y">Pixel y.</param>
    /// <param name="symbol">The marker.</param>
    void DrawMarker(double x, double y, MapSymbol.Marker symbol);

    /// <summary>
    /// Measures text without drawing it, so a label can be rejected before it paints.
    /// </summary>
    /// <remarks>
    /// <b>Measuring is the rasteriser's job and placing is ours.</b> Only the thing
    /// holding the font knows how wide a string is; only the thing holding the map
    /// knows whether that box may be used. Splitting them here is what lets label
    /// placement be a Tier 1 algorithm with tests that need no font.
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <param name="symbol">How it would be drawn.</param>
    /// <param name="x">Anchor, pixel x.</param>
    /// <param name="y">Anchor, pixel y.</param>
    /// <returns>The box it would occupy, centred on the anchor.</returns>
    PixelBox MeasureLabel(string text, MapSymbol.Label symbol, double x, double y);

    /// <summary>Draws text centred on a point, halo first.</summary>
    /// <param name="text">The text.</param>
    /// <param name="symbol">How to draw it.</param>
    /// <param name="x">Anchor, pixel x.</param>
    /// <param name="y">Anchor, pixel y.</param>
    void DrawLabel(string text, MapSymbol.Label symbol, double x, double y);

    /// <summary>Encodes the image.</summary>
    /// <param name="format">Which format.</param>
    /// <param name="quality">JPEG quality, 1–100; ignored by PNG.</param>
    /// <returns>The encoded bytes.</returns>
    byte[] Encode(MapImageFormat format, int quality);
}

/// <summary>Makes canvases, so the renderer never names an implementation.</summary>
/// <remarks>
/// <b>A factory rather than a constructor, because the host resolves it.</b> Tier 1
/// asks for <c>IMapCanvasFactory</c> from the container and receives whichever
/// adapter is registered; nothing in the render pipeline knows what it got.
/// </remarks>
public interface IMapCanvasFactory
{
    /// <summary>Opens a canvas.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>The canvas, which the caller disposes.</returns>
    IMapCanvas Create(int width, int height);
}
