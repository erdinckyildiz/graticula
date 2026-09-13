using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;

namespace Graticula.Features;

/// <summary>
/// The answers to a query that are about its result rather than its rows: the extent, the ids and
/// the statistics.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port because a second datastore arrived — ADR-066 §5.</b> These three were methods on
/// <c>PostGisFeatureSource</c> and nowhere else, so <c>returnExtentOnly</c>,
/// <c>returnIdsOnly</c>, <c>outStatistics</c>, a generated renderer and a WMS time dimension each
/// cast to the concrete provider and answered <em>implemented for PostGIS only</em> when the cast
/// failed. That was an honest sentence for a build with one provider, and it stops being a
/// sentence and becomes a hole the day a layer is served from a GeoParquet file.
/// </para>
/// <para>
/// <b>Separate from <see cref="IFeatureSource"/> rather than added to it</b>, for the reason
/// <see cref="IFeatureVersions"/> and <see cref="IGeometryStatistics"/> are separate: a source that
/// cannot answer one of these is a real thing, and a caller asking <c>is IFeatureSummaries</c>
/// can say so in a sentence, where an interface method that throws would say it as a stack
/// trace.
/// </para>
/// </remarks>
public interface IFeatureSummaries
{
    /// <summary>The extent of everything this query matches, and how many.</summary>
    /// <param name="query">The query, whose filters apply.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The extent, or null when nothing matched, and the count.</returns>
    Task<(Envelope? Extent, long Count)> ExtentAsync(
        FeatureQuery query, CancellationToken cancellationToken);

    /// <summary>The object ids of everything this query matches, in ascending order.</summary>
    /// <param name="query">The query, whose filters apply and whose limit does not.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The ids.</returns>
    Task<IReadOnlyList<long>> ObjectIdsAsync(
        FeatureQuery query, CancellationToken cancellationToken);

    /// <summary>The statistics the query asks for, grouped when it asks for groups.</summary>
    /// <param name="query">The query, carrying its statistics, groups and filters.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One row per group, or one row when there are no groups.</returns>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StatisticsAsync(
        FeatureQuery query, CancellationToken cancellationToken);
}
