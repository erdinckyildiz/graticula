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

    /// <summary>A font per script the default face cannot draw, or null for none found.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SKFont?> _substitutes =
        new();

    /// <summary>
    /// Called once per script this deployment has no face for.
    /// </summary>
    /// <remarks>
    /// <b>A hook rather than a logger, because this assembly is a Tier 2 adapter.</b> It
    /// knows about Skia and about nothing else; the host wires this to its own log at
    /// startup. Null in a test, which is why the call site tolerates it.
    /// </remarks>
    public static Action<string>? Missing { get; set; }
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
        // fonts it is the system default. Either draws Latin text.
        //
        // <b>A script it has no glyphs for is no longer drawn as boxes —
        // [Q-15](../../docs/open-questions.md), 2026-08-25.</b> That was the failure
        // the air-gap checklist ended on: no error, no warning, a map that renders and
        // is unreadable. `DrawLabel` now asks whether the resolved face can draw the
        // text and, when it cannot, asks the font manager for one that can. On a
        // machine with fonts that succeeds and the label is right; on an image with
        // none it fails and says so once, which is the difference between a deployment
        // that knows it needs a face and one that ships boxes to its users.
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

        SKFont font = FontFor(text);

        font.Size = (float)symbol.Size;

        float width = font.MeasureText(text);
        SKFontMetrics metrics = font.Metrics;

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

    /// <summary>
    /// A font that can draw this text, or the default one and a sentence about why not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[Q-15](../../docs/open-questions.md)'s last item.</b> The air-gap checklist
    /// came out clean on PROJ, on GDAL and on telemetry, and ended on fonts:
    /// <c>SKTypeface.Default</c> draws a script it has no glyphs for as boxes, with no
    /// error and no warning. A map that renders and cannot be read is worse than one
    /// that refuses, because nothing anywhere says which it is.
    /// </para>
    /// <para>
    /// <b>Asked per label and answered from a small cache</b>, because
    /// <c>MatchCharacter</c> walks the machine's fonts and a map draws thousands of
    /// labels. The key is the first character the default face cannot draw, so a map
    /// full of Turkish labels asks once.
    /// </para>
    /// <para>
    /// <b>What it does not do is bundle a face.</b> Choosing one is a size, a licence
    /// and a promise about which scripts this product draws — a decision for whoever
    /// packages the product, and it is recorded rather than taken here. What changes is
    /// that an image with no suitable face now says so instead of drawing boxes.
    /// </para>
    /// </remarks>
    /// <param name="text">The label.</param>
    /// <returns>A font, which the caller must not dispose.</returns>
    private SKFont FontFor(string text)
    {
        int missing = FirstUndrawable(_font.Typeface, text);

        if (missing < 0)
        {
            return _font;
        }

        if (_substitutes.TryGetValue(missing, out SKFont? held))
        {
            return held ?? _font;
        }

        SKTypeface? found = SKFontManager.Default.MatchCharacter(missing);

        if (found is null || FirstUndrawable(found, text) >= 0)
        {
            found?.Dispose();

            // <b>Said once per script, not once per label.</b> A map with ten thousand
            // Greek labels would otherwise write ten thousand lines and the operator
            // would learn to filter them out.
            _substitutes[missing] = null;

            Missing?.Invoke(char.ConvertFromUtf32(missing));

            return _font;
        }

        SKFont substitute = new(found);

        if (!_substitutes.TryAdd(missing, substitute))
        {
            substitute.Dispose();
            found.Dispose();

            return _substitutes.TryGetValue(missing, out SKFont? raced) && raced is not null
                ? raced
                : _font;
        }

        return substitute;
    }

    /// <summary>The first code point this face cannot draw, or -1.</summary>
    /// <remarks>
    /// <b>Code points, not chars.</b> A surrogate pair is one glyph and asking about
    /// half of it answers *no* for text the face draws perfectly well — which would send
    /// every emoji and every CJK extension through the substitution path.
    /// </remarks>
    private static int FirstUndrawable(SKTypeface? face, string text)
    {
        if (face is null)
        {
            return -1;
        }

        for (int i = 0; i < text.Length;)
        {
            int codePoint = char.ConvertToUtf32(text, i);
            i += char.IsSurrogatePair(text, i) ? 2 : 1;

            // Whitespace and control characters are not drawn and every face reports
            // them inconsistently; asking about them is how a plain Latin label ends up
            // in the substitution path.
            if (codePoint <= 0x20)
            {
                continue;
            }

            if (face.GetGlyph(codePoint) == 0)
            {
                return codePoint;
            }
        }

        return -1;
    }

    /// <inheritdoc/>
    public void DrawLabel(string text, MapSymbol.Label symbol, double x, double y)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(symbol);
        ObjectDisposedException.ThrowIf(_disposed, this);

        SKFont font = FontFor(text);

        font.Size = (float)symbol.Size;

        float width = font.MeasureText(text);
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
            _canvas.DrawText(text, left, (float)y, SKTextAlign.Left, font, _stroke);
        }

        _fill.Color = Colour(symbol.Colour);
        _canvas.DrawText(text, left, (float)y, SKTextAlign.Left, font, _fill);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>The colours arrive as RGBA and the surface is RGBA, so the copy is a
    /// memcpy.</b> `SKColorType.Rgba8888` is what this canvas was created with —
    /// choosing BGRA here to match a platform default would put a per-pixel swizzle in
    /// the one place a raster face cannot afford one.
    /// </para>
    /// <para>
    /// <b>Unpremultiplied, because the samples were.</b> The surface is premultiplied,
    /// and Skia converts on draw; doing it here by hand would be the same arithmetic in
    /// a slower place, and getting it wrong shows as a dark fringe around every
    /// no-data edge rather than as an error.
    /// </para>
    /// <para>
    /// <b>High-quality resampling, and the reason is what a coverage window is.</b>
    /// The source is the pixels the reader returned for whichever overview was chosen,
    /// so it is rarely the destination's size — nearest-neighbour would alias a
    /// continuous surface into visible blocks, which is a wrong-looking map rather
    /// than a slow one.
    /// </para>
    /// </remarks>
    public void DrawImage(ReadOnlySpan<Rgba> pixels, int width, int height, PixelBox destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (pixels.Length < width * height)
        {
            throw new RenderException(
                $"An image of {width}x{height} needs {width * height} colours and "
                + $"{pixels.Length} were given. A short buffer would draw whatever followed it "
                + "in memory, which is a picture rather than an error.");
        }

        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        using SKBitmap bitmap = new();

        byte[] bytes = new byte[width * height * 4];

        for (int i = 0, at = 0; i < width * height; i++, at += 4)
        {
            Rgba colour = pixels[i];
            bytes[at] = colour.R;
            bytes[at + 1] = colour.G;
            bytes[at + 2] = colour.B;
            bytes[at + 3] = colour.A;
        }

        System.Runtime.InteropServices.GCHandle handle =
            System.Runtime.InteropServices.GCHandle.Alloc(
                bytes, System.Runtime.InteropServices.GCHandleType.Pinned);

        try
        {
            bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), width * 4);

            using SKPaint paint = new() { IsAntialias = true };

            using SKImage image = SKImage.FromBitmap(bitmap);

            _canvas.DrawImage(
                image,
                new SKRect(0, 0, width, height),
                new SKRect(
                    (float)destination.MinX,
                    (float)destination.MinY,
                    (float)destination.MaxX,
                    (float)destination.MaxY),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                paint);
        }
        finally
        {
            handle.Free();
        }
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
        foreach (SKFont? substitute in _substitutes.Values)
        {
            substitute?.Typeface?.Dispose();
            substitute?.Dispose();
        }

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
