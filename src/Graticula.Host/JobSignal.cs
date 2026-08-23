using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;

namespace Graticula.Host;

/// <summary>
/// A nudge from whoever enqueued a job to whichever worker is waiting for one.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-110](../../docs/architecture-debt.md): two background workers polling every two seconds
/// held eight database sessions open for ever, on a server doing nothing.</b> Measured: after
/// load stopped the pools pruned correctly, 79 backends to zero in 184 seconds, and then settled
/// at sixteen and stayed — eight of which had last run `ClaimAsync`. The minimum idle time across
/// them was two seconds, which is the poll. A pool that prunes correctly cannot prune one
/// somebody keeps knocking on.
/// </para>
/// <para>
/// <b>So the poll backs off, and this is what keeps that from costing latency.</b> A worker that
/// finds nothing waits longer each time, up to half a minute; but a job enqueued in this process
/// wakes its worker at once, so the backoff is paid only by a second node's work — which is the
/// case it exists for and the rare one.
/// </para>
/// <para>
/// <b>In-process and deliberately not `LISTEN`/`NOTIFY`.</b> A Postgres notification would reach
/// every node and would cost each worker a dedicated connection held open for ever — which is the
/// thing being repaired, arriving from the other side. §82: the concrete problem here is our own
/// polling on the machine that enqueued the work, and this solves that without adding a
/// mechanism. A second node still polls, at the backed-off rate.
/// </para>
/// <para>
/// <b>One waiter per kind, because there is one worker per kind.</b> If that stops being true
/// this becomes a fan-out, and the semaphore's count is what would have to change — recorded
/// here rather than generalised now.
/// </para>
/// </remarks>
internal sealed class JobSignal
{
    private readonly ConcurrentDictionary<JobKind, SemaphoreSlim> _waiting = new();

    /// <summary>Says a job of this kind is queued.</summary>
    /// <param name="kind">What sort of work.</param>
    /// <remarks>
    /// <b>Releases at most one pending wait and never accumulates.</b> A signal that counted up
    /// while nobody waited would make the next idle worker spin through as many empty claims as
    /// there had been enqueues.
    /// </remarks>
    public void Wake(JobKind kind)
    {
        SemaphoreSlim gate = Gate(kind);

        if (gate.CurrentCount == 0)
        {
            gate.Release();
        }
    }

    /// <summary>Waits for a job of this kind, or for the time to run out.</summary>
    /// <param name="kind">What sort of work.</param>
    /// <param name="patience">How long to wait if nobody says anything.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when woken, false when the time ran out.</returns>
    public async Task<bool> WaitAsync(
        JobKind kind, TimeSpan patience, CancellationToken cancellationToken)
    {
        try
        {
            return await Gate(kind).WaitAsync(patience, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping. The caller's loop condition ends it.
            return false;
        }
    }

    private SemaphoreSlim Gate(JobKind kind) =>
        _waiting.GetOrAdd(kind, _ => new SemaphoreSlim(0, 1));
}
