using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// Remembers what a table looks like, so the request path stops asking.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this fixes (D-17).</b> Every FeatureServer request re-derived the
/// table's shape from the data source: <c>information_schema.columns</c> for the
/// fields and <c>ST_EstimatedExtent</c> for the extent, two round trips before
/// any feature was read. Measured against a real layer on 2026-08-14, the fixed
/// rediscovery cost was <b>~4–6 ms per request</b> — 51–67% of a 100-row query
/// and 43% of a 1000-row query. The work is the same on every request and the
/// answer is the same on every request.
/// </para>
/// <para>
/// <b>A claim tested and refused.</b> The first justification written here was
/// that <c>information_schema.columns</c> degrades on a large database, because
/// it is a view over <c>pg_attribute</c>. That was measured — 1,500 tables and
/// 41,409 columns against 3,909 — and it is <b>false</b>: the timing did not
/// move, because the planner reaches <c>pg_class</c> by name through an index
/// rather than scanning. The justification for this cache is round-trip count
/// alone, which is a smaller claim than the one it replaced and is the one the
/// measurement supports.
/// </para>
/// <para>
/// <b>What is deliberately NOT cached: the catalogue entry.</b> A
/// <see cref="PublishedLayer"/> carries the sharing scope, the owner and the
/// started/stopped status — which is to say it carries the authorization
/// decision and the operational one. Caching those means a layer made private
/// stays readable, and a service stopped mid-incident keeps answering, for as
/// long as the entry lives. Across two servers on one platform store no local
/// invalidation can fix that, because the mutation happens in the other process.
/// So the catalogue is read on every request and only the <em>shape</em> is
/// remembered: two of the three round trips go away and the security-relevant
/// one stays. A cache that also held the sharing scope would be faster and would
/// be trading the wrong thing.
/// </para>
/// <para>
/// <b>The key is the identity of the table, not the name of the layer.</b> A
/// layer name can be unpublished and reused for a different table; keyed by
/// name, the new layer would inherit the old one's columns. Keyed by
/// <c>(connection, schema, table, geometry column)</c> that cannot happen — a
/// republish onto a different table is simply a different entry, and needs no
/// invalidation call that somebody has to remember to write.
/// </para>
/// </remarks>
internal sealed class ServiceContexts
{
    /// <summary>
    /// How long a remembered shape is trusted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bounded by a person, not by a benchmark.</b> The failure this backstops
    /// is a DBA altering a table underneath us: adding a column that clients then
    /// cannot see, or dropping one that queries then fail on with a database
    /// error instead of our own refusal. Nothing in this process learns of it, so
    /// the only bound is time. The bound that matters is how long somebody will
    /// wait before concluding the server is broken and restarting it — which is
    /// tens of seconds, not minutes.
    /// </para>
    /// <para>
    /// <b>And long enough to be worth having.</b> At one request a second this is
    /// a thirtyfold reduction in describes; at a hundred, three thousandfold. The
    /// curve is flat past this point, so a longer window buys almost nothing and
    /// costs the whole of the staleness above.
    /// </para>
    /// <para>
    /// <b>It is still a number chosen by argument rather than measured</b>, which
    /// is the weaker kind. It is written here with its reasoning so the reasoning
    /// can be disagreed with — the failure recorded in
    /// <see cref="AuthEndpoints.MinimumPasswordLength"/> was a number with no
    /// reasoning attached at all, and it survived for exactly that reason.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Key, Entry> _entries = new();

    /// <summary>
    /// The last shape each layer was known to have, for when the database will not say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-127](../../docs/architecture-debt.md), the half that made the claim
    /// misleading.</b> [ADR-026](../../docs/adr/ADR-026-serving-through-a-platform-store-outage.md)
    /// answers Q-95 with fifteen minutes of degraded serving, and the failure gate stopped
    /// the database and measured **thirty seconds**. Two caches, one fallback:
    /// `CatalogFallback` remembers *which service exists* for fifteen minutes, and this
    /// class remembered *what shape it has* for thirty seconds with no fallback at all —
    /// so every document that needs a field list died at thirty seconds however fresh the
    /// catalogue memory was.
    /// </para>
    /// <para>
    /// <b>Separate from <c>_entries</c> rather than a longer <see cref="Lifetime"/>.</b>
    /// Those are different questions. <see cref="Lifetime"/> is *how long before I ask
    /// again*, and its thirty seconds is bounded by how long a DBA's `ALTER TABLE` may go
    /// unnoticed — lengthening it would serve a stale field list to a healthy server,
    /// which is the failure that number exists to prevent. This is *what do I say when
    /// asking is impossible*, and there the alternative is not a stale answer but no
    /// answer.
    /// </para>
    /// <para>
    /// <b>Only written from a success, and never expiring on its own.</b> A remembered
    /// shape has no upper age here because the thing that bounds it is
    /// `CatalogFallback`'s window: a service whose catalogue memory has gone stale is
    /// refused before anything asks for its shape, so a second window would be a second
    /// number to keep in step with the first.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<Key, LayerDescription> _known = new();

    /// <summary>
    /// Each layer's measured time extent, and when it was measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than in the WMS face — [D-160](../../docs/architecture-debt.md).</b>
    /// It lived as a <c>static</c> dictionary in <c>WmsEndpoints</c>, keyed by layer id,
    /// read and written and <em>never</em> removed from: the five-minute staleness check
    /// answers *may I trust this* and nothing was asking *should this still be here*. A
    /// republished layer gets a new id ([D-34](../../docs/architecture-debt.md) makes
    /// republishing the ordinary way to correct a name), so the count grew with the
    /// number of publications a deployment had ever made and the entries for layers that
    /// no longer exist were unreachable and immortal.
    /// </para>
    /// <para>
    /// <b>Moving it here is the whole fix.</b> <see cref="Forget"/> is already called by
    /// the unpublish and refresh paths and already clears both other memories; putting
    /// this one beside them means it is cleared by code that was already clearing
    /// everything else, rather than by a second thing somebody has to remember.
    /// </para>
    /// <para>
    /// <b>Keyed by layer id rather than by <see cref="Key"/>.</b> The other two are keyed
    /// by table, because two layers over one table have one shape. A time extent is a
    /// property of the *layer*: two layers over the same table can declare different time
    /// columns ([Q-129](../../docs/open-questions.md)), so sharing the entry would give
    /// one of them the other's extent.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, (Graticula.Api.Wms.TimeDimension Extent, DateTimeOffset Measured)>
        _times = new();

    private readonly IServiceSources _connections;
    private readonly TimeProvider _clock;

    /// <summary>Creates the cache.</summary>
    /// <param name="connections">Where feature sources come from.</param>
    /// <param name="clock">The clock, so expiry is testable without waiting.</param>
    public ServiceContexts(IServiceSources connections, TimeProvider clock)
    {
        _connections = connections;
        _clock = clock;
    }

    /// <summary>How many shapes are remembered for a database that will not answer.</summary>
    /// <remarks>
    /// Reported by <c>/admin/health</c> beside <see cref="Count"/>, because the two answer
    /// different questions during an outage: how much is fresh, and how much can still be
    /// served.
    /// </remarks>
    public int KnownCount => _known.Count;

    /// <summary>How many shapes are currently remembered.</summary>
    /// <remarks>Reported by <c>/admin/health</c> so the cache is observable.</remarks>
    public int Count => _entries.Count;

    /// <summary>How many time extents are currently remembered.</summary>
    /// <remarks>
    /// <b>Reported so the cache is observable, which is what D-160 was about.</b>
    /// [Q-64](../../docs/open-questions.md) wants growth without corresponding load to be
    /// the signal that separates a leak from a warm cache, and a structure nobody can
    /// count poisons that measurement before it is taken.
    /// </remarks>
    public int TimeCount => _times.Count;

    /// <summary>A layer's remembered time extent, if it is still fresh.</summary>
    /// <param name="layerId">The layer.</param>
    /// <param name="lifetime">How long a measurement is trusted.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The extent, or null to measure again.</returns>
    public Graticula.Api.Wms.TimeDimension? RememberedTime(Guid layerId, TimeSpan lifetime, DateTimeOffset now) =>
        _times.TryGetValue(
            layerId, out (Graticula.Api.Wms.TimeDimension Extent, DateTimeOffset Measured) held)
        && now - held.Measured < lifetime
            ? held.Extent
            : null;

    /// <summary>Remembers a layer's measured time extent.</summary>
    /// <param name="layerId">The layer.</param>
    /// <param name="extent">What was measured.</param>
    /// <param name="now">When.</param>
    public void RememberTime(
        Guid layerId, Graticula.Api.Wms.TimeDimension extent, DateTimeOffset now) =>
        _times[layerId] = (extent, now);

    /// <summary>
    /// The feature source and described shape for a layer.
    /// </summary>
    /// <param name="layer">The layer, freshly read from the catalogue.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The source and its shape.</returns>
    /// <remarks>
    /// <b>One describe per cold layer, not one per concurrent request.</b> The
    /// task is stored before it completes, so a burst against a layer nobody has
    /// touched yet produces a single round trip and N waiters. Storing the
    /// finished value instead would let a hundred simultaneous requests each
    /// issue their own describe — the stampede that makes a cache miss worse than
    /// no cache at all.
    /// </remarks>
    public async Task<(IFeatureSource Source, LayerDescription Description)> GetAsync(
        PublishedLayer layer, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(layer);

        IFeatureSource source = _connections.SourceFor(layer);
        Key key = Key.Of(layer);
        DateTimeOffset now = _clock.GetUtcNow();

        Entry entry = _entries.GetOrAdd(key, _ => NewEntry(source, now));

        if (entry.ExpiresAt <= now)
        {
            // Replace only the entry we actually looked at. If another thread
            // renewed it first this loses the race and takes their entry, which
            // is the right outcome — two renewals of the same expired shape is
            // the stampede in slow motion.
            Entry renewed = NewEntry(source, now);

            entry = _entries.TryUpdate(key, renewed, entry)
                ? renewed
                : _entries.TryGetValue(key, out Entry? current) ? current : renewed;
        }

        try
        {
            LayerDescription described = await entry.Description.Value.ConfigureAwait(false);

            _known[key] = described;

            return (source, described);
        }
        catch (Exception failure) when (Unreachable(failure) && _known.ContainsKey(key))
        {
            /*
              <b>The database will not say, so this says what it last said.</b> D-127: the
              alternative here is not a stale field list, it is no document at all — every
              face that needs a shape refuses, which is what made ADR-026's fifteen minutes
              thirty seconds in practice.

              <b>The entry is still forgotten</b>, by the same reasoning as the catch below:
              a failed describe must not be remembered as the *current* answer, or one
              refused connection becomes a whole lifetime of refusals for a database that
              recovered immediately. What is kept is the older successful answer, which is
              a different thing.

              <b>Only for a source that could not be reached.</b> A database that answered
              and said the table is gone is telling the truth, and serving a remembered
              field list over it would present a dropped column as present — the failure
              `Lifetime` exists to bound, arriving through the door marked resilience.
            */
            _entries.TryRemove(new KeyValuePair<Key, Entry>(key, entry));

            return (source, _known[key]);
        }
        catch
        {
            // A failed describe must not be remembered. Caching the failure
            // would turn one refused connection into a whole lifetime of
            // refusals for a database that recovered immediately — and the
            // caller who triggered it would be the only one to see a real
            // error. Removed by identity, so a renewal that has already
            // succeeded is not thrown away by a straggler's exception.
            _entries.TryRemove(new KeyValuePair<Key, Entry>(key, entry));
            throw;
        }
    }

    /// <summary>
    /// An entry whose describe has not started yet.
    /// </summary>
    /// <remarks>
    /// <b>Lazy, because <c>GetOrAdd</c> is not atomic.</b> Its factory may run on
    /// several threads at once and all but one result is discarded — so a factory
    /// that started the round trip would start several and throw most away, at
    /// the exact moment the database is least able to take them. Wrapping the
    /// task in a <see cref="Lazy{T}"/> means the discarded entries never touch
    /// <c>.Value</c> and never issue anything: the losers cost an allocation, not
    /// a query. <c>ExecutionAndPublication</c> is the default and is the mode
    /// that guarantees the factory runs once.
    /// </remarks>
    private static Lazy<Task<LayerDescription>> Describe(IFeatureSource source) =>
        new(() => source.DescribeAsync(CancellationToken.None));

    /// <summary>A fresh entry for a source, valid from now.</summary>
    /// <remarks>
    /// <b>The describe is not given the caller's cancellation token</b>, and that
    /// is deliberate. One task is shared by every waiter, so cancelling it
    /// because the first caller closed their browser would fail everybody else
    /// waiting on the same round trip. The statement timeout on the pool
    /// (<see cref="LayerConnections.StatementTimeout"/>) is what bounds it
    /// instead, and that one cannot be lost to a race.
    /// </remarks>
    private static Entry NewEntry(IFeatureSource source, DateTimeOffset now) =>
        new(Describe(source), now + Lifetime);

    /// <summary>Whether a failure means the source could not be reached.</summary>
    /// <remarks>
    /// <b>The same discriminator <see cref="SourceBreaker.Unreachable"/> uses, called
    /// rather than repeated.</b> Two copies of *what counts as an outage* is two places
    /// for a `PostgresException` to start being treated as one, and the consequence here
    /// is worse than there: this one would serve a remembered field list over a table that
    /// really had changed.
    /// </remarks>
    private static bool Unreachable(Exception failure) =>
        failure is not OperationCanceledException && SourceBreaker.Unreachable(failure);

    /// <summary>
    /// Forgets a layer's shape.
    /// </summary>
    /// <param name="layer">The layer, or null to forget everything.</param>
    /// <remarks>
    /// Called by the admin path after anything that could have changed a table.
    /// Republishing onto a different table does not need this — that is a
    /// different key — but an operator who has just altered a table and does not
    /// want to wait out <see cref="Lifetime"/> does.
    /// </remarks>
    public void Forget(PublishedLayer? layer)
    {
        if (layer is null)
        {
            _entries.Clear();
            _known.Clear();
            _times.Clear();
            return;
        }

        Key key = Key.Of(layer);

        _entries.TryRemove(key, out _);

        // <b>D-160: the time extent goes with them.</b> It used to live in the WMS face
        // and outlive every layer it described, because nothing on this path knew it
        // existed.
        _times.TryRemove(layer.Id, out _);

        // <b>Both, and the second one is the point.</b> An operator who has just altered a
        // table and asked the server to forget it must not have the old shape served back
        // to them the next time the database hiccups. Forgetting one of two memories is
        // not forgetting.
        _known.TryRemove(key, out _);
    }

    /// <summary>What makes two layers the same table.</summary>
    private readonly record struct Key(string Connection, string Schema, string Table, string Geometry)
    {
        public static Key Of(PublishedLayer layer) => new(
            layer.ConnectionString,
            layer.Definition.SchemaName,
            layer.Definition.TableName,
            layer.Definition.GeometryColumn);
    }

    /// <summary>A remembered shape and when to stop trusting it.</summary>
    private sealed record Entry(Lazy<Task<LayerDescription>> Description, DateTimeOffset ExpiresAt);
}
