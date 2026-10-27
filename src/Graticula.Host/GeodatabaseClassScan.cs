using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Asks, once the server has started, whether any registered layer is a class an enterprise geodatabase versions or
/// archives — ADR-217 condition 3 — and names each one in the log.
/// </summary>
/// <remarks>
/// <para>
/// <b>A publish has refused such a class since ADR-217; a layer published before it was not refused</b>, and keeps
/// answering deleted and superseded rows as current, or missing a version's edits, until somebody looks. This looks, by
/// asking each registered PostgreSQL, SQL Server and Oracle layer the publish's own question
/// (<see cref="AdminEndpoints.GeodatabaseRefusalOfAsync"/>), and says what it found with the publish's own sentence.
/// </para>
/// <para>
/// <b>Said, not acted on.</b> Unpublishing somebody's layer because its table grew a column would be a decision this
/// server has no business taking alone; the log line names the layer and what to do. One layer at a time, after the
/// start has settled, and a source that does not answer is passed over — the same answer as a publish gives it.
/// </para>
/// </remarks>
internal sealed partial class GeodatabaseClassScan(PostgresLayerCatalog layers, ILogger<GeodatabaseClassScan> logger)
    : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);

            (var all, _) = await layers.ListWhatCanBeReadAsync(stoppingToken).ConfigureAwait(false);
            int found = 0;

            foreach (PublishedLayer layer in all)
            {
                if (layer.Definition.IsHosted
                    || GeoParquetLocator.KindOf(layer.ConnectionString)
                        is not (DataSourceKinds.PostGis or DataSourceKinds.SqlServer or DataSourceKinds.Oracle))
                {
                    continue;
                }

                string? why;

                try
                {
                    why = await AdminEndpoints.GeodatabaseRefusalOfAsync(
                        layer.ConnectionString, layer.Definition.SchemaName, layer.Definition.TableName, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    continue;
                }

                if (why is not null)
                {
                    found++;
                    LogServedWrongly(logger, layer.ServiceName, layer.Definition.Name, why);
                }
            }

            if (found > 0)
            {
                LogFound(logger, found);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            LogScanFailed(logger, e);
        }
    }

    [LoggerMessage(EventId = 2359, Level = LogLevel.Warning,
        Message = "Layer '{Layer}' of '{Service}' was published before ADR-217 from a class this server now refuses, and is "
            + "served wrongly: {Why} Unpublish it, or republish the view the sentence names.")]
    private static partial void LogServedWrongly(ILogger logger, string service, string layer, string why);

    [LoggerMessage(EventId = 2360, Level = LogLevel.Warning,
        Message = "{Count} registered layer(s) are versioned or archived geodatabase classes; each is named above (ADR-217).")]
    private static partial void LogFound(ILogger logger, int count);

    [LoggerMessage(EventId = 2361, Level = LogLevel.Warning,
        Message = "The scan for registered layers that are versioned or archived geodatabase classes did not finish.")]
    private static partial void LogScanFailed(ILogger logger, Exception exception);
}
