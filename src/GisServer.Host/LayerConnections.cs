using System;
using System.Collections.Concurrent;
using GisServer.Features;
using GisServer.Platform.Catalog;
using GisServer.Providers.PostGis;
using Npgsql;

namespace GisServer.Host;

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
internal sealed class LayerConnections : IDisposable
{
    /// <summary>How long a single statement may run before PostgreSQL stops it.</summary>
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _pools = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>A feature source for one layer, over a shared pool.</summary>
    public IFeatureSource SourceFor(PublishedLayer layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layer);

        NpgsqlDataSource pool = _pools.GetOrAdd(
            layer.ConnectionString,
            BuildPool);

        return new PostGisFeatureSource(pool, layer.Definition);
    }

    /// <summary>
    /// One pool, with a statement timeout the registration cannot opt out of.
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
    /// </remarks>
    private static NpgsqlDataSource BuildPool(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);

        // Never override an operator who has already set it: they may know
        // something about this database that we do not.
        if (string.IsNullOrEmpty(builder.Options))
        {
            builder.Options = $"-c statement_timeout={StatementTimeout.TotalMilliseconds:F0}";
        }

        return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
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

        _pools.Clear();
    }
}
