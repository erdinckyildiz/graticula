using System;
using System.Collections.Generic;
using Graticula.Cartography;
using SkiaSharp;

namespace Graticula.Render.Skia;

/// <summary>
/// <see cref="IMapCanvas"/> on Skia.
/// </summary>
/// <remarks>
/// <para>
/// <b>One of two files in the repository that may name SkiaSharp</b>
/// ([ADR-041](../../docs/adr/ADR-041-the-map-renderer.md) §5.1). Everything above it
/// speaks in <see cref="Rgba"/>, <see cref="PixelPath"/> and
/// <see cref="MapSymbol"/>; nothing above it knows a surface, a paint or a typeface
/// exists.
/// </para>
/// <para>
/// <b>Paints are reused, not allocated per call.</b> An <c>SKPaint</c> is a native
/// handle behind a managed object; creating one per feature is both a collection and
/// a finalisation per feature, which is the allocation profile
/// [ADR-004](../../docs/adr/ADR-004-rendering-engine.md) §0 warned this decision
/// about. Three paints are made once and mutated.
/// </para>
/// </remarks>
public sealed class SkiaMapCanvas : IMapCanvas
{
    private readonly SKSurface _surface;
    private readonly SKCanvas _canvas;
    private readonly SKPaint _fill;
    private readonly SKPaint _stroke;
    private readonly SKFont _font;
    private readonly SKPath _path = new();

    private bool _disposed;

    /// <summary>Opens a canvas.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <exception cref="RenderException">The surface could not be allocated.</exception>
    public SkiaMapCanvas(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;

        // Premultiplied, which is what every Skia fast path expects. The port's
        // colours are straight alpha; the conversion happens in Colour below and
        // nowhere else.
        _surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new RenderException(
                $"A {width}×{height} drawing surface could not be allocated. That is memory "
                + "pressure or a size the caller should not have been allowed to ask for.");

        _canvas = _surface.Canvas;

        _fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        _stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,

            // Round joins and caps, because a map is full of sharp turns and mitre
            // joins spike off them. The spikes are only visible on thick lines,
            // which is where somebody notices them on a printed map and not before.
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
        };

        // <b>The default typeface, resolved once.</b> On an image built with
        // NativeAssets.Linux.NoDependencies this is Skia's own; on a machine with
        // fonts it is the system default. Either draws Latin text. Labels in a
        // script the resolved face has no glyphs for come out as boxes, which is
        // Q-15's air-gapped font question arriving with the renderer.
        _font = new SKFont(SKTypeface.Default);
    }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public void Clear(Rgba colour)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _canvas.Clear(Colour(colour));
    }

    /// <inheritdoc/>
    public void FillArea(PixelPath path, MapSymbol.Area symbol)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(symbol);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Build(path, SKPathFillType.EvenOdd))
        {
            return;
        }

        if (!symbol.Colour.IsInvisible)
        {
            _fill.Color = Colour(symbol.Colour);
            _canvas.DrawPath(_path, _fill);
        }

        if (!symbol.OutlineColour.IsInvisible && symbol.OutlineWidth > 0)
        {
            _stroke.Color = Colour(symbol.OutlineColour);
            _stroke.StrokeWidth = (float)symbol.OutlineWidth;
            _stroke.PathEffect = null;
            _canvas.DrawPath(_path, _stroke);
        }
    }

    /// <inheritdoc/>
    public void StrokeLine(PixelPath path, MapSymbol.Stroke symbol)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(symbol);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (symbol.Colour.IsInvisible || symbol.Width <= 0 || !Build(path, SKPathFillType.Winding))
        {
            return;
        }

        _stroke.Color = Colour(symbol.Colour);
        _stroke.StrokeWidth = (float)symbol.Width;

        using SKPathEffect? dash = Dash(symbol.Dash);

        _stroke.PathEffect = dash;
        _canvas.DrawPath(_path, _stroke);
        _stroke.PathEffect = null;
    }

    /// <inheritdoc/>
    public void DrawMarker(double x, double y, MapSymbol.Marker symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (symbol.Radius <= 0)
        {
            return;
        }

        if (!symbol.Colour.IsInvisible)
        {
            _fill.Color = Colour(symbol.Colour);
            _canvas.DrawCircle((float)x, (float)y, (float)symbol.Radius, _fill);
        }

        if (!symbol.OutlineColour.IsInvisible && symbol.OutlineWidth > 0)
        {
            _stroke.Color = Colour(symbol.OutlineColour);
            _stroke.StrokeWidth = (float)symbol.OutlineWidth;
            _stroke.PathEffect = null;
            _canvas.DrawCircle((float)x, (float)y, (float)symbol.Radius, _stroke);
        }
    }

    /// <inheritdoc/>
    public PixelBox MeasureLabel(string text, MapSymbol.Label symbol, double x, double y)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(symbol);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _font.Size = (float)symbol.Size;

        float width = _font.MeasureText(text);
        SKFontMetrics metrics = _font.Metrics;

        // Ascent is negative and descent positive, which is the typographic
        // convention: both are offsets from the baseline, downwards.
        float half = width / 2;
        double grow = symbol.HaloWidth;

        return new PixelBox(
            x - half - grow,
            y + metrics.Ascent - grow,
            x + half + grow,
            y + metrics.Descent + grow);
    }

    /// <inheritdoc/>
    public void DrawLabel(string text, MapSymbol.Label symbol, double x, double y)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(symbol);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _font.Size = (float)symbol.Size;

        float width = _font.MeasureText(text);
        float left = (float)x - (width / 2);

        // <b>Halo first, then the text over it.</b> Drawn the other way round the
        // halo covers the letters it exists to separate.
        if (!symbol.HaloColour.IsInvisible && symbol.HaloWidth > 0)
        {
            _stroke.Color = Colour(symbol.HaloColour);

            // Doubled, because a stroke straddles the outline: half of it falls
            // inside the glyph, where it eats the letter rather than surrounding it.
            _stroke.StrokeWidth = (float)symbol.HaloWidth * 2;
            _stroke.PathEffect = null;
            _canvas.DrawText(text, left, (float)y, SKTextAlign.Left, _font, _stroke);
        }

        _fill.Color = Colour(symbol.Colour);
        _canvas.DrawText(text, left, (float)y, SKTextAlign.Left, _font, _fill);
    }

    /// <inheritdoc/>
    public byte[] Encode(MapImageFormat format, int quality)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _canvas.Flush();

        using SKImage image = _surface.Snapshot();

        SKEncodedImageFormat encoding = format switch
        {
            MapImageFormat.Png => SKEncodedImageFormat.Png,
            MapImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            _ => throw new RenderException(
                $"This canvas encodes PNG and JPEG; it was asked for {format}. A new member of "
                + "MapImageFormat needs a case here, and without one the request would silently "
                + "produce the wrong format."),
        };

        using SKData data = image.Encode(encoding, Math.Clamp(quality, 1, 100))
            ?? throw new RenderException($"The image could not be encoded as {format}.");

        return data.ToArray();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _path.Dispose();
        _font.Dispose();
        _stroke.Dispose();
        _fill.Dispose();
        _surface.Dispose();
    }

    /// <summary>Straight alpha to Skia's colour, which is also straight alpha.</summary>
    private static SKColor Colour(Rgba colour) => new(colour.R, colour.G, colour.B, colour.A);

    /// <summary>Rebuilds the reused path from the port's figures.</summary>
    private bool Build(PixelPath path, SKPathFillType fill)
    {
        _path.Reset();
        _path.FillType = fill;

        ReadOnlySpan<double> xy = path.Coordinates;

        foreach (PixelPath.Figure figure in path.Figures)
        {
            int start = figure.Start * 2;

            _path.MoveTo((float)xy[start], (float)xy[start + 1]);

            for (int i = 1; i < figure.Count; i++)
            {
                _path.LineTo((float)xy[start + (i * 2)], (float)xy[start + (i * 2) + 1]);
            }

            if (figure.Closed)
            {
                _path.Close();
            }
        }

        return !_path.IsEmpty;
    }

    /// <summary>The dash effect, or null for a solid line.</summary>
    /// <remarks>
    /// <b>An odd-length pattern is doubled, which is what SVG and CSS both do.</b>
    /// Skia requires an even count; a style writing <c>[3]</c> means three on, three
    /// off, and refusing it would reject a pattern every other renderer accepts.
    /// </remarks>
    private static SKPathEffect? Dash(IReadOnlyList<double>? pattern)
    {
        if (pattern is null || pattern.Count == 0)
        {
            return null;
        }

        int count = pattern.Count % 2 == 0 ? pattern.Count : pattern.Count * 2;
        float[] intervals = new float[count];

        for (int i = 0; i < count; i++)
        {
            intervals[i] = (float)pattern[i % pattern.Count];
        }

        return SKPathEffect.CreateDash(intervals, 0);
    }
}

/// <summary>Makes <see cref="SkiaMapCanvas"/> instances.</summary>
/// <remarks>
/// <b>The type the host registers, and the only name it has to know.</b> Everything
/// in Tier 1 asks for <see cref="IMapCanvasFactory"/>.
/// </remarks>
public sealed class SkiaMapCanvasFactory : IMapCanvasFactory
{
    /// <inheritdoc/>
    public IMapCanvas Create(int width, int height) => new SkiaMapCanvas(width, height);
}
