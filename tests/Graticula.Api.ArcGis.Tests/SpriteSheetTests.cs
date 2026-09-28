using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Graticula.Api.ArcGis;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// What an uploaded sprite sheet has to satisfy before this server will store it — ADR-092.
/// </summary>
/// <remarks>
/// <para>
/// <b>The picture is checked by its header and nothing else.</b> These tests build the first bytes
/// of a PNG by hand rather than encoding one, because that is all the validator reads: a decoder in
/// the server would be an attacker-facing parser, and the bounds are in the header.
/// </para>
/// <para>
/// <b>The index is checked against the picture</b>, because a rectangle past the edge of the sheet
/// draws nothing and says nothing — the sprite's version of a mistyped source layer.
/// </para>
/// </remarks>
public sealed class SpriteSheetTests
{
    /// <summary>A PNG signature and header chunk for a picture of the given size; no pixels.</summary>
    private static byte[] Png(uint width, uint height)
    {
        byte[] bytes = new byte[33];

        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = 8;   // bit depth
        bytes[25] = 6;   // RGBA

        return bytes;
    }

    private const string Two = """
        {
          "marker": { "x": 0, "y": 0, "width": 16, "height": 16, "pixelRatio": 1 },
          "school": { "x": 16, "y": 0, "width": 16, "height": 16, "sdf": true, "stretchX": [[2, 14]] }
        }
        """;

    private static readonly string[] MarkerAndSchool = ["marker", "school"];

    private static bool Valid(string? index, byte[] png, out IReadOnlyList<string> icons, out string? error, int ratio = 1) =>
        SpriteSheet.TryValidate(index, png, ratio, out icons, out error);

    // ---------- what a real sheet looks like ----------

    [Fact]
    public void A_sheet_whose_icons_are_inside_the_picture_is_accepted_and_its_names_listed()
    {
        Assert.True(Valid(Two, Png(32, 16), out IReadOnlyList<string> icons, out string? error), error);
        Assert.Null(error);
        Assert.Equal(MarkerAndSchool, icons);
    }

    /// <summary>An icon exactly reaching the edge is inside it.</summary>
    [Fact]
    public void An_icon_touching_the_edge_is_inside()
    {
        Assert.True(Valid("""{"a":{"x":31,"y":15,"width":1,"height":1}}""", Png(32, 16), out _, out string? error), error);
    }

    /// <summary>The @2x sheet states ratio 2, and that is what it must say.</summary>
    [Fact]
    public void A_2x_sheet_states_ratio_two()
    {
        Assert.True(Valid("""{"a":{"x":0,"y":0,"width":32,"height":32,"pixelRatio":2}}""", Png(64, 32),
            out _, out string? error, ratio: 2), error);

        Assert.False(Valid("""{"a":{"x":0,"y":0,"width":32,"height":32,"pixelRatio":1}}""", Png(64, 32),
            out _, out error, ratio: 2));
        Assert.Contains("pixelRatio", error!, StringComparison.Ordinal);
        Assert.Contains("'a'", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_size_is_read_from_the_header()
    {
        Assert.True(SpriteSheet.TryReadSize(Png(300, 200), out int width, out int height, out _));
        Assert.Equal(300, width);
        Assert.Equal(200, height);
    }

    // ---------- the picture ----------

    [Fact]
    public void A_file_that_does_not_begin_with_the_png_signature_is_refused()
    {
        byte[] jpeg = Png(32, 16);
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;

        Assert.False(Valid(Two, jpeg, out _, out string? error));
        Assert.Contains("not a PNG", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_too_short_to_hold_a_header_is_refused()
    {
        Assert.False(Valid(Two, Png(32, 16)[..20], out _, out string? error));
        Assert.Contains("signature", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_png_whose_first_chunk_is_not_the_header_is_refused()
    {
        byte[] png = Png(32, 16);
        "IDAT"u8.CopyTo(png.AsSpan(12));

        Assert.False(Valid(Two, png, out _, out string? error));
        Assert.Contains("IHDR", error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u, 16u)]
    [InlineData(32u, 0u)]
    [InlineData(4097u, 16u)]
    [InlineData(16u, 4097u)]
    [InlineData(uint.MaxValue, 16u)]
    public void A_picture_with_a_side_of_zero_or_past_the_bound_is_refused(uint width, uint height)
    {
        Assert.False(Valid("{}", Png(width, height), out _, out string? error));
        Assert.Contains($"{width} × {height}", error!, StringComparison.Ordinal);
        Assert.Contains("4096", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_at_the_side_bound_is_accepted()
    {
        Assert.True(Valid("{}", Png(4096, 4096), out _, out string? error), error);
    }

    [Fact]
    public void A_picture_over_eight_megabytes_is_refused_before_anything_else()
    {
        byte[] huge = new byte[SpriteSheet.MaximumImageBytes + 1];
        Png(32, 16).CopyTo(huge, 0);

        Assert.False(Valid(Two, huge, out _, out string? error));
        Assert.Contains("8 MB", error!, StringComparison.Ordinal);
    }

    // ---------- the index ----------

    [Theory]
    [InlineData("[]")]
    [InlineData("\"sprite\"")]
    [InlineData("42")]
    public void An_index_that_is_not_an_object_is_refused(string index)
    {
        Assert.False(Valid(index, Png(32, 16), out _, out string? error));
        Assert.Contains("JSON object", error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void A_missing_or_unreadable_index_is_refused(string? index)
    {
        Assert.False(Valid(index, Png(32, 16), out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void An_icon_past_the_right_edge_is_refused_naming_it_and_the_numbers()
    {
        Assert.False(Valid("""{"far":{"x":20,"y":0,"width":16,"height":16}}""", Png(32, 16), out _, out string? error));

        Assert.Contains("'far'", error!, StringComparison.Ordinal);
        Assert.Contains("36", error!, StringComparison.Ordinal);
        Assert.Contains("32 × 16", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_icon_past_the_bottom_edge_is_refused()
    {
        Assert.False(Valid("""{"low":{"x":0,"y":8,"width":16,"height":9}}""", Png(32, 16), out _, out string? error));
        Assert.Contains("'low'", error!, StringComparison.Ordinal);
    }

    /// <summary>A sum that overflows an integer is still past the edge.</summary>
    [Fact]
    public void An_icon_whose_rectangle_overflows_an_integer_is_refused()
    {
        Assert.False(Valid("""{"big":{"x":2147483647,"y":0,"width":2147483647,"height":1}}""", Png(32, 16), out _, out _));
    }

    [Theory]
    [InlineData("""{"a":{"y":0,"width":1,"height":1}}""", "\"x\"")]
    [InlineData("""{"a":{"x":-1,"y":0,"width":1,"height":1}}""", "\"x\"")]
    [InlineData("""{"a":{"x":0,"y":0,"width":0,"height":1}}""", "\"width\"")]
    [InlineData("""{"a":{"x":0,"y":0,"width":1,"height":1.5}}""", "\"height\"")]
    [InlineData("""{"a":{"x":"0","y":0,"width":1,"height":1}}""", "\"x\"")]
    public void An_icon_whose_rectangle_is_not_whole_positive_numbers_is_refused(string index, string member)
    {
        Assert.False(Valid(index, Png(32, 16), out _, out string? error));
        Assert.Contains("'a'", error!, StringComparison.Ordinal);
        Assert.Contains(member, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_icon_that_is_not_an_object_is_refused()
    {
        Assert.False(Valid("""{"a":[0,0,1,1]}""", Png(32, 16), out _, out string? error));
        Assert.Contains("'a'", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pixel_ratio_that_is_not_a_number_is_refused()
    {
        Assert.False(Valid("""{"a":{"x":0,"y":0,"width":1,"height":1,"pixelRatio":"1"}}""", Png(32, 16), out _, out _));
    }

    [Fact]
    public void An_empty_icon_name_is_refused()
    {
        Assert.False(Valid("""{"":{"x":0,"y":0,"width":1,"height":1}}""", Png(32, 16), out _, out string? error));
        Assert.Contains("empty name", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_icon_name_over_the_bound_is_refused()
    {
        string name = new('n', SpriteSheet.MaximumNameLength + 1);

        Assert.False(Valid($$$"""{"{{{name}}}":{"x":0,"y":0,"width":1,"height":1}}""", Png(32, 16), out _, out string? error));
        Assert.Contains("257", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_icon_named_twice_is_refused()
    {
        Assert.False(Valid(
            """{"a":{"x":0,"y":0,"width":1,"height":1},"a":{"x":1,"y":0,"width":1,"height":1}}""",
            Png(32, 16), out _, out string? error));
        Assert.Contains("twice", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void More_icons_than_the_bound_are_refused()
    {
        StringBuilder index = new("{");

        for (int i = 0; i <= SpriteSheet.MaximumIcons; i++)
        {
            index.Append(i == 0 ? "" : ",").Append("\"i").Append(i).Append("\":{\"x\":0,\"y\":0,\"width\":1,\"height\":1}");
        }

        index.Append('}');

        Assert.False(Valid(index.ToString(), Png(32, 16), out _, out string? error));
        Assert.Contains("10,000", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ratio_other_than_one_or_two_is_refused()
    {
        Assert.False(Valid(Two, Png(32, 16), out _, out string? error, ratio: 3));
        Assert.Contains("1 or 2", error!, StringComparison.Ordinal);
    }

    // ---------- reading back what was stored ----------

    [Fact]
    public void The_names_of_a_stored_index_are_read_leniently()
    {
        Assert.Equal(MarkerAndSchool, SpriteSheet.IconNames(Two));
        Assert.Empty(SpriteSheet.IconNames(null));
        Assert.Empty(SpriteSheet.IconNames("not json"));
        Assert.Empty(SpriteSheet.IconNames("[]"));
    }
}
