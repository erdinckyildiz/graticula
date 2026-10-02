using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Raster.Tiff;
using Xunit;

namespace Graticula.Raster.Tiff.Tests;

/// <summary>
/// ADR-139: an image without overviews is given them beside it, as GDAL's <c>.ovr</c>, and the reader uses them.
/// </summary>
public sealed class TiffPyramidBuilderTests
{
    private static readonly TiffPyramidBuilder Builder = new();

    private static string Write(double[] samples, int width, int height, int bands, SampleKind kind, double? noData)
    {
        string path = Path.Combine(Path.GetTempPath(), $"graticula-pyramid-{Guid.NewGuid():N}.tif");
        File.WriteAllBytes(path, GeoTiffWriter.Write(samples, width, height, bands, kind, 30, 41, 0.001, 0.001, 4326, true, noData));
        return path;
    }

    private static void Remove(string path)
    {
        File.Delete(path);
        File.Delete(TiffCoverageReader.PyramidPath(path));
    }

    [Fact]
    public async Task A_float_image_is_halved_until_it_fits_a_tile_averaging_and_leaving_no_data_out()
    {
        const int Width = 1000, Height = 700;
        double[] samples = new double[Width * Height];

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (i % Width) + (1000 * (i / Width));
        }

        // One pixel of every top-left pair is no-data: the overview pixel is the other three's mean.
        samples[0] = -9999;
        string path = Write(samples, Width, Height, 1, SampleKind.Real32, -9999);

        try
        {
            Assert.Equal(2, await Builder.BuildAsync(path, CancellationToken.None));

            using TiffCoverageReader reader = TiffCoverageReader.Open(path);
            Assert.Equal([(500, 350), (250, 175)], reader.Info.Overviews.Select(o => (o.Width, o.Height)));

            CoverageWindow half = await reader.ReadAsync(1, 0, 0, 3, 2, CancellationToken.None);
            Assert.Equal((1 + 1000 + 1001) / 3.0, half.Samples[0], 3);
            Assert.Equal((2 + 3 + 1002 + 1003) / 4.0, half.Samples[1], 3);
            Assert.Equal((2000 + 2001 + 3000 + 3001) / 4.0, half.Samples[3], 3);

            CoverageWindow quarter = await reader.ReadAsync(2, 249, 174, 1, 1, CancellationToken.None);
            Assert.InRange(quarter.Samples[0], 696000, 699999);

            // The full resolution is the file's own, untouched.
            CoverageWindow full = await reader.ReadAsync(0, 0, 0, 2, 1, CancellationToken.None);
            Assert.Equal([-9999, 1], full.Samples);
        }
        finally
        {
            Remove(path);
        }
    }

    [Fact]
    public async Task An_integer_image_takes_one_pixel_rather_than_inventing_a_class()
    {
        const int Width = 600, Height = 300, Bands = 3;
        double[] samples = new double[Width * Height * Bands];

        for (int i = 0; i < Width * Height; i++)
        {
            int x = i % Width, y = i / Width;
            samples[(i * Bands) + 0] = (x % 2 == 0) ? 10 : 200;
            samples[(i * Bands) + 1] = y % 251;
            samples[(i * Bands) + 2] = 7;
        }

        string path = Write(samples, Width, Height, Bands, SampleKind.Unsigned8, null);

        try
        {
            Assert.Equal(2, await Builder.BuildAsync(path, CancellationToken.None));

            using TiffCoverageReader reader = TiffCoverageReader.Open(path);
            CoverageWindow half = await reader.ReadAsync(1, 0, 0, 4, 4, CancellationToken.None);

            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(10, half.Samples[i * Bands]);
                Assert.Equal((i / 4 * 2) % 251, half.Samples[(i * Bands) + 1]);
                Assert.Equal(7, half.Samples[(i * Bands) + 2]);
            }
        }
        finally
        {
            Remove(path);
        }
    }

    [Theory]
    [InlineData(SampleKind.Signed16)]
    [InlineData(SampleKind.Unsigned16)]
    [InlineData(SampleKind.Signed32)]
    [InlineData(SampleKind.Real64)]
    public async Task Every_type_is_written_as_itself(SampleKind kind)
    {
        const int Width = 520, Height = 260;
        double[] samples = [.. Enumerable.Range(0, Width * Height).Select(i => (double)(i % 9000))];
        string path = Write(samples, Width, Height, 1, kind, null);

        try
        {
            Assert.Equal(2, await Builder.BuildAsync(path, CancellationToken.None));

            using TiffCoverageReader reader = TiffCoverageReader.Open(path);
            Assert.Equal(kind, reader.Info.Bands[0].Kind);
            CoverageWindow half = await reader.ReadAsync(1, 0, 1, 2, 1, CancellationToken.None);
            double expected = kind == SampleKind.Real64 ? (1040 + 1041 + 1560 + 1561) / 4.0 : 1040;
            Assert.Equal(expected, half.Samples[0], 6);
        }
        finally
        {
            Remove(path);
        }
    }

    [Fact]
    public async Task An_image_that_has_overviews_or_fits_a_tile_is_left_alone()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);
        while (at is not null && at.GetFiles("*.sln").Length == 0) at = at.Parent;
        string corpus = Path.Combine(at!.FullName, "tests", "Graticula.Raster.Tiff.Tests", "corpus");
        string cog = Path.Combine(Path.GetTempPath(), $"graticula-pyramid-{Guid.NewGuid():N}.tif");
        File.Copy(Path.Combine(corpus, "gray-byte-deflate.tif"), cog);
        string small = Write(new double[200 * 100], 200, 100, 1, SampleKind.Unsigned8, null);

        try
        {
            using (TiffCoverageReader before = TiffCoverageReader.Open(cog))
            {
                Assert.NotEmpty(before.Info.Overviews);
            }

            Assert.Equal(0, await Builder.BuildAsync(cog, CancellationToken.None));
            Assert.Equal(0, await Builder.BuildAsync(small, CancellationToken.None));
            Assert.False(File.Exists(TiffCoverageReader.PyramidPath(cog)));
            Assert.False(File.Exists(TiffCoverageReader.PyramidPath(small)));
        }
        finally
        {
            Remove(cog);
            Remove(small);
        }
    }

    [Fact]
    public async Task An_overview_file_of_another_image_is_not_used()
    {
        string single = Write(new double[600 * 300], 600, 300, 1, SampleKind.Unsigned8, null);
        string colour = Write(new double[600 * 300 * 3], 600, 300, 3, SampleKind.Unsigned8, null);

        try
        {
            await Builder.BuildAsync(colour, CancellationToken.None);
            File.Copy(TiffCoverageReader.PyramidPath(colour), TiffCoverageReader.PyramidPath(single));

            using TiffCoverageReader reader = TiffCoverageReader.Open(single);
            Assert.Empty(reader.Info.Overviews);
        }
        finally
        {
            Remove(single);
            Remove(colour);
        }
    }
}
