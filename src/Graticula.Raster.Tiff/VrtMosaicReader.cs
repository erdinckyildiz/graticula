using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Graticula.Coverages;
using Graticula.Geometries;

namespace Graticula.Raster.Tiff;

/// <summary>
/// Reads several GeoTIFFs as one image — ADR-140's mosaic — described by a GDAL virtual raster (<c>.vrt</c>): one grid,
/// each file placed on it at a whole-pixel offset, the later drawn over the earlier where they overlap.
/// </summary>
/// <remarks>
/// <para>
/// <b>GDAL's VRT, the subset a mosaic of aligned files needs</b>: <c>SimpleSource</c> and <c>ComplexSource</c> placing
/// each whole file at its own size. A VRT that resamples, crops or reprojects a source is refused by name rather than
/// read as if it did not — the same file then opens in GDAL, QGIS and ArcGIS, which read VRT, and means the same there.
/// </para>
/// <para>
/// <b>A source's no-data is transparent</b>, as a <c>ComplexSource</c> with <c>NODATA</c> is, so a file's empty edge
/// does not cover its neighbour. A <c>SimpleSource</c> is read the same way: no mosaic wants an empty edge drawn on top.
/// </para>
/// <para>
/// <b>Overviews are the sources' own</b>, level for level, so a mosaic is as quick to see whole as its files are; it
/// has as many as the source with the fewest. A source's position at an overview is its offset halved and rounded, so
/// two files may meet a pixel apart there and never at full resolution.
/// </para>
/// </remarks>
public sealed class VrtMosaicReader : ICoverageReader
{
    private readonly List<Source> _sources;
    private readonly Dictionary<int, TiffCoverageReader> _open = [];

    private VrtMosaicReader(CoverageInfo info, List<Source> sources)
    {
        Info = info;
        _sources = sources;
    }

    /// <inheritdoc/>
    public CoverageInfo Info { get; }

    /// <summary>The files a mosaic is made of, in the order they are drawn.</summary>
    public IReadOnlyList<string> Files => [.. _sources.Select(s => s.Path)];

    /// <summary>Each file and where it lies, in the mosaic's reference, in the order they are drawn — ADR-152's catalog.</summary>
    public IReadOnlyList<(string Path, Envelope Extent)> Placed
    {
        get
        {
            double perX = Info.Extent.Width / Info.Width, perY = Info.Extent.Height / Info.Height;
            return [.. _sources.Select(s => (s.Path, new Envelope(
                Info.Extent.MinX + (s.X * perX), Info.Extent.MaxY - ((s.Y + s.Height) * perY),
                Info.Extent.MinX + ((s.X + s.Width) * perX), Info.Extent.MaxY - (s.Y * perY))))];
        }
    }

    /// <summary>
    /// The same mosaic drawing only some of its files, in another order — ADR-152's mosaic rule. The last is drawn over
    /// the others, as in the mosaic itself.
    /// </summary>
    /// <param name="order">Positions in <see cref="Files"/>, in drawing order.</param>
    /// <returns>A reader of its own, to be disposed.</returns>
    public VrtMosaicReader Arranged(IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new VrtMosaicReader(Info, [.. order.Where(i => i >= 0 && i < _sources.Count).Select(i => _sources[i])]);
    }

    /// <summary>
    /// The files a virtual raster names, read without opening them — for deleting a mosaic whose files may already be
    /// gone.
    /// </summary>
    /// <param name="path">The <c>.vrt</c>.</param>
    /// <returns>Their full paths, each once.</returns>
    public static IReadOnlyList<string> FilesOf(string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return [.. XElement.Load(path).Descendants("SourceFilename")
            .Select(f => Path.GetFullPath((string?)f.Attribute("relativeToVRT") == "1" ? Path.Combine(directory, f.Value.Trim()) : f.Value.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Whether a path names a virtual raster rather than a TIFF.</summary>
    /// <param name="path">The path.</param>
    /// <returns>Whether it ends in <c>.vrt</c>.</returns>
    public static bool IsMosaic(string path) =>
        path.EndsWith(".vrt", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a mosaic.</summary>
    /// <param name="path">The <c>.vrt</c>.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="InvalidDataException">The file is not a mosaic this server reads.</exception>
    public static VrtMosaicReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        XElement root;

        try
        {
            root = XElement.Load(path);
        }
        catch (System.Xml.XmlException e)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a GDAL virtual raster: {e.Message}");
        }

        if (root.Name.LocalName != "VRTDataset")
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a GDAL virtual raster (no VRTDataset).");
        }

        int width = Int(root, "rasterXSize");
        int height = Int(root, "rasterYSize");
        int srid = Srid(root.Element("SRS")?.Value ?? string.Empty);
        double[] transform = [.. (root.Element("GeoTransform")?.Value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture))];

        if (transform.Length != 6 || transform[2] != 0 || transform[4] != 0 || transform[1] <= 0 || transform[5] >= 0)
        {
            throw new InvalidDataException("A mosaic's GeoTransform is six numbers, north up and not rotated.");
        }

        List<XElement> bands = [.. root.Elements("VRTRasterBand")];

        if (bands.Count == 0)
        {
            throw new InvalidDataException("A mosaic has at least one VRTRasterBand.");
        }

        SampleKind kind = KindOf((string?)bands[0].Attribute("dataType") ?? "Byte");
        double? noData = bands[0].Element("NoDataValue") is { } n
            ? double.Parse(n.Value, CultureInfo.InvariantCulture) : null;
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;

        // The first band's sources are the mosaic's; every band must place the same files the same way.
        List<Source> sources = [];

        foreach (XElement placed in Placements(bands[0]))
        {
            sources.Add(SourceOf(placed, directory));
        }

        foreach (XElement band in bands.Skip(1))
        {
            List<Source> same = [.. Placements(band).Select(p => SourceOf(p, directory))];

            if (same.Count != sources.Count || same.Zip(sources).Any(p => p.First != p.Second with { Band = p.First.Band }))
            {
                throw new InvalidDataException("Every band of a mosaic places the same files at the same places.");
            }
        }

        if (sources.Count == 0)
        {
            throw new InvalidDataException("A mosaic places at least one file.");
        }

        // The overviews every source has; a level is the mosaic halved, rounded up, as each source's is.
        int levels = int.MaxValue;

        foreach (Source source in sources)
        {
            if (!File.Exists(source.Path))
            {
                throw new InvalidDataException($"The mosaic's file '{Path.GetFileName(source.Path)}' is missing.");
            }

            using TiffCoverageReader reader = TiffCoverageReader.Open(source.Path);

            if (reader.Info.Width != source.Width || reader.Info.Height != source.Height)
            {
                throw new InvalidDataException(
                    $"The mosaic places '{Path.GetFileName(source.Path)}' at {source.Width} × {source.Height}, and the file is "
                    + $"{reader.Info.Width} × {reader.Info.Height}. This server reads a mosaic whose files are placed whole, "
                    + "at their own size.");
            }

            if (reader.Info.Bands.Count != bands.Count || reader.Info.Bands[0].Kind != kind)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(source.Path)}' has {reader.Info.Bands.Count} {reader.Info.Bands[0].Kind} bands, and the "
                    + $"mosaic {bands.Count} {kind}.");
            }

            levels = Math.Min(levels, reader.Info.Overviews.Count);
        }

        List<OverviewInfo> overviews = [];

        for (int level = 1, w = width, h = height; level <= levels && Math.Max(w, h) > 1; level++)
        {
            w = (w + 1) / 2;
            h = (h + 1) / 2;
            overviews.Add(new OverviewInfo(level, w, h));
        }

        Envelope extent = new(transform[0], transform[3] + (height * transform[5]), transform[0] + (width * transform[1]), transform[3]);
        CoverageInfo info = new(
            width,
            height,
            srid,
            extent,
            [.. Enumerable.Range(0, bands.Count).Select(b => new BandInfo(b, kind, noData, null, null))],
            overviews,
            0,
            0);

        return new VrtMosaicReader(info, sources);
    }

    /// <summary>
    /// Writes the virtual raster of a mosaic of GeoTIFFs — ADR-140 — after checking that they make one: the same
    /// reference, bands, sample type and pixel size, on one grid.
    /// </summary>
    /// <param name="path">Where to write the <c>.vrt</c>; the files are named relative to it when beside it.</param>
    /// <param name="files">The GeoTIFFs, in the order they are drawn, the later over the earlier.</param>
    /// <exception cref="InvalidDataException">The files do not make one mosaic; the message says which and why.</exception>
    public static void Write(string path, IReadOnlyList<string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count < 2)
        {
            throw new InvalidDataException("A mosaic is made of two or more images.");
        }

        List<(string Path, CoverageInfo Info)> read = [];

        foreach (string file in files)
        {
            using TiffCoverageReader reader = TiffCoverageReader.Open(file);
            read.Add((file, reader.Info));
        }

        IReadOnlyList<MosaicMisfit> misfits = Misfits([.. read.Select(r => r.Info)]);

        if (misfits.Count > 0)
        {
            throw new InvalidDataException(Say(misfits, read.Count, i => $"image {i + 1}") + ".");
        }

        CoverageInfo first = read[0].Info;
        double pixelX = first.Extent.Width / first.Width;
        double pixelY = first.Extent.Height / first.Height;

        double minX = read.Min(r => r.Info.Extent.MinX);
        double maxY = read.Max(r => r.Info.Extent.MaxY);
        double maxX = read.Max(r => r.Info.Extent.MaxX);
        double minY = read.Min(r => r.Info.Extent.MinY);
        int width = (int)Math.Round((maxX - minX) / pixelX);
        int height = (int)Math.Round((maxY - minY) / pixelY);
        List<(string File, int X, int Y, int Width, int Height)> placed = [];

        for (int i = 0; i < read.Count; i++)
        {
            CoverageInfo info = read[i].Info;
            double x = (info.Extent.MinX - minX) / pixelX;
            double y = (maxY - info.Extent.MaxY) / pixelY;

            placed.Add((read[i].Path, (int)Math.Round(x), (int)Math.Round(y), info.Width, info.Height));
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        double? noData = first.Bands[0].NoData;

        XElement Band(int band) => new(
            "VRTRasterBand",
            new XAttribute("dataType", DataType(first.Bands[0].Kind)),
            new XAttribute("band", band + 1),
            noData is { } none ? new XElement("NoDataValue", none.ToString("R", CultureInfo.InvariantCulture)) : null,
            placed.Select(p =>
            {
                bool beside = string.Equals(Path.GetDirectoryName(Path.GetFullPath(p.File)), directory, StringComparison.OrdinalIgnoreCase);
                return new XElement(
                    "ComplexSource",
                    new XElement("SourceFilename", new XAttribute("relativeToVRT", beside ? 1 : 0),
                        beside ? Path.GetFileName(p.File) : Path.GetFullPath(p.File)),
                    new XElement("SourceBand", band + 1),
                    new XElement("SrcRect", Rect(0, 0, p.Width, p.Height)),
                    new XElement("DstRect", Rect(p.X, p.Y, p.Width, p.Height)),
                    noData is { } own ? new XElement("NODATA", own.ToString("R", CultureInfo.InvariantCulture)) : null);
            }));

        XElement root = new(
            "VRTDataset",
            new XAttribute("rasterXSize", width),
            new XAttribute("rasterYSize", height),
            new XElement("SRS", $"EPSG:{first.Srid.ToString(CultureInfo.InvariantCulture)}"),
            new XElement("GeoTransform", string.Join(", ", new[] { minX, pixelX, 0, maxY, 0, -pixelY }
                .Select(v => v.ToString("R", CultureInfo.InvariantCulture)))),
            Enumerable.Range(0, first.Bands.Count).Select(Band));

        root.Save(path);
    }

    /// <summary>
    /// Every image that does not fit the mosaic — ADR-140: another coordinate system, other bands, another pixel size, or
    /// off the grid — read from headers alone, so it is cheap, and naming all of them rather than the first.
    /// </summary>
    /// <remarks>
    /// <b>Measured against what most of the images share</b>, not against the first: the first by name was the odd one in
    /// the ux review's second pass, and the refusal then told its owner to remove every good tile. The largest group
    /// sharing a coordinate system, bands and pixel size is the mosaic; ties go to the group with the earlier image.
    /// </remarks>
    /// <param name="images">What each image is, in the order they were given.</param>
    /// <returns>The images that do not fit, each with how it differs and what the others are.</returns>
    public static IReadOnlyList<MosaicMisfit> Misfits(IReadOnlyList<CoverageInfo> images) =>
        Misfits(images, images is { Count: > 0 } ? ReferenceOf(images) : 0);

    /// <summary>
    /// The image the others are measured against: the earliest of the largest group sharing a reference, bands, sample
    /// type and pixel size.
    /// </summary>
    /// <param name="images">The images.</param>
    /// <returns>Its index.</returns>
    public static int ReferenceOf(IReadOnlyList<CoverageInfo> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        string Kind(CoverageInfo info) =>
            $"{info.Srid}|{info.Bands.Count}|{info.Bands[0].Kind}|{(info.Extent.Width / info.Width).ToString("G6", CultureInfo.InvariantCulture)}";

        return images
            .Select((info, index) => (Key: Kind(info), Index: index))
            .GroupBy(g => g.Key)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(m => m.Index))
            .First()
            .Min(m => m.Index);
    }

    /// <summary>Every image that does not fit the grid of the one given — ADR-147, an image added to a mosaic.</summary>
    /// <param name="images">What each image is.</param>
    /// <param name="reference">The image whose grid the rest must fit.</param>
    /// <returns>The images that do not fit.</returns>
    public static IReadOnlyList<MosaicMisfit> Misfits(IReadOnlyList<CoverageInfo> images, int reference)
    {
        ArgumentNullException.ThrowIfNull(images);

        if (images.Count < 2)
        {
            return [];
        }

        static double Pixel(CoverageInfo info) => info.Extent.Width / info.Width;

        CoverageInfo first = images[reference];
        double pixelX = Pixel(first);
        double pixelY = first.Extent.Height / first.Height;
        bool degrees = Graticula.Geometries.AxisOrder.IsGeographic(first.Srid);
        string Size(double value) => degrees
            ? value.ToString("G6", CultureInfo.InvariantCulture) + "° pixels"
            : value.ToString("G6", CultureInfo.InvariantCulture) + $"-unit pixels (EPSG:{first.Srid.ToString(CultureInfo.InvariantCulture)})";
        // In the words ArcGIS uses for a pixel type, not this server's enum names (the ux review, 2026-10-03).
        static string Kind(SampleKind kind) => kind switch
        {
            SampleKind.Unsigned8 => "8-bit unsigned",
            SampleKind.Signed16 => "16-bit signed",
            SampleKind.Unsigned16 => "16-bit unsigned",
            SampleKind.Signed32 => "32-bit signed",
            SampleKind.Real32 => "32-bit float",
            _ => "64-bit float",
        };
        string Bands(CoverageInfo info) => $"{info.Bands.Count} {Kind(info.Bands[0].Kind)} band{(info.Bands.Count == 1 ? "" : "s")}";
        List<MosaicMisfit> misfits = [];

        for (int i = 0; i < images.Count; i++)
        {
            CoverageInfo info = images[i];

            // Bands first: an image in another reference with other bands is resampled and then cannot join anyway, so
            // what cannot be fixed is said before what can (the ux review, 2026-10-03: it was a 500).
            if (info.Bands.Count != first.Bands.Count || info.Bands[0].Kind != first.Bands[0].Kind)
            {
                misfits.Add(new MosaicMisfit(i, $"have {Bands(info)}", $"have {Bands(first)}"));
                continue;
            }

            if (info.Srid != first.Srid)
            {
                misfits.Add(new MosaicMisfit(i, $"are in EPSG:{info.Srid}", $"are in EPSG:{first.Srid}") { Conformable = true });
                continue;
            }

            double x = Pixel(info), y = info.Extent.Height / info.Height;

            if (Math.Abs(x - pixelX) > pixelX * 1e-6 || Math.Abs(y - pixelY) > pixelY * 1e-6)
            {
                misfits.Add(new MosaicMisfit(i, $"have {Size(x)}", $"have {Size(pixelX)}") { Conformable = true });
                continue;
            }

            double offX = (info.Extent.MinX - first.Extent.MinX) / pixelX;
            double offY = (first.Extent.MaxY - info.Extent.MaxY) / pixelY;
            double across = Math.Abs(offX - Math.Round(offX)), down = Math.Abs(offY - Math.Round(offY));

            if (across > 1e-3 || down > 1e-3)
            {
                misfits.Add(new MosaicMisfit(i, $"are {Math.Max(across, down):0.###} of a pixel off the grid", string.Empty) { Conformable = true });
            }
        }

        return misfits;
    }

    /// <summary>
    /// Says which images do not fit, grouped by how they differ — "a.tif, b.tif and c.tif have 0.002° pixels where the
    /// other 9 have 0.001° pixels" — naming at most ten of a group.
    /// </summary>
    /// <param name="misfits">From <see cref="Misfits(IReadOnlyList{CoverageInfo})"/>.</param>
    /// <param name="total">How many images there were.</param>
    /// <param name="name">An image's name, by its index.</param>
    /// <returns>The sentence, without a final full stop.</returns>
    public static string Say(IReadOnlyList<MosaicMisfit> misfits, int total, Func<int, string> name) =>
        Say(misfits, total, name, against: null);

    /// <summary>
    /// The misfits in words, measured against what is named — "this image service" when images are added to one, so the
    /// count of the others is not a number the reader cannot see (the ux review, 2026-10-03).
    /// </summary>
    /// <param name="misfits">The misfits.</param>
    /// <param name="total">How many images there are.</param>
    /// <param name="name">Each image's name.</param>
    /// <param name="against">What they are measured against, said as one; null for "the other N".</param>
    /// <returns>The sentence.</returns>
    public static string Say(IReadOnlyList<MosaicMisfit> misfits, int total, Func<int, string> name, string? against)
    {
        ArgumentNullException.ThrowIfNull(misfits);
        ArgumentNullException.ThrowIfNull(name);

        int fitting = total - misfits.Count;

        return string.Join("; ", misfits.GroupBy(m => (m.Difference, m.Expected)).Select(group =>
        {
            List<string> names = [.. group.Select(m => name(m.Image))];
            string listed = names.Count <= 10
                ? names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
                : string.Join(", ", names.Take(10)) + $" and {names.Count - 10} more";
            string others = against ?? (fitting == 1 ? "the other one" : $"the other {fitting.ToString(CultureInfo.InvariantCulture)}");
            int agreeing = against is null ? fitting : 1;
            return group.Key.Expected.Length == 0
                ? $"{listed} {Agree(group.Key.Difference, names.Count)}"
                : $"{listed} {Agree(group.Key.Difference, names.Count)} where {others} {Agree(group.Key.Expected, agreeing)}";
        }));
    }

    /// <summary>A predicate written for many — "are …", "have …" — said of one when there is one.</summary>
    private static string Agree(string predicate, int count) => count != 1
        ? predicate
        : predicate.StartsWith("are ", StringComparison.Ordinal) ? "is " + predicate[4..]
        : predicate.StartsWith("have ", StringComparison.Ordinal) ? "has " + predicate[5..]
        : predicate;

    private static XAttribute[] Rect(int x, int y, int width, int height) =>
    [
        new("xOff", x), new("yOff", y), new("xSize", width), new("ySize", height),
    ];

    /// <inheritdoc/>
    public async Task<CoverageWindow> ReadAsync(
        int overview, int x, int y, int width, int height, CancellationToken cancellationToken)
    {
        if (overview < 0 || overview > Info.Overviews.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(overview), overview,
                $"This mosaic has {Info.Overviews.Count + 1} resolutions, counting the full one as zero.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A window has a positive size in both directions.");
        }

        int bands = Info.Bands.Count;
        double? noData = Info.Bands[0].NoData;
        double[] samples = new double[width * height * bands];

        if (noData is { } fill && fill != 0)
        {
            Array.Fill(samples, fill);
        }
        else if (noData is null && Info.Bands[0].Kind is SampleKind.Real32 or SampleKind.Real64)
        {
            // Ground no image covers is no value: a float mosaic declaring no no-data has NaN there, not a zero that
            // reads as a measurement — ADR-152, where a rule may leave a view with no image at all.
            Array.Fill(samples, double.NaN);
        }

        int factor = 1 << overview;

        for (int index = 0; index < _sources.Count; index++)
        {
            Source source = _sources[index];
            TiffCoverageReader reader = Reader(index);
            (int sourceWidth, int sourceHeight) = overview == 0
                ? (reader.Info.Width, reader.Info.Height)
                : (reader.Info.Overviews[overview - 1].Width, reader.Info.Overviews[overview - 1].Height);
            int left = source.X / factor, top = source.Y / factor;
            int fromX = Math.Max(x, left), fromY = Math.Max(y, top);
            int toX = Math.Min(x + width, left + sourceWidth), toY = Math.Min(y + height, top + sourceHeight);

            if (toX <= fromX || toY <= fromY)
            {
                continue;
            }

            CoverageWindow part = await reader.ReadAsync(overview, fromX - left, fromY - top, toX - fromX, toY - fromY, cancellationToken)
                .ConfigureAwait(false);
            double? own = reader.Info.Bands[0].NoData;

            for (int row = 0; row < part.Height; row++)
            {
                for (int column = 0; column < part.Width; column++)
                {
                    int from = ((row * part.Width) + column) * bands;
                    double first = part.Samples[from];

                    // A source's no-data is not drawn over what is beneath it.
                    if (double.IsNaN(first) || (own is { } none && first == none))
                    {
                        continue;
                    }

                    Array.Copy(part.Samples, from, samples, ((((fromY - y) + row) * width) + (fromX - x) + column) * bands, bands);
                }
            }
        }

        return new CoverageWindow(width, height, bands, samples);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (TiffCoverageReader reader in _open.Values)
        {
            reader.Dispose();
        }

        _open.Clear();
    }

    private TiffCoverageReader Reader(int index)
    {
        if (!_open.TryGetValue(index, out TiffCoverageReader? reader))
        {
            reader = TiffCoverageReader.Open(_sources[index].Path);
            _open[index] = reader;
        }

        return reader;
    }

    private sealed record Source(string Path, int Band, int X, int Y, int Width, int Height);

    private static IEnumerable<XElement> Placements(XElement band) =>
        band.Elements().Where(e => e.Name.LocalName is "SimpleSource" or "ComplexSource" ||
            (e.Name.LocalName.EndsWith("Source", StringComparison.Ordinal)
                ? throw new InvalidDataException($"A mosaic's {e.Name.LocalName} is not one this server reads; it reads SimpleSource and ComplexSource.")
                : false));

    private static Source SourceOf(XElement placed, string directory)
    {
        XElement file = placed.Element("SourceFilename")
            ?? throw new InvalidDataException("A mosaic's source names its SourceFilename.");
        string name = file.Value.Trim();
        string path = (string?)file.Attribute("relativeToVRT") == "1" ? Path.Combine(directory, name) : name;
        int band = int.TryParse(placed.Element("SourceBand")?.Value, out int b) ? b : 1;
        XElement source = placed.Element("SrcRect") ?? throw new InvalidDataException("A mosaic's source has a SrcRect.");
        XElement destination = placed.Element("DstRect") ?? throw new InvalidDataException("A mosaic's source has a DstRect.");

        if (Int(source, "xOff") != 0 || Int(source, "yOff") != 0
            || Int(source, "xSize") != Int(destination, "xSize") || Int(source, "ySize") != Int(destination, "ySize"))
        {
            throw new InvalidDataException(
                $"The mosaic crops or resamples '{Path.GetFileName(name)}'. This server reads a mosaic whose files are placed "
                + "whole, at their own size.");
        }

        return new Source(Path.GetFullPath(path), band, Int(destination, "xOff"), Int(destination, "yOff"),
            Int(destination, "xSize"), Int(destination, "ySize"));
    }

    private static int Int(XElement element, string attribute) =>
        int.TryParse((string?)element.Attribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidDataException($"A mosaic's {element.Name.LocalName} has no whole-number {attribute}.");

    /// <summary>The EPSG code of a VRT's SRS: <c>EPSG:n</c>, or the last EPSG authority of a WKT.</summary>
    private static int Srid(string srs)
    {
        Match code = Regex.Match(srs, @"^\s*EPSG:(\d+)\s*$", RegexOptions.IgnoreCase);

        if (!code.Success)
        {
            MatchCollection named = Regex.Matches(srs, @"(?:AUTHORITY|ID)\[\s*""EPSG""\s*,\s*""?(\d+)""?\s*\]", RegexOptions.IgnoreCase);
            code = named.Count > 0 ? named[^1] : code;
        }

        return code.Success ? int.Parse(code.Groups[1].Value, CultureInfo.InvariantCulture)
            : throw new InvalidDataException("A mosaic's SRS names no EPSG code, so this server cannot say where it is.");
    }

    private static SampleKind KindOf(string type) => type switch
    {
        "Byte" => SampleKind.Unsigned8,
        "Int16" => SampleKind.Signed16,
        "UInt16" => SampleKind.Unsigned16,
        "Int32" => SampleKind.Signed32,
        "Float32" => SampleKind.Real32,
        "Float64" => SampleKind.Real64,
        _ => throw new InvalidDataException($"A mosaic of {type} is not one this server reads."),
    };

    private static string DataType(SampleKind kind) => kind switch
    {
        SampleKind.Unsigned8 => "Byte",
        SampleKind.Signed16 => "Int16",
        SampleKind.Unsigned16 => "UInt16",
        SampleKind.Signed32 => "Int32",
        SampleKind.Real32 => "Float32",
        _ => "Float64",
    };
}

/// <summary>An image that does not fit a mosaic — ADR-140: which, how it differs, and what the others are.</summary>
/// <param name="Image">Its index among the images given.</param>
/// <param name="Difference">What it is, as a predicate: "have 0.002° pixels", "are in EPSG:3857".</param>
/// <param name="Expected">What the mosaic's images are: "0.001° pixels", "EPSG:4326".</param>
public sealed record MosaicMisfit(int Image, string Difference, string Expected)
{
    /// <summary>
    /// Whether resampling can make it fit — ADR-147: another reference, pixel size or grid can be; other bands or another
    /// sample type cannot.
    /// </summary>
    public bool Conformable { get; init; }
}
