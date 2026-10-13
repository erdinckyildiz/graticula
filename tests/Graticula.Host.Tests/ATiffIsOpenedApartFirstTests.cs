using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-009 condition 1, the isolation half: the import reader opens a GeoTIFF with the server's own parser, decodes a
/// block at each end, and comes back with its size — or with why not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Against the real child process</b>, because what is under test is that a file which would cost the serving
/// process is paid for somewhere else: a fake would test the answer's shape and nothing about the process boundary.
/// </para>
/// <para>
/// <b>The bomb is inside every bound on dimensions.</b> 23,000 × 23,000 samples is half a billion pixels — far under
/// the 2^36 the server registers — in one Deflate strip, which the parser allocates whole at the first read.
/// <b>Measured 2026-10-13 in a process without a ceiling: the file is 310 bytes and one 256 × 256 read allocated
/// 2,117 MB.</b> The registration bound passes it; opening it, which reads tags only, passes it; every tile request
/// would allocate it again. The reader's heap ceiling refuses it instead.
/// </para>
/// <para>
/// <b>Deflate, because uncompressed is not the bomb.</b> The parser chops a single uncompressed strip into small
/// virtual ones on its own, so the same tags uncompressed cost 3 MB to read — measured the same day, after this test
/// was first written with them and the child decoded the file cleanly. A compressed strip cannot be chopped.
/// </para>
/// <para>
/// <b>The control is the same file made honest</b>: the same tags with a strip of 16 rows decode in the same child, so
/// the refusal is the strip's size and not the reader failing on every file this builder writes.
/// </para>
/// </remarks>
public sealed class ATiffIsOpenedApartFirstTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tiff-apart-{Guid.NewGuid():N}");

    public ATiffIsOpenedApartFirstTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GeodatabaseReader Reader() =>
        new(GeodatabaseReader.ExecutableBesideThisOne(), NullLogger<GeodatabaseReader>.Instance);

    private static async Task<JsonElement> AskAsync(string path, int maximumSide = 1 << 20, long maximumPixels = 1L << 36)
    {
        using JsonDocument answer = await Reader().AskAsync(
            new { op = "tiff", path, maximumSide, maximumPixels }, TimeSpan.FromSeconds(60));

        return answer.RootElement.Clone();
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task An_honest_geotiff_is_opened_and_decoded_at_both_ends()
    {
        JsonElement answer = await AskAsync(Write("small.tif", GeoTiff(side: 300, rowsPerStrip: 300, withPixels: true)));

        Assert.True(answer.GetProperty("ok").GetBoolean(), $"An ordinary GeoTIFF was not opened: {answer}");
        Assert.Equal(300, answer.GetProperty("width").GetInt32());
        Assert.True(answer.GetProperty("decoded").GetBoolean(), $"It was not decoded: {answer}");
    }

    [Fact]
    public async Task A_strip_that_asks_for_two_gigabytes_is_refused_in_the_child_and_the_same_tags_with_small_strips_are_not()
    {
        JsonElement bomb = await AskAsync(Write("bomb.tif", GeoTiff(side: 23_000, rowsPerStrip: 23_000, withPixels: false, compression: 8)));

        Assert.False(bomb.GetProperty("ok").GetBoolean(), $"A 2 GB strip was decoded: {bomb}");
        Assert.False(
            bomb.TryGetProperty("refused", out JsonElement refused) && refused.GetBoolean(),
            $"The strip was reported as a file the parser refuses, which the host lets its own open say again — "
            + $"and that open would allocate it: {bomb}");
        Assert.Contains("OutOfMemory", bomb.GetProperty("error").GetString(), StringComparison.Ordinal);

        // ---- the control: the same declared image, in strips of 16 rows ----
        JsonElement honest = await AskAsync(Write("strips.tif", GeoTiff(side: 23_000, rowsPerStrip: 16, withPixels: false, compression: 8)));

        Assert.True(honest.GetProperty("ok").GetBoolean(), $"The same image in small strips was refused: {honest}");
        Assert.True(honest.GetProperty("decoded").GetBoolean(), $"It was not decoded: {honest}");
    }

    [Fact]
    public async Task A_file_past_the_bound_is_answered_with_its_size_and_not_decoded()
    {
        JsonElement answer = await AskAsync(Write("wide.tif", GeoTiff(side: 23_000, rowsPerStrip: 23_000, withPixels: false)),
            maximumSide: 10_000);

        Assert.True(answer.GetProperty("ok").GetBoolean(), $"A file past the bound was not answered with its size: {answer}");
        Assert.Equal(23_000, answer.GetProperty("width").GetInt32());
        Assert.False(answer.GetProperty("decoded").GetBoolean(), "A file past the bound was decoded anyway.");
    }

    [Fact]
    public async Task A_file_that_is_not_a_tiff_is_answered_as_refused_by_the_parser()
    {
        JsonElement answer = await AskAsync(Write("notes.tif", "not a tiff at all"u8.ToArray()));

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.True(answer.GetProperty("refused").GetBoolean(), $"The parser's own refusal was not marked as one: {answer}");
    }

    /// <summary>
    /// A one-band 32-bit float GeoTIFF in EPSG:4326, uncompressed, in strips of <paramref name="rowsPerStrip"/> rows.
    /// </summary>
    /// <param name="side">Pixels across and down.</param>
    /// <param name="rowsPerStrip">Rows in one strip, which is what the parser allocates for one read.</param>
    /// <param name="withPixels">
    /// Whether the strips' bytes are in the file. Without them every strip points at the same few bytes, which is a
    /// file declaring far more than it holds; the parser reads short and the read path treats that as no data.
    /// </param>
    private static byte[] GeoTiff(int side, int rowsPerStrip, bool withPixels, ushort compression = 1)
    {
        using MemoryStream file = new();
        using BinaryWriter w = new(file);

        int strips = (side + rowsPerStrip - 1) / rowsPerStrip;
        long stripBytes = (long)side * rowsPerStrip * 4;
        byte[] pixels = withPixels ? new byte[(long)side * side * 4] : new byte[16];

        int pixelsAt = 8;
        int offsetsAt = pixelsAt + pixels.Length;
        int countsAt = offsetsAt + (strips * 4);
        int scaleAt = countsAt + (strips * 4);
        int tieAt = scaleAt + 24;
        int geoAt = tieAt + 48;
        int ifdAt = geoAt + 32;

        w.Write("II"u8.ToArray());
        w.Write((ushort)42);
        w.Write(ifdAt);
        w.Write(pixels);

        for (int i = 0; i < strips; i++)
        {
            w.Write(withPixels ? pixelsAt + (int)(i * stripBytes) : pixelsAt);
        }

        for (int i = 0; i < strips; i++)
        {
            int rows = Math.Min(rowsPerStrip, side - (i * rowsPerStrip));
            w.Write(withPixels ? side * rows * 4 : pixels.Length);
        }

        double cell = 0.64 / side;
        foreach (double d in new[] { cell, cell, 0.0 }) w.Write(d);
        foreach (double d in new[] { 0.0, 0.0, 0.0, 30.0, 41.0, 0.0 }) w.Write(d);
        foreach (ushort k in new ushort[] { 1, 1, 0, 3, 1024, 0, 1, 2, 1025, 0, 1, 1, 2048, 0, 1, 4326 }) w.Write(k);

        (ushort Tag, ushort Type, int Count, int Value)[] tags =
        [
            (256, 4, 1, side), (257, 4, 1, side), (258, 3, 1, 32), (259, 3, 1, compression), (262, 3, 1, 1),
            (273, 4, strips, strips == 1 ? pixelsAt : offsetsAt),
            (277, 3, 1, 1), (278, 4, 1, rowsPerStrip),
            (279, 4, strips, strips == 1 ? (withPixels ? side * side * 4 : pixels.Length) : countsAt),
            (284, 3, 1, 1), (339, 3, 1, 3),
            (33550, 12, 3, scaleAt), (33922, 12, 6, tieAt), (34735, 3, 16, geoAt),
        ];

        w.Write((ushort)tags.Length);

        foreach ((ushort tag, ushort type, int count, int value) in tags.OrderBy(t => t.Tag))
        {
            w.Write(tag);
            w.Write(type);
            w.Write(count);

            if (type == 3 && count == 1)
            {
                w.Write((ushort)value);
                w.Write((ushort)0);
            }
            else
            {
                w.Write(value);
            }
        }

        w.Write(0);
        return file.ToArray();
    }
}
