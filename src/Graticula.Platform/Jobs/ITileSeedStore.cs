using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;

namespace Graticula.Platform.Jobs;

/// <summary>What a seed was asked to do, as it is recorded when it starts — ADR-093.</summary>
/// <param name="ServiceId">The service whose tiles are seeded.</param>
/// <param name="Area">The area, in Web Mercator, already clipped to the grid's square.</param>
/// <param name="Whole">True when the area is the service's whole extent rather than one the caller drew.</param>
/// <param name="Concurrency">How many tiles the seed builds at once.</param>
/// <param name="Levels">Each level's zoom and how many tiles of it the area covers, lowest first.</param>
public sealed record TileSeedRequest(
    Guid ServiceId,
    Envelope Area,
    bool Whole,
    int Concurrency,
    IReadOnlyList<(int Zoom, long Total)> Levels);

/// <summary>How far one level of a seed has got.</summary>
/// <param name="Zoom">The level.</param>
/// <param name="Total">How many tiles of the area are on it.</param>
/// <param name="Done">
/// How many are done, counted from the start of the level's rectangle in its fixed order — so this is
/// also where a resumed seed goes on from.
/// </param>
/// <param name="Built">Tiles this seed built and stored, with something in them.</param>
/// <param name="Present">Tiles already in the cache and fresh, left as they were.</param>
/// <param name="Empty">Tiles built and found to hold nothing, stored as the empty marker.</param>
/// <param name="Failed">Tiles whose build failed for a reason that was not an outage.</param>
/// <param name="Skipped">
/// Tiles no layer of the service draws at this level (ADR-070), which serving answers empty
/// without building, and so does the seed.
/// </param>
/// <param name="Started">When the seed first reached this level.</param>
/// <param name="Finished">When it finished the level, or null.</param>
/// <remarks>
/// <b>The five counts add up to <paramref name="Done"/>, and the store refuses a row where they do
/// not.</b> A progress report whose parts do not sum to its whole is one an operator stops trusting
/// the first time they add it up.
/// </remarks>
public sealed record TileSeedLevel(
    int Zoom,
    long Total,
    long Done,
    long Built,
    long Present,
    long Empty,
    long Failed,
    long Skipped,
    DateTimeOffset? Started,
    DateTimeOffset? Finished);

/// <summary>A seed, as the store holds it: the job, what was asked, and each level's progress.</summary>
/// <param name="Job">The job, whose status says whether it is queued, running, done, failed or cancelled.</param>
/// <param name="ServiceId">The service.</param>
/// <param name="MinZoom">The lowest level.</param>
/// <param name="MaxZoom">The highest level.</param>
/// <param name="Area">The area, in Web Mercator.</param>
/// <param name="Whole">Whether the area is the service's whole extent.</param>
/// <param name="Concurrency">How many tiles it builds at once.</param>
/// <param name="Total">How many tiles it covers across every level.</param>
/// <param name="PausedUntil">When a seed waiting out a source outage tries again, or null.</param>
/// <param name="PausedBecause">Why it is waiting, or null.</param>
/// <param name="Levels">Each level's progress, lowest first.</param>
public sealed record TileSeedState(
    JobRecord Job,
    Guid ServiceId,
    int MinZoom,
    int MaxZoom,
    Envelope Area,
    bool Whole,
    int Concurrency,
    long Total,
    DateTimeOffset? PausedUntil,
    string? PausedBecause,
    IReadOnlyList<TileSeedLevel> Levels);

/// <summary>What asking for a seed came to.</summary>
/// <param name="Started">The seed, queued, or null when one was already running.</param>
/// <param name="Running">The seed already queued or running for the service, when there is one.</param>
public sealed record TileSeedStart(TileSeedState? Started, Guid? Running);

/// <summary>When one level of a service was last seeded, and over what area — ADR-010 §6b.</summary>
/// <param name="Zoom">The level.</param>
/// <param name="Finished">When a seed last finished it.</param>
/// <param name="Job">Which seed that was.</param>
/// <param name="Area">The area that seed covered, in Web Mercator.</param>
public sealed record TileSeedZoom(int Zoom, DateTimeOffset Finished, Guid Job, Envelope Area);

/// <summary>
/// A tile seed's record: what was asked, how far it has got, and how to stop it — ADR-093.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside <see cref="IJobStore"/> rather than inside it, and over the same <c>job</c> row.</b> A
/// seed is a job — claimed, leased, reclaimed and listed exactly as every other kind — and what it
/// has that no other kind has is a cursor per level, which is neither a payload (ADR-011 condition 4)
/// nor something another kind would use. So the job table stays what it is, and the seed's progress
/// lives in two tables of its own that the job owns.
/// </para>
/// <para>
/// <b>Starting is here and not in <see cref="IJobStore.CreateAsync"/></b>, because a seed is refused
/// while another runs for the same service, and the check and the insert have to be one transaction
/// or two operators pressing Start together get two seeds.
/// </para>
/// </remarks>
public interface ITileSeedStore
{
    /// <summary>
    /// Records a seed as a queued job, unless one is already queued or running for the service.
    /// </summary>
    /// <param name="owner">Whose job it is.</param>
    /// <param name="request">What to seed.</param>
    /// <param name="subject">What the job is about, for a person to read.</param>
    /// <param name="detail">What was asked for, as JSON.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The seed, or the one already running.</returns>
    Task<TileSeedStart> StartAsync(
        Guid owner,
        TileSeedRequest request,
        string subject,
        string detail,
        CancellationToken cancellationToken);

    /// <summary>One seed, or null.</summary>
    /// <param name="job">The seed's job id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The seed, or null when there is none with that id.</returns>
    /// <remarks>
    /// <b>No owner filter, unlike <see cref="IJobStore.FindAsync"/>.</b> A seed is reached through
    /// its service, and the endpoint has already asked whether the caller may read or manage that
    /// service; whoever may manage a service may see and stop a seed of it that somebody else
    /// started, which is what an owner covering for a colleague needs.
    /// </remarks>
    Task<TileSeedState?> FindAsync(Guid job, CancellationToken cancellationToken);

    /// <summary>A service's seeds, newest first.</summary>
    /// <param name="service">The service.</param>
    /// <param name="limit">The most to return.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The seeds.</returns>
    Task<IReadOnlyList<TileSeedState>> ListAsync(
        Guid service, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Records how far a running seed has got, while the worker still holds it.
    /// </summary>
    /// <param name="job">The seed.</param>
    /// <param name="worker">The claimant, as it named itself.</param>
    /// <param name="levels">Every level's progress, as the worker holds it now.</param>
    /// <param name="pausedUntil">When a paused seed tries again, or null when it is not paused.</param>
    /// <param name="pausedBecause">Why it is paused, or null.</param>
    /// <param name="percent">The whole seed's progress, 0–100, for the job row.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// True while the job is still running and still this worker's. <b>False is how a worker learns
    /// that the seed was cancelled, or taken back</b>, and it stops at its next tile.
    /// </returns>
    Task<bool> CheckpointAsync(
        Guid job,
        string worker,
        IReadOnlyList<TileSeedLevel> levels,
        DateTimeOffset? pausedUntil,
        string? pausedBecause,
        int percent,
        CancellationToken cancellationToken);

    /// <summary>Stops a queued or running seed.</summary>
    /// <param name="job">The seed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when it was queued or running and is now cancelled.</returns>
    /// <remarks>
    /// <b>The one way <see cref="JobStatus.Cancelled"/> is reached</b>, and it is restricted to this
    /// kind in the statement itself — ADR-093 §5.4.
    /// </remarks>
    Task<bool> CancelAsync(Guid job, CancellationToken cancellationToken);

    /// <summary>For each level, the last seed of the service that finished it — ADR-010 §6b.</summary>
    /// <param name="service">The service.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One row per level that any seed has finished, lowest first.</returns>
    Task<IReadOnlyList<TileSeedZoom>> LastSeededAsync(Guid service, CancellationToken cancellationToken);
}
