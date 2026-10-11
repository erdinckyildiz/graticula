using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Finds, once the server has started, where a map of every layer with a visible range should open — so the first
/// visitor after a restart does not get the whole extent.
/// </summary>
/// <remarks>
/// <b>Added 2026-10-12, after a third walk of the showcase.</b> <see cref="InitialExtents"/> searched on the first ask
/// and answered the whole extent meanwhile, and the showcase restarts on every push: the first person after each one
/// opened Istanbul's Export link on the forest north of the city. This asks for every ranged layer shortly after
/// start, one layer at a time so a few dozen layers' counts do not land on a database together, in its own reference
/// and in the one it is served in, which are the two keys the documents and the directory's links read.
/// </remarks>
internal sealed partial class InitialExtentWarmer : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    private readonly PostgresLayerCatalog _layers;
    private readonly ServiceContexts _contexts;
    private readonly IProjector _projector;
    private readonly ILogger<InitialExtentWarmer> _logger;

    public InitialExtentWarmer(
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        IProjector projector,
        ILogger<InitialExtentWarmer> logger)
    {
        _layers = layers;
        _contexts = contexts;
        _projector = projector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);

            (var all, _) = await _layers.ListWhatCanBeReadAsync(stoppingToken).ConfigureAwait(false);
            int found = 0;

            foreach (PublishedLayer layer in all)
            {
                if (layer.VisibleRange.MinScale <= 0 || layer.Definition.GeometryColumn is not { Length: > 0 })
                {
                    continue;
                }

                try
                {
                    (_, LayerDescription described) =
                        await _contexts.GetAsync(layer, stoppingToken).ConfigureAwait(false);

                    await InitialExtents.WarmAsync(
                        layer, layer.Definition.Srid, described.Extent, _contexts, _projector).ConfigureAwait(false);

                    if (layer.ServedSrid is { } served && served != layer.Definition.Srid
                        && await ServedExtent.InAsync(
                            described.Extent, layer.Definition.Srid, served, _projector, stoppingToken)
                            .ConfigureAwait(false) is { } moved)
                    {
                        await InitialExtents.WarmAsync(layer, served, moved, _contexts, _projector).ConfigureAwait(false);
                    }

                    found++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // One unreachable source is one layer that opens on its whole extent, as before.
                    LogLayerSkipped(layer.Definition.Name, e.Message);
                }
            }

            LogWarmed(found);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            // The catalogue may be unreachable at start; the first ask still starts each search.
            LogListingFailed(e);
        }
    }

    [LoggerMessage(EventId = 1410, Level = LogLevel.Information, Message = "Found where {Count} ranged layers' maps open.")]
    private partial void LogWarmed(int count);

    [LoggerMessage(EventId = 1411, Level = LogLevel.Warning, Message = "Could not find where layer {Layer}'s map opens: {Reason}")]
    private partial void LogLayerSkipped(string layer, string reason);

    [LoggerMessage(EventId = 1412, Level = LogLevel.Warning, Message = "Could not list the layers to find where their maps open.")]
    private partial void LogListingFailed(Exception exception);
}
