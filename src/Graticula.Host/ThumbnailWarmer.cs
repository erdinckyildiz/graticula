using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Draws thumbnails before anybody asks for them — ADR-071 §5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner, 2026-09-14</b>, looking at a content list whose pictures filled in one by one after the
/// release that started keeping them: <i>"çok sürüyor. böyle olmamalı. hep cachete dursun bir tane."</i>
/// Keeping a picture is not enough when the first viewer of each one waits for it to be drawn — the list
/// draws in reading order, one at a time, and a city's buildings take four seconds each.
/// </para>
/// <para>
/// <b>So nobody is the first viewer.</b> When the server starts, every layer without a kept picture is
/// queued; a published layer is queued when it is published; a redraw or a symbology change forgets the
/// picture and queues it again. One picture at a time, so a server full of large layers is a slow
/// background trickle rather than a burst against its own sources. A request that arrives while its
/// picture is being drawn waits for that draw instead of starting another.
/// </para>
/// </remarks>
internal sealed partial class ThumbnailWarmer : BackgroundService
{
    /// <summary>How long after start the first picture is drawn, so a starting server serves first.</summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(15);

    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly PostgresLayerCatalog _layers;
    private readonly ServiceContexts _contexts;
    private readonly IMapCanvasFactory _canvases;
    private readonly ServiceThumbnails _held;
    private readonly HostSettings _settings;
    private readonly ILogger<ThumbnailWarmer> _logger;

    /// <summary>Creates the warmer.</summary>
    public ThumbnailWarmer(
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        ServiceThumbnails held,
        HostSettings settings,
        ILogger<ThumbnailWarmer> logger)
    {
        _layers = layers;
        _contexts = contexts;
        _canvases = canvases;
        _held = held;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Queues a layer's picture to be drawn if it is not kept.</summary>
    /// <param name="layer">The layer's id.</param>
    public void Enqueue(Guid layer) => _queue.Writer.TryWrite(layer);

    /// <summary>Forgets a layer's picture and queues it to be drawn again.</summary>
    /// <param name="layer">The layer's id.</param>
    public void Redraw(Guid layer)
    {
        _held.Forget(layer);
        Enqueue(layer);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);

            (var all, _) = await _layers.ListWhatCanBeReadAsync(stoppingToken).ConfigureAwait(false);

            foreach (PublishedLayer layer in all)
            {
                if (_held.Find(ServiceThumbnails.KeyFor(layer.Id, ThumbnailEndpoints.Width, ThumbnailEndpoints.Height)) is null)
                {
                    Enqueue(layer.Id);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            // The catalogue may be unreachable at start; published and redrawn layers still arrive.
            LogListingFailed(e);
        }

        await foreach (Guid id in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (await _layers.FindByIdAsync(id, stoppingToken).ConfigureAwait(false) is not { } layer
                    || !layer.IsRunning)
                {
                    continue;
                }

                await ThumbnailEndpoints
                    .DrawAndKeepAsync(layer, _contexts, _canvases, _held, _settings, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A layer whose source is down draws when somebody next asks, as it did before.
                LogDrawFailed(id, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Thumbnails were not listed at start; they are drawn as they are asked for.")]
    private partial void LogListingFailed(Exception failure);

    [LoggerMessage(Level = LogLevel.Information, Message = "The thumbnail of layer {Layer} was not drawn ahead of time.")]
    private partial void LogDrawFailed(Guid layer, Exception failure);
}
