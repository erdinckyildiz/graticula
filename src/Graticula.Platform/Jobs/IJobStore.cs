using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Jobs;

/// <summary>What kind of long work a job is.</summary>
/// <remarks>
/// <b>One value, and the enumeration exists anyway.</b> The alternative is a bare string, and
/// <see href="../../../docs/architecture-debt.md">D-74</see> is about what happens when a set of
/// values has no single place that names them all: five parsers were left behind by one new sharing
/// scope. A closed set with a check constraint beside it costs nothing now and is the thing that makes
/// the second value cheap.
/// </remarks>
public enum JobKind
{
    /// <summary>
    /// Reading a File Geodatabase into the datastore — [ADR-037]'s first job.
    /// </summary>
    GeodatabaseImport,
}

/// <summary>Where a job has got to.</summary>
/// <remarks>
/// <para>
/// <b>Five states, and <see cref="Cancelled"/> is the one with no way to reach it yet.</b> It is in the
/// schema because a job somebody can watch is a job somebody will want to stop, and widening a check
/// constraint later is cheaper than discovering the state was needed. **It is not offered anywhere**,
/// which is the same shape as <c>GroupJoinPolicy.Request</c>: stored, refused on write, and recorded as
/// deferred rather than left to look supported.
/// </para>
/// <para>
/// <b>There is no <c>retrying</c>.</b> A job that failed is failed; asking again is a new job with a
/// new record, because a status that means *failed once and is now running* cannot answer *when did
/// this succeed*.
/// </para>
/// </remarks>
public enum JobStatus
{
    /// <summary>Recorded, not yet picked up.</summary>
    Queued,

    /// <summary>A worker has it.</summary>
    Running,

    /// <summary>It finished and did what it said.</summary>
    Done,

    /// <summary>It stopped and <see cref="JobRecord.Failure"/> says why.</summary>
    Failed,

    /// <summary>Stopped on purpose. <b>Nothing can reach this state yet</b> — see the type's remarks.</summary>
    Cancelled,
}

/// <summary>One piece of long work, as the store holds it.</summary>
/// <param name="Id">Its identity, and its address under <c>/admin/jobs</c>.</param>
/// <param name="Kind">What sort of work.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="Progress">
/// How far along, 0–100. <b>A report rather than a promise</b> — a worker that cannot say leaves it at
/// zero, which is honest, and the store refuses a figure outside the range because a worker that
/// counted features instead of fractions would otherwise report 140%.
/// </param>
/// <param name="Owner">Whose job it is. Never null; see <see cref="IJobStore"/>.</param>
/// <param name="Subject">
/// What the work is about, in one string a person can read — for an import, the layer being made.
/// Deliberately not structured: a screen shows it, nothing parses it.
/// </param>
/// <param name="Detail">What was asked for and what came of it, as JSON, or null.</param>
/// <param name="Failure">Why it stopped, when it did.</param>
/// <param name="Created">When it was recorded.</param>
/// <param name="Started">When a worker took it, or null.</param>
/// <param name="Finished">When it stopped, either way, or null.</param>
public sealed record JobRecord(
    Guid Id,
    JobKind Kind,
    JobStatus Status,
    int Progress,
    Guid Owner,
    string? Subject,
    string? Detail,
    string? Failure,
    DateTimeOffset Created,
    DateTimeOffset? Started,
    DateTimeOffset? Finished);

/// <summary>
/// A record of long work, so a request that cannot be answered now can be answered later.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-037], and the first increment of [ADR-011] rather than its implementation.</b> ADR-011
/// describes fair-shared scheduling, an OGC API Processes surface and one engine two subsystems share.
/// **None of that is here.** What is here is a row per request with a status somebody can poll, because
/// reading a File Geodatabase takes minutes and cannot be answered on the request that asks for it.
/// </para>
/// <para>
/// <b>Naming it a record rather than a queue is the point.</b> Building the queue to hold one job type
/// would decide ADR-011's open questions by accident, which is what §82 exists to prevent and what
/// <see href="../../../docs/architecture-debt.md">D-46</see> records happening to the UI. When there is
/// a second job kind and they contend, that is the evidence the queue needs — and it will be evidence
/// rather than a guess.
/// </para>
/// <para>
/// <b>Every method takes who is asking, and the owner is not nullable.</b> A job is somebody's: the
/// status surface shows a caller their own and an administrator everybody's, which is the same two-axis
/// shape <see cref="Identity.IGroupDirectory"/> uses. A nullable owner would make *whose is this*
/// unanswerable for exactly the rows where it matters — an import that wrote a table into somebody's
/// content.
/// </para>
/// <para>
/// <b>What this interface deliberately does not have is a way to claim work.</b> No
/// <c>TakeNextAsync</c>, no lease, no visibility timeout. One process creates a job and the same
/// process runs it; a claim protocol is for competing consumers, which is the queue this is not. The
/// absence is load-bearing — adding it later is a decision, and finding it here would let somebody
/// assume the contention story had been thought through.
/// </para>
/// </remarks>
public interface IJobStore
{
    /// <summary>Records a job as queued.</summary>
    /// <param name="owner">Whose it is.</param>
    /// <param name="kind">What sort of work.</param>
    /// <param name="subject">What it is about, for a person to read.</param>
    /// <param name="detail">What was asked for, as JSON, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The record, as stored.</returns>
    Task<JobRecord> CreateAsync(
        Guid owner,
        JobKind kind,
        string? subject,
        string? detail,
        CancellationToken cancellationToken);

    /// <summary>One job, or null when there is none with that id.</summary>
    /// <param name="id">Which job.</param>
    /// <param name="asking">Who wants to know.</param>
    /// <param name="administrator">Whether they may see anybody's.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The job, or null.</returns>
    /// <remarks>
    /// <b>Null for *not yours* as well as for *not there*, and that is deliberate.</b> A 404 and a 403
    /// on a job id are the same answer to the caller who should not have it — ADR-018's reasoning for
    /// private content, applied here: a 403 on an id confirms the job exists, which turns this into a
    /// way to learn what other people are doing.
    /// </remarks>
    Task<JobRecord?> FindAsync(
        Guid id, Guid asking, bool administrator, CancellationToken cancellationToken);

    /// <summary>Jobs this caller may see, newest first.</summary>
    /// <param name="asking">Who is asking.</param>
    /// <param name="all">True to list everybody's, which the caller must have earned.</param>
    /// <param name="unfinishedOnly">True for only what is queued or running.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The jobs.</returns>
    Task<IReadOnlyList<JobRecord>> ListAsync(
        Guid asking, bool all, bool unfinishedOnly, CancellationToken cancellationToken);

    /// <summary>Marks a job as taken by a worker.</summary>
    /// <param name="id">Which job.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when it moved from queued to running.</returns>
    /// <remarks>
    /// <b>False when it was not queued, rather than an exception.</b> The caller's question is *may I
    /// run this*, and *somebody already is* is an answer to it. This is the seam a claim protocol would
    /// grow from if competing consumers ever arrive — it is not one today, because the conditional
    /// update is the whole of it.
    /// </remarks>
    Task<bool> StartAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Reports how far along a running job is.</summary>
    /// <param name="id">Which job.</param>
    /// <param name="percent">0–100. Outside that range is refused rather than clamped.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>Refused rather than clamped, because a clamp hides the bug.</b> A worker reporting 140% has
    /// counted the wrong thing, and silently storing 100 would make it look finished.
    /// </remarks>
    Task ProgressAsync(Guid id, int percent, CancellationToken cancellationToken);

    /// <summary>Marks a job finished, one way or the other.</summary>
    /// <param name="id">Which job.</param>
    /// <param name="status">
    /// <see cref="JobStatus.Done"/> or <see cref="JobStatus.Failed"/>. Anything else is refused.
    /// </param>
    /// <param name="detail">What came of it, as JSON, or null to leave what is there.</param>
    /// <param name="failure">Why it stopped, when it failed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>A failure must carry a reason.</b> A job that says only *failed* is a job nobody can act on,
    /// and the store refuses one rather than trusting every caller to remember.
    /// </remarks>
    Task FinishAsync(
        Guid id,
        JobStatus status,
        string? detail,
        string? failure,
        CancellationToken cancellationToken);
}
