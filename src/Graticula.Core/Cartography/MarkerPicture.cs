using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Graticula.Cartography;

/// <summary>
/// The image a picture marker draws, read from the bytes it arrived as and bounded before anything
/// decodes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-099](../../../docs/adr/ADR-099-picture-markers-are-drawn-on-every-face.md), owner
/// decision 2026-09-29: a layer's picture markers are carried, not refused.</b> Until then a
/// <c>CIMPictureMarker</c> was reported as not drawn and an <c>esriPMS</c> was refused outright, so a
/// point layer authored with an icon drew as a circle on every face. This type is the one reading of
/// *what picture is this* that every face shares: the stored CIM keeps the picture as a
/// <c>data:</c> URI, the Esri face writes it back as <c>imageData</c>, the raster faces draw it, and
/// the tile face packs it into the service's generated sprite sheet under <see cref="Name"/>.
/// </para>
/// <para>
/// <b>PNG and JPEG, and nothing is fetched.</b> A picture is accepted only as bytes carried in the
/// document. A URL is refused rather than fetched: a server that fetches whatever address a symbol
/// names can be pointed at its own network, which is server-side request forgery, and the allow-list
/// of [ADR-094](../../../docs/adr/ADR-094-several-styles-and-allowed-origins.md) is about where a
/// <i>browser</i> is sent, not about what this process reaches for. SVG is refused because the
/// rasteriser this server draws with has no SVG reader, and an SVG is a document that can name other
/// resources in its turn.
/// </para>
/// <para>
/// <b>Bounded from the header, before any decoder runs.</b> The width and height are read from the
/// PNG <c>IHDR</c> chunk or the JPEG start-of-frame segment — fixed places in the format — and a
/// picture over <see cref="MaximumSide"/> or <see cref="MaximumBytes"/> is refused without a pixel
/// being decoded. That is the rule ADR-092's <c>SpriteSheet</c> follows for an uploaded sheet, for
/// the reason ADR-027 kept a font parser out of the process: an image decoder is an attacker-facing
/// binary parser, and the bounds are what make running one acceptable.
/// </para>
/// </remarks>
public sealed class MarkerPicture : IEquatable<MarkerPicture>
{
    /// <summary>The largest picture one marker may carry, in bytes.</summary>
    /// <remarks>
    /// <b>256 KB, which is generous for an icon and small for a photograph.</b> A 128-pixel PNG icon is
    /// a few kilobytes; a picture this large is usually the wrong file, and every one of them is
    /// stored inside the layer's symbology document and read on every request that draws the layer.
    /// </remarks>
    public const int MaximumBytes = 256 * 1024;

    /// <summary>The widest or tallest picture one marker may carry, in pixels.</summary>
    /// <remarks>
    /// <b>512, and it bounds the decoder rather than the map.</b> A marker is drawn at tens of pixels;
    /// a picture four times larger than any sensible marker at a high-density screen's two pixels per
    /// point is still under this, and a compressed file that declares 20,000 × 20,000 is refused
    /// before it can ask for 1.6 GB of pixels.
    /// </remarks>
    public const int MaximumSide = 512;

    /// <summary>The most picture bytes one layer's symbology may carry, all markers together.</summary>
    /// <remarks>
    /// <b>One megabyte, so the document still fits the read bound.</b> Base64 costs four characters
    /// per three bytes, so a megabyte of pictures is about 1.4 million characters, under the
    /// 2,097,152 <c>SymbologyConversion.MaximumReadCharacters</c> allows a request — which leaves room
    /// for the classes around them. The same picture used by several classes is counted once.
    /// </remarks>
    public const int MaximumLayerBytes = 1024 * 1024;

    /// <summary>The longest side an icon has in the generated sprite sheet, at pixel ratio 1.</summary>
    /// <remarks>
    /// <b>128 pixels, which is 96 points.</b> A picture is packed at its own size, or shrunk to this
    /// when it is larger, and <c>icon-size</c> scales it to the marker's size from there. Shrinking in
    /// the sheet rather than at draw time keeps a 512-pixel picture drawn at 16 pixels from being
    /// minified 32 times by a GPU with no mipmaps, which is what makes an icon shimmer, and it keeps a
    /// service's sheet small enough that a few hundred distinct pictures fit in one texture. The
    /// @2x sheet carries the same icons at twice these dimensions.
    /// </remarks>
    public const int SheetSide = 128;

    /// <summary>What every generated icon's name begins with, and what an uploaded sheet may not use.</summary>
    /// <remarks>
    /// <b>A prefix nobody's icon set already uses.</b> The owner's example was <c>g-</c>; a two-letter
    /// prefix is one a real icon set may well contain, and refusing a publisher's existing sheet
    /// because an icon happens to begin <c>g-</c> would be this change breaking something it had no
    /// business touching. <b>INFERRED</b>, ADR-099 §5.
    /// </remarks>
    public const string NamePrefix = "graticula-";

    /// <summary>How many characters of <c>data:</c> URI the read cache holds before it forgets them all.</summary>
    /// <remarks>
    /// <b>Eight million, which is about eight layers at the per-layer bound.</b> The key is the URI itself, so
    /// the bound is on its length rather than on a count: a count of 256 would let 256 quarter-megabyte
    /// pictures hold 180 MB.
    /// </remarks>
    private const long MostCachedCharacters = 8_000_000;

    /// <summary>Pictures already read, by the exact <c>data:</c> URI they were read from.</summary>
    /// <remarks>
    /// <para>
    /// <b>Measured 2026-09-29 before it was added</b>: a stored renderer carrying 790 KB of pictures cost
    /// <b>3.0 ms</b> in <c>Cim.Project</c> alone — the base64 decode, the header read and the SHA-256 — on
    /// every FeatureServer layer document, tile style and map that read it, and 6.1 ms for the whole
    /// <c>drawingInfo</c> derivation. A stored document changes when it is written and is read far more often,
    /// so the picture is read once per content.
    /// </para>
    /// <para>
    /// <b>Keyed by the text, not by a digest of it</b>, so a hit is an exact match and two pictures can never
    /// be confused; comparing the key costs a memory compare, which is what the decode was being paid to
    /// avoid. Only pictures that were accepted are kept — a refusal is recomputed, and is rare.
    /// </para>
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MarkerPicture> Read =
        new(StringComparer.Ordinal);

    private static long _cachedCharacters;

    private readonly byte[] _bytes;

    private MarkerPicture(byte[] bytes, string contentType, int width, int height)
    {
        _bytes = bytes;
        ContentType = contentType;
        PixelWidth = width;
        PixelHeight = height;

        // <b>A hash of the bytes, not of the document around them.</b> Two layers carrying the same
        // file share one icon in the sheet, and a picture edited by one pixel is a new name — so a
        // client that cached the old sheet cannot draw the new picture under the old name.
        Hash = Convert.ToHexStringLower(SHA256.HashData(bytes).AsSpan(0, 12));

        double shrink = Math.Min(1.0, (double)SheetSide / Math.Max(width, height));

        SheetWidth = Math.Max(1, (int)Math.Round(width * shrink, MidpointRounding.AwayFromZero));
        SheetHeight = Math.Max(1, (int)Math.Round(height * shrink, MidpointRounding.AwayFromZero));
    }

    /// <summary>The encoded picture, as it arrived.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary><c>image/png</c> or <c>image/jpeg</c>, read from the bytes rather than trusted.</summary>
    public string ContentType { get; }

    /// <summary>The picture's own width, from its header.</summary>
    public int PixelWidth { get; }

    /// <summary>The picture's own height, from its header.</summary>
    public int PixelHeight { get; }

    /// <summary>Twelve bytes of the SHA-256 of the picture, as hex.</summary>
    public string Hash { get; }

    /// <summary>The icon's name in the generated sprite sheet.</summary>
    public string Name => NamePrefix + Hash;

    /// <summary>The icon's width in the 1x sprite sheet, which is also its width in style pixels.</summary>
    public int SheetWidth { get; }

    /// <summary>The icon's height in the 1x sprite sheet, which is what <c>icon-size</c> is relative to.</summary>
    public int SheetHeight { get; }

    /// <summary>The picture as base64, which is what Esri's <c>imageData</c> carries.</summary>
    public string Base64 => Convert.ToBase64String(_bytes);

    /// <summary>The picture as a <c>data:</c> URI, which is what CIM's <c>url</c> carries.</summary>
    public string DataUri => $"data:{ContentType};base64,{Base64}";

    /// <summary>How much wider than tall the picture is.</summary>
    public double Aspect => (double)PixelWidth / PixelHeight;

    /// <summary>
    /// Reads a CIM <c>url</c>, which this server accepts only as a <c>data:</c> URI.
    /// </summary>
    /// <param name="url">The URL as the document gave it.</param>
    /// <param name="where">Where in the document it sits, for a refusal that can be acted on.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="SymbologyException">It is not a picture this server will carry.</exception>
    public static MarkerPicture FromUrl(string? url, string where)
    {
        string text = (url ?? string.Empty).Trim();

        if (Read.TryGetValue(text, out MarkerPicture? known))
        {
            return known;
        }

        MarkerPicture picture = Unread(text, where);

        if (System.Threading.Interlocked.Add(ref _cachedCharacters, text.Length) > MostCachedCharacters)
        {
            Read.Clear();
            System.Threading.Interlocked.Exchange(ref _cachedCharacters, text.Length);
        }

        Read.TryAdd(text, picture);

        return picture;
    }

    /// <summary>Reads a <c>data:</c> URI that is not in the cache.</summary>
    private static MarkerPicture Unread(string text, string where)
    {
        if (text.Length == 0)
        {
            throw new SymbologyException(
                $"The picture marker at {where} has no `url`, so there is no picture to draw. Give the "
                + "picture as a data URI: `data:image/png;base64,…`.");
        }

        if (!text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            throw new SymbologyException(Unfetched(text, where));
        }

        int comma = text.IndexOf(',', StringComparison.Ordinal);

        if (comma < 0)
        {
            throw new SymbologyException(
                $"The picture marker at {where} has a data URI with no comma, so it carries no data.");
        }

        string header = text[5..comma];
        string[] parts = header.Split(';', StringSplitOptions.TrimEntries);

        if (!Array.Exists(parts, p => p.Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            throw new SymbologyException(
                $"The picture marker at {where} has a data URI that is not base64. A PNG or JPEG is "
                + "binary, so it travels as `data:image/png;base64,…`.");
        }

        return FromBase64(text[(comma + 1)..], parts[0], where);
    }

    /// <summary>
    /// Reads base64 picture data, with the content type the document declared for it.
    /// </summary>
    /// <param name="base64">The data.</param>
    /// <param name="declared">The declared type, or null or empty when the document gave none.</param>
    /// <param name="where">Where in the document it sits.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="SymbologyException">It is not a picture this server will carry.</exception>
    public static MarkerPicture FromBase64(string base64, string? declared, string where)
    {
        ArgumentNullException.ThrowIfNull(base64);

        string data = base64.Trim();

        // <b>Measured before it is decoded</b>, so a ten-megabyte string is refused without ten
        // megabytes being allocated for it. Four characters carry three bytes.
        if ((long)data.Length / 4 * 3 > MaximumBytes + 3)
        {
            throw new SymbologyException(TooLarge((long)data.Length / 4 * 3, where));
        }

        byte[] buffer = new byte[(data.Length / 4 * 3) + 3];

        if (!Convert.TryFromBase64String(data, buffer, out int written))
        {
            throw new SymbologyException(
                $"The picture at {where} is not valid base64, so it cannot be read as an image.");
        }

        return FromBytes(buffer.AsSpan(0, written).ToArray(), declared, where);
    }

    /// <summary>
    /// Checks encoded picture bytes and reads their size from the header.
    /// </summary>
    /// <param name="bytes">The encoded picture.</param>
    /// <param name="declared">The declared type, or null or empty for none.</param>
    /// <param name="where">Where in the document it sits.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="SymbologyException">It is not a picture this server will carry.</exception>
    public static MarkerPicture FromBytes(byte[] bytes, string? declared, string where)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length > MaximumBytes)
        {
            throw new SymbologyException(TooLarge(bytes.Length, where));
        }

        string? wanted = Declared(declared, where);

        if (!TryReadHeader(bytes, out string found, out int width, out int height, out string? error))
        {
            throw new SymbologyException($"The picture at {where} {error}");
        }

        if (wanted is not null && !string.Equals(wanted, found, StringComparison.Ordinal))
        {
            throw new SymbologyException(
                $"The picture at {where} is declared as `{declared}` and its bytes are a "
                + $"{(found == "image/png" ? "PNG" : "JPEG")}. A client decides how to read it from the "
                + "declaration, so the two have to agree.");
        }

        if (width > MaximumSide || height > MaximumSide)
        {
            throw new SymbologyException(
                $"The picture at {where} is {width} × {height} pixels, and a marker's picture may be at "
                + $"most {MaximumSide} on each side. A marker is drawn tens of pixels across; a larger "
                + "picture costs its decoding on every map and adds nothing a reader can see. Scale it "
                + "down before storing it.");
        }

        return new MarkerPicture(bytes, found, width, height);
    }

    /// <summary>
    /// An uploaded sprite sheet's picture, so the generated icons can be drawn beside it — ADR-099 §5.4.
    /// </summary>
    /// <remarks>
    /// <b>Bounded by ADR-092's rules, not a marker's.</b> A sheet was checked when it was uploaded — a
    /// PNG, at most 8 MB and 4096 pixels a side — and this repeats those checks rather than trusting
    /// the store, because it is about to be decoded. It is never a marker and never named.
    /// </remarks>
    /// <param name="png">The uploaded sheet's picture.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="SymbologyException">It is not a PNG within those bounds.</exception>
    public static MarkerPicture FromSheet(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        if (png.Length > 8 * 1024 * 1024
            || !TryReadHeader(png, out string type, out int width, out int height, out string? error)
            || type != "image/png"
            || width > 4096
            || height > 4096)
        {
            throw new SymbologyException(
                "The uploaded sprite sheet's picture is not a PNG of at most 8 MB and 4096 pixels a "
                + "side, so the generated icons cannot be drawn beside it.");
        }

        _ = error;

        return new MarkerPicture(png, type, width, height);
    }

    /// <summary>
    /// Reads a PNG's or a JPEG's size from its header, without decoding it.
    /// </summary>
    /// <param name="image">The encoded picture.</param>
    /// <param name="contentType">What its bytes are: <c>image/png</c> or <c>image/jpeg</c>.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    /// <param name="error">Why it could not be read, as the end of a sentence.</param>
    /// <returns>True when it is a PNG or a JPEG whose size could be read.</returns>
    /// <remarks>
    /// <b>PNG: the signature, then the 13-byte <c>IHDR</c> the format requires first.</b> <b>JPEG: the
    /// start-of-image marker, then segments skipped by their lengths until a start-of-frame</b>, whose
    /// height and width are at fixed offsets. Nothing past the header is read, and a length that would
    /// run past the end of the file is a refusal rather than a read.
    /// </remarks>
    public static bool TryReadHeader(
        ReadOnlySpan<byte> image, out string contentType, out int width, out int height, out string? error)
    {
        contentType = string.Empty;
        width = 0;
        height = 0;
        error = null;

        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        if (image.Length >= 8 && image[..8].SequenceEqual(png))
        {
            if (image.Length < 24
                || BinaryPrimitives.ReadUInt32BigEndian(image[8..12]) != 13
                || !image[12..16].SequenceEqual("IHDR"u8))
            {
                error = "begins like a PNG, but its first chunk is not the 13-byte IHDR header the "
                    + "format requires, so its size cannot be read. The file is damaged.";
                return false;
            }

            return Sized(
                "image/png",
                BinaryPrimitives.ReadUInt32BigEndian(image[16..20]),
                BinaryPrimitives.ReadUInt32BigEndian(image[20..24]),
                out contentType, out width, out height, out error);
        }

        if (image.Length >= 4 && image[0] == 0xFF && image[1] == 0xD8)
        {
            return Jpeg(image, out contentType, out width, out height, out error);
        }

        error = image.Length >= 5 && (image[..5].SequenceEqual("<?xml"u8) || image[..4].SequenceEqual("<svg"u8))
            ? "is an SVG. This server draws PNG and JPEG pictures: its rasteriser has no SVG reader, "
              + "and an SVG is a document that can name other resources, which this server does not "
              + "fetch. Export the icon as a PNG."
            : "is neither a PNG nor a JPEG. Those are the two picture formats this server draws; a GIF, "
              + "a WebP or an SVG has to be converted to one of them first.";

        return false;
    }

    /// <inheritdoc/>
    public bool Equals(MarkerPicture? other) =>
        other is not null && string.Equals(Hash, other.Hash, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as MarkerPicture);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Hash);

    /// <summary>The start-of-frame segment's size, reached by skipping the segments before it.</summary>
    private static bool Jpeg(
        ReadOnlySpan<byte> image, out string contentType, out int width, out int height, out string? error)
    {
        contentType = string.Empty;
        width = 0;
        height = 0;

        int at = 2;

        while (at + 4 <= image.Length)
        {
            if (image[at] != 0xFF)
            {
                break;
            }

            byte marker = image[at + 1];

            // Fill bytes before a marker are allowed and mean nothing.
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            at += 2;

            // Markers that carry no length: restarts, a second start of image, TEM.
            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            // The image data or its end, with no frame header before it.
            if (marker is 0xD9 or 0xDA)
            {
                break;
            }

            int length = (image[at] << 8) | image[at + 1];

            if (length < 2 || at + length > image.Length)
            {
                break;
            }

            // SOF0 to SOF15, except DHT (C4), JPG (C8) and DAC (CC), which share the range.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 7)
                {
                    break;
                }

                return Sized(
                    "image/jpeg",
                    (uint)((image[at + 5] << 8) | image[at + 6]),
                    (uint)((image[at + 3] << 8) | image[at + 4]),
                    out contentType, out width, out height, out error);
            }

            at += length;
        }

        error = "begins like a JPEG, but no frame header could be found before its image data, so its "
            + "size cannot be read. The file is damaged or truncated.";

        return false;
    }

    private static bool Sized(
        string type, uint wide, uint tall, out string contentType, out int width, out int height, out string? error)
    {
        contentType = type;
        width = 0;
        height = 0;
        error = null;

        if (wide == 0 || tall == 0 || wide > int.MaxValue || tall > int.MaxValue)
        {
            error = $"declares a size of {wide} × {tall} pixels, which has nothing to draw.";
            return false;
        }

        width = (int)wide;
        height = (int)tall;

        return true;
    }

    /// <summary>The declared content type, normalised, or null when none was declared.</summary>
    private static string? Declared(string? declared, string where)
    {
        string type = (declared ?? string.Empty).Trim().ToLowerInvariant();

        return type switch
        {
            "" => null,
            "image/png" => "image/png",
            "image/jpeg" or "image/jpg" or "image/pjpeg" => "image/jpeg",
            "image/svg+xml" => throw new SymbologyException(
                $"The picture at {where} is an SVG. This server draws PNG and JPEG pictures: its "
                + "rasteriser has no SVG reader, and an SVG is a document that can name other "
                + "resources, which this server does not fetch. Export the icon as a PNG."),
            _ => throw new SymbologyException(
                $"The picture at {where} is `{declared}`. This server draws PNG (`image/png`) and JPEG "
                + "(`image/jpeg`) pictures; convert it to one of them first."),
        };
    }

    /// <summary>The refusal for a picture over the byte bound.</summary>
    private static string TooLarge(long bytes, string where) =>
        $"The picture at {where} is {bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes, and a "
        + $"marker's picture may be at most {MaximumBytes / 1024} KB. It is stored inside the layer's "
        + "symbology and read on every request that draws the layer; an icon is a few kilobytes.";

    /// <summary>The refusal for a picture given by address rather than by content.</summary>
    private static string Unfetched(string url, string where)
    {
        string shown = url.Length > 80 ? url[..80] + "…" : url;

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("//", StringComparison.Ordinal)
            ? $"The picture marker at {where} names its picture by URL ('{shown}'). This server never "
              + "fetches a symbol's picture: a server that requests whatever address a document names "
              + "can be pointed at its own network. Put the picture in the document as a data URI — "
              + "`data:image/png;base64,…` — or, in an Esri symbol, as `imageData` with `contentType`."
            : $"The picture marker at {where} names its picture as '{shown}', which is not a data URI. "
              + "A relative name resolves against a resource this server does not hold, so the "
              + "picture has to travel in the document: `data:image/png;base64,…`, or `imageData` "
              + "with `contentType` in an Esri symbol.";
    }
}
