using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Jobs;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A tile seed's record: one at a time per service, progress only from its holder, cancelled only as a
/// seed, resumed after a lost lease, and read back per level — migration 61, ADR-093.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TileSeedStoreTests : PostgresFixture
{
    private const string Worker = "graticula/seed TileSeedStoreTests#1";
    private const string Other = "graticula/seed TileSeedStoreTests#2";

    private static readonly Envelope Area = new(-1000, -2000, 3000, 4000);

    [Fact]
    public async Task A_seed_starts_queued_with_one_row_per_level()
    {
        (PostgresTileSeedStore seeds, _, Guid owner, Guid service) = await ReadyAsync();

        TileSeedStart start = await seeds.StartAsync(owner, Request(service), "seeding s", "{\"service\":\"s\"}", CancellationToken.None);

        TileSeedState seed = Assert.IsType<TileSeedState>(start.Started);
        Assert.Null(start.Running);
        Assert.Equal(JobKind.TileSeed, seed.Job.Kind);
        Assert.Equal(JobStatus.Queued, seed.Job.Status);
        Assert.Equal(service, seed.ServiceId);
        Assert.Equal(21, seed.Total);
        Assert.Equal([0, 1, 2], seed.Levels.Select(level => level.Zoom));
        Assert.All(seed.Levels, level => Assert.Equal(0, level.Done));
        Assert.Equal(Area, seed.Area);
    }

    [Fact]
    public async Task A_second_seed_of_the_same_service_is_refused_while_the_first_runs()
    {
        (PostgresTileSeedStore seeds, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileSeedState first = (await seeds.StartAsync(owner, Request(service), "a", "{}", CancellationToken.None)).Started!;

        TileSeedStart second = await seeds.StartAsync(owner, Request(service), "b", "{}", CancellationToken.None);

        Assert.Null(second.Started);
        Assert.Equal(first.Job.Id, second.Running);

        // Running refuses too, and a finished one does not.
        await jobs.ClaimAsync(JobKind.TileSeed, Worker, 1, CancellationToken.None);
        Assert.Equal(first.Job.Id, (await seeds.StartAsync(owner, Request(service), "c", "{}", CancellationToken.None)).Running);

        await jobs.FinishAsync(first.Job.Id, JobStatus.Done, null, null, CancellationToken.None, Worker);
        Assert.NotNull((await seeds.StartAsync(owner, Request(service), "d", "{}", CancellationToken.None)).Started);
    }

    [Fact]
    public async Task Two_starts_at_once_make_one_seed()
    {
        (PostgresTileSeedStore seeds, _, Guid owner, Guid service) = await ReadyAsync();

        TileSeedStart[] both = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i =>
                seeds.StartAsync(owner, Request(service), $"race {i}", "{}", CancellationToken.None)));

        Assert.Single(both, start => start.Started is not null);
        Assert.Equal(5, both.Count(start => start.Running is not null));
    }

    [Fact]
    public async Task Only_the_worker_holding_a_running_seed_may_checkpoint_it()
    {
        (PostgresTileSeedStore seeds, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileSeedState seed = (await seeds.StartAsync(owner, Request(service), "a", "{}", CancellationToken.None)).Started!;

        // Queued: nobody holds it yet.
        Assert.False(await seeds.CheckpointAsync(seed.Job.Id, Worker, Progress(seed, 1), null, null, 5, CancellationToken.None));

        await jobs.ClaimAsync(JobKind.TileSeed, Worker, 1, CancellationToken.None);

        Assert.False(await seeds.CheckpointAsync(seed.Job.Id, Other, Progress(seed, 1), null, null, 5, CancellationToken.None));
        Assert.True(await seeds.CheckpointAsync(
            seed.Job.Id, Worker, Progress(seed, 4), DateTimeOffset.UtcNow.AddSeconds(5), "the source is away", 14,
            CancellationToken.None));

        TileSeedState now = (await seeds.FindAsync(seed.Job.Id, CancellationToken.None))!;

        Assert.Equal(14, now.Job.Progress);
        Assert.Equal("the source is away", now.PausedBecause);
        Assert.NotNull(now.PausedUntil);

        // Level 0 has one tile, so it is done and finished; level 1 has three of four, started only.
        Assert.Equal(1, now.Levels[0].Done);
        Assert.NotNull(now.Levels[0].Finished);
        Assert.Equal(3, now.Levels[1].Done);
        Assert.NotNull(now.Levels[1].Started);
        Assert.Null(now.Levels[1].Finished);
    }

    [Fact]
    public async Task A_level_whose_counts_do_not_add_up_is_refused()
    {
        (PostgresTileSeedStore seeds, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileSeedState seed = (await seeds.StartAsync(owner, Request(service), "a", "{}", CancellationToken.None)).Started!;
        await jobs.ClaimAsync(JobKind.TileSeed, Worker, 1, CancellationToken.None);

        TileSeedLevel[] wrong = [.. seed.Levels.Select(level => level with { Done = 1, Built = 0 })];

        await Assert.ThrowsAsync<PostgresException>(() =>
            seeds.CheckpointAsync(seed.Job.Id, Worker, wrong, null, null, 1, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_seed_stops_answering_its_worker()
    {
        (PostgresTileSeedStore seeds, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileSeedState seed = (await seeds.StartAsync(owner, Request(service), "a", "{}", CancellationToken.None)).Started!;
        await jobs.ClaimAsync(JobKind.TileSeed, Worker, 1, CancellationToken.None);

        Assert.True(await seeds.CancelAsync(seed.Job.Id, CancellationToken.None));
        Assert.False(await seeds.CancelAsync(seed.Job.Id, CancellationToken.None));

        TileSeedState now = (await seeds.FindAsync(seed.Job.Id, CancellationToken.None))!;
        Assert.Equal(JobStatus.Cancelled, now.Job.Status);
        Assert.NotNull(now.Job.Finished);

        Assert.False(await seeds.CheckpointAsync(seed.Job.Id, Worker, Progress(seed, 1), null, null, 5, CancellationToken.None));
        Assert.False(await jobs.RenewAsync(seed.Job.Id, Worker, CancellationToken.None));

        // And the service may be seeded again.
        Assert.NotNull((await seeds.StartAsync(owner, Request(service), "b", "{}", CancellationToken.None)).Started);
    }

    [Fact]
    public async Task Cancelling_reaches_only_a_seed()
    {
        (PostgresTileSeedStore seeds, PostgresJobStore jobs, Guid owner, _) = await ReadyAsync();

        JobRecord import = await jobs.CreateAsync(owner, JobKind.GeodatabaseImport, "hosted/x", null, CancellationToken.None);

        Assert.False(await seeds.CancelAsync(import.Id, CancellationToken.None));
        Assert.Equal(
            JobStatus.Queued,
            (await jobs.FindAsync(import.Id, owner, false, CancellationToken.None))!.Status);
    }

    /// <remarks>
    /// <b>Resumable is queued again every time, with its progress kept</b> — ADR-093 §5.4. A harmless
    /// kind would be failed on its second loss; a seed lost to the second restart of the week must not.
    /// </remarks>
    [Fact]
    public async Task A_seed_whose_worker_died_is_queued_again_with_its_progress_however_often()
    {
        (PostgresTileSeedStore seeds, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileSeedState seed = (await seeds.StartAsync(owner, Request(service), "a", "{}", CancellationToken.None)).Started!;

        for (int loss = 0; loss < 3; loss++)
        {
            string worker = loss % 2 == 0 ? Worker : Other;

            Assert.Equal(seed.Job.Id, (await jobs.ClaimAsync(JobKind.TileSeed, worker, 1, CancellationToken.None))!.Id);
            Assert.True(await seeds.CheckpointAsync(
                seed.Job.Id, worker, Progress(seed, 1 + loss), null, null, 10 * (loss + 1), CancellationToken.None));

            await ExecuteAsync("update job set lease_until = now() - interval '1 second' where id = @id", seed.Job.Id);

            JobReclaim taken = Assert.Single(await jobs.ReclaimAsync(CancellationToken.None), r => r.Id == seed.Job.Id);
            Assert.Equal(JobStatus.Queued, taken.Became);

            TileSeedState back = (await seeds.FindAsync(seed.Job.Id, CancellationToken.None))!;
            Assert.Equal(JobStatus.Queued, back.Job.Status);
            Assert.Equal(10 * (loss + 1), back.Job.Progress);
            Assert.Equal(Math.Min(4, loss), back.Levels[1].Done);
        }
    }

    [Fact]
    public async Task Each_level_reports_the_last_seed_that_finished_it()
    {
        (PostgresTileSeedStore seeds, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileSeedState seed = (await seeds.StartAsync(owner, Request(service), "a", "{}", CancellationToken.None)).Started!;
        await jobs.ClaimAsync(JobKind.TileSeed, Worker, 1, CancellationToken.None);

        // Levels 0 and 1 finished; level 2 not reached.
        await seeds.CheckpointAsync(seed.Job.Id, Worker, Progress(seed, 5), null, null, 20, CancellationToken.None);
        await seeds.CancelAsync(seed.Job.Id, CancellationToken.None);

        IReadOnlyList<TileSeedZoom> seeded = await seeds.LastSeededAsync(service, CancellationToken.None);

        Assert.Equal([0, 1], seeded.Select(zoom => zoom.Zoom));
        Assert.All(seeded, zoom => Assert.Equal(seed.Job.Id, zoom.Job));
        Assert.All(seeded, zoom => Assert.Equal(Area, zoom.Area));

        IReadOnlyList<TileSeedState> listed = await seeds.ListAsync(service, 10, CancellationToken.None);
        Assert.Equal(seed.Job.Id, Assert.Single(listed).Job.Id);
    }

    [Fact]
    public async Task Deleting_the_service_takes_its_seeds_with_it_and_leaves_the_job()
    {
        (PostgresTileSeedStore seeds, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileSeedState seed = (await seeds.StartAsync(owner, Request(service), "a", "{}", CancellationToken.None)).Started!;

        await ExecuteAsync("delete from service where id = @id", service);

        Assert.Null(await seeds.FindAsync(seed.Job.Id, CancellationToken.None));
        Assert.NotNull(await jobs.FindAsync(seed.Job.Id, owner, false, CancellationToken.None));
    }

    private async Task<(PostgresTileSeedStore Seeds, PostgresJobStore Jobs, Guid Owner, Guid Service)> ReadyAsync()
    {
        await MigrateAsync();

        Guid owner = Guid.NewGuid();

        await using (NpgsqlCommand command = DataSource.CreateCommand(
                         "insert into principal (id, kind, name, user_type) values (@id, 'user', @name, 'creator')"))
        {
            command.Parameters.AddWithValue("id", owner);
            command.Parameters.AddWithValue("name", "zz_seed_" + owner.ToString("N")[..8]);
            await command.ExecuteNonQueryAsync();
        }

        Guid service = await SeedServiceAsync("seeded_" + owner.ToString("N")[..8], null, owner);

        return (new PostgresTileSeedStore(DataSource), new PostgresJobStore(DataSource), owner, service);
    }

    private static TileSeedRequest Request(Guid service) =>
        new(service, Area, Whole: true, Concurrency: 2, [(0, 1L), (1, 4L), (2, 16L)]);

    /// <summary>Every level's progress with the first <paramref name="done"/> tiles of the seed built.</summary>
    private static TileSeedLevel[] Progress(TileSeedState seed, long done)
    {
        List<TileSeedLevel> levels = [];

        foreach (TileSeedLevel level in seed.Levels)
        {
            long here = Math.Min(level.Total, done);
            done -= here;
            levels.Add(level with { Done = here, Built = here });
        }

        return [.. levels];
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }
}
