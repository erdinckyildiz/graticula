using System;
using System.Collections.Generic;

namespace Graticula.Cartography.Printing;

/// <summary>
/// A window onto a larger canvas, at a larger pixel size — ADR-265 §5.4: how a print draws a map into a page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two problems, one decorator.</b> A page at 300 dpi has three pixels where a screen has one, and the renderer draws
/// a one-pixel line one pixel wide — so a printed map drawn at the page's own pixels has hair-thin roads and labels a
/// third of their size, and its scale denominators, which the renderer computes from its pixel size, are three times too
/// small. And a map is drawn into a frame on a page, which the renderer knows nothing about: it draws from (0, 0). This
/// canvas answers both by being what the renderer expects — a canvas of logical, screen-sized pixels starting at the
/// origin — and placing every coordinate and length onto the page underneath at <c>offset + ratio × logical</c>.
/// </para>
/// <para>
/// <b>Every method is forwarded, including the one with a default body.</b> <see cref="IMapCanvas"/>'s five-argument
/// <c>DrawImage</c> has a default implementation, and a decorator that does not override it silently loses the
/// resampling choice — which happened once in this repository already (BreakingProjector).
/// </para>
/// <para>
/// <b>It does not clip.</b> The port has no clip, deliberately; a map drawn through this window spills past its frame,
/// and <see cref="PageComposer"/> paints the page's margins over the spill afterwards.
/// </para>
/// </remarks>
public sealed class PlacedCanvas : IMapCanvas
{
    private readonly IMapCanvas _page;
    private readonly double _x;
    private readonly double _y;
    private readonly double _ratio;
    private readonly PixelPath _scratch = new();

    /// <summary>Opens a window onto a page.</summary>
    /// <param name="page">The canvas underneath, which this does not dispose.</param>
    /// <param name="x">The window's left edge on the page, in page pixels.</param>
    /// <param name="y">The window's top edge on the page, in page pixels.</param>
    /// <param name="width">The window's width, in logical pixels.</param>
    /// <param name="height">The window's height, in logical pixels.</param>
    /// <param name="ratio">Page pixels per logical pixel: the print's dpi over 96.</param>
    public PlacedCanvas(IMapCanvas page, double x, double y, int width, int height, double ratio)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (width <= 0 || height <= 0 || !(ratio > 0) || double.IsInfinity(ratio))
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), "A placed canvas needs a positive size and ratio.");
        }

        _page = page;
        _x = x;
        _y = y;
        _ratio = ratio;
        Width = width;
        Height = height;
    }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <summary>Page pixels per logical pixel.</summary>
    public double Ratio => _ratio;

    /// <summary>A factory whose canvases are windows onto <paramref name="page"/> at one place.</summary>
    /// <param name="page">The page.</param>
    /// <param name="x">Left, in page pixels.</param>
    /// <param name="y">Top, in page pixels.</param>
    /// <param name="ratio">Page pixels per logical pixel.</param>
    /// <returns>The factory.</returns>
    /// <remarks>
    /// For code that draws by asking a factory for a canvas — the legend swatch drawing does — so it draws onto the page
    /// where the composer put it rather than into a picture of its own that would have to be copied across.
    /// </remarks>
    public static IMapCanvasFactory At(IMapCanvas page, double x, double y, double ratio) =>
        new Placing(page, x, y, ratio);

    /// <summary>
    /// Paints the window one colour; transparent paints nothing, because a window has nothing of its own to clear.
    /// </summary>
    /// <param name="colour">The colour.</param>
    public void Clear(Rgba colour)
    {
        if (colour.IsInvisible)
        {
            return;
        }

        _scratch.Reset();
        _scratch.Begin(closed: true);
        _scratch.Add(_x, _y);
        _scratch.Add(_x + (Width * _ratio), _y);
        _scratch.Add(_x + (Width * _ratio), _y + (Height * _ratio));
        _scratch.Add(_x, _y + (Height * _ratio));
        _scratch.End();
        _page.FillArea(_scratch, new MapSymbol.Area(colour, Rgba.Transparent, 0));
    }

    /// <inheritdoc/>
    public void FillArea(PixelPath path, MapSymbol.Area symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        _page.FillArea(Place(path), symbol with { OutlineWidth = symbol.OutlineWidth * _ratio });
    }

    /// <inheritdoc/>
    public void StrokeLine(PixelPath path, MapSymbol.Stroke symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        _page.StrokeLine(Place(path), symbol with { Width = symbol.Width * _ratio, Dash = Scaled(symbol.Dash) });
    }

    /// <inheritdoc/>
    public void DrawMarker(double x, double y, MapSymbol.Marker symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        _page.DrawMarker(
            PageX(x), PageY(y),
            symbol with { Radius = symbol.Radius * _ratio, OutlineWidth = symbol.OutlineWidth * _ratio });
    }

    /// <inheritdoc/>
    public void DrawPicture(double x, double y, MapSymbol.Picture symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        _page.DrawPicture(
            PageX(x), PageY(y),
            symbol with
            {
                Width = symbol.Width * _ratio,
                Height = symbol.Height * _ratio,
                OffsetX = symbol.OffsetX * _ratio,
                OffsetY = symbol.OffsetY * _ratio,
            });
    }

    /// <inheritdoc/>
    public PixelBox MeasureLabel(string text, MapSymbol.Label symbol, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        PixelBox measured = _page.MeasureLabel(text, Scaled(symbol), PageX(x), PageY(y));

        return new PixelBox(
            (measured.MinX - _x) / _ratio,
            (measured.MinY - _y) / _ratio,
            (measured.MaxX - _x) / _ratio,
            (measured.MaxY - _y) / _ratio);
    }

    /// <inheritdoc/>
    public void DrawLabel(string text, MapSymbol.Label symbol, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        _page.DrawLabel(text, Scaled(symbol), PageX(x), PageY(y));
    }

    /// <inheritdoc/>
    public void DrawImage(ReadOnlySpan<Rgba> pixels, int width, int height, PixelBox destination) =>
        _page.DrawImage(pixels, width, height, Place(destination));

    /// <inheritdoc/>
    public void DrawImage(ReadOnlySpan<Rgba> pixels, int width, int height, PixelBox destination, Resampling how) =>
        _page.DrawImage(pixels, width, height, Place(destination), how);

    /// <summary>A window cannot be encoded on its own: the page is the picture.</summary>
    /// <param name="format">Unused.</param>
    /// <param name="quality">Unused.</param>
    /// <returns>Never.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public byte[] Encode(MapImageFormat format, int quality) =>
        throw new NotSupportedException("A placed canvas is a window onto a page; encode the page.");

    /// <summary>Disposes nothing: the page belongs to whoever opened it.</summary>
    public void Dispose()
    {
    }

    private double PageX(double x) => _x + (x * _ratio);

    private double PageY(double y) => _y + (y * _ratio);

    private PixelBox Place(PixelBox box) =>
        new(PageX(box.MinX), PageY(box.MinY), PageX(box.MaxX), PageY(box.MaxY));

    private MapSymbol.Label Scaled(MapSymbol.Label symbol) =>
        symbol with { Size = symbol.Size * _ratio, HaloWidth = symbol.HaloWidth * _ratio };

    private List<double>? Scaled(IReadOnlyList<double>? dash)
    {
        if (dash is null)
        {
            return null;
        }

        List<double> scaled = new(dash.Count);

        foreach (double one in dash)
        {
            scaled.Add(one * _ratio);
        }

        return scaled;
    }

    private PixelPath Place(PixelPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        _scratch.Reset();
        ReadOnlySpan<double> xy = path.Coordinates;

        foreach (PixelPath.Figure figure in path.Figures)
        {
            _scratch.Begin(figure.Closed);

            for (int i = figure.Start; i < figure.Start + figure.Count; i++)
            {
                _scratch.Add(PageX(xy[2 * i]), PageY(xy[(2 * i) + 1]));
            }
        }

        _scratch.End();
        return _scratch;
    }

    private sealed class Placing(IMapCanvas page, double x, double y, double ratio) : IMapCanvasFactory
    {
        public IMapCanvas Create(int width, int height) => new PlacedCanvas(page, x, y, width, height, ratio);
    }
}
