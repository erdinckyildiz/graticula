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
    public FeatureQuery(int limit, Envelope? boundingBox = null, IReadOnlyList<string>? fields = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumLimit);

        Limit = limit;
        BoundingBox = boundingBox;

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
}
