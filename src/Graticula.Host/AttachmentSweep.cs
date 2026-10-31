using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Removes, every hour, the attachments a registered layer keeps in the datastore whose features are gone — ADR-207.
/// </summary>
/// <remarks>
/// <b>Why it exists.</b> In the datastore an attachment has no foreign key to its feature, which is in another
/// database, so a feature deleted by another tool — ArcGIS Pro, a script, a DBA — leaves its attachments behind. This
/// server's own deletes remove them at once (<see cref="AttachmentRemovingWriter"/>); this finds the rest, by asking
/// each such layer which of the features holding attachments are still there. HistoryRetention's shape: once at start,
/// then hourly, every failure logged and the next hour tried.
/// </remarks>
internal sealed partial class AttachmentSweep(
    PostgresLayerCatalog layers, LayerConnections connections, ILogger<AttachmentSweep> logger, SiteSingletons? site = null) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // ADR-012 phase 0 item 1: once per site.
                IReadOnlyList<PublishedLayer> all = site is null || await site.LeadsAsync(SiteSingletons.AttachmentSweep, stoppingToken).ConfigureAwait(false)
                    ? await layers.ListAsync(stoppingToken).ConfigureAwait(false)
                    : [];

                foreach (PublishedLayer layer in all)
                {
                    if (layer.Definition.IsHosted || layer.AttachmentsIn != AttachmentPlaces.Datastore)
                    {
                        continue;
                    }

                    long removed = await connections.RemoveOrphanedAttachmentsAsync(layer, null, stoppingToken).ConfigureAwait(false);

                    if (removed > 0)
                    {
                        Log.Swept(logger, removed, layer.Definition.Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031
            catch (Exception failed)
#pragma warning restore CA1031
            {
                Log.SweepFailed(logger, failed);
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
        [LoggerMessage(
            EventId = 1413,
            Level = LogLevel.Information,
            Message = "Removed {Attachments} attachments of layer {Layer} whose features were deleted by another tool.")]
        public static partial void Swept(ILogger logger, long attachments, string layer);

        [LoggerMessage(
            EventId = 1414,
            Level = LogLevel.Warning,
            Message = "The attachment sweep failed; the next one is in an hour.")]
        public static partial void SweepFailed(ILogger logger, Exception failed);
    }
}
