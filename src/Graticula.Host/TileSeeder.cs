using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Tiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Claims tile seed jobs and fills a vector tile service's cache ahead of its callers — ADR-093,
/// [ADR-010](../../docs/adr/ADR-010-caching.md) §6.
/// </summary>
/// <remarks>
/// <para>
/// <b>A job worker, as ADR-010 §6 requires, and it runs where the other two run.</b> The claim,
/// the lease and the sweep are <see cref="GeodatabaseInspector"/>'s — ADR-011 §3.2 to §3.4, over
/// the pollers' own pool (D-110) — and nothing here runs on a request thread. What is new is the
/// walk (<see cref="TileSeedRun"/>) and what one tile of it is.
/// </para>
/// <para>
/// <b>One tile of a seed is one tile of the route, built by the route's own code.</b>
/// <see cref="VectorTileEndpoints.LayerPartAsync"/> reads the cache under the route's key, builds
/// through the route's source and single-flight, and writes where the route reads; a seed adds only
/// the permit (<see cref="LayerConnections.AdmitTileBuildAsync"/>). So a seeded tile is the bytes a
/// request would have made, under the key a request looks for — ADR-093 §5.5.
/// </para>
/// </remarks>
internal sealed class TileSeeder : BackgroundService
{
    /// <summary>The most tiles one seed may cover unless a deployment says otherwise — ADR-093 §5.2.</summary>
    public const long DefaultMaximumTiles = 250_000;

    /// <summary>How many tiles one seed builds at once unless a deployment says otherwise — ADR-093 §5.5.</summary>
    public const int DefaultConcurrency = 2;

    /// <summary>The most tile failures one seed writes to the log in full; the rest are counted.</summary>
    private const int FailuresSaid = 10;

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>What this worker calls itself when it claims a job — D-96.</summary>
    private static readonly string Who =
        "graticula/seed " + Environment.MachineName + "#" + Environment.ProcessId;

    private const int Speaks = 1;

    private readonly IJobStore _jobs;
    private readonly ITileSeedStore _seeds;
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
    private readonly TimeProvider _clock;
    private readonly ILogger<TileSeeder> _log;

    private readonly RepeatedFailure _claims = new();
    private readonly LeaseSweep _leases = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    private TimeSpan _waiting = Idle;

    public TileSeeder(
        IJobStore jobs,
        ITileSeedStore seeds,
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
        TimeProvider clock,
        ILogger<TileSeeder> log)
    {
        _jobs = jobs;
        _seeds = seeds;
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
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Stops a seed this process is running, at once rather than at its next checkpoint.
    /// </summary>
    /// <param name="job">The seed.</param>
    /// <returns>True when it was running here.</returns>
    /// <remarks>
    /// <b>An acceleration, not the mechanism.</b> The cancel route marks the job cancelled in the
    /// store first; a seed on another server learns that at its next checkpoint, within
    /// <see cref="TileSeedRun.CheckpointEvery"/>. One on this server is told directly, so it stops at
    /// the tile it is on — ADR-011 §3.3's same-node nudge, applied to stopping.
    /// </remarks>
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

    /// <summary>Whether a failure is a source outage, and if so what to tell the operator.</summary>
    /// <param name="failure">What a tile's build threw.</param>
    /// <returns>The reason, or null when it is an ordinary failure of one tile.</returns>
    /// <remarks>
    /// <b>The four ways a source says *not now*</b>: quiesced by an operator (ADR-059), its breaker
    /// open, its permits taken (<see cref="ConnectionBudget"/>), or the connection itself failing as
    /// <see cref="SourceBreaker.Unreachable"/> defines it. Each pauses the seed rather than failing
    /// the tile, because every tile after it would fail for the same reason — ADR-093 §5.4.
    /// </remarks>
    internal static string? OutageOf(Exception failure) => failure switch
    {
        SourceQuiescedException quiesced => quiesced.Message,
        SourceUnreachableException => "the source's breaker is open, so it is not being asked for a while",
        ConnectionBudgetFullException => "every permit for the source is in use, so this waits rather than queues",
        _ when SourceBreaker.Unreachable(failure) => "the source could not be reached: " + failure.Message,
        _ => null,
    };

    /// <summary>
    /// Why a service's tiles cannot be seeded, or null when they can — the tile route's own tests.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <returns>The sentence, or null.</returns>
    /// <remarks>
    /// <b>The same three questions <c>VectorTileEndpoints.TileableAsync</c> asks, through the same
    /// predicates</b> — <see cref="ServiceCapabilityLimits.AllowsTiles"/> and
    /// <see cref="VectorTileEndpoints.Tileable"/> — so a service the route would refuse is refused
    /// here, and one it would serve is seeded.
    /// </remarks>
    internal static string? WhyNotSeedable(PublishedService service)
    {
        if (!service.Limits.AllowsTiles(dataSupportsIt: true))
        {
            return $"The service '{service.QualifiedName}' has its tile face turned off, so it serves no tiles to seed.";
        }

        if (service.Layers.Count == 0)
        {
            return $"The service '{service.QualifiedName}' has no layers, so there is nothing to put in a tile.";
        }

        // <b>A registered PostGIS layer seeds like a hosted one since ADR-095</b>, through the same
        // `LayerPartAsync` and a permit from its own source's budget (`AdmitTileBuildAsync` is keyed on the
        // layer's connection string), so §5.5's concurrency is per source here as it is for a request.
        if (service.Layers.FirstOrDefault(layer => !VectorTileEndpoints.Tileable(layer)) is { } untiled)
        {
            return $"Layer '{untiled.Definition.Name}' of '{service.QualifiedName}' is on a source of a kind this "
                + "server cannot encode as vector tiles, so the service has none to seed. Tiles come from hosted "
                + "data, registered PostGIS databases and GeoParquet and DuckDB sources (ADR-095, ADR-066 §9).";
        }

        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            JobRecord? job;

            try
            {
                job = await _jobs.ClaimAsync(JobKind.TileSeed, Who, Speaks, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception unreachable)
            {
                // Not the end of the loop, and not the whole outage told every tick — D-133.
                Report(unreachable);
                await Wait(stopping).ConfigureAwait(false);
                continue;
            }

            Claimed();

            if (job is null)
            {
                // The same idle-tick sweep the other two workers run — D-243. A seed lost to a
                // restart is resumable, so the sweep queues it again with its progress kept.
                await _leases.SweepAsync(_jobs, _log, stopping).ConfigureAwait(false);
                await Wait(stopping).ConfigureAwait(false);
                continue;
            }

            _waiting = Idle;

            await RunAsync(job, stopping).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(JobRecord job, CancellationToken stopping)
    {
        await using JobLeaseKeeper lease = JobLeaseKeeper.Hold(_jobs, job.Id, Who, _log, stopping);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(lease.Working);
        _running[job.Id] = stop;

        try
        {
            await SeedAsync(job, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // <b>Three ways here, and none of them writes anything.</b> The server stopping leaves the
            // job running; its lease lapses and the sweep queues it again with its cursor, so the
            // next start resumes (ADR-093 §5.4). A lost lease means somebody else has it. A cancel on
            // this node has already written `cancelled`.
            if (!stopping.IsCancellationRequested && !lease.Lost)
            {
                Log.SeedReleased(_log, job.Id);
            }
        }
        catch (Exception failed)
        {
            await FailAsync(job, failed.Message, failed, stopping).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
        }
    }

    private async Task SeedAsync(JobRecord job, CancellationToken working)
    {
        TileSeedState? state = await _seeds.FindAsync(job.Id, working).ConfigureAwait(false);

        if (state is null)
        {
            // <b>The service was deleted.</b> The seed's rows go with it (migration 61), and the job
            // row stays so the listing can say what became of it.
            await FailAsync(
                job, "The service this seed was for no longer exists, so there is nothing to seed.", null, working)
                .ConfigureAwait(false);
            return;
        }

        (string? folder, string? name) = Address(job.Detail);

        PublishedService? service = name is null
            ? null
            : await _catalog.FindServiceAsync(folder, name, working).ConfigureAwait(false);

        if (service is null || service.Id != state.ServiceId)
        {
            await FailAsync(
                job,
                $"The service '{(folder is null ? name : folder + "/" + name)}' was renamed or moved after this "
                + "seed was asked for. Ask for a seed of it where it is now.",
                null,
                working).ConfigureAwait(false);
            return;
        }

        if (WhyNotSeedable(service) is { } refusal)
        {
            await FailAsync(job, refusal, null, working).ConfigureAwait(false);
            return;
        }

        TileSeedPlan plan = TileSeedPlan.For(state.Area, state.MinZoom, state.MaxZoom);

        TileSeedRun run = new(
            plan,
            state.Levels,
            state.Concurrency,
            z => service.Layers.Any(layer => layer.VisibleRange.CarriesVectorTile(z)),
            OutageOf,
            _clock);

        TimeSpan defaultLifetime = _cache is FileSystemTileCache disk ? disk.DefaultLifetime : TimeSpan.FromHours(1);

        long already = state.Levels.Sum(level => level.Done);

        Log.SeedStarted(
            _log, job.Id, service.QualifiedName, plan.Total, state.MinZoom, state.MaxZoom, already);

        int said = 0;

        TileSeedEnd end = await run.RunAsync(
            (address, token) => TileAsync(service, address, defaultLifetime, token),
            async (checkpoint, token) =>
            {
                if (checkpoint.PausedUntil is { } until && checkpoint.PausedBecause is { } why)
                {
                    Log.SeedPaused(_log, job.Id, (until - _clock.GetUtcNow()).TotalSeconds, why);
                }

                return await _seeds.CheckpointAsync(
                    job.Id, Who, checkpoint.Levels, checkpoint.PausedUntil, checkpoint.PausedBecause,
                    checkpoint.Percent, token).ConfigureAwait(false);
            },
            (address, failure) =>
            {
                if (Interlocked.Increment(ref said) <= FailuresSaid)
                {
                    Log.SeedTileFailed(_log, job.Id, address.ToString(), failure.Message, failure);
                }
            },
            working).ConfigureAwait(false);

        if (end == TileSeedEnd.Released)
        {
            Log.SeedReleased(_log, job.Id);
            return;
        }

        IReadOnlyList<TileSeedLevel> levels = run.Levels;

        await _jobs.FinishAsync(job.Id, JobStatus.Done, null, null, working, Who).ConfigureAwait(false);

        long built = levels.Sum(level => level.Built);
        long present = levels.Sum(level => level.Present);
        long empty = levels.Sum(level => level.Empty);
        long failed = levels.Sum(level => level.Failed);
        long skipped = levels.Sum(level => level.Skipped);

        Log.SeedFinished(_log, job.Id, service.QualifiedName, built, present, empty, failed, skipped);
    }

    /// <summary>Seeds one tile of a service: every layer that draws at its level.</summary>
    private async Task<TileSeedOutcome> TileAsync(
        PublishedService service, TileAddress address, TimeSpan defaultLifetime, CancellationToken token)
    {
        bool any = false;
        bool built = false;
        long bytes = 0;

        foreach (PublishedLayer layer in service.Layers)
        {
            // ADR-070 — the test serving applies to each layer before it pays for anything.
            if (!layer.VisibleRange.CarriesVectorTile(address.Z))
            {
                continue;
            }

            any = true;

            VectorTileEndpoints.LayerPart part;

            try
            {
                part = await VectorTileEndpoints.LayerPartAsync(
                        layer, address, defaultLifetime, _contexts, _connections, _cache, _building,
                        _projector, _datumShifts, _unindexed, _loggers, _geoParquet,
                        admit: permit => _connections.AdmitTileBuildAsync(layer, permit),
                        token)
                    .ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                _connections.ObserveTileBuild(layer, failure);
                throw;
            }

            if (part.Came == VectorTileEndpoints.PartCame.Built)
            {
                _connections.ObserveTileBuild(layer, null);
            }

            built |= part.Came != VectorTileEndpoints.PartCame.Cached;
            bytes += part.Bytes.Length;
        }

        return !any ? TileSeedOutcome.Skipped
            : !built ? TileSeedOutcome.Present
            : bytes == 0 ? TileSeedOutcome.Empty
            : TileSeedOutcome.Built;
    }

    /// <summary>The service a seed's detail names.</summary>
    internal static (string? Folder, string? Name) Address(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(detail);
            JsonElement root = document.RootElement;

            string? folder = root.TryGetProperty("folder", out JsonElement f) && f.ValueKind == JsonValueKind.String
                ? f.GetString()
                : null;
            string? name = root.TryGetProperty("service", out JsonElement n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            return (string.IsNullOrWhiteSpace(folder) ? null : folder, name);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private async Task FailAsync(JobRecord job, string why, Exception? cause, CancellationToken token)
    {
        try
        {
            await _jobs.FinishAsync(job.Id, JobStatus.Failed, null, why, token, Who).ConfigureAwait(false);
        }
        catch (Exception unwritable) when (unwritable is not OperationCanceledException)
        {
            // Left running; the lease lapses and the sweep takes it back, which is worse than a
            // failed job and better than a crashed worker.
            Log.SeedRefused(_log, job.Id, why, unwritable);
            return;
        }

        Log.SeedRefused(_log, job.Id, why, cause);
    }

    private void Report(Exception unreachable)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        switch (_claims.Failed(unreachable.Message, now))
        {
            case RepeatedFailure.Action.InFull:
                Log.SeederClaimFailed(_log, unreachable);
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

    /// <summary>Waits for work — woken by <see cref="JobSignal"/>, backing off to half a minute — D-110.</summary>
    private async Task Wait(CancellationToken stopping)
    {
        bool woken = await _signal.WaitAsync(JobKind.TileSeed, _waiting, stopping).ConfigureAwait(false);

        _waiting = woken ? Idle : TimeSpan.FromTicks(Math.Min(_waiting.Ticks * 2, Patience.Ticks));
    }
}
