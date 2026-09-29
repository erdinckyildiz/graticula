using System;
using System.Collections.Generic;
using Graticula.Cartography;
using System.Linq;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

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
    /// <summary>This canvas's font per face of the label stack, resized per label.</summary>
    private readonly Dictionary<LabelFace, SKFont> _fonts = new(ReferenceEqualityComparer.Instance);

    /// <summary>A face per codepoint the stack cannot draw, or null for none found.</summary>
    private readonly Dictionary<int, LabelFace?> _substitutes = [];

    /// <summary>The machine's typefaces this canvas asked for, which it disposes.</summary>
    private readonly List<SKTypeface> _owned = [];

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
    }

    /// <summary>
    /// The first face of the label stack's family name, or null when no face could be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-161](../../docs/architecture-debt.md), owner decision 2026-08-25, and
    /// [ADR-100](../../docs/adr/ADR-100-labels-in-more-scripts.md), 2026-09-29.</b> D-161 carried
    /// one face, DejaVu Sans, the file the tile face's glyphs were drawn from. ADR-100 gave both
    /// faces the same stack — DejaVu first, then Noto Sans and a Noto family per script — so this
    /// is still DejaVu, and <see cref="LabelFaces"/> holds the rest.
    /// </para>
    /// <para>
    /// <b>Exposed so a test can assert the face is *there*, not merely that a label
    /// drew.</b> The first version of that test asserted pixels and silence, and passed
    /// with the resource removed — because the machine it ran on has Turkish, Greek and
    /// Cyrillic system fonts, so the label drew from those and nothing was reported. A
    /// test that cannot tell the bundled face from the machine's is a test about the
    /// machine, and D-161's whole point is what happens on a machine with neither.
    /// </para>
    /// </remarks>
    public static string? BundledFace =>
        LabelFaces.Stack.Count > 0 ? LabelFaces.Stack[0].Typeface.FamilyName : null;

    /// <summary>The file each run of a label is drawn from, in drawing order.</summary>
    /// <param name="text">The label.</param>
    /// <returns>One file name per run; a face the machine supplied is named as such.</returns>
    /// <remarks>
    /// <b>For a test, like <see cref="BundledFace"/></b>: pixels cannot say which font drew
    /// them, and a label in a script the stack carries must not reach the machine's fonts.
    /// </remarks>
    public static IReadOnlyList<string> FacesFor(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        List<LabelFaces.Run> runs = LabelFaces.Segment(text, _ => Default);

        if (LabelFaces.RightToLeft(text))
        {
            runs.Reverse();
        }

        return [.. runs.Select(static run => run.Face.File)];
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
    /// <remarks>
    /// <para>
    /// <b>Turned about the point, then moved, then drawn centred</b> — which is how the style
    /// specification places an `icon-offset` under an `icon-rotate`, so this canvas and a browser put
    /// a turned, offset icon in the same place (ADR-099).
    /// </para>
    /// <para>
    /// <b>Linear sampling with mipmaps</b>, because a picture is nearly always drawn smaller than its
    /// pixels and a nearest-neighbour minification is a shimmer of dropped detail; the same reason
    /// <see cref="DrawImage"/> gives for a coverage.
    /// </para>
    /// </remarks>
    public void DrawPicture(double x, double y, MapSymbol.Picture symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (symbol.Width <= 0 || symbol.Height <= 0 || symbol.Opacity <= 0)
        {
            return;
        }

        bool cached = Cacheable(symbol.Image);
        SKImage? image = cached ? Decoded(symbol.Image) : Decode(symbol.Image);

        if (image is null)
        {
            return;
        }

        int saved = _canvas.Save();

        try
        {
            _canvas.Translate((float)x, (float)y);

            if (symbol.Rotation != 0)
            {
                _canvas.RotateDegrees((float)symbol.Rotation);
            }

            float left = (float)(symbol.OffsetX - (symbol.Width / 2));
            float top = (float)(symbol.OffsetY - (symbol.Height / 2));

            using SKPaint paint = new()
            {
                IsAntialias = true,
                Color = new SKColor(255, 255, 255, (byte)Math.Round(Math.Clamp(symbol.Opacity, 0, 1) * 255)),
            };

            _canvas.DrawImage(
                image,
                new SKRect(left, top, left + (float)symbol.Width, top + (float)symbol.Height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                paint);
        }
        finally
        {
            _canvas.RestoreToCount(saved);

            if (!cached)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>Decoded pictures, by the hash of their bytes, shared by every canvas in the process.</summary>
    /// <remarks>
    /// <para>
    /// <b>Decoded once, not once per feature.</b> A point layer of ten thousand features drawn with one
    /// icon asks for the same picture ten thousand times, and decoding a PNG per point would be most of
    /// the map's cost. The key is the content hash, so an edited picture is a new entry rather than a
    /// stale one.
    /// </para>
    /// <para>
    /// <b>Bounded by forgetting</b>: past <see cref="MostDecoded"/> entries the table is emptied and
    /// refilled on demand. Nothing is disposed when it is emptied, because another canvas may be
    /// drawing with an image at that moment; the images are released by their finalisers once no
    /// canvas holds them. Only marker-sized pictures are kept here — a sprite sheet being composed is
    /// decoded, drawn once and disposed.
    /// </para>
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SKImage?> DecodedPictures =
        new(StringComparer.Ordinal);

    /// <summary>How many decoded pictures the process keeps before it forgets them all.</summary>
    private const int MostDecoded = 512;

    /// <summary>Whether a picture is small enough to keep decoded.</summary>
    private static bool Cacheable(MarkerPicture picture) =>
        (long)picture.PixelWidth * picture.PixelHeight <= (long)MarkerPicture.MaximumSide * MarkerPicture.MaximumSide;

    /// <summary>A marker-sized picture, decoded, from the table or into it.</summary>
    private static SKImage? Decoded(MarkerPicture picture)
    {
        if (DecodedPictures.TryGetValue(picture.Hash, out SKImage? held))
        {
            return held;
        }

        if (DecodedPictures.Count >= MostDecoded)
        {
            DecodedPictures.Clear();
        }

        return DecodedPictures.GetOrAdd(picture.Hash, _ => Decode(picture));
    }

    /// <summary>
    /// Decodes a picture, after asking the decoder what size it will be.
    /// </summary>
    /// <remarks>
    /// <b>The codec's own size must be the header's.</b> The picture was bounded from its header when it
    /// was stored; a file whose decoder reports a different size is one whose header lied, and it is
    /// not decoded. Null for anything that does not decode — the caller draws nothing, as the port
    /// promises.
    /// </remarks>
    private static SKImage? Decode(MarkerPicture picture)
    {
        using SKData data = SKData.CreateCopy(picture.Bytes.Span);
        using SKCodec? codec = SKCodec.Create(data);

        if (codec is null
            || codec.Info.Width != picture.PixelWidth
            || codec.Info.Height != picture.PixelHeight)
        {
            return null;
        }

        SKImageInfo info = new(
            codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

        using SKBitmap? bitmap = SKBitmap.Decode(codec, info);

        return bitmap is null ? null : SKImage.FromBitmap(bitmap);
    }

    /// <inheritdoc/>
    public PixelBox MeasureLabel(string text, MapSymbol.Label symbol, double x, double y)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(symbol);
        ObjectDisposedException.ThrowIf(_disposed, this);

        (_, float width, float ascent, float descent) = Lay(text, (float)symbol.Size);

        // Ascent is negative and descent positive, which is the typographic
        // convention: both are offsets from the baseline, downwards.
        float half = width / 2;
        double grow = symbol.HaloWidth;

        return new PixelBox(
            x - half - grow,
            y + ascent - grow,
            x + half + grow,
            y + descent + grow);
    }

    /// <summary>One run of a label, shaped, in the face that draws it.</summary>
    private readonly record struct Shaped(SKFont Font, SKShaper.Result Result);

    /// <summary>
    /// A label split into runs of one face each, shaped, in the order they are drawn.
    /// </summary>
    /// <remarks>
    /// <b>Measured and drawn from the same layout</b>, because measuring with one face and
    /// drawing with another puts the label in the wrong place, and label placement is what
    /// decides whether two labels collide. <see cref="LabelFaces"/> says how a label is split
    /// and what that does not do.
    /// </remarks>
    private (List<Shaped> Runs, float Width, float Ascent, float Descent) Lay(string text, float size)
    {
        List<LabelFaces.Run> runs = LabelFaces.Segment(text, FallbackFor);

        if (LabelFaces.RightToLeft(text))
        {
            runs.Reverse();
        }

        List<Shaped> shaped = new(runs.Count);
        float width = 0;
        float ascent = 0;
        float descent = 0;

        foreach (LabelFaces.Run run in runs)
        {
            SKFont font = FontOf(run.Face);
            font.Size = size;

            SKShaper.Result result = run.Face.Shape(run.Text, font, run.Digits);
            shaped.Add(new Shaped(font, result));

            width += result.Width;

            SKFontMetrics metrics = font.Metrics;
            ascent = Math.Min(ascent, metrics.Ascent);
            descent = Math.Max(descent, metrics.Descent);
        }

        return (shaped, width, ascent, descent);
    }

    /// <summary>This canvas's font for a face, made once and resized per label.</summary>
    private SKFont FontOf(LabelFace face)
    {
        if (!_fonts.TryGetValue(face, out SKFont? font))
        {
            font = new SKFont(face.Typeface);
            _fonts[face] = font;
        }

        return font;
    }

    /// <summary>
    /// A face for a codepoint no font in the stack has: the machine's, or the stack's first and
    /// a sentence about why.
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
    /// <b>Reached far less since [ADR-100](../../docs/adr/ADR-100-labels-in-more-scripts.md)</b>,
    /// which gave the raster face the tile face's whole stack: what arrives here is a script no
    /// font in it has — CJK on an image built without it, or a script this product does not
    /// carry at all.
    /// </para>
    /// <para>
    /// <b>Asked per codepoint and answered from a small cache</b>, because
    /// <c>MatchCharacter</c> walks the machine's fonts and a map draws thousands of
    /// labels; a face the machine already gave for one codepoint is asked first for the next.
    /// </para>
    /// </remarks>
    private LabelFace FallbackFor(int codePoint)
    {
        if (_substitutes.TryGetValue(codePoint, out LabelFace? held))
        {
            return held ?? Default;
        }

        foreach (LabelFace? earlier in _substitutes.Values)
        {
            if (earlier is not null && earlier.Has(codePoint))
            {
                _substitutes[codePoint] = earlier;
                return earlier;
            }
        }

        SKTypeface? found = SKFontManager.Default.MatchCharacter(codePoint);

        if (found is null || found.GetGlyph(codePoint) == 0)
        {
            found?.Dispose();

            // <b>Said once per codepoint and canvas, not once per label.</b> A map with ten
            // thousand labels in a script nothing here draws would otherwise write ten
            // thousand lines and the operator would learn to filter them out.
            _substitutes[codePoint] = null;

            Missing?.Invoke(char.ConvertFromUtf32(codePoint));

            return Default;
        }

        LabelFace substitute = new(found, "the machine's " + found.FamilyName, []);
        _substitutes[codePoint] = substitute;
        _owned.Add(found);

        return substitute;
    }

    /// <summary>The face a codepoint nothing can draw is drawn in, as the box it is.</summary>
    private static LabelFace Default =>
        LabelFaces.Stack.Count > 0 ? LabelFaces.Stack[0] : SystemDefault.Value;

    private static readonly Lazy<LabelFace> SystemDefault =
        new(() => new LabelFace(SKTypeface.Default, "the system default", []));

    /// <summary>The runs as text blobs, ready to be drawn twice.</summary>
    private static List<(SKTextBlob Blob, float Offset)> Blobs(List<Shaped> runs)
    {
        List<(SKTextBlob, float)> blobs = new(runs.Count);
        float at = 0;

        foreach (Shaped run in runs)
        {
            int count = run.Result.Codepoints.Length;

            if (count > 0)
            {
                using SKTextBlobBuilder builder = new();
                SKPositionedRunBuffer buffer = builder.AllocatePositionedRun(run.Font, count);
                Span<ushort> glyphs = buffer.Glyphs;
                Span<SKPoint> positions = buffer.Positions;

                for (int i = 0; i < count; i++)
                {
                    glyphs[i] = (ushort)run.Result.Codepoints[i];
                    positions[i] = run.Result.Points[i];
                }

                if (builder.Build() is { } blob)
                {
                    blobs.Add((blob, at));
                }
            }

            at += run.Result.Width;
        }

        return blobs;
    }

    /// <inheritdoc/>
    public void DrawLabel(string text, MapSymbol.Label symbol, double x, double y)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(symbol);
        ObjectDisposedException.ThrowIf(_disposed, this);

        (List<Shaped> runs, float width, _, _) = Lay(text, (float)symbol.Size);

        float left = (float)x - (width / 2);
        List<(SKTextBlob Blob, float Offset)> blobs = Blobs(runs);

        try
        {
            // <b>Halo first, then the text over it.</b> Drawn the other way round the
            // halo covers the letters it exists to separate.
            if (!symbol.HaloColour.IsInvisible && symbol.HaloWidth > 0)
            {
                _stroke.Color = Colour(symbol.HaloColour);

                // Doubled, because a stroke straddles the outline: half of it falls
                // inside the glyph, where it eats the letter rather than surrounding it.
                _stroke.StrokeWidth = (float)symbol.HaloWidth * 2;
                _stroke.PathEffect = null;

                foreach ((SKTextBlob blob, float offset) in blobs)
                {
                    _canvas.DrawText(blob, left + offset, (float)y, _stroke);
                }
            }

            _fill.Color = Colour(symbol.Colour);

            foreach ((SKTextBlob blob, float offset) in blobs)
            {
                _canvas.DrawText(blob, left + offset, (float)y, _fill);
            }
        }
        finally
        {
            foreach ((SKTextBlob blob, _) in blobs)
            {
                blob.Dispose();
            }
        }
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
        foreach (SKFont font in _fonts.Values)
        {
            font.Dispose();
        }

        foreach (SKTypeface typeface in _owned)
        {
            typeface.Dispose();
        }

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
