using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Graticula.Geometries;

namespace Graticula.Features;

/// <summary>What to read from a layer.</summary>
/// <remarks>
/// <para>
/// Deliberately small. This is not the query AST — <c>ADR-008</c> owns filtering,
/// capability negotiation and the refusal model, and none of that exists yet.
/// This is the shape the first read path needs, and §82 says not to build the
/// rest until something asks for it.
/// </para>
/// <para>
/// <b>The limit is not optional.</b> An unbounded read is a denial of service
/// against a table with 6.5 million rows, and A-037 makes it one against the
/// server rather than merely a slow response.
/// </para>
/// </remarks>
public sealed class FeatureQuery
{
    /// <summary>
    /// The most features one query may return, whatever it asks for.
    /// </summary>
    /// <remarks>
    /// A backstop rather than a policy. §49's response-size limits and Q-56's
    /// three-tier oversized-feature rule are the real answer; this stops a
    /// missing configuration from being unbounded in the meantime.
    /// </remarks>
    public const int MaximumLimit = 50_000;

    /// <summary>Creates a query.</summary>
    /// <param name="limit">Maximum features to return.</param>
    /// <param name="boundingBox">
    /// Restricts results to features whose envelope intersects this. Pushed down
    /// to the provider — ADR-003 §6a tier 1, <em>the cheapest geometry operation
    /// is the one that never crosses the wire</em>.
    /// </param>
    /// <param name="fields">
    /// Attributes to return. <see langword="null"/> means none: identity and
    /// geometry only. Reading columns nobody asked for is the cheapest waste
    /// there is, so the default is nothing rather than everything.
    /// </param>
    /// <param name="offset">
    /// How many matching features to skip. Paging, and it is <b>only sound
    /// against a stable order</b> — see <see cref="Offset"/>.
    /// </param>
    /// <param name="includeGeometry">
    /// Whether to read the shape. False when a client asked for attributes only,
    /// which for a layer of large polygons is the difference between a table and
    /// a download.
    /// </param>
    /// <param name="orderBy">
    /// How to order results, outermost key first. Field names must already have
    /// been checked against the layer's columns — see <see cref="OrderBy"/>.
    /// </param>
    /// <param name="spatial">
    /// A spatial restriction richer than a box: any geometry, any of the nine
    /// relations, optionally buffered. Null for none.
    /// </param>
    /// <param name="identities">Only these features, by integer identity. Null for all.</param>
    /// <param name="distinct">Whether to collapse duplicate attribute rows.</param>
    /// <param name="statistics">Aggregates to compute instead of returning features.</param>
    /// <param name="groupBy">Fields to group those aggregates by.</param>
    /// <param name="having">
    /// <b>Removed 2026-08-16. The parameter is kept only to fail loudly.</b> It
    /// used to carry ArcGIS's <c>havingClause</c> as SQL text, and
    /// <c>PostGisFeatureSource</c> appended it to the statement unparsed — an
    /// injection reachable by any caller who could reach a public layer's query.
    /// Passing anything but null now throws. See D-41 and ADR-008 §4a-ii; the
    /// capability returns when the clause is parsed, the way <c>where</c> is.
    /// </param>
    /// <param name="precision">Decimal places for output coordinates, or null for all.</param>
    /// <param name="maxAllowableOffset">Generalisation tolerance, or null for none.</param>
    /// <param name="outSrid">Reproject output geometry to this, or null to leave it.</param>
    /// <param name="where">
    /// An attribute predicate, already parsed and parameterised. Null for none.
    /// </param>
    /// <param name="filterSrid">
    /// The reference the filter geometry is in, or null when it is already the
    /// layer's. See <see cref="FilterSrid"/>.
    /// </param>
    public FeatureQuery(
        int limit,
        Envelope? boundingBox = null,
        IReadOnlyList<string>? fields = null,
        int offset = 0,
        bool includeGeometry = true,
        IReadOnlyList<SortKey>? orderBy = null,
        SpatialFilter? spatial = null,
        IReadOnlyList<long>? identities = null,
        bool distinct = false,
        IReadOnlyList<StatisticRequest>? statistics = null,
        IReadOnlyList<string>? groupBy = null,
        string? having = null,
        int? precision = null,
        double? maxAllowableOffset = null,
        int? outSrid = null,
        ParsedWhere? where = null,
        int? filterSrid = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        Limit = limit;
        BoundingBox = boundingBox;
        Offset = offset;
        IncludeGeometry = includeGeometry;
        OrderBy = orderBy is null ? Array.Empty<SortKey>() : [.. orderBy];
        Spatial = spatial?.Validated();
        Identities = identities is null ? Array.Empty<long>() : [.. identities];
        Distinct = distinct;
        Statistics = statistics is null ? Array.Empty<StatisticRequest>() : [.. statistics];
        GroupBy = groupBy is null ? Array.Empty<string>() : [.. groupBy];
        Precision = precision;
        MaxAllowableOffset = maxAllowableOffset;
        OutSrid = outSrid;
        Where = where;
        FilterSrid = filterSrid;

        // <b>Refusing here as well as at the HTTP boundary is deliberate.</b> The
        // boundary check is what a caller sees; this one is what stops the next
        // internal caller from reopening the hole by constructing the query
        // directly. The parameter survives rather than being deleted so that any
        // code still passing a clause fails at the throw instead of compiling
        // against a different overload and silently dropping it.
        if (having is not null)
        {
            throw new ArgumentException(
                "A having clause cannot be carried as SQL text. It was, until 2026-08-16, and "
                + "PostGisFeatureSource appended it to the statement unparsed — so any caller who "
                + "could query a public layer could write SQL. The clause returns when it is "
                + "parsed into bound parameters the way 'where' already is (D-41).",
                nameof(having));
        }

        if (fields is null || fields.Count == 0)
        {
            Fields = Array.Empty<string>();
            return;
        }

        string[] copy = new string[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(fields[i]))
            {
                throw new ArgumentException($"Field {i} has no name.", nameof(fields));
            }

            copy[i] = fields[i];
        }

        Fields = new ReadOnlyCollection<string>(copy);
    }

    /// <summary>Maximum features to return.</summary>
    public int Limit { get; }

    /// <summary>
    /// A bounding-box restriction, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// <b>Kept beside <see cref="Spatial"/> rather than folded into it, and the
    /// reason is that this is the path that is fast.</b> An envelope-intersects
    /// filter is the overwhelming majority of real traffic and compiles to the
    /// index operator alone; expressing it as a general geometry filter would
    /// route it through the same predicate as a DE-9IM relate and lose that.
    /// <b>At most one of the two is ever set</b> — the compatibility layer
    /// chooses — and a provider that sees both must apply both.
    /// </remarks>
    public Envelope? BoundingBox { get; }

    /// <summary>A richer spatial restriction, or null for none.</summary>
    public SpatialFilter? Spatial { get; }

    /// <summary>
    /// Only these object ids, or empty for no such restriction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Long, not string.</b> The integer identity is a unique integer by definition, and a
    /// layer without one is refused before a query is built. Taking text here would invite a
    /// caller to pass the declared identity column instead, which may be a uuid.
    /// </para>
    /// <para>
    /// <b>Called <c>ObjectIds</c> until 2026-08-26 —
    /// [D-124](../../../docs/architecture-debt.md).</b> *Object ID* is Esri's word and this is
    /// the query model every face reads. The ArcGIS surface still calls its parameter
    /// <c>objectIds</c>, because that is its protocol's word and the adapter's job; the domain
    /// no longer borrows it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<long> Identities { get; }

    /// <summary>Whether duplicate attribute rows collapse to one.</summary>
    public bool Distinct { get; }

    /// <summary>Aggregates to compute instead of returning features.</summary>
    public IReadOnlyList<StatisticRequest> Statistics { get; }

    /// <summary>Fields to group the aggregates by.</summary>
    public IReadOnlyList<string> GroupBy { get; }

    /// <summary>Decimal places for output coordinates, or null for full precision.</summary>
    public int? Precision { get; }

    /// <summary>Generalisation tolerance in output units, or null for none.</summary>
    public double? MaxAllowableOffset { get; }

    /// <summary>Reproject output geometry into this reference, or null.</summary>
    public int? OutSrid { get; }

    /// <summary>
    /// A written reference to answer in, when the caller named one instead of a code.
    /// </summary>
    /// <remarks>
    /// <b>Owner decision 2026-09-06 — <i>"epsg güzel ama wkt de kabul etmemiz lazım"</i>.</b> A
    /// service may be served in a reference EPSG has no number for, and then the definition is
    /// the only way to name it. It sits beside <see cref="OutSrid"/> rather than replacing it
    /// because eight faces compare codes as integers; exactly one of the two is ever set, which
    /// <c>ServedReference</c> is what enforces at every place either is chosen.
    /// </remarks>
    public string? OutWkt { get; init; }

    /// <summary>
    /// The reference <see cref="BoundingBox"/> and <see cref="Spatial"/> are
    /// expressed in, or null when they are already in the layer's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One reference for both, because a request carries one <c>inSR</c>.</b>
    /// The HTTP boundary sets at most one of the two filters from the same
    /// parameter, so giving each its own reference would be two places to disagree
    /// about a single fact.
    /// </para>
    /// <para>
    /// <b>This replaces a refusal, and the refusal was not wrong for the reason it
    /// gave.</b> Until 2026-08-16 an <c>inSR</c> that differed from the layer was
    /// answered with 400, on the stated grounds that comparing two references
    /// silently yields zero features — which is true, and is the defect behind
    /// Q-96. But refusing means an ArcGIS client whose view is Web Mercator cannot
    /// draw a layer stored in EPSG:4326 at all, and it is not free to reproject the
    /// table it was pointed at: a registered layer belongs to somebody else. The
    /// silent-empty failure is avoided by transforming the filter, not by declining
    /// the request. Output reprojection was already supported, so this is the
    /// symmetric half.
    /// </para>
    /// <para>
    /// A reference is not a SQL concept, so this stays inside ADR-008 §4.1's rule —
    /// unlike <see cref="Where"/>, which is the one exception (§4a-i).
    /// </para>
    /// </remarks>
    public int? FilterSrid { get; }

    /// <summary>
    /// An attribute predicate, parsed and parameterised, or null for none.
    /// </summary>
    /// <remarks>
    /// <b>Parsed before it gets here, never raw.</b> <see cref="WhereClause"/>
    /// rebuilds the caller's SQL from an expression tree with every literal
    /// bound; what this carries is our text, not theirs. A provider may
    /// interpolate <see cref="ParsedWhere.Sql"/> directly for that reason and
    /// for no other.
    /// </remarks>
    public ParsedWhere? Where { get; }

    /// <summary>Attributes to return. Empty means identity and geometry only.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>
    /// How many matching features to skip.
    /// </summary>
    /// <remarks>
    /// <b>Paging by offset is only sound against a stable order, and this query
    /// does not specify one.</b> Rows come back in whatever order the provider
    /// finds them, so a feature inserted between page one and page two can push
    /// another feature from page two onto page three, where the client never
    /// sees it. That is a real defect and it is accepted here because the
    /// alternative — refusing to page at all — makes every ArcGIS client fail on
    /// its second request. Ordering belongs with ADR-008's query AST.
    /// </remarks>
    public int Offset { get; }

    /// <summary>Whether to read the geometry.</summary>
    public bool IncludeGeometry { get; }

    /// <summary>
    /// How to order results, outermost key first.
    /// </summary>
    /// <remarks>
    /// <b>Ordering is what makes <see cref="Offset"/> mean anything.</b> An
    /// offset against an unordered result can repeat or skip rows between
    /// pages; against a unique key it cannot. So a client that orders by its
    /// object id and pages gets correct pages — which is exactly what the
    /// ArcGIS SDK does, and why it sends <c>orderByFields</c> on every request.
    /// </remarks>
    public IReadOnlyList<SortKey> OrderBy { get; }
}

/// <summary>One ordering key.</summary>
/// <param name="Field">The column, already checked against the layer's columns.</param>
/// <param name="Descending">Whether to reverse it.</param>
public readonly record struct SortKey(string Field, bool Descending);

/// <summary>Reads features from a layer.</summary>
/// <remarks>
/// <para>
/// The port through which Tier 1 reaches spatial data. Implementations are
/// adapters and are the only place a database driver may appear
/// (<c>build-vs-adopt-policy.md</c> §4).
/// </para>
/// <para>
/// <b>Streaming, not a list.</b> A-037 measured allocation as the binding
/// constraint at 80.9% GC pause on 18% CPU, and materialising a result before
/// returning it doubles the peak for no benefit — the caller is going to
/// serialise it one feature at a time anyway.
/// </para>
/// </remarks>
public interface IFeatureSource
{
    /// <summary>
    /// The attribute schema a given query will produce, without running it.
    /// </summary>
    /// <remarks>
    /// Separate so a caller can write response headers before the first row
    /// arrives, which is what makes streaming a response possible at all.
    /// </remarks>
    FeatureSchema SchemaFor(FeatureQuery query);

    /// <summary>Reads matching features, in provider order.</summary>
    IAsyncEnumerable<Feature> ReadAsync(
        FeatureQuery query,
        System.Threading.CancellationToken cancellationToken);

    /// <summary>
    /// Describes the layer: its attribute columns and where its features are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every client needs this before it can ask a question.</b> An ArcGIS
    /// client reads the field list, the object-id field and the extent in order
    /// to add a layer at all — a server that can answer <c>query</c> and cannot
    /// answer this is one no client can reach.
    /// </para>
    /// <para>
    /// Separate from <see cref="SchemaFor"/>, which answers <em>what will this
    /// query return</em> without touching the database. This one asks the
    /// database, so it costs a round trip and is not for the request path of a
    /// query.
    /// </para>
    /// </remarks>
    System.Threading.Tasks.Task<LayerDescription> DescribeAsync(
        System.Threading.CancellationToken cancellationToken);

    /// <summary>Counts matching features without reading them.</summary>
    /// <remarks>
    /// Its own method rather than a flag on the query, because it is a different
    /// shape of answer and a different cost: no geometry crosses the wire, and
    /// the limit and offset do not apply — a client asking how many there are
    /// wants the total, not the size of a page.
    /// </remarks>
    System.Threading.Tasks.Task<long> CountAsync(
        FeatureQuery query, System.Threading.CancellationToken cancellationToken);

    /// <summary>Counts matching features, stopping once <paramref name="ceiling"/> is reached.</summary>
    /// <param name="query">The query, whose filters apply and whose limit does not.</param>
    /// <param name="ceiling">The most it needs to count. Must be positive.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The number matched, or <paramref name="ceiling"/> when there are at least that many.</returns>
    /// <remarks>
    /// <para>
    /// <b>[D-118](../../../docs/architecture-debt.md): every `GetFeature` counted the whole result
    /// before writing a page of it.</b> WFS 2.0 makes <c>numberReturned</c> a required attribute
    /// on the collection element, so it has to be known before the first feature is written, and
    /// the two ways to know it were to buffer the page — which
    /// [A-037](../../../docs/architecture-assumptions.md) rules out, allocation being the binding
    /// constraint — or to ask how many rows match. Asking cost <c>O(table)</c> beside a page that
    /// costs <c>O(page)</c>.
    /// </para>
    /// <para>
    /// <b>A third way, and it is the one the row said to measure for.</b> A caller writing a page
    /// does not need the total; it needs to know how many rows this page will hold and whether
    /// there is another. Counting to <c>offset + limit + 1</c> answers both exactly, and reads at
    /// most that many rows. Measured before it was written — see
    /// [benchmarks/wfs-count](../../../benchmarks/wfs-count/RESULTS.md).
    /// </para>
    /// <para>
    /// <b>The return is deliberately ambiguous at the ceiling, and callers must treat it so.</b>
    /// A result equal to <paramref name="ceiling"/> means *at least this many*, which is what lets
    /// the WFS writer say <c>numberMatched="unknown"</c> — a value the specification defines for
    /// exactly this case.
    /// </para>
    /// </remarks>
    System.Threading.Tasks.Task<long> CountUpToAsync(
        FeatureQuery query, long ceiling, System.Threading.CancellationToken cancellationToken);
}
