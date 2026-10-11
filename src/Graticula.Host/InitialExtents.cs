using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// Where a map of a layer should open when its whole extent is outside its visible range — the
/// <c>initialExtent</c> a service document states, and what a directory's preview links ask for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added 2026-10-11, after two walks of the showcase.</b> The first found that a link to Istanbul's buildings drew
/// a blank picture, because the layer is drawn from 1:72,224 in and the link asked for the whole province. The repair
/// asked for the middle of the province instead, and the second walk found the middle is a forest. What a visitor
/// should land on is where the buildings are, and finding that is a few counts
/// (<see cref="VisibleRangeSuggestion.DensestPlaceAsync"/>).
/// </para>
/// <para>
/// <b>Never waited for.</b> The first ask starts the search and is answered with nothing — the document states the
/// whole extent, as it always did — and a later ask gets the place once it is found. A service document is the first
/// thing every ArcGIS client reads, and making it wait on counts over a remote database would trade a better first
/// view for a slower first byte everywhere. Kept for the life of the process, keyed by the layer and its range, so
/// changing the range finds the place again; a search that fails is kept as nothing, and the whole extent stands.
/// </para>
/// </remarks>
internal static class InitialExtents
{
    // Keyed by layer and reference, with the range inside the value, so a layer whose range changes replaces its entry
    // rather than adding one: at most one per layer per reference it is served in.
    private static readonly ConcurrentDictionary<(Guid Layer, int Srid), (double MinScale, Lazy<Task<Envelope?>> Search)> Found = new();

    /// <summary>The extent to open on, if it is known yet and the layer needs one.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="servedSrid">The reference the document is written in.</param>
    /// <param name="servedExtent">The layer's whole extent in that reference.</param>
    /// <param name="contexts">For the layer's reader.</param>
    /// <param name="projector">For putting the place into the served reference.</param>
    /// <returns>An 800 × 600 picture's extent inside the range, centred on the densest place; or null.</returns>
    public static Envelope? Known(
        PublishedLayer layer,
        int servedSrid,
        Envelope? servedExtent,
        ServiceContexts contexts,
        IProjector projector) =>
        Known(layer, servedSrid, servedExtent, contexts, projector, out _);

    /// <summary>Starts the search for a layer in a reference, if it needs one, and returns it to wait on.</summary>
    /// <remarks>
    /// <b>For <see cref="InitialExtentWarmer"/> — 2026-10-12.</b> A third walk of the showcase found that the first
    /// visitor after each deploy still got the whole extent, because the search began with the first ask and every
    /// push restarts the showcase. The warmer asks for every ranged layer at start, one at a time.
    /// </remarks>
    public static Task WarmAsync(
        PublishedLayer layer,
        int servedSrid,
        Envelope? servedExtent,
        ServiceContexts contexts,
        IProjector projector)
    {
        _ = Known(layer, servedSrid, servedExtent, contexts, projector, out Task<Envelope?>? searched);
        return searched ?? Task.CompletedTask;
    }

    private static Envelope? Known(
        PublishedLayer layer,
        int servedSrid,
        Envelope? servedExtent,
        ServiceContexts contexts,
        IProjector projector,
        out Task<Envelope?>? searched)
    {
        searched = null;

        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(projector);

        double minScale = layer.VisibleRange.MinScale;

        // Only where the whole extent is out of range: a layer that draws at its whole extent opens on it.
        if (minScale <= 0
            || servedExtent is not { IsEmpty: false } whole
            || WmsEndpoints.InsideRange(whole, servedSrid, minScale, 800, 600).Equals(whole))
        {
            return null;
        }

        (double MinScale, Lazy<Task<Envelope?>> Search) entry = Found.AddOrUpdate(
            (layer.Id, servedSrid),
            _ => Start(),
            (_, kept) => kept.MinScale == minScale ? kept : Start());

        Task<Envelope?> task = entry.Search.Value;
        searched = task;

        return task.IsCompletedSuccessfully ? task.Result : null;

        (double, Lazy<Task<Envelope?>>) Start() => (
            minScale,
            new Lazy<Task<Envelope?>>(
                () => Task.Run(() => SearchAsync(layer, servedSrid, contexts, projector)),
                LazyThreadSafetyMode.ExecutionAndPublication));
    }

    private static async Task<Envelope?> SearchAsync(
        PublishedLayer layer, int servedSrid, ServiceContexts contexts, IProjector projector)
    {
        try
        {
            (IFeatureSource source, LayerDescription described) =
                await contexts.GetAsync(layer, CancellationToken.None).ConfigureAwait(false);

            if (await VisibleRangeSuggestion.DensestPlaceAsync(
                    source, described, layer.Definition.Srid, projector, layer.VisibleRange.MinScale,
                    CancellationToken.None).ConfigureAwait(false) is not { } tile)
            {
                return null;
            }

            // <b>One level inside the limit, not at it.</b> The densest place at the limit is the most expensive
            // picture the layer has — 7 to 11 seconds over Istanbul, which a third walk took for a hung Export link.
            // A level in is a quarter of the area and still a street of buildings.
            Envelope box = WmsEndpoints.Around(
                (tile.MinX + tile.MaxX) / 2, (tile.MinY + tile.MaxY) / 2, 3857, layer.VisibleRange.MinScale / 2, 800, 600);

            return servedSrid == 3857
                ? box
                : await ServedExtent.InAsync(box, 3857, servedSrid, projector, CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }
}
