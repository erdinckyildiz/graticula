using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GisServer.Geometries;

namespace GisServer.Features;

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
    public FeatureQuery(
        int limit,
        Envelope? boundingBox = null,
        IReadOnlyList<string>? fields = null,
        int offset = 0,
        bool includeGeometry = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        Limit = limit;
        BoundingBox = boundingBox;
        Offset = offset;
        IncludeGeometry = includeGeometry;

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

    /// <summary>Spatial restriction, or <see langword="null"/> for none.</summary>
    public Envelope? BoundingBox { get; }

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
}

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
}
