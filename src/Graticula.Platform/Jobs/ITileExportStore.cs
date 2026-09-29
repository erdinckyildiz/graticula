using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Catalog;

namespace Graticula.Platform.Jobs;

/// <summary>The two package formats an export writes — ADR-098 §5.2.</summary>
public enum TileExportFormat
{
    /// <summary>An ArcGIS vector tile package: compact cache V2 bundles in a zip, with its service documents.</summary>
    Vtpk,

    /// <summary>A PMTiles version 3 archive: one file, addressed by Hilbert tile id.</summary>
    PmTiles,
}

/// <summary>What an export was asked to do, as it is recorded when it starts — ADR-098.</summary>
/// <param name="ServiceId">The service whose tiles are exported.</param>
/// <param name="Format">The package it becomes.</param>
/// <param name="Levels">The levels, ascending; not necessarily a run.</param>
/// <param name="Area">The area, in the service's grid's reference, already clipped to the grid.</param>
/// <param name="Whole">True when the area is the service's whole extent.</param>
/// <param name="Total">How many tiles the levels' rectangles hold.</param>
/// <param name="EstimatedBytes">What the package is expected to take on disk, held against the budget until it is written.</param>
/// <param name="Token">The package's file name and the secret half of its download address — 32 hexadecimal characters.</param>
/// <param name="Scheme">The grid the area and levels are counted on — <c>VectorTileScheme.Key</c>.</param>
/// <param name="Origin">Where it was asked for: <c>admin</c> or <c>arcgis</c>.</param>
public sealed record TileExportRequest(
    Guid ServiceId,
    TileExportFormat Format,
    IReadOnlyList<int> Levels,
    Envelope Area,
    bool Whole,
    long Total,
    long EstimatedBytes,
    string Token,
    string Scheme,
    string Origin);

/// <summary>An export, as the store holds it.</summary>
/// <param name="Job">The job, whose status says whether it is queued, running, done, failed or cancelled.</param>
/// <param name="ServiceId">The service.</param>
/// <param name="Format">The package format.</param>
/// <param name="Levels">The levels.</param>
/// <param name="Area">The area, in the grid's reference.</param>
/// <param name="Whole">Whether the area is the service's whole extent.</param>
/// <param name="Total">Tiles the levels' rectangles hold.</param>
/// <param name="Done">Tiles walked so far in this run.</param>
/// <param name="Stored">Tiles with something in them, written into the package.</param>
/// <param name="EstimatedBytes">The estimate held against the budget.</param>
/// <param name="Bytes">The package's size once written, or null.</param>
/// <param name="Token">The package's file name — never shown to a caller who may not download it.</param>
/// <param name="Scheme">The grid's key.</param>
/// <param name="Origin">Where it was asked for.</param>
/// <param name="ExpiresAt">When a written package is removed, or null while it is not written.</param>
/// <param name="RemovedAt">When the package was removed — by its owner, by expiry or by a failure — or null.</param>
/// <param name="PausedUntil">When an export waiting out a source outage tries again, or null.</param>
/// <param name="PausedBecause">Why it is waiting, or null.</param>
public sealed record TileExportState(
    JobRecord Job,
    Guid ServiceId,
    TileExportFormat Format,
    IReadOnlyList<int> Levels,
    Envelope Area,
    bool Whole,
    long Total,
    long Done,
    long Stored,
    long EstimatedBytes,
    long? Bytes,
    string Token,
    string Scheme,
    string Origin,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RemovedAt,
    DateTimeOffset? PausedUntil,
    string? PausedBecause);

/// <summary>What asking for an export came to.</summary>
/// <param name="Started">The export, queued, or null when it was refused.</param>
/// <param name="RefusedForSpace">
/// When the export did not fit what the export budget has left: the bytes already held by other exports.
/// </param>
public sealed record TileExportStart(TileExportState? Started, long? RefusedForSpace);

/// <summary>
/// A tile export's record: what was asked, how far it has got, where its package is, and when it goes — ADR-098.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside <see cref="IJobStore"/>, over the same <c>job</c> row, for <see cref="ITileSeedStore"/>'s reason.</b>
/// An export is a job — claimed, leased, reclaimed and listed as every kind is — and what it adds is a package on
/// disk with a lifetime, which is neither a payload (ADR-011 condition 4) nor something the job table has a column
/// for.
/// </para>
/// <para>
/// <b>Starting is here, and it holds the budget.</b> The disk an export will take is counted against what every
/// other live export holds, and the check and the insert are one transaction under one lock, or two exports started
/// together could each find room for one.
/// </para>
/// </remarks>
public interface ITileExportStore
{
    /// <summary>Records an export as a queued job, if its estimate fits what the budget has left.</summary>
    /// <param name="owner">Whose job it is — the anonymous principal for an anonymous export.</param>
    /// <param name="request">What to export.</param>
    /// <param name="subject">What the job is about, for a person.</param>
    /// <param name="detail">What was asked for, as JSON.</param>
    /// <param name="budget">The most bytes live exports may hold together.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The export, or the bytes that left no room for it.</returns>
    Task<TileExportStart> StartAsync(
        Guid owner,
        TileExportRequest request,
        string subject,
        string detail,
        long budget,
        CancellationToken cancellationToken);

    /// <summary>One export, or null.</summary>
    /// <param name="job">The export's job id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The export.</returns>
    Task<TileExportState?> FindAsync(Guid job, CancellationToken cancellationToken);

    /// <summary>A service's exports, newest first.</summary>
    /// <param name="service">The service.</param>
    /// <param name="limit">The most to return.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The exports.</returns>
    Task<IReadOnlyList<TileExportState>> ListAsync(Guid service, int limit, CancellationToken cancellationToken);

    /// <summary>What every live export holds or is expected to hold — written packages at their size, the rest at their estimate.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Bytes.</returns>
    Task<long> HeldBytesAsync(CancellationToken cancellationToken);

    /// <summary>Records how far a running export has got, while the worker still holds it.</summary>
    /// <param name="job">The export.</param>
    /// <param name="worker">The claimant.</param>
    /// <param name="done">Tiles walked.</param>
    /// <param name="stored">Tiles written.</param>
    /// <param name="pausedUntil">When a paused export tries again, or null.</param>
    /// <param name="pausedBecause">Why it is paused, or null.</param>
    /// <param name="percent">0–100.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True while the job is still running and still this worker's; false is how it learns to stop.</returns>
    Task<bool> CheckpointAsync(
        Guid job,
        string worker,
        long done,
        long stored,
        DateTimeOffset? pausedUntil,
        string? pausedBecause,
        int percent,
        CancellationToken cancellationToken);

    /// <summary>Marks an export written: its size, when it expires, and the job done — one statement.</summary>
    /// <param name="job">The export.</param>
    /// <param name="worker">The claimant.</param>
    /// <param name="bytes">The package's size.</param>
    /// <param name="stored">Tiles written.</param>
    /// <param name="retention">How long the package is kept.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the worker still held it; false when it was cancelled meanwhile and the file must go.</returns>
    Task<bool> FinishAsync(
        Guid job, string worker, long bytes, long stored, TimeSpan retention, CancellationToken cancellationToken);

    /// <summary>Stops a queued or running export.</summary>
    /// <param name="job">The export.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when it was queued or running and is now cancelled.</returns>
    Task<bool> CancelAsync(Guid job, CancellationToken cancellationToken);

    /// <summary>Records that an export's package is gone — removed, expired or never written.</summary>
    /// <param name="job">The export.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when it was not already marked.</returns>
    Task<bool> MarkRemovedAsync(Guid job, CancellationToken cancellationToken);

    /// <summary>
    /// Exports whose package should not be on disk any more: expired, or ended without one (failed, cancelled),
    /// and not yet marked removed.
    /// </summary>
    /// <param name="now">The moment to judge expiry by.</param>
    /// <param name="limit">The most to return.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Their ids and tokens.</returns>
    Task<IReadOnlyList<(Guid Job, string Token)>> DueForRemovalAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Every token whose package is still meant to exist, for the sweep that deletes strays.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The tokens.</returns>
    Task<IReadOnlySet<string>> LiveTokensAsync(CancellationToken cancellationToken);

    /// <summary>Sets a service's export policy — its own columns, which the capabilities PUT never touches.</summary>
    /// <param name="service">The service.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The policy it replaced, or null when the service does not exist.</returns>
    Task<TileExportPolicy?> SetPolicyAsync(Guid service, TileExportPolicy policy, CancellationToken cancellationToken);
}
