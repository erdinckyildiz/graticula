using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GisServer.Formats;

/// <summary>The four files an import reads out of a shapefile archive.</summary>
/// <param name="Name">The base name they share.</param>
/// <param name="Shp">The geometry.</param>
/// <param name="Dbf">The attribute table, empty when absent.</param>
/// <param name="Prj">The projection as WKT, or null.</param>
/// <param name="Cpg">The declared code page, or null.</param>
public readonly record struct ShapefileBundle(
    string Name, byte[] Shp, byte[] Dbf, string? Prj, string? Cpg)
{
    /// <summary>The extensions an import will read out of an archive.</summary>
    /// <remarks>
    /// <b>Four, and none of them is <c>.zip</c>.</b> That is what makes a nested
    /// archive impossible rather than depth-limited. <c>.shx</c> is deliberately
    /// absent: it is an index into the <c>.shp</c> and this reader walks the
    /// records directly, so reading it would be trusting a second copy of
    /// information it already has.
    /// </remarks>
    public static IReadOnlyList<string> Extensions => [".shp", ".dbf", ".prj", ".cpg"];

    /// <summary>
    /// Picks the one shapefile out of an archive's members.
    /// </summary>
    /// <param name="members">What the archive yielded.</param>
    /// <param name="bundle">The files that belong together.</param>
    /// <param name="error">Why it could not be assembled.</param>
    /// <returns>Whether exactly one shapefile was found.</returns>
    /// <remarks>
    /// <b>Exactly one, and two is an error rather than a choice.</b> An archive
    /// holding <c>roads.shp</c> and <c>rivers.shp</c> is two layers, and
    /// importing the alphabetically first one silently is worse than saying so —
    /// the person gets a layer, it is the wrong one, and nothing indicates that.
    /// </remarks>
    public static bool TryAssemble(
        IReadOnlyList<ArchiveMember> members,
        out ShapefileBundle bundle,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(members);

        bundle = default;
        error = null;

        string[] names =
        [
            .. members
                .Where(m => Path.GetExtension(m.Name)
                    .Equals(".shp", StringComparison.OrdinalIgnoreCase))
                .Select(m => Path.GetFileNameWithoutExtension(m.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];

        if (names.Length == 0)
        {
            error = "The archive has no .shp in it, so there is no shapefile to import.";
            return false;
        }

        if (names.Length > 1)
        {
            error =
                $"The archive holds {names.Length} shapefiles ({string.Join(", ", names)}). Import "
                + "one at a time: picking one for you would give you a layer without telling you "
                + "it was not the layer you meant.";
            return false;
        }

        string name = names[0];

        byte[] Member(string extension) =>
            members.FirstOrDefault(m =>
                Path.GetFileNameWithoutExtension(m.Name)
                    .Equals(name, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(m.Name).Equals(extension, StringComparison.OrdinalIgnoreCase))
                .Bytes ?? [];

        byte[] prj = Member(".prj");
        byte[] cpg = Member(".cpg");

        bundle = new ShapefileBundle(
            name,
            Member(".shp"),
            Member(".dbf"),
            prj.Length > 0 ? Encoding.UTF8.GetString(prj).Trim() : null,
            cpg.Length > 0 ? Encoding.ASCII.GetString(cpg).Trim() : null);

        return true;
    }

    /// <summary>
    /// The encoding to read the DBF with, or a refusal saying why not.
    /// </summary>
    /// <param name="requested">What the caller stated, if anything.</param>
    /// <param name="encoding">What to use.</param>
    /// <param name="error">Why the file cannot be read without being told.</param>
    /// <returns>Whether an encoding could be settled on.</returns>
    /// <remarks>
    /// <para>
    /// <b>Refused rather than guessed — owner decision, Q-98.</b> A DBF has no
    /// reliable declaration. Reading Windows-1254 bytes as UTF-8 does not throw
    /// and does not look broken: it produces a string, and the damage surfaces
    /// months later in somebody's map labels. Between a little friction at
    /// import and silent corruption, the friction is cheaper.
    /// </para>
    /// <para>
    /// <b>The caller's word beats the file's.</b> A <c>.cpg</c> is written by
    /// whichever tool exported the data and is frequently wrong; somebody who
    /// has looked at their own data and typed an encoding knows more than the
    /// exporter did.
    /// </para>
    /// </remarks>
    public bool TryEncoding(
        string? requested, out Encoding encoding, out string? error)
    {
        encoding = Encoding.UTF8;
        error = null;

        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        foreach (string? candidate in (string?[])[requested, Cpg])
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (TryName(candidate.Trim(), out encoding))
            {
                return true;
            }

            if (candidate == requested)
            {
                error =
                    $"'{candidate}' is not an encoding this server knows. Use a name such as "
                    + "UTF-8, ISO8859-9 or windows-1254, or a bare code page number.";
                return false;
            }
        }

        // No .cpg the caller or the file could agree on. A DBF whose *records*
        // are pure ASCII is unambiguous, so it is not worth refusing.
        if (RecordsAreAscii(Dbf))
        {
            encoding = Encoding.UTF8;
            return true;
        }

        error =
            "This shapefile's .dbf holds bytes above 127 and carries no .cpg saying what they "
            + "mean. Reading Windows-1254 as UTF-8 produces text rather than an error, so the "
            + "damage would be silent — send 'encoding' with the import (for example "
            + "windows-1254 or UTF-8).";

        return false;
    }

    private static bool TryName(string name, out Encoding encoding)
    {
        encoding = Encoding.UTF8;

        // ESRI writes several spellings for the same thing, and none of them is
        // what .NET calls it.
        string normalised = name.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        int page = normalised switch
        {
            "utf8" or "65001" => 65001,
            "iso88599" or "latin5" or "28599" => 28599,
            "windows1254" or "cp1254" or "1254" => 1254,
            "iso88591" or "latin1" or "28591" => 28591,
            "windows1252" or "cp1252" or "1252" => 1252,
            _ => 0,
        };

        try
        {
            encoding = page > 0 ? Encoding.GetEncoding(page) : Encoding.GetEncoding(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the DBF's record area holds only ASCII.
    /// </summary>
    /// <remarks>
    /// <b>The records, not the file.</b> Scanning the whole thing catches the
    /// header, where byte 8 is the header <em>length</em> as a little-endian
    /// short — 129 for a table with four fields. Read as text that is a
    /// non-ASCII byte, so every shapefile with more than about three columns
    /// demanded an encoding it did not need. Found by importing a file whose
    /// attributes were the words "first" and "second".
    /// </remarks>
    private static bool RecordsAreAscii(ReadOnlySpan<byte> dbf)
    {
        if (dbf.Length < 12)
        {
            return true;
        }

        int headerBytes = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(dbf[8..]);

        if (headerBytes < 32 || headerBytes >= dbf.Length)
        {
            // Not a header this can reason about. Refusing to guess the encoding
            // is the safe direction; the reader will reject the file anyway.
            return false;
        }

        foreach (byte b in dbf[headerBytes..])
        {
            if (b > 127)
            {
                return false;
            }
        }

        return true;
    }
}
