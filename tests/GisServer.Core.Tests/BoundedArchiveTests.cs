using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using GisServer.Formats;
using Xunit;

namespace GisServer.Core.Tests;

/// <summary>
/// The exception to <em>never decompress</em>, and the bounds that buy it.
/// </summary>
/// <remarks>
/// <para>
/// <b>security.md's upload section says archives are never opened</b> —
/// <em>decompression bombs are not our problem if we never decompress</em> —
/// and that rule is why GeoJSON shipped and shapefile did not. The owner chose a
/// bounded exception (Q-98). These tests are what "bounded" is worth: each one
/// closes a way in that the rule used to close for free.
/// </para>
/// <para>
/// The corpus is built by <c>tools/make-shapefile-corpus.py</c>, including a
/// real 200 MB-from-200 KB bomb.
/// </para>
/// </remarks>
public sealed class BoundedArchiveTests
{
    private static readonly string[] Shapefile = [".shp", ".dbf", ".prj", ".cpg"];

    private static string Corpus =>
        Path.Combine(AppContext.BaseDirectory, "corpus", "shapefile");

    private static bool TryRead(
        string name,
        out IReadOnlyList<ArchiveMember> members,
        out string? error,
        ArchiveLimits? limits = null)
    {
        string path = Path.Combine(Corpus, name);

        Assert.True(File.Exists(path), $"The corpus file {path} is missing.");

        using FileStream file = File.OpenRead(path);

        return BoundedArchive.TryRead(
            file, Shapefile, limits ?? ArchiveLimits.ForShapefile, out members, out error);
    }

    [Fact]
    public void An_ordinary_shapefile_archive_opens()
    {
        Assert.True(TryRead("points.zip", out IReadOnlyList<ArchiveMember> members, out string? e), e);

        Assert.Contains(members, m => m.Name.EndsWith(".shp", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Name.EndsWith(".dbf", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_extensions_the_caller_named_come_out()
    {
        Assert.True(TryRead("points.zip", out IReadOnlyList<ArchiveMember> members, out _));

        Assert.All(members, m =>
            Assert.Contains(Path.GetExtension(m.Name).ToLowerInvariant(), Shapefile));
    }

    /// <summary>
    /// A 200 MB member compressed to 200 KB is refused.
    /// </summary>
    /// <remarks>
    /// <b>The case the rule existed to make impossible.</b> Refused on the
    /// declared ratio, before a byte is decompressed — which is why this test
    /// takes milliseconds rather than allocating 200 MB to find out.
    /// </remarks>
    [Fact]
    public void A_decompression_bomb_is_refused_before_it_expands()
    {
        Assert.False(TryRead("bomb.zip", out _, out string? error));

        Assert.Contains("bomb", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_bomb_is_refused_by_the_reading_limit_even_when_the_ratio_check_is_disabled()
    {
        // <b>Two independent controls, and this proves the second one alone
        // works.</b> The ratio test reads numbers the archive supplied about
        // itself; a hostile archive can lie about both. The reading limit is the
        // one that cannot be lied to, so it has to hold on its own.
        Assert.False(
            TryRead(
                "bomb.zip",
                out _,
                out string? error,
                // A member ceiling above the declared size and a ratio cap of
                // infinity, so both of the checks that read the archive's own
                // numbers pass — leaving only the total, enforced while reading.
                new ArchiveLimits(1024 * 1024, 300L * 1024 * 1024, 32, int.MaxValue)));

        Assert.Contains("expanded past", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_inside_a_folder_is_refused_rather_than_flattened()
    {
        // Flattening makes a/roads.shp and b/roads.shp the same member;
        // honouring the path is how ../ leaves the directory.
        Assert.False(TryRead("nested.zip", out _, out string? error));

        Assert.Contains("inside a folder", error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An archive inside an archive is not opened.
    /// </summary>
    /// <remarks>
    /// <b>Impossible rather than bounded.</b> The caller lists four extensions
    /// and none is <c>.zip</c>, so recursion has nowhere to start — a stronger
    /// property than a depth limit, and cheaper.
    /// </remarks>
    [Fact]
    public void A_nested_archive_is_not_opened()
    {
        Assert.False(TryRead("russian_doll.zip", out _, out string? error));

        // Refused because nothing inside it matched, not because a depth counter
        // ran out.
        Assert.Contains("holds none of the files", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_members_is_refused()
    {
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i < 40; i++)
            {
                using Stream entry = zip.CreateEntry($"file{i}.shp").Open();
                entry.Write(Encoding.UTF8.GetBytes("x"));
            }
        }

        buffer.Position = 0;

        Assert.False(BoundedArchive.TryRead(
            buffer, Shapefile, ArchiveLimits.ForShapefile, out _, out string? error));

        Assert.Contains("entries and the limit", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_archive_with_nothing_useful_in_it_says_so()
    {
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = zip.CreateEntry("readme.txt").Open();
            entry.Write(Encoding.UTF8.GetBytes("nothing here"));
        }

        buffer.Position = 0;

        Assert.False(BoundedArchive.TryRead(
            buffer, Shapefile, ArchiveLimits.ForShapefile, out _, out string? error));

        Assert.Contains(".shp", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Something_that_is_not_a_zip_is_refused_rather_than_thrown()
    {
        using MemoryStream buffer = new(Encoding.UTF8.GetBytes("this is not a zip"));

        Assert.False(BoundedArchive.TryRead(
            buffer, Shapefile, ArchiveLimits.ForShapefile, out _, out string? error));

        Assert.Contains("not a readable ZIP", error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, true)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, true)]
    [InlineData(new byte[] { 0x7B, 0x22, 0x74, 0x79 }, false)]
    [InlineData(new byte[] { 0x50, 0x4B }, false)]
    public void A_zip_is_recognised_by_its_bytes_rather_than_its_name(byte[] bytes, bool zip)
    {
        // security.md: a file name is data, never an instruction. The import has
        // to tell an archive from a document without believing what it is
        // called.
        Assert.Equal(zip, BoundedArchive.LooksLikeZip(bytes));
    }

    [Fact]
    public void A_mac_metadata_folder_is_skipped_rather_than_refusing_the_archive()
    {
        // Half the people who "zip this folder" on a Mac produce one of these,
        // and refusing their upload over it would be refusing them.
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream junk = zip.CreateEntry("__MACOSX/._points.shp").Open())
            {
                junk.Write(Encoding.UTF8.GetBytes("junk"));
            }

            using Stream real = zip.CreateEntry("points.shp").Open();
            real.Write(File.ReadAllBytes(Path.Combine(Corpus, "points.shp")));
        }

        buffer.Position = 0;

        Assert.True(
            BoundedArchive.TryRead(
                buffer, Shapefile, ArchiveLimits.ForShapefile,
                out IReadOnlyList<ArchiveMember> members, out string? error),
            error);

        Assert.Equal("points.shp", Assert.Single(members).Name);
    }
}
