using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Claims geodatabase inspection jobs and runs the reader against them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is one worker for one kind of work, and not ADR-011's queue.</b>
/// [ADR-011](../../docs/adr/ADR-011-job-system.md) decided fair sharing, cancellation and
/// an OGC API Processes surface; none of that is here. What is here is the smallest thing that makes a
/// geodatabase importable: claim, read, record. The claim protocol is ADR-011 §3.2's — <c>select … for
/// update skip locked</c> inside <see cref="IJobStore.ClaimAsync"/> — so a second server sharing the
/// platform database does not run the same job twice, and that property is the store's rather than
/// this class's.
/// </para>
/// <para>
/// <b>In the server's own process, spawning a child.</b> ADR-016 §2 puts the worker in its own
/// container and ADR-037 §5a already spent that boundary for the reader: what keeps GDAL out of the
/// serving process is the child process, not the image. So the loop that claims work can live here,
/// where it can be shut down with the server, and the parsing lives where it cannot touch us. **This
/// is the weaker arrangement of the two and it is chosen with the cost recorded** — a runaway job now
/// competes for this machine with the requests it is serving. D-94.
/// </para>
/// <para>
/// <b>Polling, at a period chosen for the work rather than for the queue.</b> An import is started by a
/// person uploading a file, so the interval is what they will wait before the job leaves *pending*;
/// two seconds is short enough not to read as broken and long enough that an idle server is not asking
/// a database a question every heartbeat. A notification channel would remove the wait and is a second
/// mechanism to operate — ADR-011's queue can have it.
/// </para>
/// </remarks>
internal sealed class GeodatabaseInspector : BackgroundService
{
    /// <summary>How long the reader has to list what is in an archive.</summary>
    /// <remarks>
    /// <b>Two minutes, against a measured 0.06 s.</b> The owner's three real archives listed in well
    /// under a second — `docs/research/file-geodatabase-readers.md` §8e — so this is three orders of
    /// magnitude of headroom rather than a guess at the work. It is a bound on a stuck process, not a
    /// budget for a large one, and a listing does not read the features.
    /// </remarks>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);

    private readonly IJobStore _jobs;
    private readonly GeodatabaseReader _reader;
    private readonly ImportScratch _scratch;
    private readonly ILogger<GeodatabaseInspector> _log;

    private readonly RepeatedFailure _claims = new();

    public GeodatabaseInspector(
        IJobStore jobs,
        GeodatabaseReader reader,
        ImportScratch scratch,
        ILogger<GeodatabaseInspector> log)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentNullException.ThrowIfNull(log);

        _jobs = jobs;
        _reader = reader;
        _scratch = scratch;
        _log = log;
    }


    /// <summary>
    /// Reports a failed claim: in full the first time, as a count thereafter.
    /// </summary>
    /// <remarks>
    /// See <see cref="RepeatedFailure"/> for why. The recovery line is emitted from the
    /// success path rather than here, because a recovery is a different event and an
    /// operator reads it as one.
    /// </remarks>
    private void Report(Exception unreachable)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        switch (_claims.Failed(unreachable.Message, now))
        {
            case RepeatedFailure.Action.InFull:
                Log.InspectorClaimFailed(_log, unreachable);
                break;

            case RepeatedFailure.Action.Summarise:
                Log.ClaimStillFailing(
                    _log,
                    _claims.Times,
                    _claims.For(now).TotalMinutes,
                    unreachable.Message);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Notes that a claim worked, and says so if it had been failing.
    /// </summary>
    private void Claimed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        int failures = _claims.Recovered(now, out TimeSpan over);

        if (failures > 0)
        {
            Log.ClaimRecovered(_log, failures, over.TotalMinutes);
        }
    }

    /// <summary>How many jobs this instance has run, for one test.</summary>
    /// <remarks>
    /// <b>A counter here rather than a count of processes on the machine.</b> `GeometryWorkerPool`'s
    /// own note records what the other way cost: counting processes by name also counts the ones
    /// belonging to tests running in parallel and to any server the developer happens to have up, and
    /// it failed intermittently for a year of afternoons.
    /// </remarks>
    internal int Ran => _ran;

    private int _ran;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        // <b>Nothing to do at all when the reader did not ship, and it says so once.</b> A loop that
        // claims jobs it cannot run would take each one and fail it; the endpoint refuses the upload
        // instead, so there is nothing here to claim.
        if (!_reader.Available)
        {
            Log.InspectorIdleWithoutReader(_log);
            return;
        }

        while (!stopping.IsCancellationRequested)
        {
            JobRecord? job;

            try
            {
                job = await _jobs.ClaimAsync(JobKind.GeodatabaseInspect, stopping)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception unreachable)
            {
                // <b>The platform database being unreachable must not end this loop.</b> A worker that
                // exits on the first failed claim never comes back without a restart, and the thing it
                // is waiting for — a database — is the thing most likely to be briefly away.
                //
                // <b>And it must not narrate the whole outage either, which is D-133.</b> One
                // stack trace every three seconds is 100 kB a minute of the same sentence.
                Report(unreachable);

                await Wait(stopping).ConfigureAwait(false);
                continue;
            }

            // <b>The claim worked, so if it had been failing say so.</b> Emitted here
            // rather than inside Report because a recovery is a different event: an
            // operator reads *it is back, after 338 failures over 17 minutes* as the line
            // that closes an incident, and a summary line cannot say it.
            Claimed();

            if (job is null)
            {
                // <b>Swept while there is nothing to do, and never while working.</b> An archive being
                // chosen from is younger than the patience by orders of magnitude, so this cannot take
                // one out from under a selection screen; and doing it on the idle tick rather than on a
                // timer means the sweep stops when the worker does.
                Sweep();

                await Wait(stopping).ConfigureAwait(false);
                continue;
            }

            await RunAsync(job, stopping).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(JobRecord job, CancellationToken stopping)
    {
        Interlocked.Increment(ref _ran);

        string archive = _scratch.PathFor(job.Id);
        bool kept = false;

        try
        {
            using JsonDocument answer = await _reader.AskAsync(
                new { op = "layers", archive },
                Deadline,
                stopping).ConfigureAwait(false);

            JsonElement root = answer.RootElement;

            bool ok = root.TryGetProperty("ok", out JsonElement flag) && flag.GetBoolean();

            if (!ok)
            {
                string why = root.TryGetProperty("error", out JsonElement error)
                    ? error.GetString() ?? "The reader refused the archive without saying why."
                    : "The reader refused the archive without saying why.";

                await _jobs.FinishAsync(job.Id, JobStatus.Failed, null, why, stopping)
                    .ConfigureAwait(false);

                Log.InspectRefused(_log, job.Id, why, null);
                return;
            }

            // <b>The reader's own answer, stored whole.</b> Re-shaping it here would be a second
            // description of a geodatabase's layers, and the one the reader wrote is the one that came
            // from the driver. The endpoint passes `detail` through as a string for the same reason.
            await _jobs.FinishAsync(
                job.Id, JobStatus.Done, root.GetRawText(), null, stopping).ConfigureAwait(false);

            kept = true;

            Log.InspectFinished(_log, job.Id);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // <b>Left running rather than failed.</b> The server is stopping; the job was claimed and
            // not finished, which is the state a restart can pick up again. Calling it failed would be
            // this process's opinion about work nobody has decided to abandon.
            Log.InspectAbandoned(_log, job.Id);
        }
        catch (Exception failed)
        {
            await Finish(job, failed, stopping).ConfigureAwait(false);
        }
        finally
        {
            // <b>Kept when the inspection succeeded, and only then.</b> ADR-038: the operator chooses
            // which feature classes to publish *from what this job found*, so releasing the archive here
            // would mean uploading it again to act on the answer. The import job releases it — either
            // way — and a failure releases it now, because there is nothing to choose from.
            //
            // <b>An inspection nobody acts on is swept by age</b> — `ImportScratch.Sweep`, run on this
            // worker's idle tick. Without it a browser closed on the selection screen would hold its
            // archive for ever, and the budget would eventually refuse an upload with a message about
            // jobs failing to clean up, which would not be what had happened.
            if (!kept)
            {
                _scratch.Release(archive);
            }
        }
    }

    private async Task Finish(JobRecord job, Exception failed, CancellationToken stopping)
    {
        try
        {
            await _jobs.FinishAsync(
                job.Id, JobStatus.Failed, null, failed.Message, stopping).ConfigureAwait(false);
        }
        catch (Exception unwritable)
        {
            // The job stays claimed and unfinished, which is worse than a failed job and better than
            // a crashed worker. Logged with both, because the second exception hides the first.
            Log.InspectUnrecorded(_log, job.Id, failed.Message, unwritable);
            return;
        }

        Log.InspectRefused(_log, job.Id, failed.Message, failed);
    }

    /// <summary>How often the sweep is worth running, however often the loop idles.</summary>
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);

    private DateTimeOffset _sweptAt = DateTimeOffset.MinValue;

    /// <summary>Sweeps abandoned archives, at most every ten minutes.</summary>
    /// <remarks>
    /// <b>Rate-limited because the idle tick is every two seconds.</b> Enumerating a directory 1,800
    /// times an hour to find nothing is not free, and nothing here is urgent: an archive that has sat
    /// for six hours can sit for six hours and ten minutes.
    /// </remarks>
    private void Sweep()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (now - _sweptAt < SweepEvery)
        {
            return;
        }

        _sweptAt = now;

        _scratch.Sweep(ImportScratch.Patience);
    }

    private static async Task Wait(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(Idle, stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping. The loop's own condition ends it.
        }
    }
}
