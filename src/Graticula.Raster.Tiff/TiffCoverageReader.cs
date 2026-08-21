using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BitMiracle.LibTiff.Classic;
using Graticula.Coverages;
using Graticula.Geometries;

namespace Graticula.Raster.Tiff;

/// <summary>
/// Reads a GeoTIFF, including the cloud-optimised arrangement of one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Tier 2 half of
/// [ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.4.</b>
/// Everything here is mechanical: which directory is which overview, where a tile
/// starts, how to undo a predictor. Nothing here decides what a map looks like, and
/// the moment something does it belongs one project up.
/// </para>
/// <para>
/// <b>A COG is a GeoTIFF with two promises</b> — the image is tiled rather than
/// striped, and its overviews are stored after it in reduced-resolution
/// subdirectories — and this reader keeps working when neither promise is kept,
/// because a file that was never run through a COG writer is still a file somebody
/// registered. `gray-byte-no-overviews.tif` in the corpus is that case.
/// </para>
/// <para>
/// <b>The geographic keys are read by hand from the GeoTIFF tags.</b> LibTiff knows
/// TIFF and not GeoTIFF: the model transformation lives in tag 33922
/// (<c>ModelTiepoint</c>) and 33550 (<c>ModelPixelScale</c>), and the reference system
/// in the GeoKey directory at 34735. That is a published specification (OGC 19-008)
/// and reading it here rather than adopting a second library keeps the dependency
/// list at one.
/// </para>
/// </remarks>
public sealed class TiffCoverageReader : ICoverageReader
{
    private readonly BitMiracle.LibTiff.Classic.Tiff _tiff;
    private readonly List<int> _directories;

    private TiffCoverageReader(
        BitMiracle.LibTiff.Classic.Tiff tiff, CoverageInfo info, List<int> directories)
    {
        _tiff = tiff;
        _directories = directories;
        Info = info;
    }

    /// <inheritdoc/>
    public CoverageInfo Info { get; }

    /// <summary>Opens a GeoTIFF from a path.</summary>
    /// <param name="path">Where it lives.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="InvalidDataException">The file is not a readable GeoTIFF.</exception>
    public static TiffCoverageReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // <b>Errors are silenced rather than printed.</b> LibTiff's default handler
        // writes to the console, and a malformed file somebody registered would put
        // its complaint in the server's stdout with no request attached to it.
        BitMiracle.LibTiff.Classic.Tiff.SetErrorHandler(new Quiet());

        BitMiracle.LibTiff.Classic.Tiff tiff =
            BitMiracle.LibTiff.Classic.Tiff.Open(path, "r")
            ?? throw new InvalidDataException(
                $"'{path}' is not a TIFF this server can read. A coverage is registered as a "
                + "GeoTIFF, and a cloud-optimised one is the arrangement it reads fastest.");

        try
        {
            return new TiffCoverageReader(tiff, Describe(tiff, out List<int> pages), pages);
        }
        catch
        {
            tiff.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<CoverageWindow> ReadAsync(
        int overview,
        int x,
        int y,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (overview < 0 || overview >= _directories.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overview),
                overview,
                $"This coverage has {_directories.Count.ToString(CultureInfo.InvariantCulture)} "
                + "resolutions, counting the full image as zero.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), "A window has a positive size in both directions.");
        }

        // Synchronous underneath: LibTiff has no async surface and the file is local.
        // The signature is async because the port's next implementation reads ranges
        // from object storage, and changing a signature later is the expensive half.
        return Task.FromResult(Read(overview, x, y, width, height, cancellationToken));
    }

    /// <inheritdoc/>
    public void Dispose() => _tiff.Dispose();

    private CoverageWindow Read(
        int overview, int x, int y, int width, int height, CancellationToken cancellationToken)
    {
        _tiff.SetDirectory((short)_directories[overview]);

        int imageWidth = Scalar(_tiff, TiffTag.IMAGEWIDTH);
        int imageHeight = Scalar(_tiff, TiffTag.IMAGELENGTH);
        int bands = Info.Bands.Count;

        double[] samples = new double[(long)width * height * bands is var n && n <= int.MaxValue
            ? (int)n
            : throw new ArgumentOutOfRangeException(nameof(width), "The window is too large.")];

        // <b>Outside the image is no-data, not zero, where the band declares one.</b>
        // A window under a map request routinely straddles an edge, and filling the
        // outside with zero would draw whatever colour zero maps to across the margin
        // — a black band that looks like data.
        for (int band = 0; band < bands; band++)
        {
            double outside = Info.Bands[band].NoData ?? 0;

            if (outside == 0)
            {
                continue;
            }

            for (int i = band; i < samples.Length; i += bands)
            {
                samples[i] = outside;
            }
        }

        bool tiled = _tiff.IsTiled();

        if (tiled)
        {
            ReadTiles(samples, overview, x, y, width, height, imageWidth, imageHeight,
                bands, cancellationToken);
        }
        else
        {
            ReadStrips(samples, x, y, width, height, imageWidth, imageHeight, bands,
                cancellationToken);
        }

        return new CoverageWindow(width, height, bands, samples);
    }

    private void ReadTiles(
        double[] samples,
        int overview,
        int x,
        int y,
        int width,
        int height,
        int imageWidth,
        int imageHeight,
        int bands,
        CancellationToken cancellationToken)
    {
        int tileWidth = Scalar(_tiff, TiffTag.TILEWIDTH);
        int tileHeight = Scalar(_tiff, TiffTag.TILELENGTH);
        int bits = Scalar(_tiff, TiffTag.BITSPERSAMPLE);
        SampleKind kind = Info.Bands[0].Kind;

        byte[] tile = new byte[_tiff.TileSize()];

        int firstColumn = Math.Max(0, x / tileWidth);
        int lastColumn = Math.Min((imageWidth - 1) / tileWidth, (x + width - 1) / tileWidth);
        int firstRow = Math.Max(0, y / tileHeight);
        int lastRow = Math.Min((imageHeight - 1) / tileHeight, (y + height - 1) / tileHeight);

        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int originX = column * tileWidth;
                int originY = row * tileHeight;

                if (_tiff.ReadTile(tile, 0, originX, originY, 0, 0) < 0)
                {
                    continue;
                }

                Blit(samples, tile, originX, originY, tileWidth, tileHeight,
                    x, y, width, height, imageWidth, imageHeight, bands, bits, kind);
            }
        }
    }

    private void ReadStrips(
        double[] samples,
        int x,
        int y,
        int width,
        int height,
        int imageWidth,
        int imageHeight,
        int bands,
        CancellationToken cancellationToken)
    {
        int rowsPerStrip = Scalar(_tiff, TiffTag.ROWSPERSTRIP);
        int bits = Scalar(_tiff, TiffTag.BITSPERSAMPLE);
        SampleKind kind = Info.Bands[0].Kind;

        byte[] strip = new byte[_tiff.StripSize()];

        int firstStrip = Math.Max(0, y / rowsPerStrip);
        int lastStrip = Math.Min((imageHeight - 1) / rowsPerStrip, (y + height - 1) / rowsPerStrip);

        for (int index = firstStrip; index <= lastStrip; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_tiff.ReadEncodedStrip(index, strip, 0, -1) < 0)
            {
                continue;
            }

            Blit(samples, strip, 0, index * rowsPerStrip, imageWidth, rowsPerStrip,
                x, y, width, height, imageWidth, imageHeight, bands, bits, kind);
        }
    }

    /// <summary>Copies the overlap of one stored block into the window.</summary>
    /// <remarks>
    /// <b>One routine for tiles and strips, because a strip is a tile the width of the
    /// image.</b> Two copies of this arithmetic is two places for an off-by-one, and
    /// the corpus's diagonal ramp exists precisely so that one would be visible.
    /// </remarks>
    private static void Blit(
        double[] samples,
        byte[] block,
        int blockX,
        int blockY,
        int blockWidth,
        int blockHeight,
        int windowX,
        int windowY,
        int windowWidth,
        int windowHeight,
        int imageWidth,
        int imageHeight,
        int bands,
        int bits,
        SampleKind kind)
    {
        int bytes = bits / 8;

        for (int row = 0; row < blockHeight; row++)
        {
            int imageY = blockY + row;

            if (imageY < windowY || imageY >= windowY + windowHeight || imageY >= imageHeight)
            {
                continue;
            }

            for (int column = 0; column < blockWidth; column++)
            {
                int imageX = blockX + column;

                if (imageX < windowX || imageX >= windowX + windowWidth || imageX >= imageWidth)
                {
                    continue;
                }

                int source = (((row * blockWidth) + column) * bands) * bytes;
                int target = ((((imageY - windowY) * windowWidth) + (imageX - windowX)) * bands);

                for (int band = 0; band < bands; band++)
                {
                    int at = source + (band * bytes);

                    if (at + bytes > block.Length)
                    {
                        continue;
                    }

                    samples[target + band] = Sample(block, at, kind);
                }
            }
        }
    }

    /// <summary>One stored value, as a number.</summary>
    private static double Sample(byte[] block, int at, SampleKind kind) => kind switch
    {
        SampleKind.Unsigned8 => block[at],
        SampleKind.Signed16 => BitConverter.ToInt16(block, at),
        SampleKind.Unsigned16 => BitConverter.ToUInt16(block, at),
        SampleKind.Signed32 => BitConverter.ToInt32(block, at),
        SampleKind.Real32 => BitConverter.ToSingle(block, at),
        SampleKind.Real64 => BitConverter.ToDouble(block, at),
        _ => 0,
    };

    private static int Scalar(BitMiracle.LibTiff.Classic.Tiff tiff, TiffTag tag)
    {
        FieldValue[]? value = tiff.GetField(tag);
        return value is { Length: > 0 } ? value[0].ToInt() : 0;
    }

    /// <summary>Reads everything knowable without touching a pixel.</summary>
    private static CoverageInfo Describe(
        BitMiracle.LibTiff.Classic.Tiff tiff, out List<int> directories)
    {
        tiff.SetDirectory(0);

        int width = Scalar(tiff, TiffTag.IMAGEWIDTH);
        int height = Scalar(tiff, TiffTag.IMAGELENGTH);
        int bands = Math.Max(1, Scalar(tiff, TiffTag.SAMPLESPERPIXEL));
        int bits = Scalar(tiff, TiffTag.BITSPERSAMPLE);

        FieldValue[]? formatTag = tiff.GetField(TiffTag.SAMPLEFORMAT);
        int format = formatTag is { Length: > 0 } ? formatTag[0].ToInt() : 1;

        SampleKind kind = KindOf(bits, format);
        double? noData = NoDataOf(tiff);

        List<BandInfo> bandList = new(bands);

        for (int i = 0; i < bands; i++)
        {
            bandList.Add(new BandInfo(i, kind, noData, null, null));
        }

        (int srid, Envelope extent) = Georeference(tiff, width, height);

        directories = [0];
        List<OverviewInfo> overviews = [];

        // <b>Reduced-resolution subdirectories, and only those.</b> A GeoTIFF may carry
        // a mask or a thumbnail as a further directory, and treating one of those as an
        // overview would answer a zoomed-out request with the wrong picture. The
        // NEWSUBFILETYPE bit is how the format says which is which.
        short page = 1;

        while (tiff.SetDirectory(page))
        {
            FieldValue[]? subfile = tiff.GetField(TiffTag.SUBFILETYPE);
            bool reduced = subfile is { Length: > 0 }
                && (subfile[0].ToInt() & (int)FileType.REDUCEDIMAGE) != 0;

            if (reduced)
            {
                directories.Add(page);
                overviews.Add(new OverviewInfo(
                    overviews.Count + 1,
                    Scalar(tiff, TiffTag.IMAGEWIDTH),
                    Scalar(tiff, TiffTag.IMAGELENGTH)));
            }

            page++;
        }

        tiff.SetDirectory(0);

        return new CoverageInfo(
            width,
            height,
            srid,
            extent,
            bandList,
            overviews,
            tiff.IsTiled() ? Scalar(tiff, TiffTag.TILEWIDTH) : 0,
            tiff.IsTiled() ? Scalar(tiff, TiffTag.TILELENGTH) : 0);
    }

    private static SampleKind KindOf(int bits, int format) => (bits, format) switch
    {
        (8, _) => SampleKind.Unsigned8,
        (16, 2) => SampleKind.Signed16,
        (16, _) => SampleKind.Unsigned16,
        (32, 3) => SampleKind.Real32,
        (32, _) => SampleKind.Signed32,
        (64, _) => SampleKind.Real64,
        _ => SampleKind.Unsigned8,
    };

    /// <summary>
    /// The no-data value, which TIFF carries as text rather than as a number.
    /// </summary>
    /// <remarks>
    /// Tag 42113 is <c>GDAL_NODATA</c>, an ASCII field, because there is no TIFF tag
    /// for the concept and this is the convention every writer settled on.
    /// </remarks>
    private static double? NoDataOf(BitMiracle.LibTiff.Classic.Tiff tiff)
    {
        // <b>A private tag has to be declared before it can be asked for.</b> LibTiff
        // returns null from `GetField` for any tag it does not know, and 42113 is not
        // in the TIFF specification — it is GDAL's convention, because the format has
        // no tag for the concept. Merging a field definition teaches this instance the
        // tag for the rest of its life; without it the corpus's no-data file read as
        // having none, which is a rendering decision silently lost.
        tiff.MergeFieldInfo(NoDataField, NoDataField.Length);

        FieldValue[]? value = tiff.GetField(NoDataTag);

        if (value is not { Length: > 0 })
        {
            return null;
        }

        string text = value[value.Length - 1].ToString() ?? string.Empty;

        return double.TryParse(
            text.Trim().Trim('\0'), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double parsed)
            ? parsed
            : null;
    }

    /// <summary>GDAL's no-data tag, which the TIFF specification does not define.</summary>
    private const TiffTag NoDataTag = (TiffTag)42113;

    /// <summary>The declaration that makes <see cref="NoDataTag"/> readable.</summary>
    private static readonly TiffFieldInfo[] NoDataField =
    [
        new TiffFieldInfo(
            NoDataTag, 1, 1, TiffType.ASCII, FieldBit.Custom, true, false, "GDAL_NODATA"),
    ];

    /// <summary>
    /// Where on the earth this is, from the GeoTIFF tags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ModelPixelScale and ModelTiepoint, which is the arrangement every writer
    /// emits.</b> GeoTIFF also allows a full 4×4 <c>ModelTransformation</c> in tag
    /// 34264 for rotated imagery; this reader does not read it, and a file carrying
    /// one is refused rather than placed wrongly, because a rotated raster silently
    /// treated as axis-aligned is data in the wrong place with no error anywhere.
    /// </para>
    /// <para>
    /// <b>The EPSG code comes from the GeoKey directory</b> — key 3072
    /// (<c>ProjectedCSTypeGeoKey</c>) or 2048 (<c>GeographicTypeGeoKey</c>), whichever
    /// the file uses. Reading them means walking tag 34735 in fours, which is what the
    /// specification says it is.
    /// </para>
    /// </remarks>
    private static (int Srid, Envelope Extent) Georeference(
        BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height)
    {
        if (tiff.GetField((TiffTag)34264) is { Length: > 0 })
        {
            throw new InvalidDataException(
                "This GeoTIFF carries a ModelTransformation, which means its pixels are rotated "
                + "or sheared relative to the ground. This server reads axis-aligned imagery "
                + "only, and placing a rotated raster as though it were not would put the data "
                + "in the wrong place with no error to notice.");
        }

        double[] scale = Doubles(tiff, 33550);
        double[] tie = Doubles(tiff, 33922);

        if (scale.Length < 2 || tie.Length < 6)
        {
            throw new InvalidDataException(
                "This TIFF has no georeference: ModelPixelScale (33550) and ModelTiepoint "
                + "(33922) are how a GeoTIFF says where it is, and one of them is missing. A "
                + "coverage with no position cannot be published as a layer.");
        }

        // The tiepoint is (i, j, k, x, y, z): raster point i,j sits at ground x,y.
        double originX = tie[3] - (tie[0] * scale[0]);
        double originY = tie[4] + (tie[1] * scale[1]);

        Envelope extent = new(
            originX,
            originY - (height * scale[1]),
            originX + (width * scale[0]),
            originY);

        return (SridOf(tiff), extent);
    }

    private static int SridOf(BitMiracle.LibTiff.Classic.Tiff tiff)
    {
        FieldValue[]? keys = tiff.GetField((TiffTag)34735);

        if (keys is not { Length: > 1 })
        {
            return 0;
        }

        short[] directory = keys[1].ToShortArray() ?? [];

        int geographic = 0;
        int projected = 0;

        // Four shorts of header, then four per key: id, location, count, value.
        // A location of zero means the value is the fourth short itself rather than an
        // offset into one of the two overflow arrays.
        for (int i = 4; i + 3 < directory.Length; i += 4)
        {
            if (directory[i + 1] != 0)
            {
                continue;
            }

            int value = directory[i + 3] & 0xFFFF;

            switch (directory[i])
            {
                case 2048: geographic = value; break;   // GeographicTypeGeoKey
                case 3072: projected = value; break;    // ProjectedCSTypeGeoKey
                default: break;
            }
        }

        // <b>Projected wins, and taking the first match instead was a real bug.</b> A
        // projected file carries both keys — EPSG:3857 in 3072 and the EPSG:4326 it is
        // built on in 2048 — and 2048 comes first in the directory, which is sorted by
        // key id. Reading whichever appeared first reported a Web Mercator coverage as
        // geographic, so its extent in metres would have been read as degrees and the
        // image placed a few hundred metres off the coast of Africa. Caught by the
        // corpus's one 3857 file on the first run.
        return projected != 0 ? projected : geographic;
    }

    private static double[] Doubles(BitMiracle.LibTiff.Classic.Tiff tiff, int tag)
    {
        FieldValue[]? value = tiff.GetField((TiffTag)tag);

        return value is { Length: > 1 } ? value[1].ToDoubleArray() ?? [] : [];
    }

    /// <summary>Swallows LibTiff's console chatter.</summary>
    private sealed class Quiet : TiffErrorHandler
    {
        public override void WarningHandler(
            BitMiracle.LibTiff.Classic.Tiff tiff, string method, string format, params object[] args)
        {
        }

        public override void ErrorHandler(
            BitMiracle.LibTiff.Classic.Tiff tiff, string method, string format, params object[] args)
        {
        }
    }
}
