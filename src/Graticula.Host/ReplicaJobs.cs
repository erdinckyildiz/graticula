using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>One <c>createReplica</c> or <c>synchronizeReplica</c>, from submission to its file — ADR-288.</summary>
internal sealed class ReplicaJob
{
    /// <summary>Creates a job.</summary>
    /// <param name="id">Its id, which the status URL names.</param>
    /// <param name="owner">Who asked; the only one besides an administrator who may read it, and the only one who may download.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="responseType">What it answers when done: ArcGIS's <c>esriReplicaResponseType…</c>.</param>
    /// <param name="submitted">When it was asked for.</param>
    public ReplicaJob(Guid id, Guid owner, Guid serviceId, string responseType, DateTimeOffset submitted)
    {
        Id = id;
        Owner = owner;
        ServiceId = serviceId;
        ResponseType = responseType;
        Submitted = submitted;
        Updated = submitted;
    }

    /// <summary>Its id.</summary>
    public Guid Id { get; }

    /// <summary>Who asked.</summary>
    public Guid Owner { get; }

    /// <summary>The service.</summary>
    public Guid ServiceId { get; }

    /// <summary>ArcGIS's status word: <c>Pending</c>, <c>ExportingData</c>, <c>ImportChanges</c>, <c>ExportChanges</c>,
    /// <c>Completed</c> or <c>Failed</c>.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>What it answers when done.</summary>
    public string ResponseType { get; set; }

    /// <summary>The replica's name.</summary>
    public string? ReplicaName { get; set; }

    /// <summary>The replica's id, or null for a snapshot.</summary>
    public Guid? ReplicaId { get; set; }

    /// <summary>Each layer's server generation once it is done, or null for a snapshot.</summary>
    public IReadOnlyList<ReplicaLayer>? Generations { get; set; }

    /// <summary>When it was asked for.</summary>
    public DateTimeOffset Submitted { get; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset Updated { get; set; }

    /// <summary>Its file once written, or null.</summary>
    public string? File { get; set; }

    /// <summary>The file's name when downloaded.</summary>
    public string DownloadName { get; set; } = "replica.geodatabase";

    /// <summary>Why it failed, or null.</summary>
    public string? Error { get; set; }

    /// <summary>Whether it has finished, either way.</summary>
    public bool Finished => Status is "Completed" or "Failed";
}

/// <summary>
/// The replica jobs this process is running or has finished, and their files — ADR-288.
/// </summary>
/// <remarks>
/// <para>
/// <b>In this process, not in the job table — <c>INFERRED</c>, recorded as debt.</b> A replica is written from documents
/// the request's own routes answer for the caller (their capabilities, their sharing), so its work begins inside the
/// request and cannot be resumed by another node from a row; a restart loses the jobs running and finished here, and the
/// client is told the job does not exist. The registration a finished <c>createReplica</c> made is durable; only the
/// status URL and the file are not.
/// </para>
/// <para>
/// <b>A file is kept for a day</b>, under <c>&lt;tile exports&gt;/replicas/</c>, which neither export sweep reads.
/// </para>
/// </remarks>
internal sealed class ReplicaJobs
{
    /// <summary>How long a finished job and its file are kept.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromDays(1);

    /// <summary>How many jobs this process holds at once, running and finished; past it a new one is refused.</summary>
    public const int MaximumJobs = 512;

    private readonly ConcurrentDictionary<Guid, ReplicaJob> _jobs = new();
    private readonly TimeProvider _clock;

    /// <summary>Creates the registry.</summary>
    /// <param name="directory">Where the files are written.</param>
    /// <param name="clock">The clock.</param>
    public ReplicaJobs(string directory, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(clock);

        Directory = directory;
        _clock = clock;
    }

    /// <summary>Where the files are written.</summary>
    public string Directory { get; }

    /// <summary>Now.</summary>
    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>Starts a job.</summary>
    /// <param name="owner">Who asked.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="responseType">What it answers when done.</param>
    /// <returns>The job, or null when <see cref="MaximumJobs"/> are already held.</returns>
    public ReplicaJob? Start(Guid owner, Guid serviceId, string responseType)
    {
        Sweep();

        if (_jobs.Count >= MaximumJobs)
        {
            return null;
        }

        ReplicaJob job = new(Guid.NewGuid(), owner, serviceId, responseType, Now);
        _jobs[job.Id] = job;
        return job;
    }

    /// <summary>A job, or null.</summary>
    /// <param name="id">The job.</param>
    /// <returns>The job, or null.</returns>
    public ReplicaJob? Find(Guid id) => _jobs.TryGetValue(id, out ReplicaJob? job) ? job : null;

    /// <summary>The file a job writes: inside the directory, named by the job's id and nothing a caller sent.</summary>
    /// <param name="job">The job.</param>
    /// <returns>The path.</returns>
    /// <param name="extension">The file's extension: <c>.geodatabase</c>, or <c>.json</c> for a JSON replica.</param>
    public string FileFor(ReplicaJob job, string extension = ".geodatabase")
    {
        ArgumentNullException.ThrowIfNull(job);
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, job.Id.ToString("N") + extension);
    }

    /// <summary>Drops the jobs older than <see cref="Kept"/>, and their files.</summary>
    public void Sweep()
    {
        DateTimeOffset before = Now - Kept;

        foreach ((Guid id, ReplicaJob job) in _jobs)
        {
            if (job.Updated < before && job.Finished && _jobs.TryRemove(id, out _) && job.File is { } file)
            {
                TryDelete(file);
            }
        }
    }

    /// <summary>Deletes a file that may not be there.</summary>
    /// <param name="path">The file.</param>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A file that will not go now goes at the next sweep's restart, with the folder.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
