using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Providers.PostGis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// Deletes, every hour, the versions a hosted layer's history no longer keeps — ADR-078 condition 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner decision 2026-10-07: a history is kept for so many days, set per layer, or all of it.</b> The period lives
/// on the history table (<see cref="PostGisFeatureHistory.SetKeepAsync"/>); this sweep reads every one in the
/// datastore and deletes what ended before it. Nothing else in this server deletes a version.
/// </para>
/// <para>
/// <b>LogRetention's shape</b>: once at start, then hourly, every failure logged and the next hour tried — a sweep
/// that died on one bad hour would stop keeping the period for the life of the process. Every node runs it; a delete
/// another node already made deletes nothing.
/// </para>
/// </remarks>
internal sealed partial class HistoryRetention : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    private readonly NpgsqlDataSource _datastore;
    private readonly ILogger<HistoryRetention> _logger;

    /// <summary>Creates the sweep.</summary>
    /// <param name="datastore">The datastore, where every hosted layer and its history live.</param>
    /// <param name="logger">Where what it deleted is said.</param>
    public HistoryRetention(NpgsqlDataSource datastore, ILogger<HistoryRetention> logger)
    {
        ArgumentNullException.ThrowIfNull(datastore);
        ArgumentNullException.ThrowIfNull(logger);

        _datastore = datastore;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<(string Table, long Versions)> pruned =
                    await PostGisFeatureHistory.PruneAsync(_datastore, stoppingToken).ConfigureAwait(false);

                foreach ((string table, long versions) in pruned)
                {
                    Log.Pruned(_logger, versions, table);
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
                Log.PruneFailed(_logger, failed);
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
            EventId = 1408,
            Level = LogLevel.Information,
            Message = "Deleted {Versions} versions from {Table}, older than its history keeps.")]
        public static partial void Pruned(ILogger logger, long versions, string table);

        [LoggerMessage(
            EventId = 1409,
            Level = LogLevel.Warning,
            Message = "The history sweep failed; the next one is in an hour.")]
        public static partial void PruneFailed(ILogger logger, Exception failed);
    }
}
