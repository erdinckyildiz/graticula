using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Features;

/// <summary>
/// A source that can say, before running a query, whether its data has changed since a caller
/// last saw it.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-069, and the shape follows <see cref="IFeatureVersions"/>'s exactly</b> — a separate
/// interface rather than a member of <c>IFeatureSource</c>, because whether a source can be
/// cheaply versioned is a property of the source. A read-only file has one; a table anybody with
/// database credentials can write to does not, and asking it to answer would mean either a query
/// (defeating the point) or a wrong answer.
/// </para>
/// <para>
/// <b>The difference from <see cref="IFeatureVersions"/> is what the version is of.</b> That
/// interface versions one row, for optimistic concurrency, and costs a round trip on purpose —
/// D-186 requires it to be database-maintained truth. This versions the whole source, for HTTP
/// caching, and exists only where the answer is available without touching the datastore at
/// all: <c>GeoParquetFeatureSource</c> reads it off the file's length and modification time,
/// which <c>GeoParquetFolder</c> already tracks to decide whether to reopen the file. Nothing is
/// queried to answer it.
/// </para>
/// <para>
/// ~~<b>Registered PostGIS data has no cheap answer, so it has no implementation.</b> A table's
/// version would have to be a full scan, a trigger-maintained column nothing here can assume
/// exists, or the same <c>xmin</c> read <see cref="IFeatureVersions"/> already charges a round
/// trip for — none of which is cheaper than just answering the query it would be validating in
/// place of. <c>QueryResponseCaching</c> does not fall back to hashing a query response's body
/// either: that response is streamed (ADR-062), and buffering a multi-megabyte body to hash it
/// is the allocation A-037 already measured as the binding constraint on this face. A layer
/// with no cheap validator is still cached — <c>Cache-Control: max-age</c> alone — and simply
/// cannot be revalidated for less than its full cost once that expires.~~ <b>Corrected 2026-11-01 (D-266): it has
/// one, from PostgreSQL's cumulative statistics</b> — a table's write counters, its file node and the two reset times,
/// one catalogue read with no scan of the table. It is a round trip, so the PostGIS source says so with
/// <see cref="IDatabaseCacheValidator"/> and the connection budget counts it. What it costs in freshness is measured
/// where it is implemented (<c>PostGisFeatureSource.CacheValidatorAsync</c>).
/// </para>
/// <para>
/// <b>An opaque string, deliberately</b> — the caller (<c>QueryResponseCaching</c>) compares it
/// byte for byte inside a strong <c>ETag</c> and never parses it.
/// </para>
/// </remarks>
public interface ISourceCacheValidator
{
    /// <summary>
    /// The source's current version, or null when it has none to offer right now.
    /// </summary>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>An opaque version that changes whenever the underlying data does.</returns>
    Task<string?> CacheValidatorAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A <see cref="ISourceCacheValidator"/> whose answer is a round trip to the database, so a caller that bounds database
/// work (the connection budget) counts it — D-266. A file's validator, read from a stat, is not one.
/// </summary>
/// <remarks>
/// <b>A marker rather than a member with a default body</b>: a decorator that forwards the interface does not forward a
/// default member's override, and a bound silently skipped is the shape D-196 records.
/// </remarks>
public interface IDatabaseCacheValidator : ISourceCacheValidator
{
}
