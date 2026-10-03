using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// An image service's answers about an area or a line — ADR-141: <c>computeStatisticsHistograms</c>, the statistics and
/// histogram of the pixels inside a polygon, and <c>getSamples</c>, the values at points, along a line or across an area.
/// </summary>
/// <remarks>
/// <para>
/// <b>One read for the whole request.</b> The geometry is projected into the image's reference, and the window it
/// covers is read once, from the finest level of the image's pyramid at which it is at most
/// <see cref="MaximumAreaPixels"/> pixels — so an area the size of a country answers from an overview, as ArcGIS's
/// does, and says the pixel size it used.
/// </para>
/// <para>
/// <b>Through a raster function when one is asked for</b>, or the service's default, as identify is: the slope inside
/// a polygon is a question an elevation model is uploaded to answer.
/// </para>
/// </remarks>
internal static partial class ImageServerEndpoints
{
    /// <summary>The most pixels one area request reads; a larger area is read from an overview.</summary>
    private const long MaximumAreaPixels = 4_000_000;

    /// <summary>The most samples one <c>getSamples</c> answers.</summary>
    private const int MaximumSamples = 5000;

    /// <summary>The statistics and histogram of each band inside a polygon or envelope.</summary>
    private static async Task ComputeStatisticsHistogramsAsync(
        HttpContext context,
        string serviceName,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        IProjector projector,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        if (!ImageServerExportParameters.TryUnoffered(parameter, coverage.Info, out RasterFunction? asked, out string? unoffered))
        {
            await RefuseAsync(context, 400, unoffered!).ConfigureAwait(false);
            return;
        }

        (Geometry? area, string? error) = await AreaGeometryAsync(parameter, coverage.Info, projector, cancellation).ConfigureAwait(false);

        if (area is null)
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        if (area is not (Polygon or MultiPolygon))
        {
            await RefuseAsync(context, 400,
                "`computeStatisticsHistograms` describes the pixels inside an area: send a polygon or an envelope.")
                .ConfigureAwait(false);
            return;
        }

        // ADR-152, ADR-153: the images a mosaic rule or a time chooses.
        (readers, error) = await MosaicReadersAsync(context, parameter, coverage, readers, area.Envelope, cancellation).ConfigureAwait(false);

        if (error is not null)
        {
            await RefuseAsync(context, 400, error).ConfigureAwait(false);
            return;
        }

        RasterFunction function = asked ?? RasterFunction.FromStyleText(coverage.Style);
        AreaRead? read = await ReadAreaAsync(coverage, function, area.Envelope, PixelSizeAsked(parameter), readers, cancellation)
            .ConfigureAwait(false);
        int bands = read?.Window.Bands ?? Math.Max(1, coverage.Info.Bands.Count);
        double[] sums = new double[bands], squares = new double[bands];
        double[] lows = Enumerable.Repeat(double.MaxValue, bands).ToArray(), highs = Enumerable.Repeat(double.MinValue, bands).ToArray();
        long[] counts = new long[bands];
        List<double>[] inside = [.. Enumerable.Range(0, bands).Select(_ => new List<double>())];

        if (read is not null)
        {
            foreach ((int column, int row) in read.Cells(area))
            {
                for (int band = 0; band < bands; band++)
                {
                    double value = read.Window.At(column, row, band);

                    if (read.Absent(value, band))
                    {
                        continue;
                    }

                    sums[band] += value;
                    squares[band] += value * value;
                    lows[band] = Math.Min(lows[band], value);
                    highs[band] = Math.Max(highs[band], value);
                    counts[band]++;
                    inside[band].Add(value);
                }
            }
        }

        bool bytes = !function.Derives && function.ResultBandsFor(coverage.Info.Bands) is { Count: > 0 } kept
            && kept[0].Kind == SampleKind.Unsigned8;
        const int Size = 256;

        await Results.Ok(new
        {
            statistics = Enumerable.Range(0, bands).Select(band => counts[band] == 0
                ? (object)new { count = 0L, min = 0d, max = 0d, sum = 0d, mean = 0d, standardDeviation = 0d }
                : new
                {
                    count = counts[band],
                    min = lows[band],
                    max = highs[band],
                    sum = sums[band],
                    mean = sums[band] / counts[band],
                    standardDeviation = Math.Sqrt(Math.Max(0, (squares[band] / counts[band]) - Math.Pow(sums[band] / counts[band], 2))),
                    median = Median(inside[band]),
                }).ToArray(),
            histograms = Enumerable.Range(0, bands).Select(band =>
            {
                double low = bytes ? -0.5 : counts[band] == 0 ? 0 : lows[band];
                double high = bytes ? 255.5 : counts[band] == 0 ? 0 : highs[band];
                long[] bins = new long[Size];

                if (high > low)
                {
                    foreach (double value in inside[band])
                    {
                        bins[Math.Clamp((int)((value - low) / (high - low) * Size), 0, Size - 1)]++;
                    }
                }

                return new { size = Size, min = low, max = high, counts = bins };
            }).ToArray(),
            // Not ArcGIS's field: the cell size the answer was read at, which is coarser than the image's own for an area
            // too large to read whole (ADR-141).
            pixelSize = read is null ? null : new { x = read.PixelWidth, y = read.PixelHeight },
            rasterFunction = function.Kind == RasterFunctionKind.None ? null : function.Name,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The values at points, along lines, or across an area.</summary>
    private static async Task GetSamplesAsync(
        HttpContext context,
        string serviceName,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        IProjector projector,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        if (!ImageServerExportParameters.TryUnoffered(parameter, coverage.Info, out RasterFunction? asked, out string? unoffered))
        {
            await RefuseAsync(context, 400, unoffered!).ConfigureAwait(false);
            return;
        }

        (Geometry? geometry, string? error) = await AreaGeometryAsync(parameter, coverage.Info, projector, cancellation).ConfigureAwait(false);

        if (geometry is null)
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        int count = MaximumSamples;

        if (parameter("sampleCount") is { Length: > 0 } countText
            && (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count <= 0))
        {
            await RefuseAsync(context, 400, "`sampleCount` is a whole number above zero.").ConfigureAwait(false);
            return;
        }

        count = Math.Min(count, MaximumSamples);
        double? distance = null;

        if (parameter("sampleDistance") is { Length: > 0 } distanceText)
        {
            if (!double.TryParse(distanceText, NumberStyles.Float, CultureInfo.InvariantCulture, out double given) || !(given > 0))
            {
                await RefuseAsync(context, 400, "`sampleDistance` is a distance above zero, in the image's units.").ConfigureAwait(false);
                return;
            }

            distance = given;
        }

        CoverageInfo info = coverage.Info;

        // ADR-152, ADR-153: the images a mosaic rule or a time chooses.
        (readers, error) = await MosaicReadersAsync(context, parameter, coverage, readers, geometry.Envelope, cancellation).ConfigureAwait(false);

        if (error is not null)
        {
            await RefuseAsync(context, 400, error).ConfigureAwait(false);
            return;
        }

        RasterFunction function = asked ?? RasterFunction.FromStyleText(coverage.Style);
        AreaRead? read = await ReadAreaAsync(coverage, function, geometry.Envelope, PixelSizeAsked(parameter), readers, cancellation)
            .ConfigureAwait(false);
        List<(double X, double Y)> points = SamplePoints(geometry, count, distance ?? read?.PixelWidth ?? info.PixelWidth, distance is not null);
        object reference = new { wkid = info.Srid, latestWkid = info.Srid };

        await Results.Ok(new
        {
            samples = points.Select((p, index) => new
            {
                location = new { x = p.X, y = p.Y, spatialReference = reference },
                locationId = index,
                value = read?.ValueAt(p.X, p.Y, function) ?? "NoData",
                rasterId = 1,
                resolution = read?.PixelWidth ?? info.PixelWidth,
                attributes = new { },
            }).ToArray(),
            rasterFunction = function.Kind == RasterFunctionKind.None ? null : function.Name,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The request's <c>geometry</c> — a point, multipoint, polyline, polygon or envelope — in the image's reference.
    /// </summary>
    private static async Task<(Geometry? Geometry, string? Error)> AreaGeometryAsync(
        Func<string, string?> parameter, CoverageInfo info, IProjector projector, CancellationToken cancellation)
    {
        if (parameter("geometry") is not { Length: > 0 } text)
        {
            return (null, "`geometry` is required: a point, multipoint, polyline, polygon or envelope as Esri JSON.");
        }

        JsonElement json;

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            json = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // The comma form a browser address bar sends for a point: x,y.
            string[] parts = text.Split(',');

            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double px)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double py))
            {
                return (new Point(px, py), null);
            }

            return (null, "`geometry` is not JSON, nor a point written x,y.");
        }

        if (json.ValueKind != JsonValueKind.Object)
        {
            return (null, "`geometry` is an Esri JSON object.");
        }

        int srid = info.Srid;

        if (json.TryGetProperty("spatialReference", out JsonElement reference) && reference.ValueKind == JsonValueKind.Object)
        {
            if ((reference.TryGetProperty("latestWkid", out JsonElement wkid) || reference.TryGetProperty("wkid", out wkid))
                && wkid.TryGetInt32(out int declared))
            {
                srid = declared == 102100 ? 3857 : declared;
            }
        }
        else if (parameter("sr") is { Length: > 0 } sr && int.TryParse(sr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int given))
        {
            srid = given == 102100 ? 3857 : given;
        }

        Geometry? geometry;

        if (json.TryGetProperty("xmin", out JsonElement xmin))
        {
            if (!xmin.TryGetDouble(out double minX) || !Number(json, "ymin", out double minY)
                || !Number(json, "xmax", out double maxX) || !Number(json, "ymax", out double maxY) || !(maxX > minX) || !(maxY > minY))
            {
                return (null, "An envelope is xmin, ymin, xmax and ymax, the maximums above the minimums.");
            }

            geometry = new Polygon(new LinearRing(XySequence.Wrap([minX, minY, maxX, minY, maxX, maxY, minX, maxY, minX, minY])));
        }
        else if (!ArcGisGeometryReader.TryReadForEdit(json, srid, out geometry, out _, out string? error))
        {
            return (null, error);
        }

        if (srid == info.Srid)
        {
            return (geometry, null);
        }

        (IReadOnlyList<Geometry> projected, _) = await projector.ProjectAsync([geometry!], srid, info.Srid, cancellation)
            .ConfigureAwait(false);

        return projected.Count == 1 && !projected[0].IsEmpty
            ? (projected[0], null)
            : (null, $"The geometry could not be projected from EPSG:{srid.ToString(CultureInfo.InvariantCulture)} into this "
                + $"image's EPSG:{info.Srid.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static bool Number(JsonElement json, string name, out double value)
    {
        value = 0;
        return json.TryGetProperty(name, out JsonElement element) && element.TryGetDouble(out value);
    }

    /// <summary>The <c>pixelSize</c> a client asks the answer to be read at, as its x, or null.</summary>
    private static double? PixelSizeAsked(Func<string, string?> parameter)
    {
        if (parameter("pixelSize") is not { Length: > 0 } text)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("x", out JsonElement x) && x.TryGetDouble(out double size) && size > 0 ? size : null;
        }
        catch (JsonException)
        {
            return double.TryParse(text.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double size) && size > 0 ? size : null;
        }
    }

    /// <summary>
    /// Reads the window an envelope covers, from the finest level at which it is at most <see cref="MaximumAreaPixels"/>
    /// pixels and not finer than a pixel size asked for; null when the envelope misses the image.
    /// </summary>
    private static async Task<AreaRead?> ReadAreaAsync(
        PublishedCoverage coverage, RasterFunction function, Envelope envelope, double? pixelSize,
        ICoverageReaderFactory readers, CancellationToken cancellation)
    {
        CoverageInfo info = coverage.Info;
        double minX = Math.Max(envelope.MinX, info.Extent.MinX), maxX = Math.Min(envelope.MaxX, info.Extent.MaxX);
        double minY = Math.Max(envelope.MinY, info.Extent.MinY), maxY = Math.Min(envelope.MaxY, info.Extent.MaxY);

        if (minX > maxX || minY > maxY)
        {
            return null;
        }

        for (int level = 0; level <= info.Overviews.Count; level++)
        {
            int width = level == 0 ? info.Width : info.Overviews[level - 1].Width;
            int height = level == 0 ? info.Height : info.Overviews[level - 1].Height;
            double pixelX = info.Extent.Width / width, pixelY = info.Extent.Height / height;
            int left = Math.Clamp((int)Math.Floor((minX - info.Extent.MinX) / pixelX), 0, width - 1);
            // One more pixel each way than the envelope reaches, so a point on its right or bottom edge — the end of a
            // line, a corner — falls in the window rather than just outside it.
            int right = Math.Clamp((int)Math.Floor((maxX - info.Extent.MinX) / pixelX) + 1, left + 1, width);
            int top = Math.Clamp((int)Math.Floor((info.Extent.MaxY - maxY) / pixelY), 0, height - 1);
            int bottom = Math.Clamp((int)Math.Floor((info.Extent.MaxY - minY) / pixelY) + 1, top + 1, height);

            bool last = level == info.Overviews.Count;

            if (!last && (((long)(right - left) * (bottom - top) > MaximumAreaPixels) || (pixelSize is { } wanted && pixelX < wanted * 0.999)))
            {
                continue;
            }

            if ((long)(right - left) * (bottom - top) > MaximumAreaPixels * 4)
            {
                return null;
            }

            using ICoverageReader reader = await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);
            (CoverageWindow window, _) = await ReadThroughAsync(reader, coverage, function, level, left, top, right - left, bottom - top, cancellation)
                .ConfigureAwait(false);

            return new AreaRead(window, info.Extent.MinX + (left * pixelX), info.Extent.MaxY - (top * pixelY), pixelX, pixelY,
                function.Derives ? [] : function.ResultBandsFor(info.Bands));
        }

        return null;
    }

    /// <summary>Where to sample: the points themselves, evenly along each line, or pixel centres across an area.</summary>
    private static List<(double X, double Y)> SamplePoints(Geometry geometry, int count, double step, bool stepGiven)
    {
        List<(double, double)> points = [];

        switch (geometry)
        {
            case Point point:
                points.Add((point.X, point.Y));
                break;

            case MultiPoint many:
                points.AddRange(many.Parts.Take(count).Select(p => (p.X, p.Y)));
                break;

            case LineString or MultiLineString:
            {
                List<LineString> lines = geometry is LineString one ? [one] : [.. ((MultiLineString)geometry).Parts];
                double length = lines.Sum(Length);
                double spacing = stepGiven ? step : Math.Max(step, length / Math.Max(1, count - 1));

                foreach (LineString line in lines)
                {
                    double[] xy = line.Coordinates.ToInterleavedArray();
                    double carried = 0;
                    points.Add((xy[0], xy[1]));

                    for (int i = 2; i + 1 < xy.Length && points.Count < count; i += 2)
                    {
                        double dx = xy[i] - xy[i - 2], dy = xy[i + 1] - xy[i - 1];
                        double segment = Math.Sqrt((dx * dx) + (dy * dy));

                        for (double along = spacing - carried; along <= segment && points.Count < count; along += spacing)
                        {
                            points.Add((xy[i - 2] + (dx * along / segment), xy[i - 1] + (dy * along / segment)));
                        }

                        carried = (carried + segment) % spacing;
                    }
                }

                break;
            }

            default:
            {
                // An area: pixel centres on a grid, as coarse as keeps them within the count.
                Envelope box = geometry.Envelope;
                List<double[]> rings = RingsOf(geometry);
                double cell = Math.Max(step, Math.Sqrt(box.Width * box.Height / Math.Max(1, count)));

                for (double y = box.MaxY - (cell / 2); y > box.MinY && points.Count < count; y -= cell)
                {
                    for (double x = box.MinX + (cell / 2); x < box.MaxX && points.Count < count; x += cell)
                    {
                        if (Inside(rings, x, y))
                        {
                            points.Add((x, y));
                        }
                    }
                }

                break;
            }
        }

        return points;
    }

    private static double Length(LineString line)
    {
        double[] xy = line.Coordinates.ToInterleavedArray();
        double total = 0;

        for (int i = 2; i + 1 < xy.Length; i += 2)
        {
            total += Math.Sqrt(Math.Pow(xy[i] - xy[i - 2], 2) + Math.Pow(xy[i + 1] - xy[i - 1], 2));
        }

        return total;
    }

    /// <summary>An area's rings, each as interleaved x and y, for even-odd tests: a hole is outside.</summary>
    private static List<double[]> RingsOf(Geometry area)
    {
        IEnumerable<Polygon> polygons = area switch
        {
            Polygon one => [one],
            MultiPolygon many => many.Parts,
            _ => [],
        };

        return [.. polygons.SelectMany(p => p.Holes.Prepend(p.Shell)).Select(r => r.Coordinates.ToInterleavedArray())];
    }

    /// <summary>Where a horizontal line crosses an area's rings, left to right.</summary>
    private static List<double> Crossings(List<double[]> rings, double y)
    {
        List<double> crossings = [];

        foreach (double[] xy in rings)
        {
            for (int i = 0, j = xy.Length - 2; i + 1 < xy.Length; j = i, i += 2)
            {
                if ((xy[i + 1] > y) != (xy[j + 1] > y))
                {
                    crossings.Add(((xy[j] - xy[i]) * (y - xy[i + 1]) / (xy[j + 1] - xy[i + 1])) + xy[i]);
                }
            }
        }

        crossings.Sort();
        return crossings;
    }

    /// <summary>Whether a point is inside an area: an odd number of crossings to its left.</summary>
    private static bool Inside(List<double[]> rings, double x, double y) =>
        Crossings(rings, y).Count(c => c < x) % 2 == 1;

    private static double? Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2;
    }

    /// <summary>A window read for an area: its values and where they sit.</summary>
    private sealed record AreaRead(
        CoverageWindow Window, double Left, double Top, double PixelWidth, double PixelHeight, IReadOnlyList<BandInfo> Bands)
    {
        /// <summary>Whether a value is no value: NaN, or a band's no-data.</summary>
        public bool Absent(double value, int band) =>
            double.IsNaN(value) || (band < Bands.Count && Bands[band].NoData is { } none && value == none);

        /// <summary>The cells whose centres are inside an area, a row at a time between the rings' crossings.</summary>
        public IEnumerable<(int Column, int Row)> Cells(Geometry area)
        {
            List<double[]> rings = RingsOf(area);

            for (int row = 0; row < Window.Height; row++)
            {
                List<double> crossings = Crossings(rings, Top - ((row + 0.5) * PixelHeight));

                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    // Columns whose centres lie between this pair of crossings.
                    int first = Math.Max(0, (int)Math.Ceiling(((crossings[k] - Left) / PixelWidth) - 0.5));
                    int last = Math.Min(Window.Width - 1, (int)Math.Floor(((crossings[k + 1] - Left) / PixelWidth) - 0.5));

                    for (int column = first; column <= last; column++)
                    {
                        yield return (column, row);
                    }
                }
            }
        }

        /// <summary>The value at a point, as identify spells it: bands space-separated, or <c>NoData</c>.</summary>
        public string ValueAt(double x, double y, RasterFunction function)
        {
            int column = (int)Math.Floor((x - Left) / PixelWidth), row = (int)Math.Floor((Top - y) / PixelHeight);

            if (column < 0 || row < 0 || column >= Window.Width || row >= Window.Height)
            {
                return "NoData";
            }

            if (function.Derives)
            {
                double derived = Window.At(column, row, 0);
                return double.IsNaN(derived) ? "NoData" : RasterFunction.Say(derived);
            }

            string[] values = new string[Window.Bands];
            bool measured = false;

            for (int band = 0; band < Window.Bands; band++)
            {
                double value = Window.At(column, row, band);
                values[band] = value.ToString(CultureInfo.InvariantCulture);
                measured |= !Absent(value, band);
            }

            return measured ? string.Join(' ', values) : "NoData";
        }
    }
}
