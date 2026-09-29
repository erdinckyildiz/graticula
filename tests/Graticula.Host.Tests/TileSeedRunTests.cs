using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Jobs;
using Graticula.Tiles;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A seed's walk: its order, where it resumes, what pauses it and what it counts — ADR-093 §5.3–§5.4.
/// </summary>
/// <remarks>
/// <b>Without a database or a tile.</b> The walk takes two functions — build one tile, write a
/// checkpoint — and everything these pin is decided between them, so a fake of each is the whole
/// harness. The worker's own wiring is covered by the conformance test against a live server.
/// </remarks>
public sealed class TileSeedRunTests
{
    private const double Half = TileAddress.WebMercatorHalfExtent;

    private static readonly Envelope World = new(-Half, -Half, Half, Half);

    [Fact]
    public async Task Tiles_are_walked_lowest_level_first_and_row_by_row()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 2);
        List<TileAddress> walked = [];

        TileSeedRun run = Run(plan, concurrency: 1);

        TileSeedEnd end = await run.RunAsync(
            (address, _) =>
            {
                walked.Add(address);
                return Task.FromResult(TileSeedOutcome.Built);
            },
            Always,
            Never,
            CancellationToken.None);

        Assert.Equal(TileSeedEnd.Finished, end);
        Assert.Equal(21, walked.Count);
        Assert.Equal(new TileAddress(0, 0, 0), walked[0]);
        Assert.Equal([new TileAddress(1, 0, 0), new TileAddress(1, 1, 0), new TileAddress(1, 0, 1), new TileAddress(1, 1, 1)],
            walked.Skip(1).Take(4));
        Assert.All(walked.Skip(5), address => Assert.Equal(2, address.Z));
    }

    [Fact]
    public async Task A_resumed_seed_goes_on_from_its_cursor_and_keeps_its_counts()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 2);

        // Level 0 done, three of level 1 done, level 2 untouched: what a checkpoint left behind.
        TileSeedLevel[] stored =
        [
            new(0, 1, 1, 1, 0, 0, 0, 0, null, null),
            new(1, 4, 3, 2, 1, 0, 0, 0, null, null),
            new(2, 16, 0, 0, 0, 0, 0, 0, null, null),
        ];

        List<TileAddress> walked = [];

        TileSeedRun run = new(plan, stored, 2, _ => true, _ => null, new FakeTimeProvider());

        await run.RunAsync(
            (address, _) =>
            {
                lock (walked)
                {
                    walked.Add(address);
                }

                return Task.FromResult(TileSeedOutcome.Present);
            },
            Always,
            Never,
            CancellationToken.None);

        Assert.Equal(new TileAddress(1, 1, 1), walked.OrderBy(a => a.Z).First());
        Assert.Equal(17, walked.Count);

        TileSeedLevel level1 = run.Levels[1];
        Assert.Equal(4, level1.Done);
        Assert.Equal(2, level1.Built);
        Assert.Equal(2, level1.Present);
        Assert.Equal(16, run.Levels[2].Present);
    }

    [Fact]
    public async Task A_resume_against_a_different_grid_is_refused()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 1);

        TileSeedLevel[] stored =
        [
            new(0, 1, 1, 1, 0, 0, 0, 0, null, null),
            new(1, 9, 0, 0, 0, 0, 0, 0, null, null),
        ];

        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => new TileSeedRun(plan, stored, 1, _ => true, _ => null, new FakeTimeProvider()));

        Assert.Contains("different grid", refused.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    /// <remarks>
    /// <b>A failed tile is counted and passed; the seed does not stop for it.</b> One layer's
    /// statement timing out on one dense tile says nothing about the next tile.
    /// </remarks>
    [Fact]
    public async Task A_tile_that_fails_is_counted_and_the_seed_goes_on()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 1, 1);
        List<TileAddress> told = [];

        TileSeedRun run = Run(plan, concurrency: 1);

        TileSeedEnd end = await run.RunAsync(
            (address, _) => address == new TileAddress(1, 1, 0)
                ? Task.FromException<TileSeedOutcome>(new InvalidOperationException("statement timeout"))
                : Task.FromResult(TileSeedOutcome.Empty),
            Always,
            (address, _) => told.Add(address),
            CancellationToken.None);

        Assert.Equal(TileSeedEnd.Finished, end);
        Assert.Equal([new TileAddress(1, 1, 0)], told);

        TileSeedLevel level = Assert.Single(run.Levels);
        Assert.Equal(4, level.Done);
        Assert.Equal(1, level.Failed);
        Assert.Equal(3, level.Empty);
    }

    /// <remarks>
    /// <b>An outage pauses and retries the same tile; it does not fail the rest.</b> Without this a
    /// source that goes away for a minute turns into a seed reporting every remaining tile failed.
    /// </remarks>
    [Fact]
    public async Task A_source_outage_pauses_the_seed_and_the_same_tile_is_tried_again()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 1, 1);
        int down = 3;
        List<TimeSpan> waits = [];
        List<TileSeedCheckpoint> checkpoints = [];

        TileSeedRun run = new(
            plan,
            Fresh(plan),
            1,
            _ => true,
            failure => failure is TimeoutException ? "the source could not be reached" : null,
            new FakeTimeProvider(),
            (span, _) =>
            {
                waits.Add(span);
                return Task.CompletedTask;
            });

        TileSeedEnd end = await run.RunAsync(
            (address, _) => address == new TileAddress(1, 1, 0) && down-- > 0
                ? Task.FromException<TileSeedOutcome>(new TimeoutException())
                : Task.FromResult(TileSeedOutcome.Built),
            (checkpoint, _) =>
            {
                checkpoints.Add(checkpoint);
                return Task.FromResult(true);
            },
            Never,
            CancellationToken.None);

        Assert.Equal(TileSeedEnd.Finished, end);
        Assert.Equal(3, run.Pauses);

        // Backing off: five seconds, then doubling.
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], waits);

        TileSeedLevel level = Assert.Single(run.Levels);
        Assert.Equal(4, level.Built);
        Assert.Equal(0, level.Failed);

        // The pause is written, with its reason, and cleared again before the retry.
        Assert.Contains(checkpoints, c => c.PausedBecause == "the source could not be reached" && c.PausedUntil is not null);
        Assert.Null(checkpoints[^1].PausedUntil);
    }

    /// <remarks>
    /// <b>Only the finished prefix of a batch moves the cursor.</b> With two built at once and the
    /// first meeting an outage, the second's tile is in the cache but the cursor must not pass the
    /// first — otherwise the first is never built.
    /// </remarks>
    [Fact]
    public async Task An_outage_in_a_batch_holds_the_cursor_at_the_tile_that_met_it()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 1, 1);
        bool once = true;
        List<long> doneAtPause = [];

        TileSeedRun run = new(
            plan, Fresh(plan), 2, _ => true,
            failure => failure is TimeoutException ? "away" : null,
            new FakeTimeProvider(), (_, _) => Task.CompletedTask);

        await run.RunAsync(
            (address, _) =>
            {
                if (address == new TileAddress(1, 0, 0) && once)
                {
                    once = false;
                    return Task.FromException<TileSeedOutcome>(new TimeoutException());
                }

                return Task.FromResult(TileSeedOutcome.Built);
            },
            (checkpoint, _) =>
            {
                if (checkpoint.PausedUntil is not null)
                {
                    doneAtPause.Add(checkpoint.Levels[0].Done);
                }

                return Task.FromResult(true);
            },
            Never,
            CancellationToken.None);

        Assert.Equal([0L], doneAtPause);
        Assert.Equal(4, run.Levels[0].Built);
    }

    [Fact]
    public async Task A_level_no_layer_draws_at_is_skipped_whole_without_building_anything()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 2);
        List<TileAddress> walked = [];

        TileSeedRun run = new(plan, Fresh(plan), 1, z => z != 1, _ => null, new FakeTimeProvider());

        await run.RunAsync(
            (address, _) =>
            {
                walked.Add(address);
                return Task.FromResult(TileSeedOutcome.Built);
            },
            Always,
            Never,
            CancellationToken.None);

        Assert.DoesNotContain(walked, address => address.Z == 1);
        Assert.Equal(4, run.Levels[1].Skipped);
        Assert.Equal(4, run.Levels[1].Done);
    }

    /// <remarks>
    /// <b>A checkpoint that answers *not yours* stops the walk</b> — the seed was cancelled, or its
    /// job taken back — and nothing more is built.
    /// </remarks>
    [Fact]
    public async Task A_checkpoint_that_says_the_job_is_gone_stops_the_walk()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 3);
        int built = 0;

        TileSeedRun run = Run(plan, concurrency: 1);

        TileSeedEnd end = await run.RunAsync(
            (_, _) =>
            {
                built++;
                return Task.FromResult(TileSeedOutcome.Built);
            },
            (_, _) => Task.FromResult(false),
            Never,
            CancellationToken.None);

        Assert.Equal(TileSeedEnd.Released, end);

        // Level 0's single tile, then its end-of-level checkpoint said no.
        Assert.Equal(1, built);
    }

    [Fact]
    public async Task Cancelling_the_token_stops_at_the_next_tile()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 5);
        using CancellationTokenSource stop = new();
        int built = 0;

        TileSeedRun run = Run(plan, concurrency: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.RunAsync(
            (_, _) =>
            {
                if (++built == 7)
                {
                    stop.Cancel();
                }

                return Task.FromResult(TileSeedOutcome.Built);
            },
            Always,
            Never,
            stop.Token));

        Assert.Equal(7, built);
    }

    [Fact]
    public async Task The_percentage_on_the_job_is_of_the_whole_seed()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 1);
        List<int> percents = [];

        TileSeedRun run = Run(plan, concurrency: 1);

        await run.RunAsync(
            (_, _) => Task.FromResult(TileSeedOutcome.Built),
            (checkpoint, _) =>
            {
                percents.Add(checkpoint.Percent);
                return Task.FromResult(true);
            },
            Never,
            CancellationToken.None);

        // One tile of five after level 0, all five after level 1.
        Assert.Equal([20, 100], percents);
    }

    [Theory]
    [InlineData(typeof(SourceUnreachableException))]
    [InlineData(typeof(ConnectionBudgetFullException))]
    public void The_limiter_and_the_breaker_saying_not_now_is_an_outage(Type thrown)
    {
        Exception failure = (Exception)Activator.CreateInstance(thrown)!;

        Assert.NotNull(TileSeeder.OutageOf(failure));
    }

    [Fact]
    public void An_ordinary_failure_is_not_an_outage()
    {
        Assert.Null(TileSeeder.OutageOf(new InvalidOperationException("column \"x\" does not exist")));
    }

    [Fact]
    public void A_quiesced_source_pauses_the_seed_with_the_operators_sentence()
    {
        SourceQuiescedException held = new("Taken out of service until 14:00 for a column change.", DateTimeOffset.UtcNow);

        Assert.Equal(held.Message, TileSeeder.OutageOf(held));
    }

    [Fact]
    public void Over_the_cap_the_refusal_names_the_count_and_the_highest_level_that_fits()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 6);

        // 1 + 4 + 16 + 64 + 256 = 341; with 1024 at level 5 it is 1365.
        Assert.Equal(4, AdminEndpoints.HighestLevelThatFits(plan, 500));

        string refusal = AdminEndpoints.TooManyTiles(plan, 500);

        Assert.Contains(plan.Total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), refusal, StringComparison.Ordinal);
        Assert.Contains("Levels 0 to 4 fit", refusal, StringComparison.Ordinal);
        Assert.Contains("TileSeedMaximumTiles", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void When_not_even_the_first_level_fits_the_refusal_says_so()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 10, 11);

        Assert.Null(AdminEndpoints.HighestLevelThatFits(plan, 1000));
        Assert.Contains("Level 10 alone", AdminEndpoints.TooManyTiles(plan, 1000), StringComparison.Ordinal);
    }

    [Fact]
    public void A_seeds_detail_names_the_service_it_is_for()
    {
        Assert.Equal(("hosted", "roads"), TileSeeder.Address("{\"folder\":\"hosted\",\"service\":\"roads\",\"tiles\":5}"));
        Assert.Equal(((string?)null, "roads"), TileSeeder.Address("{\"folder\":null,\"service\":\"roads\"}"));
        Assert.Equal(((string?)null, (string?)null), TileSeeder.Address("not json"));
    }

    private static TileSeedRun Run(TileSeedPlan plan, int concurrency) =>
        new(plan, Fresh(plan), concurrency, _ => true, _ => null, new FakeTimeProvider(), (_, _) => Task.CompletedTask);

    private static TileSeedLevel[] Fresh(TileSeedPlan plan) =>
        [.. plan.Levels.Select(level => new TileSeedLevel(level.Z, level.Count, 0, 0, 0, 0, 0, 0, null, null))];

    private static Task<bool> Always(TileSeedCheckpoint checkpoint, CancellationToken token) => Task.FromResult(true);

    private static void Never(TileAddress address, Exception failure) =>
        throw new Xunit.Sdk.XunitException($"Tile {address} failed and none was expected to: {failure.Message}");
}
