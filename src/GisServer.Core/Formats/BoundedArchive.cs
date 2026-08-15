using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace GisServer.Formats;

/// <summary>What an archive may contain before it is refused.</summary>
/// <param name="TotalUncompressedBytes">Everything, added up, as actually read.</param>
/// <param name="MemberBytes">Any single member.</param>
/// <param name="Members">How many entries.</param>
/// <param name="Ratio">Uncompressed divided by compressed, per member.</param>
public readonly record struct ArchiveLimits(
    long TotalUncompressedBytes,
    long MemberBytes,
    int Members,
    int Ratio)
{
    /// <summary>
    /// What a shapefile needs, and nothing beyond it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every number is a shapefile-shaped number rather than a round one.</b>
    /// A shapefile is three to six small files. 256 MB uncompressed is well past
    /// any single layer somebody uploads through a browser and far below what
    /// would trouble the machine; 32 members allows a few sidecars and a
    /// <c>__MACOSX</c> folder without allowing a directory tree.
    /// </para>
    /// <para>
    /// <b>The ratio cap is the one that stops a bomb.</b> A 42 KB zip that
    /// expands to 4.5 GB has a ratio near 100,000; ordinary shapefile content
    /// compresses somewhere between 2× and 20×. A hundred is far above anything
    /// real and far below anything hostile.
    /// </para>
    /// </remarks>
    public static ArchiveLimits ForShapefile => new(256L * 1024 * 1024, 256L * 1024 * 1024, 32, 100);
}

/// <summary>One member, read into memory under the limits.</summary>
/// <param name="Name">Its name inside the archive, without any path.</param>
/// <param name="Bytes">Its contents.</param>
public readonly record struct ArchiveMember(string Name, byte[] Bytes);

/// <summary>
/// Opens a ZIP, under bounds, and only for extensions the caller names.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a deliberate exception to a security rule, written as one.</b>
/// <see href="../../../docs/security.md">security.md</see>'s upload section says
/// archives are never opened — <em>decompression bombs are not our problem if we
/// never decompress</em> — and that rule is why GeoJSON shipped and shapefile
/// did not. A shapefile is a ZIP of three to six files, so accepting one means
/// breaking the rule or writing an exception. The owner chose the exception
/// (Q-98), and this class is the whole of it: nothing else in the server opens
/// an archive.
/// </para>
/// <para>
/// <b>The declared size is not trusted.</b> A ZIP's central directory states
/// each member's uncompressed length, and that number is written by whoever made
/// the file. Checking it is worth doing because it rejects the obvious case
/// cheaply, but the limit that matters is enforced <em>while reading</em> — the
/// read stops at the ceiling whatever the header claimed.
/// </para>
/// <para>
/// <b>Nothing is written to disk and no path is honoured.</b> Members are read
/// into memory, bounded above; a member whose name contains a directory
/// separator is refused rather than flattened, because flattening is how two
/// members silently become one and honouring it is how <c>../</c> escapes.
/// </para>
/// <para>
/// <b>Nested archives are refused by extension.</b> The caller lists what it
/// will read, and a shapefile import lists four extensions — none of which is
/// <c>.zip</c>. That makes recursive expansion impossible rather than bounded,
/// which is the stronger property and the cheaper one.
/// </para>
/// </remarks>
public static class BoundedArchive
{
    /// <summary>The bytes a ZIP begins with.</summary>
    /// <remarks>
    /// Checked so that a caller can tell an archive from a plain document
    /// without trusting a file name — which is user input, and which
    /// security.md says is data rather than instruction.
    /// </remarks>
    public static bool LooksLikeZip(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B
        && (bytes[2] == 0x03 || bytes[2] == 0x05 || bytes[2] == 0x07);

    /// <summary>Reads the members whose extension the caller asked for.</summary>
    /// <param name="archive">The ZIP.</param>
    /// <param name="extensions">Lower-case, with the dot. Nothing else is read.</param>
    /// <param name="limits">What it may contain.</param>
    /// <param name="members">What came out.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it was read.</returns>
    public static bool TryRead(
        Stream archive,
        IReadOnlyCollection<string> extensions,
        ArchiveLimits limits,
        out IReadOnlyList<ArchiveMember> members,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(extensions);

        members = [];
        error = null;

        List<ArchiveMember> read = [];
        long total = 0;

        ZipArchive zip;

        try
        {
            zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException e)
        {
            error = $"The upload is not a readable ZIP: {e.Message}";
            return false;
        }

        using (zip)
        {
            if (zip.Entries.Count > limits.Members)
            {
                error =
                    $"The archive holds {zip.Entries.Count} entries and the limit is "
                    + $"{limits.Members}. A shapefile is a handful of files; anything larger is "
                    + "not one.";
                return false;
            }

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                // A directory entry, which has no content and no name after the
                // separator. Skipped rather than refused: exporters emit them.
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue;
                }

                if (entry.FullName.Contains('/', StringComparison.Ordinal)
                    || entry.FullName.Contains('\\', StringComparison.Ordinal))
                {
                    // <b>Refused rather than flattened.</b> Flattening makes
                    // a/roads.shp and b/roads.shp the same member, and honouring
                    // the path is how ../ leaves the directory. A shapefile does
                    // not need folders.
                    //
                    // The one exception every Mac makes is skipped rather than
                    // refused, because refusing it would turn "zip this folder"
                    // into an error for half the people who try.
                    if (entry.FullName.StartsWith("__MACOSX", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    error =
                        $"The archive entry '{entry.FullName}' is inside a folder. Zip the "
                        + "shapefile's files directly rather than the folder holding them: a path "
                        + "inside an archive is not something this server will act on.";
                    return false;
                }

                string extension = Path.GetExtension(entry.FullName).ToLowerInvariant();

                if (!extensions.Contains(extension))
                {
                    // Ignored, not refused. A real export carries .sbn, .shx,
                    // .qmd, .xml and more, and refusing an archive because it is
                    // complete would be absurd.
                    continue;
                }

                if (entry.Length > limits.MemberBytes)
                {
                    error =
                        $"'{entry.FullName}' declares {entry.Length:N0} bytes and the limit is "
                        + $"{limits.MemberBytes:N0}.";
                    return false;
                }

                // <b>The ratio test, on the declared sizes.</b> Cheap, and it
                // rejects a bomb before a byte is decompressed. It is not the
                // bound — the reading limit below is — because both numbers here
                // were written by whoever made the file.
                if (entry.CompressedLength > 0
                    && entry.Length / entry.CompressedLength > limits.Ratio)
                {
                    error =
                        $"'{entry.FullName}' claims to expand {entry.Length / entry.CompressedLength}"
                        + $"× and the limit is {limits.Ratio}×. Shapefile content compresses far "
                        + "less than that; a ratio this high is a decompression bomb.";
                    return false;
                }

                long remaining = Math.Min(limits.MemberBytes, limits.TotalUncompressedBytes - total);

                if (!TryReadEntry(entry, remaining, out byte[] bytes, out error))
                {
                    return false;
                }

                total += bytes.Length;
                read.Add(new ArchiveMember(entry.FullName, bytes));
            }
        }

        if (read.Count == 0)
        {
            error =
                "The archive holds none of the files this import needs: "
                + string.Join(", ", extensions) + ".";
            return false;
        }

        members = read;
        return true;
    }

    /// <summary>
    /// Reads one member, stopping at the ceiling whatever the header claimed.
    /// </summary>
    /// <remarks>
    /// <b>This is the bound.</b> Everything above is a cheap early refusal on
    /// numbers the archive supplied about itself. A member that decompresses
    /// past its allowance stops here, with the archive named — and it stops
    /// having allocated the allowance and not a byte more.
    /// </remarks>
    private static bool TryReadEntry(
        ZipArchiveEntry entry, long allowance, out byte[] bytes, out string? error)
    {
        bytes = [];
        error = null;

        if (allowance <= 0)
        {
            error =
                "The archive's members add up to more than the uncompressed limit for an import.";
            return false;
        }

        using Stream source = entry.Open();
        using MemoryStream destination = new();

        byte[] buffer = new byte[81_920];

        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);

            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > allowance)
            {
                error =
                    $"'{entry.FullName}' expanded past {allowance.ToString("N0", CultureInfo.InvariantCulture)} "
                    + "bytes while being read. The declared size said otherwise, which is why the "
                    + "limit is enforced here rather than believed there.";
                return false;
            }

            destination.Write(buffer, 0, read);
        }

        bytes = destination.ToArray();
        return true;
    }
}
