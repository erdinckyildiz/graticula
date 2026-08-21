using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Raster.Tiff;
using Xunit;

namespace Graticula.Raster.Tiff.Tests;

/// <summary>
/// The reader, checked against GDAL's answers for the same files.
/// </summary>
/// <remarks>
/// <para>
/// <b>The corpus and the expected answers are both GDAL's, and that is the point.</b>
/// <c>tools/make-raster-corpus.py</c> writes nine COGs and then reads each one back
/// and records what it saw into <c>truth.json</c>: size, band count, overview count,
/// block size, no-data, the geotransform, and the value of six named pixels. A reader
/// verified only against files it was written alongside proves nothing about a file
/// somebody else produced — which is the reasoning
/// <c>make-shapefile-corpus.py</c> already carries, applied to a second format.
/// </para>
/// <para>
/// <b>The pixels are a diagonal ramp, deliberately.</b> A constant image cannot tell a
/// correct reader from one that returns the first tile for every request. Every value
/// here is a function of where it is, so a transposition, an off-by-one row or a
/// repeated tile each change a number a test reads.
/// </para>
/// </remarks>
public sealed class TiffCoverageReaderTests
{
    private static readonly string Corpus =
        Path.Combine(AppContext.BaseDirectory, "corpus");

    private static readonly Lazy<JsonElement> Truth = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus, "truth.json"))).RootElement);

    public static TheoryData<string> EveryFile()
    {
        TheoryData<string> data = [];

        foreach (JsonProperty file in Truth.Value.EnumerateObject())
        {
            data.Add(file.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFile))]
    public void The_shape_of_a_coverage_is_what_gdal_says_it_is(string file)
    {
        JsonElement expected = Truth.Value.GetProperty(file);

        using TiffCoverageReader reader = TiffCoverageReader.Open(Path.Combine(Corpus, file));
        CoverageInfo info = reader.Info;

        Assert.Equal(expected.GetProperty("width").GetInt32(), info.Width);
        Assert.Equal(expected.GetProperty("height").GetInt32(), info.Height);
        Assert.Equal(expected.GetProperty("bands").GetInt32(), info.Bands.Count);

        // <b>The count of overviews, not merely that there are some.</b> A reader that
        // treated a mask or a thumbnail as a resolution would answer a zoomed-out
        // request with the wrong picture, and the count is the cheapest place that
        // shows.
        Assert.Equal(expected.GetProperty("overviews").GetInt32(), info.Overviews.Count);

        JsonElement block = expected.GetProperty("block");
        Assert.Equal(block[0].GetInt32(), info.TileWidth);
        Assert.Equal(block[1].GetInt32(), info.TileHeight);
    }

    [Theory]
    [MemberData(nameof(EveryFile))]
    public void A_coverage_lands_where_gdal_puts_it(string file)
    {
        JsonElement expected = Truth.Value.GetProperty(file);

        using TiffCoverageReader reader = TiffCoverageReader.Open(Path.Combine(Corpus, file));

        JsonElement extent = expected.GetProperty("extent");

        // <b>Nine decimal places, because the failure this catches is subtle.</b> A
        // tiepoint read as an origin, or a pixel height taken as positive, moves the
        // image by less than its own size — which looks like data slightly out of
        // register rather than like an error.
        Assert.Equal(extent[0].GetDouble(), reader.Info.Extent.MinX, 9);
        Assert.Equal(extent[1].GetDouble(), reader.Info.Extent.MinY, 9);
        Assert.Equal(extent[2].GetDouble(), reader.Info.Extent.MaxX, 9);
        Assert.Equal(extent[3].GetDouble(), reader.Info.Extent.MaxY, 9);
    }

    [Theory]
    [InlineData("gray-byte-deflate.tif", 4326)]
    [InlineData("gray-byte-lzw.tif", 4326)]
    [InlineData("rgb-byte-3857.tif", 3857)]
    public void The_reference_system_is_read_from_the_geokeys(string file, int srid)
    {
        using TiffCoverageReader reader = TiffCoverageReader.Open(Path.Combine(Corpus, file));

        Assert.Equal(srid, reader.Info.Srid);
    }

    [Theory]
    [MemberData(nameof(EveryFile))]
    public async Task Every_probed_pixel_reads_back_as_gdal_read_it(string file)
    {
        JsonElement expected = Truth.Value.GetProperty(file);

        using TiffCoverageReader reader = TiffCoverageReader.Open(Path.Combine(Corpus, file));

        foreach (JsonProperty probe in expected.GetProperty("samples").EnumerateObject())
        {
            string[] parts = probe.Name.Split(',');
            int x = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int y = int.Parse(parts[1], CultureInfo.InvariantCulture);

            CoverageWindow window =
                await reader.ReadAsync(0, x, y, 1, 1, CancellationToken.None);

            double[] wanted = [.. probe.Value.EnumerateArray().Select(v => v.GetDouble())];

            for (int band = 0; band < wanted.Length; band++)
            {
                Assert.Equal(wanted[band], window.At(0, 0, band), 4);
            }
        }
    }

    [Fact]
    public async Task A_window_spanning_several_tiles_is_assembled_in_the_right_order()
    {
        // <b>The test the single-pixel probes cannot be.</b> Reading one pixel at a
        // time never exercises the blit, so a tile placed at the wrong offset would
        // pass every probe above and produce a scrambled image. This reads a window
        // wider than the 128-pixel tile and asserts that each pixel matches the same
        // pixel read alone.
        using TiffCoverageReader reader =
            TiffCoverageReader.Open(Path.Combine(Corpus, "gray-byte-deflate.tif"));

        CoverageWindow block =
            await reader.ReadAsync(0, 100, 100, 60, 40, CancellationToken.None);

        Assert.Equal(60, block.Width);
        Assert.Equal(40, block.Height);

        foreach ((int x, int y) in new[] { (0, 0), (27, 27), (59, 39), (30, 10) })
        {
            CoverageWindow one =
                await reader.ReadAsync(0, 100 + x, 100 + y, 1, 1, CancellationToken.None);

            Assert.Equal(one.At(0, 0, 0), block.At(x, y, 0), 4);
        }
    }

    [Fact]
    public async Task Reading_past_the_edge_is_clamped_rather_than_refused()
    {
        // A window under a map request routinely straddles an edge. Making that an
        // error would put the arithmetic in every caller instead of once in the reader.
        using TiffCoverageReader reader =
            TiffCoverageReader.Open(Path.Combine(Corpus, "gray-byte-deflate.tif"));

        CoverageWindow window =
            await reader.ReadAsync(0, 240, 180, 32, 32, CancellationToken.None);

        Assert.Equal(32, window.Width);
        Assert.Equal(32 * 32, window.Samples.Length);
    }

    [Fact]
    public async Task An_overview_is_coarser_and_covers_the_same_ground()
    {
        using TiffCoverageReader reader =
            TiffCoverageReader.Open(Path.Combine(Corpus, "gray-byte-deflate.tif"));

        Assert.NotEmpty(reader.Info.Overviews);

        OverviewInfo first = reader.Info.Overviews[0];

        Assert.True(
            first.Width < reader.Info.Width && first.Height < reader.Info.Height,
            $"Overview 1 is {first.Width}x{first.Height} and the full image is "
            + $"{reader.Info.Width}x{reader.Info.Height}. An overview that is not smaller "
            + "is not an overview, and reading one would cost more than the image it "
            + "was meant to save.");

        CoverageWindow window = await reader.ReadAsync(
            1, 0, 0, Math.Min(16, first.Width), Math.Min(16, first.Height),
            CancellationToken.None);

        Assert.NotEmpty(window.Samples);
    }

    [Fact]
    public void A_file_with_no_pyramid_still_reads()
    {
        // A GeoTIFF that was never run through a COG writer is still a file somebody
        // registered, and a reader that assumed a pyramid would return nothing for it.
        using TiffCoverageReader reader =
            TiffCoverageReader.Open(Path.Combine(Corpus, "gray-byte-no-overviews.tif"));

        Assert.Empty(reader.Info.Overviews);
        Assert.Equal(256, reader.Info.Width);
    }

    [Theory]
    [MemberData(nameof(EveryFile))]
    public async Task A_corpus_file_is_not_blank(string file)
    {
        /*
          <b>Written after one of them was, and everything still passed.</b>
          `gray-byte-nodata.tif` was silently all zeros for its first generation: the
          MEM driver treats `SetNoDataValue` as an instruction to initialise the buffer,
          so setting it after writing the pixels wiped them. GDAL agreed the file was
          empty, so every probe matched, and the no-data test asserted only that *some*
          pixel was absent — trivially true of an image where all of them are.

          It was caught by rendering a PNG and looking at it. This is the cheap version
          of that: `truth.json` now carries how many distinct values band one holds, and
          a ramp cannot have one.
        */
        JsonElement expected = Truth.Value.GetProperty(file);

        int distinct = expected.GetProperty("distinctValuesInBand1").GetInt32();

        Assert.True(
            distinct > 8,
            $"{file} has {distinct} distinct values in band 1. The corpus is a ramp, so a "
            + "file with almost none is a file whose pixels were lost on the way in — and "
            + "every probe against it passes, because the expected answers came from the "
            + "same empty file.");

        using TiffCoverageReader reader = TiffCoverageReader.Open(Path.Combine(Corpus, file));

        CoverageWindow window = await reader.ReadAsync(
            0, 0, 0, Math.Min(64, reader.Info.Width), Math.Min(64, reader.Info.Height),
            CancellationToken.None);

        Assert.True(
            window.Samples.Distinct().Count() > 8,
            $"{file} read back {window.Samples.Distinct().Count()} distinct values, and "
            + "GDAL says the file holds " + distinct.ToString(CultureInfo.InvariantCulture)
            + ". The reader is flattening it.");
    }

    [Fact]
    public void A_declared_no_data_value_survives_the_read()
    {
        // It is a rendering decision carried by the reader rather than made by it: a
        // no-data pixel is absent rather than black, and the number has to arrive for
        // that choice to be available further up.
        using TiffCoverageReader reader =
            TiffCoverageReader.Open(Path.Combine(Corpus, "gray-byte-nodata.tif"));

        Assert.Equal(0, reader.Info.Bands[0].NoData);

        using TiffCoverageReader without =
            TiffCoverageReader.Open(Path.Combine(Corpus, "gray-byte-deflate.tif"));

        Assert.Null(without.Info.Bands[0].NoData);
    }

    [Theory]
    [InlineData("gray-byte-deflate.tif", SampleKind.Unsigned8)]
    [InlineData("gray-uint16-deflate.tif", SampleKind.Unsigned16)]
    [InlineData("gray-float32-deflate.tif", SampleKind.Real32)]
    public void The_sample_kind_is_read_from_the_file(string file, SampleKind kind)
    {
        using TiffCoverageReader reader = TiffCoverageReader.Open(Path.Combine(Corpus, file));

        Assert.Equal(kind, reader.Info.Bands[0].Kind);
    }

    [Fact]
    public void Something_that_is_not_a_tiff_is_refused_with_a_reason()
    {
        string path = Path.Combine(Path.GetTempPath(), $"not-a-tiff-{Guid.NewGuid():N}.tif");
        File.WriteAllText(path, "this is not a TIFF");

        try
        {
            InvalidDataException refused =
                Assert.Throws<InvalidDataException>(() => TiffCoverageReader.Open(path));

            Assert.Contains("GeoTIFF", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_overview_index_the_file_does_not_have_is_refused()
    {
        using TiffCoverageReader reader =
            TiffCoverageReader.Open(Path.Combine(Corpus, "gray-byte-no-overviews.tif"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.ReadAsync(3, 0, 0, 8, 8, CancellationToken.None));
    }
}
