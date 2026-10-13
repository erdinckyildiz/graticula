using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The sample an image service's statistics, histograms and default stretch come from covers the whole image.
/// </summary>
/// <remarks>
/// It was the top-left 512 × 512 of the coarsest level. A four-tile Copernicus DEM mosaic on the showcase has a
/// 900-pixel coarsest level, so its statistics described Thrace alone — 496 m at most, against Uludağ's 2,489 m — and
/// the default stretch painted every mountain white (2026-10-13).
/// </remarks>
public sealed class AnImageIsDescribedFromAllOfItTests
{
    /// <summary>A coverage whose value at a pixel is computed, and which counts what it is asked for.</summary>
    private sealed class Computed(int width, int height, int bands, Func<int, int, int, double> value) : ICoverageReader
    {
        public CoverageInfo Info { get; } = new(
            width, height, 4326, new Envelope(0, 0, 1, 1),
            [.. Enumerable.Range(0, bands).Select(b => new BandInfo(b, SampleKind.Real32, null, null, null))], [], 256, 256);

        public long Asked { get; private set; }

        public int Reads { get; private set; }

        public long Largest { get; private set; }

        public Task<CoverageWindow> ReadAsync(int overview, int x, int y, int w, int h, CancellationToken cancellationToken)
        {
            Reads++;
            Asked += (long)w * h;
            Largest = Math.Max(Largest, (long)w * h * bands);
            double[] samples = new double[w * h * bands];

            for (int r = 0; r < h; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    for (int b = 0; b < bands; b++)
                    {
                        samples[(((r * w) + c) * bands) + b] = value(x + c, y + r, b);
                    }
                }
            }

            return Task.FromResult(new CoverageWindow(w, h, bands, samples));
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_level_past_512_is_thinned_to_the_whole_of_it_not_cut_to_its_corner()
    {
        // The showcase's shape: 900 a side, low in the top-left quarter, the mountain in the bottom-right one.
        using Computed reader = new(900, 900, 1, (x, y, _) => x >= 450 && y >= 450 ? 2489 : 496);

        CoverageWindow sample = await ImageServerEndpoints.ReadWholeAsync(reader, 3, 900, 900, CancellationToken.None);

        Assert.Equal((450, 450), (sample.Width, sample.Height));
        Assert.Equal(2489, sample.Samples.Max());
        Assert.Equal(496, sample.At(0, 0, 0));
        Assert.Equal(2489, sample.At(449, 449, 0));
    }

    [Fact]
    public async Task Each_sampled_pixel_is_the_one_at_its_place_band_by_band()
    {
        // An uneven size, so the last row and column are a short step, and three bands interleaved.
        using Computed reader = new(1300, 777, 3, (x, y, b) => (x * 10_000) + (y * 10) + b);

        CoverageWindow sample = await ImageServerEndpoints.ReadWholeAsync(reader, 0, 1300, 777, CancellationToken.None);

        int step = 3;
        Assert.Equal((434, 259), (sample.Width, sample.Height));

        foreach ((int c, int r) in new[] { (0, 0), (1, 1), (433, 0), (0, 258), (433, 258), (200, 100) })
        {
            for (int b = 0; b < 3; b++)
            {
                Assert.Equal((c * step * 10_000) + (r * step * 10) + b, sample.At(c, r, b));
            }
        }
    }

    [Fact]
    public async Task An_image_no_larger_than_512_is_read_as_it_is_in_one_read()
    {
        using Computed reader = new(512, 300, 1, (x, y, _) => x + y);

        CoverageWindow sample = await ImageServerEndpoints.ReadWholeAsync(reader, 0, 512, 300, CancellationToken.None);

        Assert.Equal((512, 300), (sample.Width, sample.Height));
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task A_file_with_no_pyramid_is_read_a_sampled_row_at_a_time()
    {
        // 60,000 across: one thinned row's source rows would be 118 × 60,000 samples, past the budget, so only the
        // sampled row is read — bounded by memory, and a few hundred rows rather than the whole file.
        using Computed reader = new(60_000, 2_000, 1, (x, y, _) => y);

        CoverageWindow sample = await ImageServerEndpoints.ReadWholeAsync(reader, 0, 60_000, 2_000, CancellationToken.None);

        Assert.Equal((509, 17), (sample.Width, sample.Height));
        Assert.Equal(17, reader.Reads);
        Assert.Equal(17L * 60_000, reader.Asked);
        Assert.Equal(16 * 118, sample.At(0, 16, 0));
        Assert.True(reader.Largest <= 1 << 22);
    }
}
