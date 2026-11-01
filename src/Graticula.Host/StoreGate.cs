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
/// <b>A burst is queued; a standing queue is refused — 2026-11-01, measured.</b> Four waiting per permit is the
/// data-source rule, where a permit is held for a whole query. Here it is held for the store phase alone — four
/// statements, a few milliseconds — so 96 waiting is a queue the store clears in tens of milliseconds, and refusing on
/// that count alone refused bursts rather than overload: three HTTP/2 map viewers firing 64 tiles each at once had
/// 27–37 % of their requests answered 503 while the whole burst was served in about 50 ms
/// (<c>benchmarks/admission-control/2026-11-01-bursts</c>). So the depth is enforced only while the queue is
/// <i>standing</i> — requests have been granted only after waiting longer than <see cref="Target"/> for at least
/// <see cref="Interval"/>, without the queue emptying, and are still being granted so: CoDel's test for a queue that is
/// not draining — and otherwise arrivals queue up to <see cref="QueueCeiling"/>, which nothing passes. A flood is a
/// standing queue within a tenth of a second and is refused on arrival at the same depth as before; a burst the store
/// clears inside the interval is served whole. <b>The price</b> is a flood's moments of not standing — its first
/// interval, and each time the store's queue empties while its callers are queued at a source — when it can queue to
/// the ceiling; the measured cost is in ADR-046 §5a.
/// </para>
/// <para>
/// <b>It counts requests, not connections</b>, for the reason <see cref="ConnectionBudget"/> gives: a request holds
/// at most one store connection at a time, so bounding requests in the store phase bounds the pool's demand and moves
/// its queue here, where it is counted and bounded.
/// </para>
/// </remarks>
internal sealed class StoreGate : IDisposable
{
    /// <summary>
    /// A grant that waited longer than this counts towards a standing queue. Above the store phase's unloaded queue at
    /// its depth (96 requests at a few milliseconds each over 24 permits, about 12 ms) and below the about 110 ms a
    /// flood's standing queue makes at the same depth on the measured fixture.
    /// </summary>
    public static readonly TimeSpan Target = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long grants have to have waited past <see cref="Target"/> before the queue is standing and its depth is
    /// enforced — CoDel's interval, and longer than the measured viewer bursts take to clear. Also how recent the last
    /// such grant must be: a mark no grant has renewed for an interval is a burst that ended, not a queue.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How many may wait per permit while the queue is not standing. The hard bound: past it an arrival is refused
    /// whatever the queue is doing, so a flood's first interval cannot queue without limit.
    /// </summary>
    /// <remarks>
    /// <b>Sixteen rather than eight, chosen against both measurements of 2026-11-01.</b> 24 + 384 holds four browsers'
    /// worth of HTTP/2 streams (Kestrel's 100 each): five viewers firing 64 at once were refused none at sixteen and
    /// 26–30 % at eight, three firing 128 none against 29–31 %. The price is a flood's admitted p95 at 480 callers —
    /// 537–625 ms at sixteen, 354–394 at eight, 314–352 with no ceiling past the depth — because while the queue is not
    /// standing it can fill to the ceiling. A burst of normal use is served; overload is still refused, a little later.
    /// </remarks>
    public const int CeilingPerPermit = 16;

    private static readonly AsyncLocal<Holder?> Current = new();

    private readonly TimeProvider _time;
    private readonly int _ceiling;
    private long _aboveSince;
    private long _lastLong;

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
    /// <param name="time">The clock a queue is judged standing by, or null for the system's.</param>
    public StoreGate(int permits, int waitersPerPermit, TimeSpan? wait = null, TimeProvider? time = null)
    {
        _permitCount = Math.Max(0, permits);
        _permits = _permitCount > 0 ? new SemaphoreSlim(_permitCount, _permitCount) : null;
        _depth = _permitCount * Math.Max(1, waitersPerPermit);
        _ceiling = Math.Max(_depth, _permitCount * CeilingPerPermit);
        _wait = wait ?? ConnectionBudget.Default;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How many requests may be in the store phase at once, or zero when there is no gate.</summary>
    public int Permits => _permitCount;

    /// <summary>The most that may wait before an arrival is refused while the queue is standing.</summary>
    public int QueueDepth => _depth;

    /// <summary>The most that may wait before an arrival is refused, standing or not.</summary>
    public int QueueCeiling => _ceiling;

    /// <summary>
    /// Whether the queue is standing: requests granted for at least <see cref="Interval"/> waited longer than
    /// <see cref="Target"/> without the queue emptying, and the last of them within an interval.
    /// </summary>
    public bool Standing => IsStanding(_time.GetTimestamp());

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
        // never passes the bound it is compared with — the defect ADR-046 condition 3 recorded in ConnectionBudget's counter, which
        // counts a caller before refusing it — and it is given back in the finally, so a cancelled caller never
        // leaves the count high.
        // <b>Past the depth only while the queue is standing, past the ceiling always</b> — see the remarks. A burst
        // into a draining queue is served; a queue that has not drained for an interval refuses at the depth it did.
        long arrived = _time.GetTimestamp();
        int ahead;

        do
        {
            ahead = Volatile.Read(ref _waiting);

            if (ahead >= _ceiling || (ahead >= _depth && IsStanding(arrived)))
            {
                Interlocked.Increment(ref _refused);
                throw new ConnectionBudgetFullException(
                    $"This server already has {_permitCount} requests reading its platform store and {ahead} more "
                    + (ahead >= _ceiling
                        ? $"waiting, which is the {_ceiling} it will queue at most. "
                        : $"waiting, past the {_depth} it queues once its queue has stopped draining. ")
                    + "You are told now rather than after a wait that would not have helped (ADR-046). Retry shortly.");
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

        Granted(arrived);

        return new Holder(_permits);
    }

    // CoDel's bookkeeping, on each grant. A long wait sets the mark (if unset) and renews it; a short one clears it, but
    // only with nobody left waiting — in a flood held at the depth an occasional quick grant is not the queue draining.
    // A grant that found a permit free waited nothing with nobody waiting, so an idle gate is never standing.
    private void Granted(long arrived)
    {
        long now = _time.GetTimestamp();

        if (_time.GetElapsedTime(arrived, now) < Target)
        {
            if (Volatile.Read(ref _waiting) == 0)
            {
                Volatile.Write(ref _aboveSince, 0);
            }
        }
        else
        {
            // A mark no long grant renewed for an interval belongs to a burst that ended: this one starts a new episode.
            long last = Volatile.Read(ref _lastLong);

            if (Volatile.Read(ref _aboveSince) == 0 || _time.GetElapsedTime(last, now) >= Interval)
            {
                Volatile.Write(ref _aboveSince, now);
            }

            Volatile.Write(ref _lastLong, now);
        }
    }

    private bool IsStanding(long now)
    {
        long since = Volatile.Read(ref _aboveSince);

        return since != 0
            && _time.GetElapsedTime(since, now) >= Interval
            && _time.GetElapsedTime(Volatile.Read(ref _lastLong), now) < Interval;
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
