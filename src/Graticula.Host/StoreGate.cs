using System;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host;

/// <summary>
/// ADR-046's queue bound applied where a request's wait was measured to be: before it reaches a data source, while it
/// reads the platform store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists — ADR-046 condition 1, re-run 2026-10-31 and 2026-11-01.</b> A request under
/// <c>/rest/services</c> resolves its caller (a token's session and user type) and its service from the platform store
/// before it asks <see cref="ConnectionBudget"/> for a data-source permit, and the store's pool holds 24 connections.
/// At 480 callers Little's law put about 410 ms of every request in a queue, and the data-source queue was at its
/// bound in 0.5 % of samples: the wait was at the store's pool, where Npgsql queues without bound and nothing refuses.
/// With a source narrowed to two permits, 80 % refused, the admitted median still grew from 59 to 481 ms, because a
/// refused request had already paid the store's queue to be refused.
/// </para>
/// <para>
/// <b>What it does.</b> A request entering the store phase takes one of <see cref="Permits"/> permits — the store
/// pool's size — or waits for one; when more than <see cref="QueueDepth"/> are already waiting it is refused on
/// arrival, before authentication, with the same 503 and <c>Retry-After</c> as a data-source refusal. The permit is
/// given back the moment the request asks for a data-source permit (<see cref="ConnectionBudget.EnterAsync"/> calls
/// <see cref="Leave"/>), so a request waiting for a slow source holds nothing here and one saturated database cannot
/// stop every other service resolving its callers — ADR-007's N4 — and otherwise when the request ends.
/// </para>
/// <para>
/// <b>It counts requests, not connections</b>, for the reason <see cref="ConnectionBudget"/> gives: a request holds
/// at most one store connection at a time, so bounding requests in the store phase bounds the pool's demand and moves
/// its queue here, where it is counted and bounded.
/// </para>
/// </remarks>
internal sealed class StoreGate : IDisposable
{
    private static readonly AsyncLocal<Holder?> Current = new();

    private readonly SemaphoreSlim? _permits;
    private readonly int _permitCount;
    private readonly int _depth;
    private readonly TimeSpan _wait;
    private int _waiting;
    private long _refused;

    /// <summary>Creates the gate.</summary>
    /// <param name="permits">How many requests may be in the store phase at once; zero for no gate.</param>
    /// <param name="waitersPerPermit">How many may wait per permit before arrivals are refused (ADR-046 §5).</param>
    /// <param name="wait">How long one waits before it is refused anyway, or null for <see cref="ConnectionBudget.Default"/>.</param>
    public StoreGate(int permits, int waitersPerPermit, TimeSpan? wait = null)
    {
        _permitCount = Math.Max(0, permits);
        _permits = _permitCount > 0 ? new SemaphoreSlim(_permitCount, _permitCount) : null;
        _depth = _permitCount * Math.Max(1, waitersPerPermit);
        _wait = wait ?? ConnectionBudget.Default;
    }

    /// <summary>How many requests may be in the store phase at once, or zero when there is no gate.</summary>
    public int Permits => _permitCount;

    /// <summary>The most that may wait before an arrival is refused.</summary>
    public int QueueDepth => _depth;

    /// <summary>How many are waiting right now.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>How many requests are holding a permit right now.</summary>
    public int Inside => _permits is null ? 0 : _permitCount - _permits.CurrentCount;

    /// <summary>How many arrivals have been refused since start-up.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>
    /// Takes a permit for the request flowing through this call, or refuses it.
    /// </summary>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>What gives the permit back when the request ends, whether or not it left early.</returns>
    /// <exception cref="ConnectionBudgetFullException">The queue is past its depth, or the wait expired.</exception>
    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        if (_permits is null)
        {
            return Nothing.Instance;
        }

        // A place in the queue is taken only while one is free (compare-and-swap), so the count /admin/health reads
        // never passes the depth — the defect ADR-046 condition 3 recorded in ConnectionBudget's counter, which
        // counts a caller before refusing it — and it is given back in the finally, so a cancelled caller never
        // leaves the count high.
        int ahead;

        do
        {
            ahead = Volatile.Read(ref _waiting);

            if (ahead >= _depth)
            {
                Interlocked.Increment(ref _refused);
                throw new ConnectionBudgetFullException(
                    $"This server already has {_permitCount} requests reading its platform store and {ahead} more "
                    + $"waiting, which is the {_depth} it will queue. You are told now rather than after a wait that "
                    + "would not have helped (ADR-046). Retry shortly.");
            }
        }
        while (Interlocked.CompareExchange(ref _waiting, ahead + 1, ahead) != ahead);

        bool took;

        try
        {
            took = await _permits.WaitAsync(_wait, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        if (!took)
        {
            Interlocked.Increment(ref _refused);
            throw new ConnectionBudgetFullException(
                $"This server already has {_permitCount} requests reading its platform store and waited "
                + $"{_wait.TotalSeconds:0.#} s for one to finish (ADR-046). Retry shortly.");
        }

        return new Holder(_permits);
    }

    /// <summary>
    /// Makes <paramref name="lease"/> the permit of the request flowing on from the caller, so that <see cref="Leave"/>
    /// further down can give it back.
    /// </summary>
    /// <param name="lease">What <see cref="EnterAsync"/> returned.</param>
    /// <remarks>
    /// <b>The caller's own frame sets it, not <see cref="EnterAsync"/>:</b> a value an async method gives an
    /// <see cref="AsyncLocal{T}"/> is gone when the method returns, so set there it would never reach the endpoint.
    /// </remarks>
    public static void Bind(IDisposable lease) => Current.Value = lease as Holder;

    /// <summary>
    /// Gives back the permit of the request flowing through this call, if it holds one — when it leaves the store
    /// phase for a data source.
    /// </summary>
    public static void Leave() => Current.Value?.Dispose();

    /// <summary>Whether the request flowing through this call holds a permit.</summary>
    internal static bool Holding => Current.Value is { Released: false };

    public void Dispose() => _permits?.Dispose();

    private sealed class Holder(SemaphoreSlim permits) : IDisposable
    {
        private int _released;

        public bool Released => Volatile.Read(ref _released) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                permits.Release();
            }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
