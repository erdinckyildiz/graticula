using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// One layer that cannot be described leaves a listing; it does not take the listing down.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found on the showcase 2026-09-23, by the third ArcGIS review (V-43).</b> One layer's source failed —
/// two MotherDuck services answered 500 to every request — and so did every document that <i>lists</i>
/// layers: OGC API Features <c>/collections</c>, WMS <c>GetCapabilities</c> and WFS
/// <c>GetCapabilities</c>. Those three are the first thing QGIS asks for, so from QGIS the whole server was
/// invisible because of one dead source. The REST services directory stayed up, because it lists without
/// describing; the three OGC faces describe every layer to write their extents and fields.
/// </para>
/// <para>
/// <b>Left out and said, rather than listed with a guess.</b> A collection with no extent or a feature type
/// with no fields would be a claim the server cannot back, and a client that picked it would fail anyway.
/// The layer's own address still answers with its error, which is where a failure belongs; the warning
/// lands in the server's own log, which the console shows under Logs › Server warnings.
/// </para>
/// <para>
/// <b>Everything but cancellation is caught, and that is the point rather than a shortcut.</b> The sources
/// behind a layer are PostgreSQL, DuckDB, a remote file or MotherDuck, each with its own exception types,
/// and a listing that enumerates them is a listing that goes down on the one nobody enumerated.
/// </para>
/// </remarks>
internal static class ListingGuard
{
    /// <summary>Describes one layer for a listing, or logs why it could not and answers null.</summary>
    /// <typeparam name="T">What the face lists a layer as.</typeparam>
    /// <param name="context">The request, for its logger.</param>
    /// <param name="face">Which document is being written — <c>WFS GetCapabilities</c> and the like.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="describe">The face's own description of it.</param>
    /// <param name="cancellation">Cancellation, which is never swallowed.</param>
    /// <returns>The description, or null when the layer is left out.</returns>
    public static async Task<T?> DescribeOrLeaveOutAsync<T>(
        HttpContext context,
        string face,
        PublishedLayer layer,
        Func<Task<T>> describe,
        CancellationToken cancellation)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(describe);

        try
        {
            return await describe().ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException && cancellation.IsCancellationRequested))
        {
            ILogger logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("listing")
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

            string qualified = layer.Folder is { Length: > 0 } folder
                ? $"{folder}/{layer.ServiceName}/{layer.Definition.Name}"
                : $"{layer.ServiceName}/{layer.Definition.Name}";

            Log.LayerLeftOutOfListing(logger, qualified, face, e.GetType().Name, e.Message);
            return null;
        }
    }
}
