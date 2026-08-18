using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Graticula.Features;
using Graticula.Platform.Catalog;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// One connection pool per data source, shared by every layer that uses it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per data source, not per layer</b> — ADR-007 §4.8. A hundred layers over
/// one database is one pool, because the database's connection limit is the
/// scarce thing and it does not care how many services we have published on top.
/// Pooling per layer is the arithmetic that killed the process-per-service model.
/// </para>
/// <para>
/// <b>What this does not yet do</b>, recorded rather than left to be discovered:
/// shrink-to-zero when idle (§4.8), a global cap per worker, the circuit breaker
/// N3 asked for, and the quiesce path that lets a DBA run DDL without us holding
/// their table open (§5b). Each is a real requirement with a number attached that
/// nobody has measured — Q-04, still open and still blocking.
/// </para>
/// </remarks>
/// <summary>
/// Where a feature source for a layer comes from.
/// </summary>
/// <remarks>
/// Extracted so <see cref="ServiceContexts"/> can be tested for how many times
/// it asks, which is the whole of what that class does. Testing it against a
/// real pool would test PostgreSQL's ability to answer the same question twice.
/// </remarks>
internal interface IServiceSources
{
    /// <summary>A feature source for one layer.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The source.</returns>
    IFeatureSource SourceFor(PublishedLayer layer);
}

internal sealed class LayerConnections : IServiceSources, IDisposable
{
    /// <summary>How long a single statement may run before PostgreSQL stops it.</summary>
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _pools = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _attachmentPools =
        new(StringComparer.Ordinal);
    private bool _disposed;

    /// <inheritdoc/>
    /// <remarks>Over a pool shared by every layer on the same data source.</remarks>
    public IFeatureSource SourceFor(PublishedLayer layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layer);

        NpgsqlDataSource pool = _pools.GetOrAdd(
            layer.ConnectionString,
            BuildPool);

        // <b>Lowered here, never raised.</b> The pool's own 30 seconds is in the connection
        // options and cannot be opted out of (ADR-007 §4.8); a service asking for more than that
        // gets the pool's figure, which is ADR-031 §2a's *may only lower* rule applied at the one
        // place a statement is issued from.
        TimeSpan? asked = layer.StatementTimeout;

        return new PostGisFeatureSource(
            pool,
            layer.Definition,
            asked is { } wanted && wanted > TimeSpan.Zero && wanted < StatementTimeout
                ? wanted
                : null);
    }

    /// <summary>
    /// The connection string with a statement timeout the registration cannot
    /// accidentally opt out of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADR-007 §4.8 makes statement timeouts mandatory, and the reason is not
    /// politeness to the database: without one, a single expensive query holds a
    /// pooled connection until the client gives up, and enough of them exhaust
    /// the pool for every other layer sharing that data source.
    /// </para>
    /// <para>
    /// <b>Applied server-side</b> via <c>options=-c statement_timeout</c> rather
    /// than through Npgsql's <c>CommandTimeout</c>, because the two do different
    /// things. <c>CommandTimeout</c> stops <em>us</em> waiting and then asks
    /// PostgreSQL to cancel — a request that races the query and can lose.
    /// <c>statement_timeout</c> is enforced by the server, so the work actually
    /// stops.
    /// </para>
    /// <para>
    /// <b>Thirty seconds is a guess.</b> Q-04 wants a measured number and has
    /// none. It is written here rather than left at the driver default so that
    /// the guess is visible and has a place to be corrected.
    /// </para>
    /// <para>
    /// <b>Corrected 2026-08-16 (D-42). The test used to be "is <c>Options</c>
    /// empty", and that made a mandatory control removable by an unrelated
    /// setting.</b> Deferring to an operator who set <c>statement_timeout</c>
    /// themselves is right — they may know something about this database that we do
    /// not. Deferring because they set <c>application_name</c> is not: the timeout
    /// then silently never applies, and ADR-007 §4.8 requires it because without
    /// one a single expensive query holds a pooled connection until the client
    /// gives up, and enough of them exhaust the pool for every other layer sharing
    /// that data source.
    /// </para>
    /// <para>
    /// So the question is whether <em>this</em> option is set, not whether any
    /// option is. Extracted from <c>BuildPool</c> to be testable without a
    /// database: the old form could only be checked by reading it, which is how it
    /// stayed wrong.
    /// </para>
    /// </remarks>
    internal static string WithStatementTimeout(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string options = builder.Options ?? string.Empty;

        if (!options.Contains("statement_timeout", StringComparison.OrdinalIgnoreCase))
        {
            string ours = $"-c statement_timeout={StatementTimeout.TotalMilliseconds:F0}";
            builder.Options = options.Length == 0 ? ours : options + " " + ours;
        }

        return builder.ConnectionString;
    }

    private static NpgsqlDataSource BuildPool(string connectionString) =>
        new NpgsqlDataSourceBuilder(WithStatementTimeout(connectionString)).Build();

    /// <summary>A writer for one layer, over the same shared pool.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="fields">
    /// Its real columns, which the writer uses as the identifier whitelist
    /// ADR-008 §4.6 requires.
    /// </param>
    public IFeatureWriter WriterFor(PublishedLayer layer, IReadOnlyList<FieldDescription> fields)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layer);

        return new PostGisFeatureWriter(
            _pools.GetOrAdd(layer.ConnectionString, BuildPool), layer.Definition, fields);
    }

    /// <summary>A tile source for one layer, over the same shared pool.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="attributes">
    /// The columns to carry into the tile, already checked against the table's
    /// real columns — the same identifier whitelist ADR-008 §4.6 requires of the
    /// select list, for the same reason.
    /// </param>
    /// <returns>The tile source.</returns>
    public ITileSource TileSourceFor(PublishedLayer layer, IReadOnlyList<string> attributes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layer);

        return new PostGisTileSource(
            _pools.GetOrAdd(layer.ConnectionString, BuildPool), layer.Definition, attributes);
    }

    /// <summary>
    /// An attachment store for one layer, over a pool of its own.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The store.</returns>
    /// <remarks>
    /// <b>A separate, small, bounded pool — ADR-013 §4b, and it is a condition
    /// rather than a nicety.</b> Streaming an attachment out of the database
    /// holds a pooled connection for as long as the client takes to read it, and
    /// a client reading one byte per second holds it indefinitely. That is
    /// slowloris pointed at the connection pool. Sharing the query pool means
    /// enough slow readers stop the <em>whole layer</em> serving; a separate one
    /// means they stop attachments, which is a bad afternoon rather than an
    /// outage.
    /// </remarks>
    public PostGisAttachmentStore AttachmentsFor(PublishedLayer layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layer);

        return new PostGisAttachmentStore(
            _attachmentPools.GetOrAdd(layer.ConnectionString, BuildAttachmentPool),
            layer.Definition,
            layer.AttachmentQuotaBytes);
    }

    /// <summary>
    /// A pool for attachment traffic, deliberately small.
    /// </summary>
    /// <remarks>
    /// <b>Eight is a bound, not a capacity figure.</b> Nobody has measured what
    /// a real attachment workload needs, and the number that matters here is not
    /// how many concurrent downloads are comfortable — it is how many stuck ones
    /// the deployment can afford to have doing nothing. Eight is enough for
    /// ordinary use and few enough that exhausting them is survivable.
    ///
    /// <b>No statement timeout.</b> The query pool has one because an expensive
    /// query should be stopped; an attachment read is slow because the client is
    /// slow, and cutting it off after thirty seconds would refuse every large
    /// download on a domestic connection.
    /// </remarks>
    private static NpgsqlDataSource BuildAttachmentPool(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString)
        {
            MaxPoolSize = 8,
            MinPoolSize = 0,
        };

        return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
    }

    /// <summary>
    /// A related-records reader over two layers.
    /// </summary>
    /// <param name="origin">The layer the caller started from.</param>
    /// <param name="related">The layer being reached.</param>
    /// <returns>The reader.</returns>
    /// <remarks>
    /// <b>Whether both layers share a database is decided here</b>, by comparing
    /// their connection strings, because this is the only place that holds both.
    /// A join is one statement in one database; a relationship declared across
    /// two is refused at query time rather than at declaration time, since a
    /// data source can be re-registered elsewhere after the declaration.
    /// </remarks>
    public PostGisRelatedRecords RelatedFor(PublishedLayer origin, PublishedLayer related)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(related);

        bool same = string.Equals(
            origin.ConnectionString, related.ConnectionString, StringComparison.Ordinal);

        return new PostGisRelatedRecords(
            _pools.GetOrAdd(origin.ConnectionString, BuildPool),
            origin.Definition,
            related.Definition,
            same);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (NpgsqlDataSource pool in _pools.Values)
        {
            pool.Dispose();
        }

        foreach (NpgsqlDataSource pool in _attachmentPools.Values)
        {
            pool.Dispose();
        }

        _pools.Clear();
    }
}
