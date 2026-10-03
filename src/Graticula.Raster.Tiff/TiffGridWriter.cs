using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BitMiracle.LibTiff.Classic;
using Graticula.Coverages;

namespace Graticula.Raster.Tiff;

/// <summary>
/// Writes a GeoTIFF a block at a time — ADR-147, an image conformed to a mosaic's grid: tiled 256, deflated, with the
/// GeoTIFF keys that place it and GDAL's no-data, so an image of any size is written without holding it in memory.
/// </summary>
public static class TiffGridWriter
{
    private const int Tile = 256;

    private static readonly TiffFieldInfo[] GeoFields =
    [
        new((TiffTag)33550, -1, -1, TiffType.DOUBLE, FieldBit.Custom, true, true, "ModelPixelScale"),
        new((TiffTag)33922, -1, -1, TiffType.DOUBLE, FieldBit.Custom, true, true, "ModelTiepoint"),
        new((TiffTag)34735, -1, -1, TiffType.SHORT, FieldBit.Custom, true, true, "GeoKeyDirectory"),
        new((TiffTag)42113, -1, -1, TiffType.ASCII, FieldBit.Custom, true, false, "GDAL_NODATA"),
    ];

    /// <summary>Writes an image whose values come from a function asked for one 256-pixel block at a time.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    /// <param name="bands">Values a pixel.</param>
    /// <param name="kind">Their type.</param>
    /// <param name="minX">The ground X of its left edge.</param>
    /// <param name="maxY">The ground Y of its top edge.</param>
    /// <param name="pixelWidth">A pixel's width in ground units.</param>
    /// <param name="pixelHeight">A pixel's height, positive.</param>
    /// <param name="srid">Its EPSG code.</param>
    /// <param name="geographic">Whether that is a geographic reference.</param>
    /// <param name="noData">The value meaning nothing was measured, or null.</param>
    /// <param name="block">Reads a block — its left, top, width and height — as pixel-interleaved values.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(
        string path, int width, int height, int bands, SampleKind kind, double minX, double maxY, double pixelWidth,
        double pixelHeight, int srid, bool geographic, double? noData,
        Func<int, int, int, int, CancellationToken, Task<double[]>> block, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(block);

        string writing = path + ".writing";

        try
        {
            using (BitMiracle.LibTiff.Classic.Tiff output = BitMiracle.LibTiff.Classic.Tiff.Open(writing, "w")
                ?? throw new IOException($"Could not write '{Path.GetFileName(path)}'."))
            {
                output.MergeFieldInfo(GeoFields, GeoFields.Length);
                bool colour = bands >= 3 && kind == SampleKind.Unsigned8;
                output.SetField(TiffTag.IMAGEWIDTH, width);
                output.SetField(TiffTag.IMAGELENGTH, height);
                output.SetField(TiffTag.SAMPLESPERPIXEL, bands);
                output.SetField(TiffTag.BITSPERSAMPLE, Bytes(kind) * 8);
                output.SetField(TiffTag.SAMPLEFORMAT, kind switch
                {
                    SampleKind.Real32 or SampleKind.Real64 => SampleFormat.IEEEFP,
                    SampleKind.Signed16 or SampleKind.Signed32 => SampleFormat.INT,
                    _ => SampleFormat.UINT,
                });
                output.SetField(TiffTag.PHOTOMETRIC, colour ? Photometric.RGB : Photometric.MINISBLACK);
                output.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                output.SetField(TiffTag.COMPRESSION, Compression.ADOBE_DEFLATE);
                output.SetField(TiffTag.ZIPQUALITY, 1);
                output.SetField(TiffTag.TILEWIDTH, Tile);
                output.SetField(TiffTag.TILELENGTH, Tile);

                int extra = bands - (colour ? 3 : 1);

                if (extra > 0)
                {
                    output.SetField(TiffTag.EXTRASAMPLES, extra, new short[extra]);
                }

                output.SetField((TiffTag)33550, 3, new[] { pixelWidth, pixelHeight, 0d });
                output.SetField((TiffTag)33922, 6, new[] { 0d, 0, 0, minX, maxY, 0 });
                short[] keys =
                [
                    1, 1, 0, 3,
                    1024, 0, 1, (short)(geographic ? 2 : 1),
                    1025, 0, 1, 1,
                    (short)(geographic ? 2048 : 3072), 0, 1, unchecked((short)Math.Clamp(srid, 0, ushort.MaxValue)),
                ];
                output.SetField((TiffTag)34735, keys.Length, keys);

                if (noData is { } none)
                {
                    output.SetField((TiffTag)42113, none.ToString("R", CultureInfo.InvariantCulture));
                }

                int across = (width + Tile - 1) / Tile;
                int down = (height + Tile - 1) / Tile;
                byte[] encoded = new byte[Tile * Tile * bands * Bytes(kind)];

                for (int row = 0; row < down; row++)
                {
                    for (int column = 0; column < across; column++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int w = Math.Min(Tile, width - (column * Tile)), h = Math.Min(Tile, height - (row * Tile));
                        double[] values = await block(column * Tile, row * Tile, w, h, cancellationToken).ConfigureAwait(false);
                        TiffPyramidBuilder.Encode(values, w, h, bands, kind, noData ?? 0, encoded);

                        if (output.WriteEncodedTile((row * across) + column, encoded, encoded.Length) < 0)
                        {
                            throw new IOException($"Could not write a tile of '{Path.GetFileName(path)}'.");
                        }
                    }
                }

                if (!output.WriteDirectory())
                {
                    throw new IOException($"Could not finish '{Path.GetFileName(path)}'.");
                }
            }

            File.Move(writing, path, overwrite: true);
        }
        catch
        {
            File.Delete(writing);
            throw;
        }
    }

    private static int Bytes(SampleKind kind) => kind switch
    {
        SampleKind.Unsigned8 => 1,
        SampleKind.Signed16 or SampleKind.Unsigned16 => 2,
        SampleKind.Signed32 or SampleKind.Real32 => 4,
        _ => 8,
    };
}
