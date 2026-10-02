using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BitMiracle.LibTiff.Classic;
using Graticula.Coverages;

namespace Graticula.Raster.Tiff;

/// <summary>
/// Writes an image's overviews as GDAL's external overview file — ADR-139: a TIFF beside the image, <c>.ovr</c>, whose
/// directories are the image halved and halved again, tiled 256 and deflated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside the image, not inside it</b>, so the file uploaded is the file kept, byte for byte, and the full resolution
/// is never rewritten; and because it is the arrangement ArcGIS's Build Pyramids writes for a TIFF, the reader that finds
/// this one finds theirs.
/// </para>
/// <para>
/// <b>Floating-point values are averaged</b> over the pixels each overview pixel covers, no-data left out, as a
/// measurement seen from further away is; <b>integer values are taken from one pixel</b>, because an integer band may be
/// classes, and the average of forest and water is neither. The full resolution is never changed, so a value read
/// close up is always the file's.
/// </para>
/// <para>
/// <b>Bounded memory.</b> A level small enough is kept to build the next from; a larger one is built from the image
/// itself, a tile at a time, in windows of at most a few million values.
/// </para>
/// </remarks>
public sealed class TiffPyramidBuilder : ICoveragePyramidBuilder
{
    private const int Tile = 256;
    private const long KeptLevel = 16L * 1024 * 1024;
    private const long ReadWindow = 4L * 1024 * 1024;

    /// <inheritdoc/>
    public Task<int> BuildAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using TiffCoverageReader image = TiffCoverageReader.Open(path);
        CoverageInfo info = image.Info;

        if (info.Overviews.Count > 0 || Math.Max(info.Width, info.Height) <= Tile)
        {
            return Task.FromResult(0);
        }

        string target = TiffCoverageReader.PyramidPath(path);
        string writing = target + ".writing";
        int levels = 0;

        try
        {
            using (BitMiracle.LibTiff.Classic.Tiff output = BitMiracle.LibTiff.Classic.Tiff.Open(writing, "w")
                ?? throw new IOException($"Could not write the overviews of '{Path.GetFileName(path)}'."))
            {
                int bands = info.Bands.Count;
                SampleKind kind = info.Bands[0].Kind;
                double? noData = info.Bands[0].NoData;
                bool average = kind is SampleKind.Real32 or SampleKind.Real64;

                // The level kept in memory to build the next from, and its size; null while levels are too large.
                double[]? kept = null;
                int keptWidth = info.Width, keptHeight = info.Height;
                int width = info.Width, height = info.Height, factor = 1;

                while (Math.Max(width, height) > Tile)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    width = (width + 1) / 2;
                    height = (height + 1) / 2;
                    factor *= 2;

                    bool fromKept = kept is not null;
                    int step = fromKept ? 2 : factor;
                    double[]? level = (long)width * height * bands <= KeptLevel ? new double[width * height * bands] : null;

                    Begin(output, width, height, bands, kind);
                    int across = (width + Tile - 1) / Tile;
                    int down = (height + Tile - 1) / Tile;
                    byte[] encoded = new byte[Tile * Tile * bands * Size(kind)];

                    for (int row = 0; row < down; row++)
                    {
                        // A whole row of tiles at once, so the image is read across its full width and each of its strips
                        // or tiles is decoded once, not once for every overview tile it falls under.
                        int rowHeight = Math.Min(Tile, height - (row * Tile));
                        double[] band = fromKept
                            ? Reduce(kept!, keptWidth, keptHeight, bands, 0, row * Tile * step, width, rowHeight, step, average, noData)
                            : ReduceFromImage(image, info, 0, row * Tile * step, width, rowHeight, step, average, noData, cancellationToken);
                        double[] values = new double[Tile * Tile * bands];

                        for (int column = 0; column < across; column++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            int tileWidth = Math.Min(Tile, width - (column * Tile));
                            int tileHeight = rowHeight;

                            for (int y = 0; y < tileHeight; y++)
                            {
                                Array.Copy(band, ((y * width) + (column * Tile)) * bands, values, y * tileWidth * bands, tileWidth * bands);
                            }

                            Encode(values, tileWidth, tileHeight, bands, kind, noData ?? 0, encoded);

                            if (output.WriteEncodedTile((row * across) + column, encoded, encoded.Length) < 0)
                            {
                                throw new IOException($"Could not write an overview tile of '{Path.GetFileName(path)}'.");
                            }

                            if (level is not null)
                            {
                                for (int y = 0; y < tileHeight; y++)
                                {
                                    Array.Copy(values, y * tileWidth * bands, level,
                                        ((((row * Tile) + y) * width) + (column * Tile)) * bands, tileWidth * bands);
                                }
                            }
                        }
                    }

                    if (!output.WriteDirectory())
                    {
                        throw new IOException($"Could not finish an overview of '{Path.GetFileName(path)}'.");
                    }

                    levels++;
                    kept = level;
                    keptWidth = width;
                    keptHeight = height;
                }
            }

            File.Move(writing, target, overwrite: true);
            return Task.FromResult(levels);
        }
        catch
        {
            File.Delete(writing);
            throw;
        }
    }

    private static void Begin(BitMiracle.LibTiff.Classic.Tiff output, int width, int height, int bands, SampleKind kind)
    {
        bool colour = bands >= 3 && kind == SampleKind.Unsigned8;
        output.SetField(TiffTag.SUBFILETYPE, FileType.REDUCEDIMAGE);
        output.SetField(TiffTag.IMAGEWIDTH, width);
        output.SetField(TiffTag.IMAGELENGTH, height);
        output.SetField(TiffTag.SAMPLESPERPIXEL, bands);
        output.SetField(TiffTag.BITSPERSAMPLE, Size(kind) * 8);
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
    }

    /// <summary>One overview tile from the image itself, read in windows of bounded size.</summary>
    private static double[] ReduceFromImage(
        TiffCoverageReader image, CoverageInfo info, int x, int y, int width, int height, int step, bool average,
        double? noData, CancellationToken cancellationToken)
    {
        int bands = info.Bands.Count;
        double[] sums = new double[width * height * bands];
        int[] counts = new int[width * height * bands];
        int sourceWidth = Math.Min(width * step, info.Width - x);
        int sourceHeight = Math.Min(height * step, info.Height - y);
        int rowsAtOnce = (int)Math.Max(step, ReadWindow / Math.Max(1, (long)sourceWidth * bands) / step * step);

        for (int top = 0; top < sourceHeight; top += rowsAtOnce)
        {
            int rows = Math.Min(rowsAtOnce, sourceHeight - top);
            CoverageWindow window = image.ReadAsync(0, x, y + top, sourceWidth, rows, cancellationToken).GetAwaiter().GetResult();
            Accumulate(window.Samples, sourceWidth, rows, bands, top, step, width, average, noData, sums, counts);
        }

        return Finish(sums, counts, noData);
    }

    /// <summary>One overview tile from the level above it, kept in memory.</summary>
    private static double[] Reduce(
        double[] kept, int keptWidth, int keptHeight, int bands, int x, int y, int width, int height, int step,
        bool average, double? noData)
    {
        double[] sums = new double[width * height * bands];
        int[] counts = new int[width * height * bands];
        int sourceWidth = Math.Min(width * step, keptWidth - x);
        int sourceHeight = Math.Min(height * step, keptHeight - y);
        double[] window = new double[sourceWidth * sourceHeight * bands];

        for (int row = 0; row < sourceHeight; row++)
        {
            Array.Copy(kept, (((y + row) * keptWidth) + x) * bands, window, row * sourceWidth * bands, sourceWidth * bands);
        }

        Accumulate(window, sourceWidth, sourceHeight, bands, 0, step, width, average, noData, sums, counts);
        return Finish(sums, counts, noData);
    }

    private static void Accumulate(
        double[] samples, int sourceWidth, int rows, int bands, int top, int step, int width, bool average, double? noData,
        double[] sums, int[] counts)
    {
        for (int row = 0; row < rows; row++)
        {
            int sourceY = top + row;

            if (!average && sourceY % step != 0)
            {
                continue;
            }

            int outY = sourceY / step;

            for (int column = 0; column < sourceWidth; column++)
            {
                if (!average && column % step != 0)
                {
                    continue;
                }

                int to = ((outY * width) + (column / step)) * bands;
                int from = ((row * sourceWidth) + column) * bands;

                for (int band = 0; band < bands; band++)
                {
                    double value = samples[from + band];

                    if (average && (double.IsNaN(value) || (noData is { } none && value == none)))
                    {
                        continue;
                    }

                    sums[to + band] += value;
                    counts[to + band]++;
                }
            }
        }
    }

    private static double[] Finish(double[] sums, int[] counts, double? noData)
    {
        for (int i = 0; i < sums.Length; i++)
        {
            sums[i] = counts[i] == 0 ? noData ?? double.NaN : sums[i] / counts[i];
        }

        return sums;
    }

    private static void Encode(double[] values, int width, int height, int bands, SampleKind kind, double fill, byte[] into)
    {
        int size = Size(kind);

        for (int y = 0; y < Tile; y++)
        {
            for (int x = 0; x < Tile; x++)
            {
                for (int band = 0; band < bands; band++)
                {
                    double value = x < width && y < height ? values[(((y * width) + x) * bands) + band] : fill;
                    Span<byte> at = into.AsSpan((((y * Tile) + x) * bands + band) * size, size);

                    switch (kind)
                    {
                        case SampleKind.Unsigned8: at[0] = (byte)Math.Clamp(Math.Round(value), 0, 255); break;
                        case SampleKind.Signed16: BinaryPrimitives.WriteInt16LittleEndian(at, (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue)); break;
                        case SampleKind.Unsigned16: BinaryPrimitives.WriteUInt16LittleEndian(at, (ushort)Math.Clamp(Math.Round(value), 0, ushort.MaxValue)); break;
                        case SampleKind.Signed32: BinaryPrimitives.WriteInt32LittleEndian(at, (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)); break;
                        case SampleKind.Real32: BinaryPrimitives.WriteSingleLittleEndian(at, (float)value); break;
                        default: BinaryPrimitives.WriteDoubleLittleEndian(at, value); break;
                    }
                }
            }
        }
    }

    private static int Size(SampleKind kind) => kind switch
    {
        SampleKind.Unsigned8 => 1,
        SampleKind.Signed16 or SampleKind.Unsigned16 => 2,
        SampleKind.Signed32 or SampleKind.Real32 => 4,
        _ => 8,
    };
}
