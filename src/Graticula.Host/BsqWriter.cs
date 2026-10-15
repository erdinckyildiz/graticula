using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Graticula.Host;

/// <summary>
/// An image service's values as ArcGIS Pro asks for them to draw and to export — <c>exportImage?format=None&amp;bsq=true</c>:
/// band after band, then a validity mask — owner decision 2026-10-15, Q-168.
/// </summary>
/// <remarks>
/// <para>
/// <b>Learned by measuring a public service's output, not from a document or a source.</b> The REST reference names
/// <c>bsq</c>; it does not describe the bytes. Esri's public Terrain3D image service, asked
/// <c>size=4,3&amp;format=None&amp;pixelType=F32&amp;bsq=true&amp;noData=0</c>, answers <c>application/octet-stream</c> of 50
/// bytes: twelve little-endian floats, row by row, then <c>fff0</c> — one bit a pixel, most significant first, set
/// where the pixel holds a value, padded to the byte; ten pixels give <c>ffc0</c>, and eight with nothing under them
/// (across the antimeridian) give eight zeros and <c>00</c>, the values written as the <c>noData</c> asked. The owner
/// accepted that measuring a public service's answer is where the line of CLAUDE.md §5 allows learning it.
/// </para>
/// <para>
/// <b>Several bands are written one after another, then one mask</b> — band sequential is what <c>bsq</c> names. No
/// public multiband service measured answers <c>format=None</c> with values (Esri's samples draw a picture), so this
/// half is checked against Pro itself: what it writes from the answer is compared with the values served.
/// </para>
/// </remarks>
internal static class BsqWriter
{
    /// <summary>The pixel types this answer writes, by the name a request gives them.</summary>
    /// <param name="name">The request's <c>pixelType</c>, or null for the image's own.</param>
    /// <param name="own">The image's own type.</param>
    /// <param name="type">What to write.</param>
    /// <returns>Whether the name is one this writes.</returns>
    public static bool TryPixelType(string? name, Coverages.SampleKind own, out string type)
    {
        type = string.IsNullOrWhiteSpace(name) || name.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
            ? own switch
            {
                Coverages.SampleKind.Unsigned8 => "U8",
                Coverages.SampleKind.Signed16 => "S16",
                Coverages.SampleKind.Unsigned16 => "U16",
                Coverages.SampleKind.Signed32 => "S32",
                Coverages.SampleKind.Real64 => "F64",
                _ => "F32",
            }
            : name.Trim().ToUpperInvariant();

        return type is "U8" or "S8" or "U16" or "S16" or "U32" or "S32" or "F32" or "F64";
    }

    /// <summary>The bytes one value of a type takes.</summary>
    private static int Size(string type) => type switch
    {
        "U8" or "S8" => 1,
        "U16" or "S16" => 2,
        "U32" or "S32" or "F32" => 4,
        _ => 8,
    };

    /// <summary>The answer.</summary>
    /// <param name="samples">The values, pixel-interleaved, row by row.</param>
    /// <param name="bands">How many a pixel.</param>
    /// <param name="width">Pixels a row.</param>
    /// <param name="height">Rows.</param>
    /// <param name="valid">Whether each pixel holds a value.</param>
    /// <param name="type">The pixel type to write, from <see cref="TryPixelType"/>.</param>
    /// <param name="noData">The request's <c>noData</c>, one value or one a band; what an empty pixel is written as.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Write(
        double[] samples, int bands, int width, int height, bool[] valid, string type, string? noData)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(valid);

        int pixels = width * height;
        int size = Size(type);
        double[] empty = EmptyValues(noData, bands);
        byte[] answer = new byte[(bands * pixels * size) + ((pixels + 7) / 8)];
        Span<byte> into = answer;

        for (int band = 0; band < bands; band++)
        {
            for (int i = 0; i < pixels; i++)
            {
                double value = valid[i] ? samples[(i * bands) + band] : empty[band];
                Put(into.Slice(((band * pixels) + i) * size, size), type, value);
            }
        }

        int mask = bands * pixels * size;

        for (int i = 0; i < pixels; i++)
        {
            if (valid[i])
            {
                answer[mask + (i >> 3)] |= (byte)(0x80 >> (i & 7));
            }
        }

        return answer;
    }

    private static double[] EmptyValues(string? noData, int bands)
    {
        double[] values = new double[bands];
        string[] given = (noData ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int band = 0; band < bands && given.Length > 0; band++)
        {
            values[band] = double.TryParse(given[Math.Min(band, given.Length - 1)], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : 0;
        }

        return values;
    }

    private static void Put(Span<byte> at, string type, double value)
    {
        double whole = double.IsNaN(value) ? 0 : Math.Round(value);

        switch (type)
        {
            case "U8":
                at[0] = (byte)Math.Clamp(whole, byte.MinValue, byte.MaxValue);
                break;
            case "S8":
                at[0] = unchecked((byte)(sbyte)Math.Clamp(whole, sbyte.MinValue, sbyte.MaxValue));
                break;
            case "U16":
                BinaryPrimitives.WriteUInt16LittleEndian(at, (ushort)Math.Clamp(whole, ushort.MinValue, ushort.MaxValue));
                break;
            case "S16":
                BinaryPrimitives.WriteInt16LittleEndian(at, (short)Math.Clamp(whole, short.MinValue, short.MaxValue));
                break;
            case "U32":
                BinaryPrimitives.WriteUInt32LittleEndian(at, (uint)Math.Clamp(whole, uint.MinValue, uint.MaxValue));
                break;
            case "S32":
                BinaryPrimitives.WriteInt32LittleEndian(at, (int)Math.Clamp(whole, int.MinValue, int.MaxValue));
                break;
            case "F32":
                BinaryPrimitives.WriteSingleLittleEndian(at, (float)value);
                break;
            default:
                BinaryPrimitives.WriteDoubleLittleEndian(at, value);
                break;
        }
    }
}
