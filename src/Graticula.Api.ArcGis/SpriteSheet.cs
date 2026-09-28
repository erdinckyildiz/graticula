using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Graticula.Api.ArcGis;

/// <summary>
/// A sprite sheet somebody uploaded — an index and the picture it indexes — checked before it is
/// stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-092: the sheet stops being empty because a publisher can now give it icons.</b> A MapLibre
/// client draws an <c>icon-image</c> by looking its name up in <c>sprite.json</c> and cutting the
/// rectangle it finds there out of <c>sprite.png</c>. A name missing from the index, or a rectangle
/// outside the picture, draws nothing and reports nothing — the same silent blank the style
/// validator exists to prevent — so those are the checks, and they run while the uploader is still
/// holding the files.
/// </para>
/// <para>
/// <b>The picture is never decoded.</b> Its width and height are read from the PNG header, which
/// is the first twenty-four bytes and in a fixed place; the pixels are the browser's to decode.
/// An image decoder in the server process would be an attacker-facing binary parser — the
/// argument ADR-027 used to keep a font parser out — and nothing here needs a pixel.
/// </para>
/// <para>
/// <b>The index is stored as it was sent</b>, like a style: it is a file somebody generated and
/// will diff against their own copy. Members this server does not check — <c>sdf</c>,
/// <c>content</c>, <c>stretchX</c> and whatever the specification adds next — are passed through
/// untouched.
/// </para>
/// </remarks>
public static class SpriteSheet
{
    /// <summary>The largest picture this server will store.</summary>
    /// <remarks>
    /// Eight megabytes. A sheet of a few hundred icons at 2x is a few hundred kilobytes, so this is
    /// a bound on abuse rather than a limit a real sheet meets; the column carries the same number.
    /// </remarks>
    public const int MaximumImageBytes = 8 * 1024 * 1024;

    /// <summary>The largest index this server will store.</summary>
    /// <remarks>The style's bound, for the same reason: real ones are tens of kilobytes.</remarks>
    public const int MaximumIndexBytes = 1024 * 1024;

    /// <summary>The widest or tallest picture this server will store.</summary>
    /// <remarks>
    /// <b>4096 pixels, because that is what a client can upload as one texture.</b> A GPU's
    /// smallest guaranteed texture size is in that range, and a sheet past it fails to draw on
    /// exactly the machines nobody tested on.
    /// </remarks>
    public const int MaximumSide = 4096;

    /// <summary>The most icons one index may name.</summary>
    public const int MaximumIcons = 10_000;

    /// <summary>The longest icon name.</summary>
    public const int MaximumNameLength = 256;

    /// <summary>The eight bytes every PNG begins with.</summary>
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The first chunk's type, which the format requires to be the header.</summary>
    private static ReadOnlySpan<byte> HeaderType => "IHDR"u8;

    /// <summary>
    /// Reads a PNG's width and height from its header, and nothing else.
    /// </summary>
    /// <param name="png">The file.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    /// <param name="error">Why it is not a picture this server will store.</param>
    /// <returns>True when the header is a PNG header and the size is within bounds.</returns>
    /// <remarks>
    /// <b>The signature, then the header chunk, then two big-endian numbers at 16 and 20.</b> The
    /// format fixes all of it: the first chunk is <c>IHDR</c>, its data is thirteen bytes, and the
    /// width and height are the first eight. Anything else in the first twenty-four bytes is not a
    /// PNG, whatever its name says.
    /// </remarks>
    public static bool TryReadSize(ReadOnlySpan<byte> png, out int width, out int height, out string? error)
    {
        width = 0;
        height = 0;
        error = null;

        if (png.Length > MaximumImageBytes)
        {
            error = $"A sprite image may be at most {MaximumImageBytes / (1024 * 1024)} MB, and this one is "
                  + $"{png.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes. A real sheet is a few "
                  + "hundred kilobytes; one this size is usually the wrong file.";
            return false;
        }

        if (png.Length < 24 || !png[..8].SequenceEqual(Signature))
        {
            error = "The sprite image is not a PNG: it does not begin with the PNG signature. A client reads "
                  + "sprite.png as PNG whatever it contains, so another format would draw nothing.";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(png[8..12]) != 13 || !png[12..16].SequenceEqual(HeaderType))
        {
            error = "The sprite image begins like a PNG but its first chunk is not the 13-byte IHDR header "
                  + "the format requires, so its size cannot be read. The file is damaged or not a PNG.";
            return false;
        }

        uint wide = BinaryPrimitives.ReadUInt32BigEndian(png[16..20]);
        uint tall = BinaryPrimitives.ReadUInt32BigEndian(png[20..24]);

        if (wide == 0 || tall == 0 || wide > MaximumSide || tall > MaximumSide)
        {
            error = $"The sprite image is {wide} × {tall} pixels. Each side must be between 1 and "
                  + $"{MaximumSide}: a side of 0 has nothing to cut an icon from, and past {MaximumSide} "
                  + "some graphics cards cannot hold the sheet as one texture and draw no icons at all.";
            return false;
        }

        width = (int)wide;
        height = (int)tall;

        return true;
    }

    /// <summary>
    /// Checks an index against the picture it indexes.
    /// </summary>
    /// <param name="index">The index as it was sent.</param>
    /// <param name="png">The picture.</param>
    /// <param name="pixelRatio">Which sheet this is: 1, or 2 for <c>@2x</c>.</param>
    /// <param name="icons">The icon names the index defines, in the order it defines them.</param>
    /// <param name="error">Why it was refused, naming the icon and the numbers.</param>
    /// <returns>True when it is safe to store.</returns>
    public static bool TryValidate(
        string? index, ReadOnlySpan<byte> png, int pixelRatio, out IReadOnlyList<string> icons, out string? error)
    {
        icons = [];

        if (pixelRatio is not (1 or 2))
        {
            error = $"A sprite sheet's pixel ratio is 1 or 2, not {pixelRatio}: those are the two sheets a "
                  + "client asks for, sprite and sprite@2x.";
            return false;
        }

        if (!TryReadSize(png, out int width, out int height, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(index))
        {
            error = "A sprite index is required: sprite.json, the document that says where each icon is in "
                  + "the picture.";
            return false;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(index) > MaximumIndexBytes)
        {
            error = $"A sprite index may be at most {MaximumIndexBytes / 1024} KB. Real ones are tens of "
                  + "kilobytes; one this size is usually a generated file sent by mistake.";
            return false;
        }

        JsonDocument document;

        try
        {
            // An index is two levels deep, three with `content` or `stretchX`; the bound is the
            // reader's, for the reason the style gives.
            document = JsonDocument.Parse(index, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException e)
        {
            error = $"The sprite index is not valid JSON: {e.Message}";
            return false;
        }

        using (document)
        {
            return Entries(document.RootElement, width, height, pixelRatio, out icons, out error);
        }
    }

    /// <summary>
    /// The icon names a stored index defines, for checking a style against it.
    /// </summary>
    /// <param name="index">The stored index, or null when there is no sheet.</param>
    /// <returns>The names, or an empty list when there are none or it cannot be read.</returns>
    /// <remarks>
    /// <b>Lenient, because it reads what was already checked.</b> An index reaches the store only
    /// through <see cref="TryValidate"/>; this is the serving side's question about it, and an
    /// answer of <em>no names</em> for something unreadable makes a style that uses one refuse
    /// rather than draw nothing.
    /// </remarks>
    public static IReadOnlyList<string> IconNames(string? index)
    {
        if (string.IsNullOrWhiteSpace(index))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(index, new JsonDocumentOptions { MaxDepth = 16 });

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? [.. document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Entries(
        JsonElement root, int width, int height, int pixelRatio, out IReadOnlyList<string> icons, out string? error)
    {
        icons = [];
        error = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "A sprite index is a JSON object whose keys are icon names and whose values say where "
                  + "each icon is in the picture.";
            return false;
        }

        List<string> names = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonProperty entry in root.EnumerateObject())
        {
            if (names.Count == MaximumIcons)
            {
                error = $"A sprite index may name at most {MaximumIcons.ToString("N0", CultureInfo.InvariantCulture)} "
                      + "icons. A sheet that size is several sheets, and a client lays the whole index out "
                      + "before it draws the first one.";
                return false;
            }

            string name = entry.Name;

            if (name.Length is 0 or > MaximumNameLength)
            {
                error = name.Length == 0
                    ? "An icon in the sprite index has an empty name, and a style has no way to ask for it."
                    : $"The icon name '{name[..40]}…' is {name.Length} characters long; names may be at most "
                      + $"{MaximumNameLength}.";
                return false;
            }

            if (!seen.Add(name))
            {
                error = $"The sprite index names '{name}' twice. A client keeps whichever it read last, so "
                      + "one of the two rectangles would silently never draw.";
                return false;
            }

            if (!Entry(name, entry.Value, width, height, pixelRatio, out error))
            {
                return false;
            }

            names.Add(name);
        }

        icons = names;

        return true;
    }

    /// <summary>One icon's rectangle, which must lie inside the picture.</summary>
    /// <remarks>
    /// <b>The check that pays for this type</b>, as the source-layer check pays for the style's: a
    /// rectangle past the edge of the sheet draws transparent pixels or a neighbour's, and nothing
    /// anywhere says so.
    /// </remarks>
    private static bool Entry(string name, JsonElement value, int width, int height, int pixelRatio, out string? error)
    {
        error = null;

        if (value.ValueKind != JsonValueKind.Object)
        {
            error = $"Icon '{name}' in the sprite index is not an object. Each icon is "
                  + "{\"x\", \"y\", \"width\", \"height\"}, in pixels of the picture.";
            return false;
        }

        if (!Whole(name, value, "x", 0, out int x, out error)
            || !Whole(name, value, "y", 0, out int y, out error)
            || !Whole(name, value, "width", 1, out int wide, out error)
            || !Whole(name, value, "height", 1, out int tall, out error))
        {
            return false;
        }

        if ((long)x + wide > width || (long)y + tall > height)
        {
            error = $"Icon '{name}' is the rectangle x {x}, y {y}, {wide} × {tall}, which reaches "
                  + $"{(long)x + wide} across and {(long)y + tall} down — past the edge of the {width} × {height} "
                  + "picture. A client would cut it from pixels that are not there and draw nothing.";
            return false;
        }

        if (value.TryGetProperty("pixelRatio", out JsonElement ratio)
            && (ratio.ValueKind != JsonValueKind.Number || ratio.GetDouble() != pixelRatio))
        {
            error = $"Icon '{name}' says \"pixelRatio\": {ratio.GetRawText()}, and this is the "
                  + $"{(pixelRatio == 1 ? "1x" : "@2x")} sheet, whose ratio is {pixelRatio}. A client divides "
                  + "the rectangle by the ratio the index states, so a wrong one draws the icon at the wrong size.";
            return false;
        }

        return true;
    }

    private static bool Whole(string name, JsonElement value, string member, int least, out int number, out string? error)
    {
        error = null;

        if (value.TryGetProperty(member, out JsonElement found)
            && found.ValueKind == JsonValueKind.Number
            && found.TryGetInt32(out number)
            && number >= least)
        {
            return true;
        }

        number = 0;
        error = found.ValueKind == JsonValueKind.Undefined
            ? $"Icon '{name}' has no \"{member}\". Each icon needs x, y, width and height, in pixels of the picture."
            : $"Icon '{name}' has \"{member}\": {found.GetRawText()}, and it must be a whole number of at least "
              + $"{least}.";

        return false;
    }
}
