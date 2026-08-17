using System;
using System.IO;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The glyph lookup, and mostly the fact that nothing the caller typed becomes
/// a path.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are path-traversal tests wearing a font costume.</b> The font stack
/// and the range both arrive in the URL and both used to look like the obvious
/// thing to concatenate onto a directory. security.md's rule is that filenames
/// are data and never paths, so the stack is matched against the directories
/// that exist and the range is parsed into two integers and the name rebuilt —
/// neither is sanitised, because a check that rejects is worth more than a
/// filter that repairs.
/// </para>
/// <para>
/// The fallback is tested as hard as the refusals. An ArcGIS style names a font
/// nobody can ship, and a 404 there makes a client drop every label on the map
/// and log a fetch error, which reads as a broken server rather than a
/// substituted typeface.
/// </para>
/// </remarks>
public sealed class GlyphStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gis-glyph-tests-" + Guid.NewGuid().ToString("n"));

    public GlyphStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, GlyphStore.Fallback));
        Directory.CreateDirectory(Path.Combine(_root, "Some Other Sans"));

        File.WriteAllBytes(Path.Combine(_root, GlyphStore.Fallback, "0-255.pbf"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, GlyphStore.Fallback, "256-511.pbf"), [4, 5]);
        File.WriteAllBytes(Path.Combine(_root, "Some Other Sans", "0-255.pbf"), [9]);

        // The thing a traversal is trying to reach, one level up from the root.
        File.WriteAllText(Path.Combine(_root, "secrets.txt"), "not a font");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GlyphStore Store() => new(_root);

    // ---------- the happy path ----------

    [Fact]
    public void A_range_that_exists_is_served()
    {
        Assert.True(Store().TryRead(GlyphStore.Fallback, "0-255", out byte[] bytes, out string served));

        Assert.Equal([1, 2, 3], bytes);
        Assert.Equal(GlyphStore.Fallback, served);
    }

    [Fact]
    public void A_named_stack_that_exists_is_preferred_over_the_fallback()
    {
        Assert.True(Store().TryRead("Some Other Sans", "0-255", out byte[] bytes, out string served));

        Assert.Equal([9], bytes);
        Assert.Equal("Some Other Sans", served);
    }

    /// <summary>
    /// A style names several fonts and means <em>whichever you have</em>.
    /// </summary>
    [Fact]
    public void The_first_stack_in_the_list_that_exists_wins()
    {
        Assert.True(Store().TryRead(
            "Arial Unicode MS Regular,Some Other Sans,DejaVu Sans Regular",
            "0-255", out _, out string served));

        Assert.Equal("Some Other Sans", served);
    }

    /// <summary>A font nobody can ship is substituted, not refused.</summary>
    [Fact]
    public void An_unknown_stack_falls_back_rather_than_failing()
    {
        Assert.True(Store().TryRead("Arial Unicode MS Regular", "0-255", out _, out string served));

        Assert.Equal(GlyphStore.Fallback, served);
    }

    /// <summary>A range the font does not cover is absent, not substituted.</summary>
    /// <remarks>
    /// The fallback is about the <em>font</em>. Answering a request for the
    /// Japanese range with Latin glyphs would render a label as mojibake, which
    /// is worse than rendering nothing: the client can draw a box for a missing
    /// glyph, and it cannot un-draw a wrong one.
    /// </remarks>
    [Fact]
    public void A_range_the_font_does_not_have_is_not_invented()
    {
        Assert.False(Store().TryRead(GlyphStore.Fallback, "65280-65535", out _, out _));
    }

    // ---------- nothing typed becomes a path ----------

    [Theory]
    [InlineData("../secrets")]
    [InlineData("..\\secrets")]
    [InlineData("0-255/../../secrets")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("0-255%00")]
    public void A_range_that_is_not_a_range_is_refused(string range)
    {
        Assert.False(Store().TryRead(GlyphStore.Fallback, range, out _, out _));
    }

    /// <summary>
    /// A traversal in the font stack cannot escape, because an unknown stack
    /// falls back to a directory name we chose.
    /// </summary>
    /// <remarks>
    /// <b>The interesting case, and the one a filter would get wrong.</b> The
    /// stack is not rejected — it is not matched, so the fallback answers. The
    /// caller gets ordinary Latin glyphs and the filesystem is never asked about
    /// the string they sent.
    /// </remarks>
    [Theory]
    [InlineData("../..")]
    [InlineData("..\\..\\..\\appsettings.json")]
    [InlineData("/")]
    public void A_traversal_in_the_font_stack_lands_on_the_fallback(string stack)
    {
        Assert.True(Store().TryRead(stack, "0-255", out byte[] bytes, out string served));

        Assert.Equal(GlyphStore.Fallback, served);
        Assert.Equal([1, 2, 3], bytes);
    }

    // ---------- the range grid ----------

    [Theory]
    [InlineData("0-255", 0, 255)]
    [InlineData("256-511", 256, 511)]
    [InlineData("65280-65535", 65280, 65535)]
    public void A_range_on_the_grid_parses(string text, int start, int end)
    {
        Assert.True(GlyphStore.TryRange(text, out int gotStart, out int gotEnd));
        Assert.Equal(start, gotStart);
        Assert.Equal(end, gotEnd);
    }

    /// <summary>
    /// Anything off the fixed grid is a client that built the URL wrongly.
    /// </summary>
    /// <remarks>
    /// Being permissive here would turn a client bug into a file lookup with an
    /// attacker-influenced name, and buy no compatibility: the ranges are a
    /// fixed grid of 256 and every real client walks it.
    /// </remarks>
    [Theory]
    [InlineData("100-200")]      // not on the grid
    [InlineData("0-100")]        // not 256 wide
    [InlineData("256-255")]      // backwards
    [InlineData("-1-254")]       // negative
    [InlineData("+0-255")]       // a sign is not a digit
    [InlineData(" 0-255")]       // nor is whitespace
    [InlineData("0-255-511")]
    [InlineData("65536-65791")]  // past the plane
    [InlineData("")]
    [InlineData("-")]
    [InlineData("0-")]
    [InlineData(null)]
    public void A_range_off_the_grid_is_refused(string? text)
    {
        Assert.False(GlyphStore.TryRange(text, out _, out _));
    }

    // ---------- shipping without glyphs at all ----------

    /// <summary>
    /// A build with no glyph directory says so rather than throwing.
    /// </summary>
    /// <remarks>
    /// The directory is copied by the build and could be missing from a
    /// hand-assembled deployment. The server should still start and still serve
    /// tiles; only labels are gone, and the style then omits its glyphs key so a
    /// client does not fetch what is not there.
    /// </remarks>
    [Fact]
    public void A_build_with_no_glyphs_is_empty_rather_than_broken()
    {
        GlyphStore empty = new(Path.Combine(_root, "nothing-here"));

        Assert.False(empty.Any);
        Assert.Empty(empty.Stacks);
        Assert.False(empty.TryRead(GlyphStore.Fallback, "0-255", out _, out _));
    }
}
