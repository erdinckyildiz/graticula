using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Tiles;
using Graticula.Tiles.Packages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Claims tile export jobs and writes a service's tiles into a package — a VTPK or a PMTiles archive — ADR-098.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seed's machinery with a file at the end.</b> The claim, the lease and the sweep are
/// <see cref="TileSeeder"/>'s; the walk is <see cref="TileSeedRun"/> — the same order, batches, pauses on a source
/// outage and checkpoint that learns of a cancel; and each tile is <see cref="TileSeeder.ServiceTileAsync"/>, the loop
/// a seed runs, so a cached tile is read and a cold one is built under the same permit a seed takes and stored where
/// the route will find it. What is added is that the bytes are kept (<see cref="TileExportStaging"/>) and written
/// into a package once the walk is over (<see cref="TileExportPackage"/>).
/// </para>
/// <para>
/// <b>Restarted, not resumed — ADR-098 §5.4.</b> A half-written zip or archive has no durable cursor, so a run that
/// loses its lease leaves nothing behind (the staging file deletes itself when closed, and a stray one is swept) and
/// the next run walks again from the first tile. It is cheap the second time: every tile the first run built is in the
/// cache. The kind is <see cref="JobRerun.Harmless"/>, so a second loss fails it.
/// </para>
/// <para>
/// <b>A tile that fails fails the export.</b> A seed counts a failed tile and goes on, because the next request builds
/// it; a package is what a device takes somewhere with no signal, and a hole in it is found there. So the walk finishes
/// — the failures are counted and the first one named — and the export is failed rather than written with holes.
/// </para>
/// </remarks>
internal sealed class TileExporter : BackgroundService
{
    /// <summary>The most bytes every live package may hold together unless a deployment says otherwise.</summary>
    public const long DefaultBudgetBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>How long a written package is kept unless a deployment says otherwise.</summary>
    public const int DefaultRetentionHours = 24;

    /// <summary>The most tiles one export may hold unless a deployment or a service says otherwise.</summary>
    /// <remarks>ArcGIS's documented default for <c>maxExportTilesCount</c>.</remarks>
    public const long DefaultMaximumTiles = 100_000;

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StrayAfter = TimeSpan.FromHours(1);

    /// <summary>What this worker calls itself when it claims a job — D-96.</summary>
    private static readonly string Who =
        "graticula/export " + Environment.MachineName + "#" + Environment.ProcessId;

    private const int Speaks = 1;

    private readonly IJobStore _jobs;
    private readonly ITileExportStore _exports;
    private readonly JobSignal _signal;
    private readonly PostgresLayerCatalog _catalog;
    private readonly ServiceContexts _contexts;
    private readonly LayerConnections _connections;
    private readonly ITileCache _cache;
    private readonly TileSingleFlight _building;
    private readonly IProjector _projector;
    private readonly DatumShiftNotices _datumShifts;
    private readonly UnindexedLayerNotices _unindexed;
    private readonly ILoggerFactory _loggers;
    private readonly GeoParquetSources _geoParquet;
    private readonly GlyphStore _glyphs;
    private readonly StyleOriginList _origins;
    private readonly IMapCanvasFactory _canvases;
    private readonly HostSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<TileExporter> _log;

    private readonly RepeatedFailure _claims = new();
    private readonly LeaseSweep _leases = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    private TimeSpan _waiting = Idle;
    private DateTimeOffset _swept = DateTimeOffset.MinValue;
    private DateTimeOffset _strays = DateTimeOffset.MinValue;

    public TileExporter(
        IJobStore jobs,
        ITileExportStore exports,
        JobSignal signal,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        LayerConnections connections,
        ITileCache cache,
        TileSingleFlight building,
        IProjector projector,
        DatumShiftNotices datumShifts,
        UnindexedLayerNotices unindexed,
        ILoggerFactory loggers,
        GeoParquetSources geoParquet,
        GlyphStore glyphs,
        StyleOriginList origins,
        IMapCanvasFactory canvases,
        HostSettings settings,
        TimeProvider clock,
        ILogger<TileExporter> log)
    {
        _jobs = jobs;
        _exports = exports;
        _signal = signal;
        _catalog = catalog;
        _contexts = contexts;
        _connections = connections;
        _cache = cache;
        _building = building;
        _projector = projector;
        _datumShifts = datumShifts;
        _unindexed = unindexed;
        _loggers = loggers;
        _geoParquet = geoParquet;
        _glyphs = glyphs;
        _origins = origins;
        _canvases = canvases;
        _settings = settings;
        _clock = clock;
        _log = log;
    }

    /// <summary>A new package token: 128 random bits as 32 lowercase hexadecimal characters.</summary>
    /// <remarks>
    /// <b>The file's name and the unguessable half of its address</b> (ADR-098 §5.7). From the operating system's
    /// cryptographic generator, because a token anyone could predict would let anyone who may read the service fetch
    /// somebody else's package without asking for it — which they could make themselves, but not at our expense.
    /// </remarks>
    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Whether a string is a token this server could have made — nothing else is ever a file name here.</summary>
    public static bool IsToken(string? token) =>
        token is { Length: 32 } && token.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// The package file of a token, inside the export directory and nowhere else — or null for anything that is not a
    /// token.
    /// </summary>
    /// <param name="directory">The export directory.</param>
    /// <param name="token">The token.</param>
    /// <param name="format">The format, for the extension.</param>
    /// <returns>The full path.</returns>
    /// <remarks>
    /// <b>No part of a request becomes a path.</b> The token is the store's, checked against <see cref="IsToken"/> —
    /// 32 characters of <c>0-9a-f</c>, so it cannot hold a separator or a dot — and the result is checked once more to
    /// lie inside the directory, which is the belt to that pair of braces.
    /// </remarks>
    public static string? FileOf(string directory, string token, TileExportFormat format) =>
        Inside(directory, token + TileExportPackage.Extension(format), token);

    /// <summary>A package being written — renamed to <see cref="FileOf"/> when it is complete.</summary>
    public static string? PartOf(string directory, string token) => Inside(directory, token + ".part", token);

    /// <summary>The staging file of an export's walk.</summary>
    public static string? StagingOf(string directory, string token) => Inside(directory, token + ".staging", token);

    internal static string? Inside(string directory, string name, string token)
    {
        if (!IsToken(token))
        {
            return null;
        }

        string root = Path.GetFullPath(directory);
        string path = Path.GetFullPath(Path.Combine(root, name));

        return path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : null;
    }

    /// <summary>Stops an export this process is running at once, rather than at its next checkpoint.</summary>
    /// <param name="job">The export.</param>
    /// <returns>True when it was running here.</returns>
    public bool Stop(Guid job)
    {
        if (!_running.TryGetValue(job, out CancellationTokenSource? stop))
        {
            return false;
        }

        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    /// <summary>Deletes an export's files, whatever state they are in; true when none is left.</summary>
    public bool DeleteFiles(string token, ILogger? log = null)
    {
        bool clean = true;

        foreach (string? path in (IEnumerable<string?>)
                 [
                     FileOf(_settings.TileExportDirectory, token, TileExportFormat.Vtpk),
                     FileOf(_settings.TileExportDirectory, token, TileExportFormat.PmTiles),
                     PartOf(_settings.TileExportDirectory, token),
                     StagingOf(_settings.TileExportDirectory, token),
                 ])
        {
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // <b>A staging file still open is its own run's</b>, and it deletes itself when that run closes it; a
                // package a download is reading is tried again by the sweep.
                if (path.EndsWith(".staging", StringComparison.Ordinal))
                {
                    continue;
                }

                Log.ExportFileStuck(log ?? _log, path, e.Message);
                clean = false;
            }
        }

        return clean;
    }

    /// <summary>
    /// How many bytes a service's tiles over a plan are expected to take in a package, and whether every level's
    /// figure came from the cache's own tiles.
    /// </summary>
    /// <remarks>
    /// <b>ADR-093's estimate, counting every tile.</b> <see cref="TileSeedEstimate.Of"/> leaves out tiles already cached
    /// because a seed adds nothing for them; an export writes every tile, cached or not, so each layer's holding is
    /// taken with nothing present. The per-level average from the cache — or the default guess where it holds fewer
    /// than sixteen — is the same number the seed uses.
    /// </remarks>
    internal static async Task<(long TileBytes, bool Sampled)> TileBytesAsync(
        PublishedService service,
        TileSeedPlan plan,
        ServiceContexts contexts,
        ITileCache cache,
        GeoParquetSources geoParquet,
        CancellationToken cancellationToken)
    {
        List<TileSeedEstimate.Layer> layers = [];

        foreach (PublishedLayer layer in service.Layers)
        {
            IReadOnlyDictionary<int, TileSeedEstimate.Holding> held = new Dictionary<int, TileSeedEstimate.Holding>();

            if (cache is FileSystemTileCache disk)
            {
                (_, LayerDescription description) = await contexts.GetAsync(layer, cancellationToken).ConfigureAwait(false);

                TileCacheKey key = VectorTileEndpoints.KeyOf(
                    layer, VectorTileEndpoints.AttributesOf(layer, description), new TileAddress(0, 0, 0), geoParquet,
                    service.TileScheme);

                held = disk.HoldingOf(key, plan.Levels)
                    .ToDictionary(pair => pair.Key, pair => pair.Value with { Present = 0 });
            }

            VisibleScaleRange range = layer.VisibleRange;
            layers.Add(new TileSeedEstimate.Layer(level => service.TileScheme.Draws(range, level), held));
        }

        TileSeedEstimate.Result result = TileSeedEstimate.Of(
            plan.Levels, layers, long.MaxValue, 0, new Dictionary<int, long>(), service.TileScheme);

        return (result.Bytes, result.Sampled);
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            Directory.CreateDirectory(_settings.TileExportDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Said when an export tries to write there, where somebody is looking.
            Log.ExportFileStuck(_log, _settings.TileExportDirectory, e.Message);
        }

        while (!stopping.IsCancellationRequested)
        {
            JobRecord? job;

            try
            {
                job = await _jobs.ClaimAsync(JobKind.TileExport, Who, Speaks, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception unreachable)
            {
                Report(unreachable);
                await Wait(stopping).ConfigureAwait(false);
                continue;
            }

            Claimed();

            if (job is null)
            {
                await _leases.SweepAsync(_jobs, _log, stopping).ConfigureAwait(false);
                await SweepAsync(stopping).ConfigureAwait(false);
                await Wait(stopping).ConfigureAwait(false);
                continue;
            }

            _waiting = Idle;

            await RunAsync(job, stopping).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes packages past their time and the files of exports that ended without one — ADR-098 §5.6 — and, once an
    /// hour, files in the directory that no live export owns.
    /// </summary>
    internal async Task SweepAsync(CancellationToken stopping)
    {
        DateTimeOffset now = _clock.GetUtcNow();

        if (now - _swept < SweepEvery)
        {
            return;
        }

        _swept = now;

        try
        {
            foreach ((Guid job, string token) in
                     await _exports.DueForRemovalAsync(now, 100, stopping).ConfigureAwait(false))
            {
                // <b>Marked only when the file is gone</b>, so a package a download is still reading is tried again on
                // the next sweep rather than forgotten on disk.
                if (DeleteFiles(token) && await _exports.MarkRemovedAsync(job, stopping).ConfigureAwait(false))
                {
                    Log.ExportRemoved(_log, job);
                }
            }

            if (now - _strays >= StrayAfter)
            {
                _strays = now;
                IReadOnlySet<string> live = await _exports.LiveTokensAsync(stopping).ConfigureAwait(false);

                DeleteStrays(live, now);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    /// <summary>
    /// Deletes files an hour old whose token no live export owns — a package whose row went with its service, a
    /// staging file a killed process left.
    /// </summary>
    /// <remarks>
    /// <b>Only names this server makes</b> — a token and one of the four extensions — so nothing an operator put in the
    /// directory is touched. An hour's grace so a file an export on another node has just started is not taken.
    /// </remarks>
    private void DeleteStrays(IReadOnlySet<string> live, DateTimeOffset now)
    {
        if (!Directory.Exists(_settings.TileExportDirectory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(_settings.TileExportDirectory))
        {
            string name = Path.GetFileName(path);
            int dot = name.IndexOf('.', StringComparison.Ordinal);

            if (dot != 32 || !IsToken(name[..dot])
                || name[dot..] is not (".vtpk" or ".pmtiles" or ".part" or ".staging")
                || live.Contains(name[..dot]))
            {
                continue;
            }

            try
            {
                if (now - File.GetLastWriteTimeUtc(path) >= StrayAfter)
                {
                    File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.ExportFileStuck(_log, path, e.Message);
            }
        }
    }

    private async Task RunAsync(JobRecord job, CancellationToken stopping)
    {
        await using JobLeaseKeeper lease = JobLeaseKeeper.Hold(_jobs, job.Id, Who, _log, stopping);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(lease.Working);
        _running[job.Id] = stop;

        TileExportState? state = null;

        try
        {
            state = await _exports.FindAsync(job.Id, stop.Token).ConfigureAwait(false);
            await ExportAsync(job, state, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The server stopping, a lost lease or a cancel: the partial files go, and nothing is written — a cancel
            // has already said `cancelled`, and a lost lease belongs to whoever has the job now.
            if (state is not null)
            {
                DeleteFiles(state.Token);
            }

            if (!stopping.IsCancellationRequested && !lease.Lost)
            {
                Log.ExportReleased(_log, job.Id);
            }
        }
        catch (Exception failed)
        {
            if (state is not null)
            {
                DeleteFiles(state.Token);
            }

            await FailAsync(job, failed.Message, failed, stopping).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
        }
    }

    private async Task ExportAsync(JobRecord job, TileExportState? state, CancellationToken working)
    {
        if (state is null)
        {
            await FailAsync(
                job, "The service this export was for no longer exists, so there is nothing to export.", null, working)
                .ConfigureAwait(false);
            return;
        }

        (string? folder, string? name) = TileSeeder.Address(job.Detail);

        PublishedService? service = name is null
            ? null
            : await _catalog.FindServiceAsync(folder, name, working).ConfigureAwait(false);

        if (service is null || service.Id != state.ServiceId)
        {
            await FailAsync(
                job,
                $"The service '{(folder is null ? name : folder + "/" + name)}' was renamed or moved after this export "
                + "was asked for. Ask for an export of it where it is now.",
                null,
                working).ConfigureAwait(false);
            return;
        }

        if (TileSeeder.WhyNotSeedable(service) is { } refusal)
        {
            await FailAsync(job, refusal, null, working).ConfigureAwait(false);
            return;
        }

        VectorTileScheme scheme = service.TileScheme;

        if (state.Scheme != scheme.Key)
        {
            await FailAsync(
                job,
                $"The tiling scheme of '{service.QualifiedName}' changed after this export was asked for, so its area and "
                + "levels are counted on a grid the service no longer has. Ask for an export of it again.",
                null,
                working).ConfigureAwait(false);
            return;
        }

        if (state.Format == TileExportFormat.PmTiles && !scheme.IsWebMercator)
        {
            await FailAsync(job, TileExportPackage.WhyNotPmTiles(service), null, working).ConfigureAwait(false);
            return;
        }

        string directory = _settings.TileExportDirectory;
        Directory.CreateDirectory(directory);

        string staged = StagingOf(directory, state.Token)
            ?? throw new InvalidOperationException("The export's token is not one this server makes.");
        string part = PartOf(directory, state.Token)!;
        string final = FileOf(directory, state.Token, state.Format)!;

        TileSeedPlan plan = TileSeedPlan
            .For(scheme, state.Area, state.Levels.Min(), state.Levels.Max())
            .Keeping(state.Levels.ToHashSet());

        // <b>From nothing, every run</b> — §5.4. A fresh set of counts, not the stored ones.
        TileSeedLevel[] fresh =
        [
            .. plan.Levels.Select(level => new TileSeedLevel(level.Z, level.Count, 0, 0, 0, 0, 0, 0, null, null)),
        ];

        TileSeedRun run = new(
            plan,
            fresh,
            _settings.TileSeedConcurrency,
            z => service.Layers.Any(layer => scheme.Draws(layer.VisibleRange, z)),
            TileSeeder.OutageOf,
            _clock);

        TimeSpan defaultLifetime = _cache is FileSystemTileCache disk ? disk.DefaultLifetime : TimeSpan.FromHours(1);

        string format = PostgresTileExportStore.Wire(state.Format);

        Log.ExportStarted(_log, job.Id, service.QualifiedName, format, plan.Total, plan.Levels[0].Z, plan.Levels[^1].Z);

        using TileExportStaging staging = new(staged);

        long failures = 0;
        string? firstFailure = null;

        TileSeedEnd end = await run.RunAsync(
            async (address, token) =>
            {
                (TileSeedOutcome outcome, byte[] tile) = await TileSeeder.ServiceTileAsync(
                        service, address, defaultLifetime, _contexts, _connections, _cache, _building, _projector,
                        _datumShifts, _unindexed, _loggers, _geoParquet, token)
                    .ConfigureAwait(false);

                await staging.AddAsync(address, tile, token).ConfigureAwait(false);

                return outcome;
            },
            (checkpoint, token) =>
            {
                // A tile built after one that met an outage is kept before the cursor passes it (§5.4), so what is
                // kept can briefly run ahead of what is done; the row says no more than is done.
                long done = checkpoint.Levels.Sum(level => level.Done);

                return _exports.CheckpointAsync(
                    job.Id, Who, done, Math.Min(done, staging.Count), checkpoint.PausedUntil, checkpoint.PausedBecause,
                    Math.Min(99, checkpoint.Percent), token);
            },
            (address, failure) =>
            {
                if (Interlocked.Increment(ref failures) == 1)
                {
                    firstFailure = $"tile {address}: {failure.Message}";
                    Log.ExportTileFailed(_log, job.Id, address.ToString(), failure.Message, failure);
                }
            },
            working).ConfigureAwait(false);

        if (end == TileSeedEnd.Released)
        {
            DeleteFiles(state.Token);
            Log.ExportReleased(_log, job.Id);
            return;
        }

        if (failures > 0)
        {
            DeleteFiles(state.Token);
            await FailAsync(
                job,
                $"{failures:N0} of {plan.Total:N0} tiles could not be built, and a package with holes in it would be "
                + $"found out offline. The first was {firstFailure} Export again when the source is sound.",
                null,
                working).ConfigureAwait(false);
            return;
        }

        DateTimeOffset now = _clock.GetUtcNow();

        await using (FileStream output = new(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            if (state.Format == TileExportFormat.Vtpk)
            {
                TileExportPackage.VtpkDocuments documents = await TileExportPackage.VtpkDocumentsAsync(
                        service, plan, job.Id, now, _contexts, _projector, _glyphs, _origins, _catalog, _canvases, working)
                    .ConfigureAwait(false);

                await TileExportPackage.WriteVtpkAsync(output, staging, documents, working).ConfigureAwait(false);
            }
            else
            {
                PmTilesDescription description = await TileExportPackage.PmTilesDescriptionAsync(
                        service, plan, _contexts, working)
                    .ConfigureAwait(false);

                await TileExportPackage.WritePmTilesAsync(output, staging, description, working).ConfigureAwait(false);
            }

            await output.FlushAsync(working).ConfigureAwait(false);
        }

        File.Move(part, final, overwrite: true);

        long bytes = new FileInfo(final).Length;

        if (!await _exports.FinishAsync(job.Id, Who, bytes, staging.Count, _settings.TileExportRetention, working)
                .ConfigureAwait(false))
        {
            // Cancelled while the package was being written: nobody will be given its address.
            DeleteFiles(state.Token);
            Log.ExportReleased(_log, job.Id);
            return;
        }

        Log.ExportFinished(_log, job.Id, service.QualifiedName, staging.Count, bytes, now + _settings.TileExportRetention);
    }

    private async Task FailAsync(JobRecord job, string why, Exception? cause, CancellationToken token)
    {
        try
        {
            await _jobs.FinishAsync(job.Id, JobStatus.Failed, null, why, token, Who).ConfigureAwait(false);
        }
        catch (Exception unwritable) when (unwritable is not OperationCanceledException)
        {
            Log.ExportFailed(_log, job.Id, why, unwritable);
            return;
        }

        Log.ExportFailed(_log, job.Id, why, cause);
    }

    private void Report(Exception unreachable)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        switch (_claims.Failed(unreachable.Message, now))
        {
            case RepeatedFailure.Action.InFull:
                Log.ExporterClaimFailed(_log, unreachable);
                break;

            case RepeatedFailure.Action.Summarise:
                Log.ClaimStillFailing(_log, _claims.Times, _claims.For(now).TotalMinutes, unreachable.Message);
                break;

            default:
                break;
        }
    }

    private void Claimed()
    {
        int failures = _claims.Recovered(DateTimeOffset.UtcNow, out TimeSpan over);

        if (failures > 0)
        {
            Log.ClaimRecovered(_log, failures, over.TotalMinutes);
        }
    }

    private async Task Wait(CancellationToken stopping)
    {
        bool woken = await _signal.WaitAsync(JobKind.TileExport, _waiting, stopping).ConfigureAwait(false);

        _waiting = woken ? Idle : TimeSpan.FromTicks(Math.Min(_waiting.Ticks * 2, Patience.Ticks));
    }

    /// <summary>The detail an export's job carries: the service's address and what was asked — ADR-011 condition 4.</summary>
    internal static string Detail(PublishedService service, TileExportFormat format, IReadOnlyList<int> levels, long tiles) =>
        JsonSerializer.Serialize(new
        {
            folder = service.Folder,
            service = service.Name,
            format = PostgresTileExportStore.Wire(format),
            levels,
            tiles,
        });
}
