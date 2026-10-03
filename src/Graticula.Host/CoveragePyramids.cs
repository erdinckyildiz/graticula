using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Platform.Catalog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Gives overviews to the images that have none — ADR-148, by owner decision: those uploaded before ADR-139 built them
/// on upload, and those registered in place, whose overviews are written beside the registered file as ArcGIS's Build
/// Pyramids writes them. Once at startup, and again whenever an image is registered.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the background, one image at a time</b>, so a large registered file does not hold its registration request
/// open, and two builds do not compete for the disk. The image is served without overviews until its own are written.
/// </para>
/// <para>
/// <b>A folder this server may not write to is said and passed over</b> — a registered file on a read-only share keeps
/// working as it did — and so is a file that cannot be read now; the next startup tries again.
/// </para>
/// </remarks>
internal sealed partial class CoveragePyramids : BackgroundService
{
    private readonly ICoverageCatalog _coverages;
    private readonly ICoverageReaderFactory _readers;
    private readonly ICoveragePyramidBuilder _builder;
    private readonly ILogger<CoveragePyramids> _log;
    private readonly Channel<bool> _asked = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Builds overviews for the images that lack them.</summary>
    public CoveragePyramids(
        ICoverageCatalog coverages, ICoverageReaderFactory readers, ICoveragePyramidBuilder builder, ILogger<CoveragePyramids> log)
    {
        _coverages = coverages;
        _readers = readers;
        _builder = builder;
        _log = log;
    }

    /// <summary>Asks for a pass soon — after a registration. Asks made while one is waiting are the same ask.</summary>
    public void Ask() => _asked.Writer.TryWrite(true);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A moment after startup, so the first requests are not competing with a build.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                PassFailed(_log, e.Message);
            }

            try
            {
                await _asked.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass over the catalogue: every image without overviews is given them.</summary>
    internal async Task<int> PassAsync(CancellationToken cancellation)
    {
        int built = 0;

        foreach (PublishedCoverage coverage in await _coverages.ListAsync(cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();

            // A mosaic's overviews are its images' own (ADR-140), so each image that lacks them is given them.
            IReadOnlyList<string> files = Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path)
                ? File.Exists(coverage.Path) ? Graticula.Raster.Tiff.VrtMosaicReader.FilesOf(coverage.Path) : []
                : [coverage.Path];
            int before = built;

            foreach (string file in files.Where(File.Exists))
            {
                try
                {
                    if (await _builder.BuildAsync(file, cancellation).ConfigureAwait(false) > 0)
                    {
                        built++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    NotBuilt(_log, coverage.QualifiedName, e.Message);
                }
            }

            if (built == before)
            {
                continue;
            }

            // The catalogue says how many levels it has; it is told.
            using ICoverageReader reader = await _readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);
            await _coverages.ReplaceImageAsync(coverage.Folder, coverage.ServiceName, coverage.Path, reader.Info, cancellation)
                .ConfigureAwait(false);
            Built(_log, coverage.QualifiedName, reader.Info.Overviews.Count);
        }

        return built;
    }

    [LoggerMessage(EventId = 1091, Level = LogLevel.Information,
        Message = "Image service {Service} was given {Levels} overview levels, beside its file (ADR-148).")]
    private static partial void Built(ILogger logger, string service, int levels);

    [LoggerMessage(EventId = 1092, Level = LogLevel.Warning,
        Message = "The overviews of image service {Service} could not be written beside its file, so it is served without "
            + "them and a zoomed-out picture reads every pixel: {Why}. The next startup tries again (ADR-148).")]
    private static partial void NotBuilt(ILogger logger, string service, string why);

    [LoggerMessage(EventId = 1093, Level = LogLevel.Warning,
        Message = "A pass giving images their overviews stopped part way: {Why}. The next registration or startup tries again.")]
    private static partial void PassFailed(ILogger logger, string why);
}
