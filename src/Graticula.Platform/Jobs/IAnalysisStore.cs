using System;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Jobs;

/// <summary>
/// One run of a Spatial Analysis Service task — ADR-222: the job, the task, the parameters as the caller sent them, and
/// what it answered.
/// </summary>
/// <param name="Job">The job row: status, owner, times, failure.</param>
/// <param name="Task">The task's name, as the service lists it — <c>CreateBuffers</c>.</param>
/// <param name="Parameters">The parameters as sent, a JSON object, with the layers each input resolved to beside them.</param>
/// <param name="Results">The result parameters as the job resource answers them, a JSON object; null until it ends.</param>
/// <param name="Messages">The messages, a JSON array of <c>{type, description}</c>.</param>
/// <param name="CancelAsked">Whether the caller asked for it to stop.</param>
public sealed record AnalysisRun(
    JobRecord Job,
    string Task,
    string Parameters,
    string? Results,
    string Messages,
    bool CancelAsked);

/// <summary>Where analysis runs are kept, beside the job rows — ADR-222.</summary>
/// <remarks>
/// <b>A job of its own kind, and a row of its own for what a job row has no place for</b>, as a feature export keeps its
/// format and file beside its job (ADR-106). Behind the tenant floor; whose a run is, is the job's owner, and the host
/// answers another member's id as no job, as every job route here does (ADR-037).
/// </remarks>
public interface IAnalysisStore
{
    /// <summary>Queues a run.</summary>
    /// <param name="owner">Who asked.</param>
    /// <param name="task">The task.</param>
    /// <param name="parameters">The parameters, a JSON object.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The run, queued.</returns>
    Task<AnalysisRun> CreateAsync(Guid owner, string task, string parameters, CancellationToken cancellationToken);

    /// <summary>A run, or null when there is none in this tenant.</summary>
    /// <param name="job">The job's id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The run.</returns>
    Task<AnalysisRun?> FindAsync(Guid job, CancellationToken cancellationToken);

    /// <summary>Writes what a run answered — before its job is finished, so a finished job always has them.</summary>
    /// <param name="job">The job.</param>
    /// <param name="results">The result parameters, a JSON object, or null for a run that produced none.</param>
    /// <param name="messages">The messages, a JSON array.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>When it is written.</returns>
    Task RecordAsync(Guid job, string? results, string messages, CancellationToken cancellationToken);

    /// <summary>Marks a run as asked to stop; the runner reads it between steps.</summary>
    /// <param name="job">The job.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether there was such a run.</returns>
    Task<bool> AskCancelAsync(Guid job, CancellationToken cancellationToken);
}
