using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Removes, every hour, the map area package files no row names any more — D-407, as D-389 does for scene packages.
/// Once per site (ADR-012), since the files are the site's.
/// </summary>
internal sealed partial class MapAreaPackageFileSweep(
    MapAreaPackageFiles files, IMapAreaStore areas, ILogger<MapAreaPackageFileSweep> logger, SiteSingletons? site = null) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (site is null || await site.LeadsAsync(SiteSingletons.MapAreaPackageFiles, stoppingToken).ConfigureAwait(false))
                {
                    IReadOnlySet<string> named = await areas.FilesAsync(stoppingToken).ConfigureAwait(false);
                    int orphans = files.SweepOrphans(named, Grace);
                    if (orphans > 0)
                    {
                        Log.Swept(logger, orphans);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // A failed pass is logged and the next hour tried.
            catch (Exception failed)
#pragma warning restore CA1031
            {
                Log.Failed(logger, failed);
            }

            try
            {
                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1473, Level = LogLevel.Information, Message = "Removed {Orphans} map area package files nothing names.")]
        public static partial void Swept(ILogger logger, int orphans);

        [LoggerMessage(EventId = 1474, Level = LogLevel.Warning, Message = "The map area package file sweep failed; the next hour will try again.")]
        public static partial void Failed(ILogger logger, Exception failed);
    }
}
