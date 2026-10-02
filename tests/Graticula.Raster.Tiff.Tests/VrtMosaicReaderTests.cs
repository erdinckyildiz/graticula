using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Raster.Tiff;
using Xunit;

namespace Graticula.Raster.Tiff.Tests;

/// <summary>ADR-140: several GeoTIFFs on one grid read as one image, through a GDAL virtual raster.</summary>
public sealed class VrtMosaicReaderTests : IDisposable
{
    private const double Pixel = 0.001;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"graticula-mosaic-{Guid.NewGuid():N}");

    public VrtMosaicReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>A tile whose every value says where on the mosaic's grid it is: column + 10000 × row.</summary>
    private string Tile(string name, int left, int top, int width, int height, int srid = 4326, double pixel = Pixel,
        double shift = 0, Func<int, int, double>? value = null)
    {
        double[] samples = new double[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                samples[(y * width) + x] = value?.Invoke(left + x, top + y) ?? (left + x) + (10000.0 * (top + y));
            }
        }

        string path = Path.Combine(_directory, name + ".tif");
        File.WriteAllBytes(path, GeoTiffWriter.Write(
            samples, width, height, 1, SampleKind.Real32, 30 + (left * pixel) + shift, 41 - (top * pixel), pixel, pixel, srid, true, -9999));
        return path;
    }

    [Fact]
    public async Task Four_tiles_read_as_one_grid_at_full_resolution_and_from_their_overviews()
    {
        List<string> tiles =
        [
            Tile("a", 0, 0, 600, 400), Tile("b", 600, 0, 500, 400),
            Tile("c", 0, 400, 600, 300), Tile("d", 600, 400, 500, 300),
        ];

        foreach (string tile in tiles)
        {
            await new TiffPyramidBuilder().BuildAsync(tile, CancellationToken.None);
        }

        string vrt = Path.Combine(_directory, "mosaic.vrt");
        VrtMosaicReader.Write(vrt, tiles);

        using ICoverageReader mosaic = await new TiffCoverageReaderFactory().OpenAsync(vrt, CancellationToken.None);
        Assert.Equal((1100, 700, 4326), (mosaic.Info.Width, mosaic.Info.Height, mosaic.Info.Srid));
        Assert.Equal(30, mosaic.Info.Extent.MinX, 9);
        Assert.Equal(41, mosaic.Info.Extent.MaxY, 9);
        // As many as the file with the fewest: b, 500 × 400, is halved once to fit a tile.
        Assert.Single(mosaic.Info.Overviews);

        // A window across all four meets: every value is its own position.
        CoverageWindow across = await mosaic.ReadAsync(0, 598, 398, 4, 4, CancellationToken.None);

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(598 + x + (10000.0 * (398 + y)), across.At(x, y, 0));
            }
        }

        // Halved: each overview pixel is the mean of the four beneath it, from the file that holds them.
        CoverageWindow half = await mosaic.ReadAsync(1, 300, 200, 1, 1, CancellationToken.None);
        Assert.Equal(600.5 + (10000 * 400.5), half.Samples[0], 0);

        // Off the grid's tiles is no-data.
        CoverageWindow outside = await mosaic.ReadAsync(0, 1099, 699, 1, 1, CancellationToken.None);
        Assert.Equal(1099 + (10000.0 * 699), outside.Samples[0]);
    }

    [Fact]
    public async Task The_later_image_is_drawn_over_the_earlier_except_where_it_has_no_data()
    {
        string under = Tile("under", 0, 0, 300, 300, value: (_, _) => 1);
        string over = Tile("over", 100, 100, 300, 300, value: (x, _) => x < 150 ? -9999 : 2);

        string vrt = Path.Combine(_directory, "overlap.vrt");
        VrtMosaicReader.Write(vrt, [under, over]);

        using VrtMosaicReader mosaic = VrtMosaicReader.Open(vrt);
        Assert.Equal((400, 400), (mosaic.Info.Width, mosaic.Info.Height));
        CoverageWindow row = await mosaic.ReadAsync(0, 0, 200, 400, 1, CancellationToken.None);

        Assert.Equal(1, row.Samples[50]);
        Assert.Equal(1, row.Samples[120]);
        Assert.Equal(2, row.Samples[200]);
        Assert.Equal(2, row.Samples[350]);
        Assert.Equal(1, (await mosaic.ReadAsync(0, 10, 10, 1, 1, CancellationToken.None)).Samples[0]);
        Assert.Equal(-9999, (await mosaic.ReadAsync(0, 350, 50, 1, 1, CancellationToken.None)).Samples[0]);
    }

    [Fact]
    public void Images_that_do_not_make_one_grid_are_refused_and_say_why()
    {
        string a = Tile("a", 0, 0, 100, 100);

        Assert.Contains("EPSG:3857", Assert.Throws<InvalidDataException>(() =>
            VrtMosaicReader.Write(Path.Combine(_directory, "x.vrt"), [a, Tile("srid", 100, 0, 100, 100, srid: 3857)])).Message);
        Assert.Contains("0.002° pixels", Assert.Throws<InvalidDataException>(() =>
            VrtMosaicReader.Write(Path.Combine(_directory, "x.vrt"), [a, Tile("coarse", 50, 0, 100, 100, pixel: 0.002)])).Message);
        Assert.Contains("grid", Assert.Throws<InvalidDataException>(() =>
            VrtMosaicReader.Write(Path.Combine(_directory, "x.vrt"), [a, Tile("shifted", 100, 0, 100, 100, shift: Pixel / 3)])).Message);
        Assert.Contains("two or more", Assert.Throws<InvalidDataException>(() =>
            VrtMosaicReader.Write(Path.Combine(_directory, "x.vrt"), [a])).Message);
    }

    [Fact]
    public void Every_image_that_does_not_fit_is_named_not_only_the_first()
    {
        string[] files =
        [
            Tile("a", 0, 0, 100, 100), Tile("b", 100, 0, 100, 100), Tile("srid", 200, 0, 100, 100, srid: 3857),
            Tile("coarse", 300, 0, 100, 100, pixel: 0.002), Tile("shifted", 0, 100, 100, 100, shift: Pixel / 2),
        ];
        IReadOnlyList<CoverageInfo> infos = [.. files.Select(f => { using TiffCoverageReader r = TiffCoverageReader.Open(f); return r.Info; })];

        IReadOnlyList<MosaicMisfit> misfits = VrtMosaicReader.Misfits(infos);

        Assert.Equal([2, 3, 4], misfits.Select(m => m.Image));
        string said = VrtMosaicReader.Say(misfits, files.Length, i => Path.GetFileName(files[i]));
        Assert.Contains("srid.tif is in EPSG:3857 where the other 2 are in EPSG:4326", said, StringComparison.Ordinal);
        Assert.Contains("coarse.tif has 0.002° pixels where the other 2 have 0.001° pixels", said, StringComparison.Ordinal);
        Assert.Contains("shifted.tif is 0.5 of a pixel off the grid", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_odd_image_is_measured_against_the_rest_even_when_it_comes_first()
    {
        // The ux review's case: the coarse tile sorts first by name, and the good ones were blamed.
        string[] files = [Tile("a_coarse", 0, 0, 100, 100, pixel: 0.002), Tile("b", 100, 0, 100, 100), Tile("c", 200, 0, 100, 100)];
        IReadOnlyList<CoverageInfo> infos = [.. files.Select(f => { using TiffCoverageReader r = TiffCoverageReader.Open(f); return r.Info; })];

        IReadOnlyList<MosaicMisfit> misfits = VrtMosaicReader.Misfits(infos);

        Assert.Equal(0, Assert.Single(misfits).Image);
        Assert.Equal("a_coarse.tif has 0.002° pixels where the other 2 have 0.001° pixels",
            VrtMosaicReader.Say(misfits, files.Length, i => Path.GetFileName(files[i])));
    }

    [Fact]
    public void A_virtual_raster_that_crops_or_resamples_is_refused_by_name()
    {
        string a = Tile("a", 0, 0, 100, 100);
        string vrt = Path.Combine(_directory, "crop.vrt");
        File.WriteAllText(vrt, """
            <VRTDataset rasterXSize="50" rasterYSize="50">
              <SRS>GEOGCS["WGS 84",AUTHORITY["EPSG","4326"]]</SRS>
              <GeoTransform>30, 0.001, 0, 41, 0, -0.001</GeoTransform>
              <VRTRasterBand dataType="Float32" band="1">
                <SimpleSource>
                  <SourceFilename relativeToVRT="1">a.tif</SourceFilename>
                  <SourceBand>1</SourceBand>
                  <SrcRect xOff="0" yOff="0" xSize="100" ySize="100"/>
                  <DstRect xOff="0" yOff="0" xSize="50" ySize="50"/>
                </SimpleSource>
              </VRTRasterBand>
            </VRTDataset>
            """);

        Assert.Contains("resamples", Assert.Throws<InvalidDataException>(() => VrtMosaicReader.Open(vrt)).Message);
        _ = a;
    }

    [Fact]
    public async Task A_virtual_raster_written_elsewhere_with_a_wkt_reference_is_read()
    {
        Tile("a", 0, 0, 100, 100);
        string vrt = Path.Combine(_directory, "gdal.vrt");
        File.WriteAllText(vrt, """
            <VRTDataset rasterXSize="100" rasterYSize="100">
              <SRS dataAxisToSRSAxisMapping="2,1">GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563,AUTHORITY["EPSG","7030"]],AUTHORITY["EPSG","6326"]],AUTHORITY["EPSG","4326"]]</SRS>
              <GeoTransform>  3.0000000000000000e+01,  1.0000000000000000e-03,  0.0000000000000000e+00,  4.1000000000000000e+01,  0.0000000000000000e+00, -1.0000000000000000e-03</GeoTransform>
              <VRTRasterBand dataType="Float32" band="1">
                <SimpleSource>
                  <SourceFilename relativeToVRT="1">a.tif</SourceFilename>
                  <SourceBand>1</SourceBand>
                  <SrcRect xOff="0" yOff="0" xSize="100" ySize="100"/>
                  <DstRect xOff="0" yOff="0" xSize="100" ySize="100"/>
                </SimpleSource>
              </VRTRasterBand>
            </VRTDataset>
            """);

        using VrtMosaicReader mosaic = VrtMosaicReader.Open(vrt);
        Assert.Equal(4326, mosaic.Info.Srid);
        Assert.Equal(42 + (10000.0 * 7), (await mosaic.ReadAsync(0, 42, 7, 1, 1, CancellationToken.None)).Samples[0]);
    }
}
