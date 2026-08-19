using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host;

/// <summary>
/// The bound on how much of a database this worker will ask for at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-007](../../docs/adr/ADR-007-service-runtime.md) §4.8 has required this since
/// 2026-08-12 and it did not exist</b> — a global connection cap per worker, *enforced across all
/// pools, so a deployment with many data sources degrades by queueing rather than by exhausting the
/// database*, and beside it a per-source concurrency limit sized for blast radius rather than for
/// throughput (N4). [Q-04](../../docs/open-questions.md) measured what their absence costs:
/// `(data sources + 1) × 100` potential connections per worker, because no connection string sets
/// `Maximum Pool Size` and Npgsql's default is 100. Six data sources is 700 against a default
/// PostgreSQL's `max_connections` of 100, and the failure at that ceiling is
/// `FATAL: sorry, too many clients already` on an arbitrary request.
/// </para>
/// <para>
/// <b>It counts requests, not connections, and that is the design rather than a compromise.</b>
/// Intercepting connection acquisition would mean wrapping Npgsql inside eight provider classes —
/// `CreateCommand` opens one implicitly — and it would put a lock on the hottest path in the server.
/// The measurement in [connection-budget](../../benchmarks/connection-budget/RESULTS.md) is what makes
/// the cheaper seam sound: **peak backends track concurrent requests**, 48 clients to 64 backends,
/// because a request holds at most one connection from a source at a time. So bounding requests per
/// source bounds that source's pool, and bounding requests overall bounds the worker. What it does not
/// bound is a connection held by something that is not a request — the job workers' claim poll, which
/// is D-110 and eight sessions.
/// </para>
/// <para>
/// <b>Queue, then refuse — §4.9's shape, not a silent wait.</b> A request that cannot get a slot waits
/// up to the configured wait and is then refused with a retry signal, because *accepting work it cannot
/// do* is the failure §4.9 exists to prevent. A queue with no bound is how a slow database becomes a
/// server that answers nothing at all, ten minutes later, having accumulated every request that
/// arrived in between.
/// </para>
/// <para>
/// <b>Two semaphores, and the per-source one is entered first.</b> Taking the global slot first would
/// let one saturated data source fill the worker's whole budget while every other source's requests
/// wait behind it for slots they are not competing for — which is exactly the blast-radius problem N4
/// describes. Entering the narrow gate first means a source can only ever hold its own share.
/// </para>
/// </remarks>
internal sealed class ConnectionBudget : IDisposable
{
    /// <summary>How long a request waits for a slot before it is refused, by default.</summary>
    /// <remarks>
    /// <b>Five seconds, and it is a fraction of the statement timeout on purpose.</b>
    /// `LayerConnections.StatementTimeout` is 30 s, so a request that has waited five seconds for a
    /// slot has spent a sixth of its budget before touching the database. Waiting longer would convert
    /// a queue into a timeout somewhere else, where the reason is no longer visible.
    /// </remarks>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim? _global;
    private readonly int _worker;
    private readonly int _perSource;
    private readonly TimeSpan _wait;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sources =
        new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>Creates the budget.</summary>
    /// <param name="worker">
    /// How many data-source requests this worker may have in flight at once, or zero for no bound.
    /// </param>
    /// <param name="perSource">
    /// How many against any one data source, or zero for no bound.
    /// </param>
    /// <param name="wait">
    /// How long a request waits for a slot, or null for <see cref="Default"/>. A parameter because the
    /// refusal is the interesting path and a test of it should not take five seconds.
    /// </param>
    public ConnectionBudget(int worker, int perSource, TimeSpan? wait = null)
    {
        _wait = wait ?? Default;
        _worker = Math.Max(0, worker);
        _global = _worker > 0 ? new SemaphoreSlim(_worker, _worker) : null;
        _perSource = Math.Max(0, perSource);
    }

    /// <summary>What the worker's bound is, or zero when there is none.</summary>
    public int Worker => _worker;

    /// <summary>What one data source's bound is, or zero when there is none.</summary>
    public int PerSource => _perSource;

    /// <summary>How long a request waits for a slot before it is refused.</summary>
    public TimeSpan Wait => _wait;

    /// <summary>
    /// Waits for a slot against one data source.
    /// </summary>
    /// <param name="source">A key identifying the data source — its connection string.</param>
    /// <param name="cancellationToken">The caller's own cancellation.</param>
    /// <returns>A lease. Dispose it to give the slot back.</returns>
    /// <exception cref="ConnectionBudgetFullException">
    /// The wait expired, which means this worker is at its bound.
    /// </exception>
    public async ValueTask<Lease> EnterAsync(string source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        SemaphoreSlim? perSource = _perSource > 0
            ? _sources.GetOrAdd(source, _ => new SemaphoreSlim(_perSource, _perSource))
            : null;

        bool tookSource = false;

        if (perSource is not null)
        {
            tookSource = await perSource.WaitAsync(_wait, cancellationToken).ConfigureAwait(false);

            if (!tookSource)
            {
                throw new ConnectionBudgetFullException(
                    $"This server already has {_perSource} requests in flight against that data "
                    + $"source and waited {_wait.TotalSeconds:0.#} s for one to finish. The limit is "
                    + "per data source and exists so that one slow database cannot take the whole "
                    + "server with it (ADR-007 §4.8, N4). Retry, or raise "
                    + "Graticula:PerSourceConcurrency if the database can take more.");
            }
        }

        if (_global is null)
        {
            return new Lease(perSource, null);
        }

        try
        {
            if (!await _global.WaitAsync(_wait, cancellationToken).ConfigureAwait(false))
            {
                throw new ConnectionBudgetFullException(
                    $"This worker already has its full budget of database requests in flight and "
                    + $"waited {_wait.TotalSeconds:0.#} s for one to finish. The bound is per worker "
                    + "and across every data source, so that many databases degrade by queueing "
                    + "rather than by exhausting one of them (ADR-007 §4.8). Retry, or raise "
                    + "Graticula:ConnectionBudget.");
            }
        }
        catch
        {
            if (tookSource)
            {
                perSource!.Release();
            }

            throw;
        }

        return new Lease(perSource, _global);
    }

    /// <summary>A held slot. Disposing it returns the slot, narrow gate last.</summary>
    /// <remarks>
    /// <b>A struct, because this is on the query path.</b> One of these is taken per query and the
    /// alternative is an allocation per request for something whose whole content is two references.
    /// </remarks>
    internal readonly struct Lease(SemaphoreSlim? perSource, SemaphoreSlim? global) : IDisposable
    {
        /// <summary>Gives the slots back.</summary>
        public void Dispose()
        {
            global?.Release();
            perSource?.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _global?.Dispose();

        foreach (SemaphoreSlim one in _sources.Values)
        {
            one.Dispose();
        }

        _sources.Clear();
    }
}

/// <summary>
/// The worker is at its connection budget and the caller waited.
/// </summary>
/// <remarks>
/// <b>Its own type, because the answer it produces is a different answer.</b> A database that refused
/// is 503 *the database is unreachable*; this is 503 *this server is at its limit, come back* — with a
/// `Retry-After`, which is the retry signal ADR-007 §4.9 asks admission control to send. Reusing the
/// unreachable-database message would send an operator to look at a database that is working.
/// </remarks>
internal sealed class ConnectionBudgetFullException : Exception
{
    public ConnectionBudgetFullException(string message)
        : base(message)
    {
    }

    public ConnectionBudgetFullException()
        : base("This worker is at its database connection budget.")
    {
    }

    public ConnectionBudgetFullException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
