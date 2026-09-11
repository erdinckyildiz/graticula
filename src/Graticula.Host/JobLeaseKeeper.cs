using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Renews a job's lease while a worker holds it, and ends the work if the lease is lost — D-243.
/// </summary>
/// <remarks>
/// <para>
/// <b>On its own clock, beside the work rather than inside it</b> — see <see cref="JobLease"/>. A
/// worker renews every <see cref="JobLease.RenewEvery"/> whatever it is doing, so a slow layer is
/// not a lost lease and a dead process is.
/// </para>
/// <para>
/// <b>A failed renewal is not a lost lease.</b> The store being briefly away makes a renewal
/// throw; the lease outlasts three of those, and the claim loop is already the thing that reports
/// an unreachable store. Only a renewal that *answers* and says the job is no longer this
/// worker's cancels <see cref="Working"/> — ADR-011 §3.9: a partitioned worker must stop at its
/// next checkpoint rather than keep writing.
/// </para>
/// </remarks>
internal sealed class JobLeaseKeeper : IAsyncDisposable
{
    private readonly IJobStore _jobs;
    private readonly Guid _job;
    private readonly string _worker;
    private readonly TimeSpan _every;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _working;
    private readonly CancellationTokenSource _done = new();
    private readonly Task _loop;
    private int _lost;

    private JobLeaseKeeper(
        IJobStore jobs, Guid job, string worker, TimeSpan every, ILogger log, CancellationToken stopping)
    {
        _jobs = jobs;
        _job = job;
        _worker = worker;
        _every = every;
        _log = log;
        _working = CancellationTokenSource.CreateLinkedTokenSource(stopping);

        // <b>Not handed `stopping`, deliberately.</b> The loop watches that token itself, through
        // `_working`, and it has to start even when the server is already stopping — otherwise
        // `DisposeAsync` would await a task that was cancelled before it began and never ran the
        // cleanup it owns.
        _loop = Task.Run(RenewAsync, CancellationToken.None);
    }

    /// <summary>Starts renewing the lease on a job this worker has just claimed.</summary>
    /// <param name="jobs">The store.</param>
    /// <param name="job">The job.</param>
    /// <param name="worker">The claimant, as it named itself when it claimed.</param>
    /// <param name="log">Where a lost lease is said.</param>
    /// <param name="stopping">The server stopping.</param>
    /// <param name="every">How often to renew; the default is <see cref="JobLease.RenewEvery"/>, and
    /// only a test passes anything else.</param>
    /// <returns>The keeper, to be disposed when the work ends.</returns>
    public static JobLeaseKeeper Hold(
        IJobStore jobs,
        Guid job,
        string worker,
        ILogger log,
        CancellationToken stopping,
        TimeSpan? every = null) =>
        new(jobs, job, worker, every ?? JobLease.RenewEvery, log, stopping);

    /// <summary>
    /// Cancelled when the server stops or the lease is lost. The work runs under this, so either
    /// ends it at its next checkpoint.
    /// </summary>
    public CancellationToken Working => _working.Token;

    /// <summary>True once a renewal has said the job is no longer this worker's.</summary>
    public bool Lost => Volatile.Read(ref _lost) == 1;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _done.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);

        _done.Dispose();
        _working.Dispose();
    }

    private async Task RenewAsync()
    {
        using CancellationTokenSource either =
            CancellationTokenSource.CreateLinkedTokenSource(_done.Token, _working.Token);

        while (true)
        {
            try
            {
                await Task.Delay(_every, either.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool held;

            try
            {
                held = await _jobs.RenewAsync(_job, _worker, either.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (either.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // The store is away. The lease outlasts three missed renewals, and the claim
                // loop is what reports an unreachable store — saying it here too would be the
                // same outage told twice a quarter-minute, which is D-133.
                continue;
            }

            if (!held)
            {
                Volatile.Write(ref _lost, 1);
                Log.JobLeaseLost(_log, _job);

                await _working.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }
}

/// <summary>
/// Takes back the jobs whose workers stopped renewing, from a worker's idle tick — D-243.
/// </summary>
/// <remarks>
/// <b>On the idle tick, so the sweep stops when the worker does</b> — the same placement
/// <c>ImportScratch.Sweep</c> has — and at most every <see cref="JobLease.SweepEvery"/>, because
/// the tick can be every two seconds and the condition it looks for arises when a process dies.
/// </remarks>
internal sealed class LeaseSweep
{
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Sweeps, unless this worker swept within the last <see cref="JobLease.SweepEvery"/>.</summary>
    /// <param name="jobs">The store.</param>
    /// <param name="log">Where each reclaim is said.</param>
    /// <param name="stopping">The server stopping.</param>
    /// <returns>A task.</returns>
    public async Task SweepAsync(IJobStore jobs, ILogger log, CancellationToken stopping)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (now - _last < JobLease.SweepEvery)
        {
            return;
        }

        _last = now;

        IReadOnlyList<JobReclaim> taken;

        try
        {
            taken = await jobs.ReclaimAsync(stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // The claim that follows this tick meets the same store and reports it, once.
            return;
        }

        foreach (JobReclaim reclaim in taken)
        {
            Log.JobReclaimed(
                log, reclaim.Id, reclaim.Kind, reclaim.LostBy ?? "an unnamed worker", reclaim.Became);
        }
    }
}
