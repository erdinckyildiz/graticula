using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Graticula.Host;

/// <summary>
/// A Shapefile's field names, as ADR-106 §5.6 says they are cut, and the list that says what each was — condition 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>A dBase field name is ten bytes.</b> A name that fits in ten bytes of UTF-8 is kept; a longer one is cut at ten
/// bytes on a character boundary; one that then repeats an earlier name — compared without case, as dBase does — is cut
/// to eight bytes and given <c>_1</c> … <c>_9</c>, then seven and <c>_10</c> …, in field order, so the same layer always
/// gets the same names. Every name that changed is listed in <c>&lt;layer&gt;.fieldnames.csv</c> beside the layer.
/// </para>
/// <para>
/// <b>Applied to the file GDAL wrote, not given to GDAL.</b> GDAL cuts at ten bytes too, but by bytes: a Turkish letter
/// at the ninth and tenth byte is split, and the file holds a name that is not UTF-8. So the names GDAL chose are
/// rewritten in the <c>.dbf</c>'s field descriptors, after checking that the file has the fields expected, in the order
/// expected — when it does not, nothing is rewritten and GDAL's names stand.
/// </para>
/// </remarks>
internal static class ShapefileFieldNames
{
    private const int NameBytes = 10;

    /// <summary>§5.6's names for a layer's fields, in their order.</summary>
    /// <param name="fields">The served names.</param>
    /// <returns>The Shapefile names, one per field.</returns>
    public static IReadOnlyList<string> Of(IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        List<string> names = [];
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

        foreach (string field in fields)
        {
            string name = Cut(field, NameBytes);

            for (int n = 1; !taken.Add(name); n++)
            {
                string suffix = "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                name = Cut(field, NameBytes - suffix.Length) + suffix;
            }

            names.Add(name);
        }

        return names;
    }

    /// <summary>A name cut to at most <paramref name="bytes"/> bytes of UTF-8, never inside a character.</summary>
    internal static string Cut(string name, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(name) <= bytes)
        {
            return name;
        }

        StringBuilder kept = new();
        int used = 0;

        foreach (System.Text.Rune rune in name.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > bytes)
            {
                break;
            }

            kept.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }

        return kept.ToString();
    }

    /// <summary>The field names a <c>.dbf</c>'s descriptors hold, in order.</summary>
    internal static IReadOnlyList<string> Read(byte[] dbf)
    {
        List<string> names = [];

        for (int at = 32; at + 32 <= dbf.Length && dbf[at] != 0x0D; at += 32)
        {
            int length = Array.IndexOf(dbf, (byte)0, at, 11) is int end and >= 0 ? end - at : 11;
            names.Add(Encoding.UTF8.GetString(dbf, at, length));
        }

        return names;
    }

    /// <summary>
    /// Rewrites a written Shapefile's field names to §5.6's and lists every one that changed, or leaves both alone when
    /// the file does not hold the fields expected.
    /// </summary>
    /// <param name="folder">The folder GDAL wrote the layer's files into.</param>
    /// <param name="layer">The layer's file name, without extension.</param>
    /// <param name="fields">The served field names, in the order they were staged.</param>
    /// <returns>Whether the names were rewritten.</returns>
    public static bool Apply(string folder, string layer, IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        string path = Path.Combine(folder, layer + ".dbf");

        if (!File.Exists(path))
        {
            return false;
        }

        byte[] dbf = File.ReadAllBytes(path);
        IReadOnlyList<string> written = Read(dbf);
        IReadOnlyList<string> wanted = Of(fields);

        // The same fields in the same order: each name GDAL wrote begins as the served one does, to eight bytes.
        if (written.Count != wanted.Count
            || written.Where((name, i) => !SharePrefix(name, fields[i])).Any())
        {
            return false;
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            byte[] slot = new byte[11];
            Encoding.UTF8.GetBytes(wanted[i]).CopyTo(slot, 0);
            slot.CopyTo(dbf, 32 + (i * 32));
        }

        File.WriteAllBytes(path, dbf);

        List<string> lines = [];

        for (int i = 0; i < fields.Count; i++)
        {
            if (!string.Equals(fields[i], wanted[i], StringComparison.Ordinal))
            {
                lines.Add(Csv(fields[i]) + "," + Csv(wanted[i]));
            }
        }

        if (lines.Count > 0)
        {
            File.WriteAllText(
                Path.Combine(folder, layer + ".fieldnames.csv"),
                "served,shapefile\r\n" + string.Join("\r\n", lines) + "\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        return true;
    }

    private static bool SharePrefix(string written, string served)
    {
        byte[] a = Encoding.UTF8.GetBytes(written);
        byte[] b = Encoding.UTF8.GetBytes(served);
        int length = Math.Min(Math.Min(a.Length, b.Length), 7);
        return a.AsSpan(0, length).SequenceEqual(b.AsSpan(0, length))
            || string.Equals(written, served, StringComparison.OrdinalIgnoreCase);
    }

    private static string Csv(string value) =>
        value.IndexOfAny(['"', ',', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
