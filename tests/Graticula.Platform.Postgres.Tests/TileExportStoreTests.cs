using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Jobs;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A tile export's record: the budget checked with the insert, progress only from its holder, finished with a size
/// and an expiry in one statement, cancelled only as an export, removed once, a lost lease run again once, and the
/// service's policy off until set — migration 64, ADR-098.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TileExportStoreTests : PostgresFixture
{
    private const string Worker = "graticula/export TileExportStoreTests#1";
    private const string Other = "graticula/export TileExportStoreTests#2";

    private const long Plenty = long.MaxValue / 4;

    private static readonly Envelope Area = new(-1000, -2000, 3000, 4000);

    [Fact]
    public async Task An_export_starts_queued_with_its_token_levels_and_estimate()
    {
        (PostgresTileExportStore exports, _, Guid owner, Guid service) = await ReadyAsync();
        string token = Token();

        TileExportStart start = await exports.StartAsync(owner, Request(service, token), "exporting", "{}", Plenty, CancellationToken.None);

        TileExportState export = Assert.IsType<TileExportState>(start.Started);
        Assert.Null(start.RefusedForSpace);
        Assert.Equal(JobKind.TileExport, export.Job.Kind);
        Assert.Equal(JobStatus.Queued, export.Job.Status);
        Assert.Equal(TileExportFormat.PmTiles, export.Format);
        Assert.Equal([2, 3, 5], export.Levels);
        Assert.Equal(Area, export.Area);
        Assert.Equal(token, export.Token);
        Assert.Equal(1234, export.EstimatedBytes);
        Assert.Null(export.Bytes);
        Assert.Null(export.ExpiresAt);
        Assert.Equal("arcgis", export.Origin);
    }

    [Fact]
    public async Task An_export_that_does_not_fit_what_the_budget_has_left_is_refused_and_writes_nothing()
    {
        (PostgresTileExportStore exports, _, Guid owner, Guid service) = await ReadyAsync();

        long held = await exports.HeldBytesAsync(CancellationToken.None);

        TileExportStart refused = await exports.StartAsync(
            owner, Request(service, Token()), "too big", "{}", held + 1233, CancellationToken.None);

        Assert.Null(refused.Started);
        Assert.Equal(held, refused.RefusedForSpace);
        Assert.Empty(await exports.ListAsync(service, 10, CancellationToken.None));

        // Exactly enough fits.
        Assert.NotNull((await exports.StartAsync(
            owner, Request(service, Token()), "fits", "{}", held + 1234, CancellationToken.None)).Started);
    }

    [Fact]
    public async Task Only_the_worker_holding_it_may_checkpoint_and_finishing_sets_the_size_and_expiry()
    {
        (PostgresTileExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileExportState export = (await exports.StartAsync(owner, Request(service, Token()), "a", "{}", Plenty, CancellationToken.None)).Started!;

        Assert.False(await exports.CheckpointAsync(export.Job.Id, Worker, 1, 1, null, null, 5, CancellationToken.None));

        await ClaimAsync(jobs, export.Job.Id, Worker);

        Assert.False(await exports.CheckpointAsync(export.Job.Id, Other, 1, 1, null, null, 5, CancellationToken.None));
        Assert.True(await exports.CheckpointAsync(
            export.Job.Id, Worker, 10, 7, DateTimeOffset.UtcNow.AddSeconds(5), "the source is away", 40, CancellationToken.None));

        TileExportState now = (await exports.FindAsync(export.Job.Id, CancellationToken.None))!;
        Assert.Equal((10, 7, 40), (now.Done, now.Stored, now.Job.Progress));
        Assert.Equal("the source is away", now.PausedBecause);

        Assert.False(await exports.FinishAsync(export.Job.Id, Other, 999, 7, TimeSpan.FromHours(1), CancellationToken.None));
        Assert.True(await exports.FinishAsync(export.Job.Id, Worker, 999, 7, TimeSpan.FromHours(1), CancellationToken.None));

        TileExportState done = (await exports.FindAsync(export.Job.Id, CancellationToken.None))!;
        Assert.Equal(JobStatus.Done, done.Job.Status);
        Assert.Equal(999, done.Bytes);
        Assert.Equal(done.Total, done.Done);
        Assert.Null(done.PausedBecause);
        Assert.NotNull(done.ExpiresAt);
        Assert.InRange(done.ExpiresAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(70));
    }

    [Fact]
    public async Task A_cancelled_export_stops_answering_its_worker_and_cannot_be_finished()
    {
        (PostgresTileExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileExportState export = (await exports.StartAsync(owner, Request(service, Token()), "a", "{}", Plenty, CancellationToken.None)).Started!;
        await ClaimAsync(jobs, export.Job.Id, Worker);

        Assert.True(await exports.CancelAsync(export.Job.Id, CancellationToken.None));
        Assert.False(await exports.CancelAsync(export.Job.Id, CancellationToken.None));
        Assert.False(await exports.CheckpointAsync(export.Job.Id, Worker, 1, 1, null, null, 5, CancellationToken.None));
        Assert.False(await exports.FinishAsync(export.Job.Id, Worker, 1, 1, TimeSpan.FromHours(1), CancellationToken.None));

        Assert.Equal(JobStatus.Cancelled, (await exports.FindAsync(export.Job.Id, CancellationToken.None))!.Job.Status);
    }

    [Fact]
    public async Task Cancelling_reaches_only_an_export()
    {
        (PostgresTileExportStore exports, PostgresJobStore jobs, Guid owner, _) = await ReadyAsync();

        JobRecord import = await jobs.CreateAsync(owner, JobKind.GeodatabaseImport, "hosted/x", null, CancellationToken.None);

        Assert.False(await exports.CancelAsync(import.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Expired_and_ended_exports_are_due_for_removal_and_are_marked_once()
    {
        (PostgresTileExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileExportState written = (await exports.StartAsync(owner, Request(service, Token()), "w", "{}", Plenty, CancellationToken.None)).Started!;
        await ClaimAsync(jobs, written.Job.Id, Worker);
        await exports.FinishAsync(written.Job.Id, Worker, 10, 1, TimeSpan.FromHours(1), CancellationToken.None);

        TileExportState cancelled = (await exports.StartAsync(owner, Request(service, Token()), "c", "{}", Plenty, CancellationToken.None)).Started!;
        await exports.CancelAsync(cancelled.Job.Id, CancellationToken.None);

        IReadOnlyList<(Guid Job, string Token)> now = await exports.DueForRemovalAsync(DateTimeOffset.UtcNow, 1000, CancellationToken.None);
        Assert.DoesNotContain(now, due => due.Job == written.Job.Id);
        Assert.Contains(now, due => due.Job == cancelled.Job.Id);

        IReadOnlyList<(Guid Job, string Token)> later = await exports.DueForRemovalAsync(DateTimeOffset.UtcNow.AddHours(2), 1000, CancellationToken.None);
        Assert.Contains(later, due => due.Job == written.Job.Id && due.Token == written.Token);

        Assert.Contains(written.Token, await exports.LiveTokensAsync(CancellationToken.None));
        Assert.True(await exports.MarkRemovedAsync(written.Job.Id, CancellationToken.None));
        Assert.False(await exports.MarkRemovedAsync(written.Job.Id, CancellationToken.None));
        Assert.DoesNotContain(written.Token, await exports.LiveTokensAsync(CancellationToken.None));

        Assert.DoesNotContain(
            await exports.DueForRemovalAsync(DateTimeOffset.UtcNow.AddHours(2), 1000, CancellationToken.None),
            due => due.Job == written.Job.Id);
    }

    [Fact]
    public async Task A_written_package_holds_its_size_against_the_budget_and_a_removed_one_holds_nothing()
    {
        (PostgresTileExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        long before = await exports.HeldBytesAsync(CancellationToken.None);

        TileExportState export = (await exports.StartAsync(owner, Request(service, Token()), "a", "{}", Plenty, CancellationToken.None)).Started!;
        Assert.Equal(before + 1234, await exports.HeldBytesAsync(CancellationToken.None));

        await ClaimAsync(jobs, export.Job.Id, Worker);
        await exports.FinishAsync(export.Job.Id, Worker, 5000, 1, TimeSpan.FromHours(1), CancellationToken.None);
        Assert.Equal(before + 5000, await exports.HeldBytesAsync(CancellationToken.None));

        await exports.MarkRemovedAsync(export.Job.Id, CancellationToken.None);
        Assert.Equal(before, await exports.HeldBytesAsync(CancellationToken.None));
    }

    /// <remarks>
    /// <b>Harmless: queued again once, then failed</b> — ADR-098 §5.4. An export is written from the start every run,
    /// so a second run is safe; an export that kills its process twice is not tried a third time.
    /// </remarks>
    [Fact]
    public async Task An_export_whose_worker_died_is_run_again_once_and_then_failed()
    {
        (PostgresTileExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileExportState export = (await exports.StartAsync(owner, Request(service, Token()), "a", "{}", Plenty, CancellationToken.None)).Started!;

        JobStatus[] became = new JobStatus[2];

        for (int loss = 0; loss < 2; loss++)
        {
            await ClaimAsync(jobs, export.Job.Id, loss == 0 ? Worker : Other);
            await ExecuteAsync("update job set lease_until = now() - interval '1 second' where id = @id", export.Job.Id);

            became[loss] = Assert.Single(await jobs.ReclaimAsync(CancellationToken.None), r => r.Id == export.Job.Id).Became;
        }

        Assert.Equal([JobStatus.Queued, JobStatus.Failed], became);
    }

    [Fact]
    public async Task A_services_policy_is_off_until_set_and_setting_it_answers_the_old_one()
    {
        (PostgresTileExportStore exports, _, _, Guid service) = await ReadyAsync();

        TileExportPolicy? before = await exports.SetPolicyAsync(service, new TileExportPolicy(true, true, 500), CancellationToken.None);

        Assert.Equal(TileExportPolicy.Off, before);

        Assert.Equal(
            new TileExportPolicy(true, true, 500),
            await exports.SetPolicyAsync(service, new TileExportPolicy(false, false, null), CancellationToken.None));

        Assert.Null(await exports.SetPolicyAsync(Guid.NewGuid(), TileExportPolicy.Off, CancellationToken.None));

        await Assert.ThrowsAsync<PostgresException>(() =>
            exports.SetPolicyAsync(service, new TileExportPolicy(true, false, 0), CancellationToken.None));
    }

    [Fact]
    public async Task A_token_that_is_not_32_hexadecimal_characters_is_refused_by_the_store()
    {
        (PostgresTileExportStore exports, _, Guid owner, Guid service) = await ReadyAsync();

        await Assert.ThrowsAsync<PostgresException>(() =>
            exports.StartAsync(owner, Request(service, "../../etc/passwd"), "a", "{}", Plenty, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_service_takes_its_exports_with_it_and_leaves_the_job()
    {
        (PostgresTileExportStore exports, PostgresJobStore jobs, Guid owner, Guid service) = await ReadyAsync();

        TileExportState export = (await exports.StartAsync(owner, Request(service, Token()), "a", "{}", Plenty, CancellationToken.None)).Started!;

        await ExecuteAsync("delete from service where id = @id", service);

        Assert.Null(await exports.FindAsync(export.Job.Id, CancellationToken.None));
        Assert.NotNull(await jobs.FindAsync(export.Job.Id, owner, false, CancellationToken.None));
    }

    private static string Token() => Guid.NewGuid().ToString("N");

    private static TileExportRequest Request(Guid service, string token) =>
        new(service, TileExportFormat.PmTiles, [2, 3, 5], Area, Whole: false, Total: 21, EstimatedBytes: 1234, token,
            "webmercator", "arcgis");

    /// <summary>Claims one particular export, whatever else of the kind is queued in the shared schema.</summary>
    private async Task ClaimAsync(PostgresJobStore jobs, Guid id, string worker)
    {
        // Other exports queued by other tests would be claimed first; move this one to the front.
        await ExecuteAsync("update job set created_at = '1970-01-01' where id = @id", id);

        JobRecord? claimed = await jobs.ClaimAsync(JobKind.TileExport, worker, 1, CancellationToken.None);
        Assert.Equal(id, claimed?.Id);
    }

    private async Task<(PostgresTileExportStore Exports, PostgresJobStore Jobs, Guid Owner, Guid Service)> ReadyAsync()
    {
        await MigrateAsync();

        Guid owner = Guid.NewGuid();

        await using (NpgsqlCommand command = DataSource.CreateCommand(
                         "insert into principal (id, kind, name, user_type) values (@id, 'user', @name, 'creator')"))
        {
            command.Parameters.AddWithValue("id", owner);
            command.Parameters.AddWithValue("name", "zz_export_" + owner.ToString("N")[..8]);
            await command.ExecuteNonQueryAsync();
        }

        Guid service = await SeedServiceAsync("exported_" + owner.ToString("N")[..8], null, owner);

        return (new PostgresTileExportStore(DataSource), new PostgresJobStore(DataSource), owner, service);
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }
}
