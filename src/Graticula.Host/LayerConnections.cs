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
/// <b>Two of the four things this did not do are done, and the other two are not.</b> The list was
/// shrink-to-zero when idle (§4.8), a global cap per worker, the circuit breaker N3 asked for, and the
/// quiesce path that lets a DBA run DDL without us holding their table open (§5b) — *each a real
/// requirement with a number attached that nobody has measured*. **Q-04 measured them on 2026-08-19**
/// ([connection-budget](../../benchmarks/connection-budget/RESULTS.md)): shrink-to-zero turned out to
/// already work, from Npgsql's own pruning rather than from anything here — 79 backends to zero in 184
/// seconds — and the cap is now <see cref="ConnectionBudget"/>, bounding requests per source and per
/// worker, which bounds the pools because a request holds at most one connection from a source at a
/// time. **The circuit breaker landed 2026-08-23** — <see cref="SourceBreaker"/>, after
/// [D-131](../../docs/architecture-debt.md) measured what its absence cost: 8.0 seconds a
/// refusal during an outage, every one of them holding a connection for its whole four
/// seconds. **Still absent: quiesce.** And the floor on an idle server is not
/// zero but eight, held open by our own job polling, which is D-110 and which a request budget cannot
/// reach.
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
    private readonly ConnectionBudget _budget;
    private readonly SourceBreaker _breaker;
    private readonly SourceQuiesce? _quiesce;

    /// <summary>Creates the pool cache.</summary>
    /// <param name="budget">ADR-007 §4.8's bound on how much of a database this worker asks for.</param>
    /// <param name="breaker">§4.8's N3, which was the last of that section's four still absent.</param>
    /// <param name="quiesce">
    /// §4.8's fifth bullet, which was the actual last one — ADR-059.
    /// <para>
    /// <b>Optional, so the tests that build this directly do not have to care.</b> They are about
    /// pooling and the budget; a null register is a process where nothing is ever quiesced.
    /// </para>
    /// </param>
    public LayerConnections(
        ConnectionBudget budget,
        SourceBreaker breaker,
        SourceQuiesce? quiesce = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(breaker);

        _budget = budget;
        _breaker = breaker;
        _quiesce = quiesce;
    }

    /// <summary>
    /// Closes this source's pool, so nothing of ours is holding its tables.
    /// </summary>
    /// <param name="connectionString">The source key, which is how the pools are keyed.</param>
    /// <returns>Whether a pool was open.</returns>
    /// <remarks>
    /// <para>
    /// <b>ADR-059 §5c — and this is not what frees the DBA's lock, which the ADR got backwards
    /// until it was measured.</b> An idle pooled connection does not block <c>ALTER TABLE</c> at
    /// all: 0.410 s with one held open, against 5.262 s to <c>lock_timeout</c> with a connection
    /// idle *in a transaction*. What frees the lock is the refusal — no new query starts and the
    /// running ones drain.
    /// </para>
    /// <para>
    /// <b>So this is here for three smaller reasons, all real.</b> A DBA verifies by looking at
    /// <c>pg_stat_activity</c> and cannot tell a source that is refusing from one that ignored
    /// them; a delegated query is idle-in-transaction and <i>does</i> block (A-036), so those are
    /// exactly the connections worth removing; and the connection count goes back, which matters
    /// on a database near <c>max_connections</c> at the moment somebody is working on it.
    /// </para>
    /// <para>
    /// <b>Removed from the cache as well as disposed.</b> The pool is keyed by connection string
    /// and rebuilt by <c>GetOrAdd</c> on the next request after the quiesce ends, which is the
    /// same path a cold start takes — so there is nothing to reopen and nothing to remember.
    /// </para>
    /// <para>
    /// <b>A request already in flight keeps its connection to the end.</b> Disposing a
    /// <c>NpgsqlDataSource</c> does not kill borrowed connections, and killing them would be
    /// [D-144](../../docs/architecture-debt.md)'s problem — a response part-written is worse than
    /// a lock held a few seconds longer. ADR-059 §4 states this as a cost rather than hiding it.
    /// </para>
    /// </remarks>
    public bool CloseSource(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        bool closed = false;

        if (_pools.TryRemove(connectionString, out NpgsqlDataSource? pool))
        {
            pool.Dispose();
            closed = true;
        }

        // <b>The attachment pools too, and forgetting them would have been the whole defect.</b>
        // They are a second dictionary keyed the same way, opened by the attachment store, and a
        // source whose feature pool is closed while its attachment pool is open is a source still
        // holding the table.
        if (_attachmentPools.TryRemove(connectionString, out NpgsqlDataSource? attachments))
        {
            attachments.Dispose();
            closed = true;
        }

        return closed;
    }

    /// <summary>How long a single statement may run before PostgreSQL stops it.</summary>
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _pools = new(StringComparer.Ordinal);

    /// <summary>
    /// The pool for one source, unless an operator has taken it out of service.
    /// </summary>
    /// <param name="connectionString">The source key.</param>
    /// <returns>The pool.</returns>
    /// <exception cref="SourceQuiescedException">When the source is quiesced.</exception>
    /// <remarks>
    /// <para>
    /// <b>Written 2026-09-08 on re-reading what had just been built, and it closes a hole that
    /// would have made ADR-059 fail at its own job.</b> The quiesce gate went into
    /// <see cref="BudgetedFeatureSource"/>, which is the *read* path. Writes, tiles and
    /// attachments each reached <c>_pools.GetOrAdd</c> directly — so a quiesced source still
    /// accepted `applyEdits`, and an edit runs in a transaction, which is precisely the thing
    /// that blocks a DBA's <c>ALTER TABLE</c>.
    /// </para>
    /// <para>
    /// <b>And <c>GetOrAdd</c> would have rebuilt the pool that had just been closed.</b> The next
    /// tile request after a quiesce would have reopened the connections §5c closes, which is the
    /// same defect wearing a second face: the operator's instruction would have been undone by
    /// the traffic it was meant to stop.
    /// </para>
    /// <para>
    /// <b>One place, because there are four callers and a fifth will be written.</b> A check
    /// repeated at each hand-out is a check the next hand-out forgets — which is exactly how this
    /// hole came to exist.
    /// </para>
    /// </remarks>
    private NpgsqlDataSource PoolFor(string connectionString)
    {
        if (_quiesce?.Holding(connectionString) is { } held)
        {
            throw new SourceQuiescedException(SourceQuiesce.Says(held), held.Until);
        }

        return _pools.GetOrAdd(connectionString, BuildPool);
    }


    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _attachmentPools =
        new(StringComparer.Ordinal);
    private bool _disposed;

    /// <inheritdoc/>
    /// <remarks>Over a pool shared by every layer on the same data source.</remarks>
    public IFeatureSource SourceFor(PublishedLayer layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layer);

        // <b>Through the gate like every other hand-out.</b> The read path's own refusal lives in
        // `BudgetedFeatureSource`, which fires when a query runs — but the *pool* was still taken
        // with `GetOrAdd`, so a quiesced source rebuilt the connections it had just closed on the
        // first request that came in to be refused. The gate closes both halves; the decorator's
        // check stays as the one that catches a quiesce beginning after this line.
        NpgsqlDataSource pool = PoolFor(layer.ConnectionString);

        // <b>Lowered here, never raised.</b> The pool's own 30 seconds is in the connection
        // options and cannot be opted out of (ADR-007 §4.8); a service asking for more than that
        // gets the pool's figure, which is ADR-031 §2a's *may only lower* rule applied at the one
        // place a statement is issued from.
        TimeSpan? asked = layer.StatementTimeout;

        // <b>Wrapped in the budget, keyed on the connection string — which is what makes the key the
        // data source rather than the layer.</b> Two layers in the same database share a pool and now
        // share a bound, which is the whole of ADR-007 §4.8's arithmetic.
        return new BudgetedFeatureSource(
            new PostGisFeatureSource(
                pool,
                layer.Definition,
                asked is { } wanted && wanted > TimeSpan.Zero && wanted < StatementTimeout
                    ? wanted
                    : null),
            _budget,
            layer.ConnectionString,
            _breaker,
            _quiesce);
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
    /// <b>And the sentence above describes a failure this does not prevent —
    /// measured 2026-08-24</b>
    /// (`benchmarks/slow-reader`). <em>Until the client gives up</em> is exactly
    /// the part that stays unbounded: a statement timeout bounds a
    /// <em>statement</em>, and a response is not one. Rows are pulled as the
    /// writer consumes them, so a client reading slowly restarts the clock on
    /// every round trip — one reader at 150 kB/s held a connection active for
    /// <b>115.7 s</b> against a service whose statement timeout was <b>one
    /// second</b>. Eight of them held eight of a source's twenty-four permits
    /// for 289 seconds. What this timeout buys is a bound on a pathological
    /// <em>query</em>, which is real and is not the same promise.
    /// </para>
    /// <para>
    /// <b>A wall-clock bound does cover it, and this paragraph used to imply
    /// nothing did.</b> <c>RequestDeadline</c> has put every request under a
    /// deadline since 2026-08-18 — installed before authentication, 600 s by
    /// default, lowerable per service and never raisable — and
    /// <c>BudgetedFeatureSource</c> holds its permit in a <c>using</c> around
    /// the enumeration, so the permit goes back when the deadline unwinds it.
    /// Ten minutes is a bound; against the 115.7 s and 289 s holds measured in
    /// <c>benchmarks/slow-reader</c> it is not a defence, which is a different
    /// complaint and the one worth carrying.
    /// </para>
    /// <para>
    /// The bound that would charge *slowness itself* is Kestrel's
    /// <c>MinResponseDataRate</c>, which this server does not set — so it is at
    /// the framework default of 240 bytes a second. Choosing a floor is
    /// [D-144](../../../docs/architecture-debt.md) and
    /// [Q-139](../../../docs/open-questions.md), and it is a decision rather
    /// than an omission because ending a response cannot say why it ended.
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

    /// <summary>
    /// Names this pool in <c>pg_stat_activity</c>, unless the connection string already
    /// names it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every connection that reads a layer was anonymous until 2026-09-09</b>, while
    /// <see cref="PoolNames"/>'s own summary said <i>this server's connection pools</i>.
    /// <c>PoolNames.Of</c> was applied to the platform store and to the job pollers and to
    /// nothing else, so the pool an operator most needs to attribute — the one holding their
    /// database while a query runs — was the one with no name. Found while measuring
    /// [Q-139](../../docs/open-questions.md).
    /// </para>
    /// <para>
    /// <b>The operator's name wins, which is not the rule the statement timeout uses.</b>
    /// A timeout is a bound this server owes its own reliability, so it is imposed where
    /// unset; a name is a label on <em>somebody else's</em> server, and an operator who
    /// wrote one into a registered connection string wrote it for their own monitoring.
    /// Overwriting it would take away the answer to give them ours.
    /// </para>
    /// <para>
    /// Extracted rather than inlined for <c>WithStatementTimeout</c>'s reason: the old
    /// form could only be checked by reading it, which is how it stayed wrong.
    /// </para>
    /// </remarks>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The connection string, named.</returns>
    internal static string WithApplicationName(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);

        if (string.IsNullOrWhiteSpace(builder.ApplicationName))
        {
            builder.ApplicationName = PoolNames.Of(PoolNames.Layers);
        }

        return builder.ConnectionString;
    }

    private static NpgsqlDataSource BuildPool(string connectionString) =>
        new NpgsqlDataSourceBuilder(
            WithApplicationName(WithStatementTimeout(connectionString))).Build();

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
            PoolFor(layer.ConnectionString), layer.Definition, fields);
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
            PoolFor(layer.ConnectionString), layer.Definition, attributes);
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

        // <b>The same guard, on the second dictionary.</b> Attachments are a separate pool keyed
        // the same way — a quiesced source that still served them would still be holding the
        // database, which is what `CloseSource` empties both of.
        if (_quiesce?.Holding(layer.ConnectionString) is { } attachmentsHeld)
        {
            throw new SourceQuiescedException(
                SourceQuiesce.Says(attachmentsHeld), attachmentsHeld.Until);
        }

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
            PoolFor(origin.ConnectionString),
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
