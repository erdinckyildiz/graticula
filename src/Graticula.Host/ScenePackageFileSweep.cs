using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Removes, every hour, the scene layer package files nothing names any more — D-389, ADR-274 §5.4. Once per site
/// (ADR-012), since the files are the site's; partial uploads older than a day go too.
/// </summary>
internal sealed partial class ScenePackageFileSweep(
    ScenePackageFiles files, IDocumentStore documents, ILogger<ScenePackageFileSweep> logger, SiteSingletons? site = null) : BackgroundService
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
                if (site is null || await site.LeadsAsync(SiteSingletons.ScenePackageFiles, stoppingToken).ConfigureAwait(false))
                {
                    IReadOnlySet<string> named = await documents.ContentFilesAsync(stoppingToken).ConfigureAwait(false);
                    int orphans = files.SweepOrphans(named, Grace);
                    int partials = files.SweepPartials(Grace);
                    if (orphans + partials > 0)
                    {
                        Log.Swept(logger, orphans, partials);
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
        [LoggerMessage(EventId = 1453, Level = LogLevel.Information, Message = "Removed {Orphans} scene package files nothing names and {Partials} unfinished uploads.")]
        public static partial void Swept(ILogger logger, int orphans, int partials);

        [LoggerMessage(EventId = 1454, Level = LogLevel.Warning, Message = "The scene package file sweep failed; the next hour will try again.")]
        public static partial void Failed(ILogger logger, Exception failed);
    }
}
