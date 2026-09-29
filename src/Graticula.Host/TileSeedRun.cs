using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Graticula.Tiles;

namespace Graticula.Host;

/// <summary>What seeding one tile came to.</summary>
internal enum TileSeedOutcome
{
    /// <summary>Built and stored, with something in it.</summary>
    Built,

    /// <summary>Already cached and fresh; left as it was.</summary>
    Present,

    /// <summary>Built, and it held nothing — stored as the empty marker.</summary>
    Empty,

    /// <summary>No layer of the service draws at this level (ADR-070); nothing built.</summary>
    Skipped,
}

/// <summary>Why a seed's walk stopped.</summary>
internal enum TileSeedEnd
{
    /// <summary>Every level is done.</summary>
    Finished,

    /// <summary>
    /// A checkpoint found the job no longer running or no longer this worker's — cancelled, or taken
    /// back by the lease sweep. The walk stops and writes nothing more.
    /// </summary>
    Released,
}

/// <summary>What a checkpoint writes: every level as it stands, and whether the seed is paused.</summary>
/// <param name="Levels">Each level's progress, lowest first.</param>
/// <param name="PausedUntil">When a paused seed tries again, or null.</param>
/// <param name="PausedBecause">Why it is paused, or null.</param>
/// <param name="Percent">The whole seed's progress, 0–100.</param>
internal sealed record TileSeedCheckpoint(
    IReadOnlyList<TileSeedLevel> Levels, DateTimeOffset? PausedUntil, string? PausedBecause, int Percent);

/// <summary>
/// The walk of one seed: level by level, tile by tile in the plan's order, from where it last got to
/// — ADR-093 §5.3 and §5.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from the worker so the order, the resume, the pause and the counting can be tested
/// without a database or a tile.</b> The worker supplies two functions — build one tile, write a
/// checkpoint — and this decides everything else.
/// </para>
/// <para>
/// <b>A batch at a time, and the cursor moves only over a finished prefix.</b> Up to
/// <c>concurrency</c> tiles of one level are built together; the cursor then moves over them in
/// order and stops at the first that met a source outage. So the cursor always means *everything
/// before this is done*, which is what makes one number per level enough to resume from, and a tile
/// after the outage that did get built is found in the cache on the retry and counted as present.
/// </para>
/// <para>
/// <b>An outage pauses the seed; any other failure is counted and passed.</b> A source that is down
/// would otherwise fail every remaining tile in seconds and leave a seed that reports ninety
/// thousand failures for one event. The pause starts at <see cref="FirstPause"/>, doubles to
/// <see cref="LongestPause"/>, and resets on the first tile that works — the shape
/// <c>GeodatabaseInspector</c>'s idle wait has, for the reason D-110 gives.
/// </para>
/// </remarks>
internal sealed class TileSeedRun
{
    /// <summary>The first wait after a source outage.</summary>
    public static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between attempts while a source stays out.</summary>
    /// <remarks>
    /// <b>Five minutes</b>: a seed is background work and there is no caller waiting on it, so what
    /// matters is that it stops asking a sick database every few seconds and still notices within
    /// minutes that it is back.
    /// </remarks>
    public static readonly TimeSpan LongestPause = TimeSpan.FromMinutes(5);

    /// <summary>How often the walk writes where it has got to, at most.</summary>
    /// <remarks>
    /// <b>Two seconds, and it bounds two things.</b> A crash loses at most two seconds of counts —
    /// the tiles themselves are in the cache and are found there on the resume — and a cancellation
    /// from another server is noticed within two seconds, since the checkpoint is where a worker
    /// learns the job is no longer running. A write per tile would be a write per 20 ms against the
    /// platform store for nobody's benefit.
    /// </remarks>
    public static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(2);

    private readonly TileSeedPlan _plan;
    private readonly Counts[] _levels;
    private readonly int _concurrency;
    private readonly Func<int, bool> _drawsAt;
    private readonly Func<Exception, string?> _outage;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    /// <summary>Sets up a walk.</summary>
    /// <param name="plan">The levels and their rectangles.</param>
    /// <param name="progress">Each level's progress as stored, lowest first, one per level of the plan.</param>
    /// <param name="concurrency">How many tiles to build at once.</param>
    /// <param name="drawsAt">Whether any layer of the service draws at a level — ADR-070's rule.</param>
    /// <param name="outage">Why a failure is a source outage, or null when it is not one.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="wait">How to wait out a pause; a test passes one that does not sleep.</param>
    public TileSeedRun(
        TileSeedPlan plan,
        IReadOnlyList<TileSeedLevel> progress,
        int concurrency,
        Func<int, bool> drawsAt,
        Func<Exception, string?> outage,
        TimeProvider clock,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(drawsAt);
        ArgumentNullException.ThrowIfNull(outage);
        ArgumentNullException.ThrowIfNull(clock);

        if (progress.Count != plan.Levels.Count)
        {
            throw new ArgumentException(
                $"The seed was recorded with {progress.Count} levels and its plan has {plan.Levels.Count}.",
                nameof(progress));
        }

        _plan = plan;
        _concurrency = Math.Max(1, concurrency);
        _drawsAt = drawsAt;
        _outage = outage;
        _clock = clock;
        _wait = wait ?? ((span, token) => Task.Delay(span, clock, token));

        _levels = new Counts[progress.Count];

        for (int i = 0; i < progress.Count; i++)
        {
            TileSeedLevel stored = progress[i];

            // <b>The stored rectangle is checked against the recomputed one</b>, because the plan is
            // derived again from the stored area on every run and a resumed cursor means nothing
            // against a different rectangle.
            if (stored.Zoom != plan.Levels[i].Z || stored.Total != plan.Levels[i].Count)
            {
                throw new ArgumentException(
                    $"Level {stored.Zoom} was recorded with {stored.Total} tiles and the area now gives "
                    + $"level {plan.Levels[i].Z} {plan.Levels[i].Count}. The seed cannot resume against a "
                    + "different grid.",
                    nameof(progress));
            }

            _levels[i] = new Counts(stored);
        }
    }

    /// <summary>Each level's progress as the walk holds it now.</summary>
    public IReadOnlyList<TileSeedLevel> Levels => [.. _levels.Select(level => level.Snapshot())];

    /// <summary>How many times the walk has paused for an outage.</summary>
    public int Pauses { get; private set; }

    /// <summary>
    /// Walks the seed from where it got to.
    /// </summary>
    /// <param name="tile">Seeds one tile, or throws.</param>
    /// <param name="checkpoint">Writes a checkpoint; false means the job is no longer this worker's.</param>
    /// <param name="failed">Told about each tile that failed for a reason other than an outage.</param>
    /// <param name="cancellationToken">The server stopping, the lease lost, or a cancel on this node.</param>
    /// <returns>Why it stopped.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing more is written.</exception>
    public async Task<TileSeedEnd> RunAsync(
        Func<TileAddress, CancellationToken, Task<TileSeedOutcome>> tile,
        Func<TileSeedCheckpoint, CancellationToken, Task<bool>> checkpoint,
        Action<TileAddress, Exception> failed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(failed);

        DateTimeOffset written = _clock.GetUtcNow();
        TimeSpan pause = FirstPause;

        for (int i = 0; i < _levels.Length; i++)
        {
            Counts level = _levels[i];
            TileRange range = _plan.Levels[i];

            // <b>A level no layer draws at is skipped whole, and is not walked.</b> Serving answers
            // every tile of it empty without a build or a cache entry (ADR-070), so there is nothing
            // to fill; walking it tile by tile to find that out would be the waste the rule exists
            // to prevent. It is still counted, so the level's figures add up to its total.
            if (level.Done < level.Total && !_drawsAt(range.Z))
            {
                level.Skipped += level.Total - level.Done;
                level.Done = level.Total;

                if (!await checkpoint(Checkpoint(null, null), cancellationToken).ConfigureAwait(false))
                {
                    return TileSeedEnd.Released;
                }

                written = _clock.GetUtcNow();
                continue;
            }

            while (level.Done < level.Total)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int batch = (int)Math.Min(_concurrency, level.Total - level.Done);
                long from = level.Done;

                Task<(TileSeedOutcome? Outcome, Exception? Failure)>[] work = new Task<(TileSeedOutcome?, Exception?)>[batch];

                for (int k = 0; k < batch; k++)
                {
                    work[k] = Attempt(tile, range.At(from + k), cancellationToken);
                }

                (TileSeedOutcome? Outcome, Exception? Failure)[] results =
                    await Task.WhenAll(work).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                string? outage = null;

                for (int k = 0; k < batch; k++)
                {
                    (TileSeedOutcome? outcome, Exception? failure) = results[k];

                    if (failure is not null && _outage(failure) is { } why)
                    {
                        // The cursor stops here; this tile and the rest of the batch are tried again.
                        outage = why;
                        break;
                    }

                    if (failure is not null)
                    {
                        level.Failed++;
                        failed(range.At(from + k), failure);
                    }
                    else
                    {
                        level.Count(outcome!.Value);
                    }

                    level.Done++;
                }

                if (outage is not null)
                {
                    Pauses++;

                    DateTimeOffset until = _clock.GetUtcNow() + pause;

                    if (!await checkpoint(Checkpoint(until, outage), cancellationToken).ConfigureAwait(false))
                    {
                        return TileSeedEnd.Released;
                    }

                    await _wait(pause, cancellationToken).ConfigureAwait(false);

                    pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, LongestPause.Ticks));

                    // Cleared once the wait is over, so the read-back does not say *paused* while it
                    // is trying again.
                    if (!await checkpoint(Checkpoint(null, null), cancellationToken).ConfigureAwait(false))
                    {
                        return TileSeedEnd.Released;
                    }

                    written = _clock.GetUtcNow();
                    continue;
                }

                pause = FirstPause;

                bool levelOver = level.Done == level.Total;

                if (levelOver || _clock.GetUtcNow() - written >= CheckpointEvery)
                {
                    if (!await checkpoint(Checkpoint(null, null), cancellationToken).ConfigureAwait(false))
                    {
                        return TileSeedEnd.Released;
                    }

                    written = _clock.GetUtcNow();
                }
            }
        }

        return TileSeedEnd.Finished;
    }

    private static async Task<(TileSeedOutcome? Outcome, Exception? Failure)> Attempt(
        Func<TileAddress, CancellationToken, Task<TileSeedOutcome>> tile,
        TileAddress address,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await tile(address, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            return (null, failure);
        }
    }

    private TileSeedCheckpoint Checkpoint(DateTimeOffset? until, string? because)
    {
        long done = _levels.Sum(level => level.Done);

        int percent = _plan.Total == 0
            ? 100
            : (int)Math.Clamp(done * 100 / _plan.Total, 0, 100);

        return new TileSeedCheckpoint(Levels, until, because, percent);
    }

    /// <summary>One level's figures, mutable while the walk owns them.</summary>
    private sealed class Counts(TileSeedLevel stored)
    {
        public int Zoom { get; } = stored.Zoom;

        public long Total { get; } = stored.Total;

        public long Done { get; set; } = stored.Done;

        public long Built { get; set; } = stored.Built;

        public long Present { get; set; } = stored.Present;

        public long Empty { get; set; } = stored.Empty;

        public long Failed { get; set; } = stored.Failed;

        public long Skipped { get; set; } = stored.Skipped;

        public void Count(TileSeedOutcome outcome)
        {
            switch (outcome)
            {
                case TileSeedOutcome.Built:
                    Built++;
                    break;
                case TileSeedOutcome.Present:
                    Present++;
                    break;
                case TileSeedOutcome.Empty:
                    Empty++;
                    break;
                case TileSeedOutcome.Skipped:
                    Skipped++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not an outcome of a tile.");
            }
        }

        // The stored times are the store's to set (PostgresTileSeedStore.CheckpointAsync), so the
        // snapshot carries none and the store keeps the ones it has.
        public TileSeedLevel Snapshot() =>
            new(Zoom, Total, Done, Built, Present, Empty, Failed, Skipped, null, null);
    }
}
