using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Features;
using GisServer.Platform.Catalog;

namespace GisServer.Host;

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

    /// <summary>How many shapes are currently remembered.</summary>
    /// <remarks>Reported by <c>/admin/health</c> so the cache is observable.</remarks>
    public int Count => _entries.Count;

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
            return (source, await entry.Description.Value.ConfigureAwait(false));
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
            return;
        }

        _entries.TryRemove(Key.Of(layer), out _);
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
