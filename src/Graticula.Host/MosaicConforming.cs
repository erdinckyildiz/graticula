using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;

namespace Graticula.Host;

/// <summary>
/// Puts an image on a mosaic's grid — ADR-147, by owner decision: an image in another reference, at another pixel size
/// or off the grid is projected and resampled into a GeoTIFF of its own on the grid, so the mosaic can place it as it
/// places every other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read as a raw export reads</b> — the same warp, the same overview choice — a block of 256 pixels at a time, so an
/// image of any size is conformed without holding it in memory. Bilinear for floating-point values, which are
/// measurements; nearest for integers, which may be classes (ADR-142's rule).
/// </para>
/// <para>
/// <b>Ground the image does not cover is no-data</b>: its own no-data, NaN for floating point when it has none, and 0
/// for integers when it has none — so a conformed image's empty edge does not cover its neighbour.
/// </para>
/// </remarks>
internal static class MosaicConforming
{
    /// <summary>The most pixels a conformed image may have on a side.</summary>
    private const int MostPixels = 60_000;

    /// <summary>Whether an image is already on a grid: the same reference and pixel size, at a whole-pixel offset.</summary>
    internal static bool Fits(CoverageInfo image, CoverageInfo grid)
    {
        double px = grid.PixelWidth, py = grid.PixelHeight;
        double offX = (image.Extent.MinX - grid.Extent.MinX) / px, offY = (grid.Extent.MaxY - image.Extent.MaxY) / py;
        return image.Srid == grid.Srid
            && Math.Abs(image.PixelWidth - px) <= px * 1e-6 && Math.Abs(image.PixelHeight - py) <= py * 1e-6
            && Math.Abs(offX - Math.Round(offX)) <= 1e-3 && Math.Abs(offY - Math.Round(offY)) <= 1e-3;
    }

    /// <summary>Writes an image conformed to a grid, and answers what it now is.</summary>
    /// <param name="source">The image's file.</param>
    /// <param name="image">What it is.</param>
    /// <param name="grid">The image whose reference, pixel size and grid it is put on.</param>
    /// <param name="target">Where to write the conformed image.</param>
    /// <param name="readers">Opens images.</param>
    /// <param name="projector">Projects between references.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The conformed image's description.</returns>
    /// <exception cref="InvalidDataException">The image cannot be put on the grid; the message says why.</exception>
    internal static async Task<CoverageInfo> ConformAsync(
        string source, CoverageInfo image, CoverageInfo grid, string target, ICoverageReaderFactory readers, IProjector projector,
        CancellationToken cancellation)
    {
        Envelope extent = image.Extent;

        if (image.Srid != grid.Srid)
        {
            // The image's outline in the grid's reference: its edges densified, since a projected rectangle's edges curve.
            List<double> ring = [];

            for (int i = 0; i <= 24; i++)
            {
                double t = i / 24.0;
                ring.AddRange([extent.MinX + (extent.Width * t), extent.MinY]);
                ring.AddRange([extent.MaxX, extent.MinY + (extent.Height * t)]);
                ring.AddRange([extent.MaxX - (extent.Width * t), extent.MaxY]);
                ring.AddRange([extent.MinX, extent.MaxY - (extent.Height * t)]);
            }

            ring.AddRange([ring[0], ring[1]]);
            (IReadOnlyList<Geometry> moved, _) = await projector
                .ProjectAsync([new Polygon(new LinearRing(XySequence.Wrap([.. ring])))], image.Srid, grid.Srid, cancellation)
                .ConfigureAwait(false);

            if (moved.Count != 1 || moved[0].IsEmpty || !double.IsFinite(moved[0].Envelope.MinX))
            {
                throw new InvalidDataException($"its EPSG:{image.Srid} could not be projected into the mosaic's EPSG:{grid.Srid}");
            }

            extent = moved[0].Envelope;
        }

        double px = grid.PixelWidth, py = grid.PixelHeight;
        double originX = grid.Extent.MinX, originY = grid.Extent.MaxY;
        double minX = originX + (Math.Floor(((extent.MinX - originX) / px) + 1e-6) * px);
        double maxX = originX + (Math.Ceiling(((extent.MaxX - originX) / px) - 1e-6) * px);
        double maxY = originY - (Math.Floor(((originY - extent.MaxY) / py) + 1e-6) * py);
        double minY = originY - (Math.Ceiling(((originY - extent.MinY) / py) - 1e-6) * py);
        int width = (int)Math.Round((maxX - minX) / px), height = (int)Math.Round((maxY - minY) / py);

        if (width < 1 || height < 1 || width > MostPixels || height > MostPixels)
        {
            throw new InvalidDataException(
                $"on the mosaic's grid it would be {width:N0} × {height:N0} pixels, and an image put on a grid is at most "
                + $"{MostPixels:N0} a side");
        }

        SampleKind kind = image.Bands[0].Kind;
        bool real = kind is SampleKind.Real32 or SampleKind.Real64;
        double? noData = image.Bands[0].NoData ?? (real ? double.NaN : 0);
        int bands = image.Bands.Count;
        Resampling how = real ? Resampling.Bilinear : Resampling.Nearest;

        // The image as a coverage the raw read accepts, its no-data the one written for the ground it does not cover.
        CoverageInfo described = new(image.Width, image.Height, image.Srid, image.Extent,
            [.. image.Bands.Select(b => b with { NoData = noData })], image.Overviews, image.TileWidth, image.TileHeight);
        PublishedCoverage reading = new(
            Guid.Empty, Guid.Empty, "conforming", null, "conforming", source, described, null,
            SharingScope.Private, ServiceStatus.Started, null);

        await Graticula.Raster.Tiff.TiffGridWriter.WriteAsync(
            target, width, height, bands, kind, minX, maxY, px, py, grid.Srid, AxisOrder.IsGeographic(grid.Srid), noData,
            async (x, y, w, h, token) =>
            {
                Envelope block = new(minX + (x * px), maxY - ((y + h) * py), minX + ((x + w) * px), maxY - (y * py));
                (ImageServerEndpoints.RawValues? values, string? refused) = await ImageServerEndpoints.RawValuesAsync(
                    reading, ImageServerExportParameters.ForValues(block, w, h, grid.Srid), RasterFunction.None, readers,
                    projector, how, token).ConfigureAwait(false);

                return values?.Samples ?? throw new InvalidDataException(refused ?? "a block could not be read");
            },
            cancellation).ConfigureAwait(false);

        using ICoverageReader conformed = await readers.OpenAsync(target, cancellation).ConfigureAwait(false);
        return conformed.Info;
    }
}
