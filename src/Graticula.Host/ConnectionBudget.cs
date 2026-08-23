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

    /// <summary>How many callers may wait per permit before arrivals are refused.</summary>
    /// <remarks>
    /// <para>
    /// <b>Four, and it is the number
    /// [ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md)
    /// exists to introduce.</b> Expressed in permits rather than requests so that raising
    /// capacity raises the queue with it: a source with 24 permits admits 96 waiters, and an
    /// operator who doubles the permits does not have to find a second setting.
    /// </para>
    /// <para>
    /// <b>Four service times is the wait it promises</b>, which for a query of 25 ms is a
    /// tenth of a second and for a four-second render is sixteen seconds. That asymmetry is
    /// deliberate: the bound is on how much work is queued, and a caller who queued behind
    /// four rounds of renders has queued behind sixteen seconds of real work.
    /// </para>
    /// </remarks>
    public const int WaitersPerPermit = 4;

    private readonly int _perPermit;

    private readonly SemaphoreSlim? _global;
    private readonly int _worker;
    private readonly int _perSource;
    private readonly TimeSpan _wait;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sources =
        new(StringComparer.Ordinal);

    /// <summary>
    /// How many callers are waiting for each source's permits, and for the worker's.
    /// </summary>
    /// <remarks>
    /// <b>Counted here because a semaphore does not expose it.</b> `SemaphoreSlim.CurrentCount`
    /// is the number of free permits, which is zero the moment a source saturates and stays
    /// zero however many callers pile up behind it — so the thing that grows is the one thing
    /// the primitive cannot report. That is the whole reason the old bound could not fire: it
    /// measured how long one caller had waited, which stays small when service is fast, rather
    /// than how many were waiting, which does not.
    /// </remarks>
    private readonly ConcurrentDictionary<string, int> _waiting =
        new(StringComparer.Ordinal);

    private int _waitingGlobal;

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
    /// <param name="waitersPerPermit">
    /// How many callers may queue per permit before arrivals are refused, or null for
    /// <see cref="WaitersPerPermit"/>. A parameter because
    /// [ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md)
    /// condition 2 asks for the number to be chosen against a measurement, and a constant
    /// cannot be measured at several values.
    /// </param>
    public ConnectionBudget(
        int worker, int perSource, TimeSpan? wait = null, int? waitersPerPermit = null)
    {
        _wait = wait ?? Default;
        _worker = Math.Max(0, worker);
        _global = _worker > 0 ? new SemaphoreSlim(_worker, _worker) : null;
        _perSource = Math.Max(0, perSource);
        _perPermit = Math.Max(1, waitersPerPermit ?? WaitersPerPermit);
    }

    /// <summary>What the worker's bound is, or zero when there is none.</summary>
    public int Worker => _worker;

    /// <summary>What one data source's bound is, or zero when there is none.</summary>
    public int PerSource => _perSource;

    /// <summary>How long a request waits for a slot before it is refused.</summary>
    public TimeSpan Wait => _wait;

    /// <summary>How many callers are waiting for a worker permit right now.</summary>
    public int WaitingForWorker => Volatile.Read(ref _waitingGlobal);

    /// <summary>How many callers are waiting for one source's permits right now.</summary>
    /// <param name="source">The data source key.</param>
    /// <returns>The count, or zero for a source nothing has asked for.</returns>
    public int WaitingFor(string source) =>
        _waiting.TryGetValue(source, out int waiting) ? waiting : 0;

    /// <summary>The largest number of callers waiting for any one source right now.</summary>
    /// <remarks>
    /// <b>The maximum rather than a per-source list, because the question is *is anything
    /// queueing* and a list would name connection strings.</b> A health response that
    /// enumerated data sources would be telling a reader which databases this deployment has.
    /// </remarks>
    public int WaitingForSource
    {
        get
        {
            int most = 0;

            foreach (int waiting in _waiting.Values)
            {
                if (waiting > most)
                {
                    most = waiting;
                }
            }

            return most;
        }
    }

    /// <summary>The most callers that may wait for one source before arrivals are refused.</summary>
    public int QueueDepth => _perSource > 0 ? _perSource * _perPermit : 0;

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
            /*
              <b>Refused on arrival when the queue is already too deep, which is ADR-046.</b>
              The wait below stays as a backstop and is no longer the decisive test: it asks
              *how long has this caller waited*, which stays small whenever service is fast,
              and a query that takes 25 ms keeps freeing a permit inside any window. Measured
              before this: 720 requests at concurrency 240, none refused, median latency
              growing from 79 ms to 611 ms in step with the concurrency. The queue was the
              only thing absorbing the load and nothing was watching it.

              <b>Counted before the wait and released after it, in a finally, because a
              cancelled caller must not leave the count high.</b> A leaked waiter is a
              permanent reduction in what this source will admit, which would look exactly
              like the database getting slower.
            */
            int depth = _perSource * _perPermit;
            int queued = _waiting.AddOrUpdate(source, 1, static (_, n) => n + 1);

            try
            {
                if (queued > depth)
                {
                    throw new ConnectionBudgetFullException(
                        $"This server already has {_perSource} requests in flight against that "
                        + $"data source and {queued - 1} more waiting, which is past the "
                        + $"{depth} it will queue. Waiting would take about "
                        + $"{queued / Math.Max(1, _perSource)} times as long as one request, so "
                        + "you are told now rather than in a while. The limit is per data source "
                        + "and exists so that one slow database cannot take the whole server "
                        + "with it (ADR-007 §4.8, N4; ADR-046 for why the queue is what is "
                        + "bounded). Retry, or raise Graticula:PerSourceConcurrency if the "
                        + "database can take more.");
                }

                tookSource =
                    await perSource.WaitAsync(_wait, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _waiting.AddOrUpdate(source, 0, static (_, n) => n > 0 ? n - 1 : 0);
            }

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
            // The same bound across every source, for the same reason. A worker whose every
            // source is busy is a worker whose queue is the only thing still growing.
            int workerDepth = _worker * _perPermit;
            int workerQueued = Interlocked.Increment(ref _waitingGlobal);

            bool tookGlobal;

            try
            {
                if (workerQueued > workerDepth)
                {
                    throw new ConnectionBudgetFullException(
                        $"This worker already has its full budget of {_worker} database requests "
                        + $"in flight and {workerQueued - 1} more waiting, which is past the "
                        + $"{workerDepth} it will queue. You are told now rather than after a "
                        + "wait that would not have helped (ADR-007 §4.8; ADR-046). Retry, or "
                        + "raise Graticula:ConnectionBudget.");
                }

                tookGlobal =
                    await _global.WaitAsync(_wait, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _waitingGlobal);
            }

            if (!tookGlobal)
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
