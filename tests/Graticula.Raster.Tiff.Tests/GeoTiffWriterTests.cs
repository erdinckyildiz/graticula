using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Raster.Tiff;
using Xunit;

namespace Graticula.Raster.Tiff.Tests;

/// <summary>
/// ADR-127: what the raw export writes is a GeoTIFF the reader opens back — same place, same reference, same type,
/// same values — for each type an image service can hold.
/// </summary>
public sealed class GeoTiffWriterTests
{
    [Theory]
    [InlineData(SampleKind.Unsigned8, 1, 4326)]
    [InlineData(SampleKind.Unsigned8, 3, 4326)]
    [InlineData(SampleKind.Signed16, 1, 3857)]
    [InlineData(SampleKind.Unsigned16, 2, 3857)]
    [InlineData(SampleKind.Signed32, 1, 4326)]
    [InlineData(SampleKind.Real32, 1, 3857)]
    [InlineData(SampleKind.Real64, 1, 4326)]
    public async Task What_is_written_is_read_back_where_it_was_and_as_it_was(SampleKind kind, int bands, int srid)
    {
        const int Width = 7, Height = 5;
        double[] samples = new double[Width * Height * bands];

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = kind is SampleKind.Real32 or SampleKind.Real64 ? 800.5 + i : i % 200;
        }

        byte[] file = GeoTiffWriter.Write(
            samples, Width, Height, bands, kind, 30, 41, 0.25, 0.5, srid, srid == 4326, kind == SampleKind.Real32 ? -9999 : null);

        string path = Path.Combine(Path.GetTempPath(), $"graticula-writer-{Guid.NewGuid():N}.tif");

        try
        {
            await File.WriteAllBytesAsync(path, file);
            using TiffCoverageReader reader = TiffCoverageReader.Open(path);

            Assert.Equal(srid, reader.Info.Srid);
            Assert.Equal(Width, reader.Info.Width);
            Assert.Equal(Height, reader.Info.Height);
            Assert.Equal(bands, reader.Info.Bands.Count);
            Assert.Equal(kind, reader.Info.Bands[0].Kind);
            Assert.Equal(30, reader.Info.Extent.MinX, 9);
            Assert.Equal(41, reader.Info.Extent.MaxY, 9);
            Assert.Equal(30 + (Width * 0.25), reader.Info.Extent.MaxX, 9);
            Assert.Equal(41 - (Height * 0.5), reader.Info.Extent.MinY, 9);

            if (kind == SampleKind.Real32)
            {
                Assert.Equal(-9999, reader.Info.Bands[0].NoData);
            }

            CoverageWindow window = await reader.ReadAsync(0, 0, 0, Width, Height, CancellationToken.None);

            for (int i = 0; i < samples.Length; i++)
            {
                Assert.Equal(samples[i], window.Samples[i], 3);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
