using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Jobs;

/// <summary>The eight files a service's rows are exported as — ADR-106 §5.6.</summary>
/// <remarks>
/// <b>Named for what the file is, and stored as the short token the layer export route has taken since ADR-107</b>
/// (<c>gpkg</c>, <c>fgdb</c>, <c>xlsx</c> …), so the console's one vocabulary and the job's are the same. ArcGIS
/// Online's spellings (<c>File Geodatabase</c>, <c>Feature Collection</c> …) are accepted at the door and never stored.
/// </remarks>
public enum FeatureExportFormat
{
    /// <summary>One <c>.gpkg</c>, a table per layer, in each layer's own reference.</summary>
    GeoPackage,

    /// <summary>A <c>.zip</c> holding a set of files per layer, in each layer's own reference.</summary>
    Shapefile,

    /// <summary>One <c>.xlsx</c>, a sheet per layer; attributes only, as ADR-107 built it.</summary>
    Excel,

    /// <summary>A zipped <c>.gdb</c> folder, a feature class per layer, in each layer's own reference.</summary>
    FileGeodatabase,

    /// <summary>One <c>.kml</c>, a document per layer, in WGS 84.</summary>
    Kml,

    /// <summary>A <c>.csv</c> — or a zip of one per layer — in WGS 84, with a byte-order mark.</summary>
    Csv,

    /// <summary>A <c>.geojson</c> — or a zip of one per layer — in WGS 84 (RFC 7946).</summary>
    GeoJson,

    /// <summary>An Esri JSON FeatureSet — or a zip of one per layer — in each layer's own reference.</summary>
    EsriJson,
}

/// <summary>Which half of its work a running export is in.</summary>
public enum FeatureExportPhase
{
    /// <summary>The host is reading a layer's rows into staging.</summary>
    Reading,

    /// <summary>The reader is writing the format, or the files are being zipped.</summary>
    Writing,
}

/// <summary>What a feature export was asked to do, as it is recorded when it starts — ADR-106.</summary>
/// <param name="ServiceId">The service whose layers are exported.</param>
/// <param name="Format">The file it becomes.</param>
/// <param name="Layers">The layer ids chosen, ascending and distinct.</param>
/// <param name="RowsTotal">How many rows the chosen layers held when it was asked for, counted up to the cap.</param>
/// <param name="EstimatedBytes">What the file and its staging are expected to take, held against the budget until it is written.</param>
/// <param name="Token">The file's name and the secret half of its address — 32 hexadecimal characters.</param>
/// <param name="FileName">The name a download is saved under.</param>
public sealed record FeatureExportRequest(
    Guid ServiceId,
    FeatureExportFormat Format,
    IReadOnlyList<int> Layers,
    long RowsTotal,
    long EstimatedBytes,
    string Token,
    string FileName);

/// <summary>A feature export, as the store holds it.</summary>
/// <param name="Job">The job, whose status says whether it is queued, running, done, failed or cancelled; its owner is the caller.</param>
/// <param name="ServiceId">The service.</param>
/// <param name="Format">The file format.</param>
/// <param name="Layers">The layer ids chosen.</param>
/// <param name="RowsTotal">Rows counted when it was asked for.</param>
/// <param name="RowsWritten">Rows read into staging so far in this run.</param>
/// <param name="Phase">Reading or writing.</param>
/// <param name="EstimatedBytes">The estimate held against the budget.</param>
/// <param name="Bytes">The file's size once written, or null.</param>
/// <param name="Token">The file's name — never shown to a caller who may not download it.</param>
/// <param name="FileName">The name a download is saved under.</param>
/// <param name="ExpiresAt">When a written file is removed, or null while it is not written.</param>
/// <param name="RemovedAt">When the file was removed — by its owner, by expiry or by a failure — or null.</param>
public sealed record FeatureExportState(
    JobRecord Job,
    Guid ServiceId,
    FeatureExportFormat Format,
    IReadOnlyList<int> Layers,
    long RowsTotal,
    long RowsWritten,
    FeatureExportPhase Phase,
    long EstimatedBytes,
    long? Bytes,
    string Token,
    string FileName,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RemovedAt);

/// <summary>What asking for a feature export came to.</summary>
/// <param name="Started">The export, queued, or null when it was refused.</param>
/// <param name="RefusedForSpace">
/// When the export did not fit what the exports budget has left: the bytes already held by other exports, tile
/// packages included.
/// </param>
/// <param name="AlreadyRunning">
/// When the caller already has an export queued or running: its job id. One at a time per caller (ADR-106 §5.4).
/// </param>
public sealed record FeatureExportStart(FeatureExportState? Started, long? RefusedForSpace, Guid? AlreadyRunning);

/// <summary>
/// A feature export's record: what was asked, how far it has got, where its file is, and when it goes — ADR-106.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside <see cref="ITileExportStore"/> and shaped as it is, over the same <c>job</c> row.</b> An export is a job
/// — claimed, leased, reclaimed and listed as every kind is — and what it adds is a file on disk with a lifetime.
/// </para>
/// <para>
/// <b>Starting holds the one exports budget.</b> The bytes a feature export will take are counted against everything
/// the tile exports hold too, and the check and the insert are one transaction under the lock the tile store takes, so
/// a tile package and a GeoPackage started together cannot each find room for one. <b>The same transaction refuses a
/// second export from the same caller</b>, for the same reason: two starts racing would each see none running.
/// </para>
/// </remarks>
public interface IFeatureExportStore
{
    /// <summary>Records an export as a queued job, if the caller has none running and its estimate fits.</summary>
    /// <param name="owner">Whose job it is — the caller.</param>
    /// <param name="request">What to export.</param>
    /// <param name="subject">What the job is about, for a person.</param>
    /// <param name="detail">What was asked for, as JSON.</param>
    /// <param name="budget">The most bytes live exports of every kind may hold together.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The export, or why there was none.</returns>
    Task<FeatureExportStart> StartAsync(
        Guid owner,
        FeatureExportRequest request,
        string subject,
        string detail,
        long budget,
        CancellationToken cancellationToken);

    /// <summary>One export, or null.</summary>
    /// <param name="job">The export's job id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The export.</returns>
    Task<FeatureExportState?> FindAsync(Guid job, CancellationToken cancellationToken);

    /// <summary>A service's exports, newest first.</summary>
    /// <param name="service">The service.</param>
    /// <param name="owner">Only this caller's, or null for everybody's — an administrator's view.</param>
    /// <param name="limit">The most to return.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The exports.</returns>
    Task<IReadOnlyList<FeatureExportState>> ListAsync(
        Guid service, Guid? owner, int limit, CancellationToken cancellationToken);

    /// <summary>What every live export of every kind holds or is expected to hold — the one budget.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Bytes.</returns>
    Task<long> HeldBytesAsync(CancellationToken cancellationToken);

    /// <summary>Records how far a running export has got, while the worker still holds it.</summary>
    /// <param name="job">The export.</param>
    /// <param name="worker">The claimant.</param>
    /// <param name="rowsWritten">Rows read into staging.</param>
    /// <param name="phase">Reading or writing.</param>
    /// <param name="percent">0–100.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True while the job is still running and still this worker's; false is how it learns to stop.</returns>
    Task<bool> CheckpointAsync(
        Guid job, string worker, long rowsWritten, FeatureExportPhase phase, int percent, CancellationToken cancellationToken);

    /// <summary>Marks an export written: its size, when it expires, and the job done — one statement.</summary>
    /// <param name="job">The export.</param>
    /// <param name="worker">The claimant.</param>
    /// <param name="bytes">The file's size.</param>
    /// <param name="rowsWritten">Rows written.</param>
    /// <param name="retention">How long the file is kept.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the worker still held it; false when it was cancelled meanwhile and the file must go.</returns>
    Task<bool> FinishAsync(
        Guid job, string worker, long bytes, long rowsWritten, TimeSpan retention, CancellationToken cancellationToken);

    /// <summary>Stops a queued or running export.</summary>
    /// <param name="job">The export.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when it was queued or running and is now cancelled.</returns>
    Task<bool> CancelAsync(Guid job, CancellationToken cancellationToken);

    /// <summary>Records that an export's file is gone — removed, expired or never written.</summary>
    /// <param name="job">The export.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when it was not already marked.</returns>
    Task<bool> MarkRemovedAsync(Guid job, CancellationToken cancellationToken);

    /// <summary>
    /// Exports whose file should not be on disk any more: expired, or ended without one (failed, cancelled), and not
    /// yet marked removed.
    /// </summary>
    /// <param name="now">The moment to judge expiry by.</param>
    /// <param name="limit">The most to return.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Their ids and tokens.</returns>
    Task<IReadOnlyList<(Guid Job, string Token)>> DueForRemovalAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Every token whose file is still meant to exist, for the sweep that deletes strays.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The tokens.</returns>
    Task<IReadOnlySet<string>> LiveTokensAsync(CancellationToken cancellationToken);
}
