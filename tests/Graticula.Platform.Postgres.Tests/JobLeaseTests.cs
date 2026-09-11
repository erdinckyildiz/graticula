using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A claimed job holds a lease, and a job whose worker died is taken back — D-243.
/// </summary>
/// <remarks>
/// <para>
/// <b>Before migration 45 a job claimed by a worker that then died stayed `running` for ever</b>:
/// the claim selects queued rows only, and nothing could clear one. These pin the lease itself,
/// and — the half that matters to an operator — what becomes of a job when its lease lapses.
/// </para>
/// <para>
/// <b>Time is moved by writing the lease into the past</b> rather than by waiting a minute, which is
/// the one thing a test can do to this row that production cannot.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class JobLeaseTests : PostgresFixture
{
    private const string Worker = "graticula/test JobLeaseTests#1";
    private const string Other = "graticula/test JobLeaseTests#2";

    [Fact]
    public async Task A_claim_takes_a_lease_and_only_its_claimant_can_renew_it()
    {
        PostgresJobStore jobs = await ReadyAsync();
        Guid owner = await OwnerAsync();

        JobRecord made = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseImport, "hosted/leased", null, CancellationToken.None);

        await jobs.ClaimAsync(JobKind.GeodatabaseImport, Worker, 1, CancellationToken.None);

        (DateTimeOffset? lease, int attempts) = await LeaseAsync(made.Id);

        Assert.NotNull(lease);
        Assert.InRange(
            (lease!.Value - DateTimeOffset.UtcNow).TotalSeconds,
            JobLease.Duration.TotalSeconds - 10, JobLease.Duration.TotalSeconds + 1);
        Assert.Equal(1, attempts);

        Assert.True(await jobs.RenewAsync(made.Id, Worker, CancellationToken.None));
        Assert.False(await jobs.RenewAsync(made.Id, Other, CancellationToken.None));

        // A finished job has no lease to renew, and says so rather than pretending.
        await jobs.FinishAsync(made.Id, JobStatus.Done, null, null, CancellationToken.None, Worker);

        Assert.False(await jobs.RenewAsync(made.Id, Worker, CancellationToken.None));
        Assert.Null((await LeaseAsync(made.Id)).Lease);
    }

    [Fact]
    public async Task A_live_lease_is_not_taken_back()
    {
        PostgresJobStore jobs = await ReadyAsync();
        Guid owner = await OwnerAsync();

        JobRecord made = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseInspect, "hosted/alive", null, CancellationToken.None);

        await jobs.ClaimAsync(JobKind.GeodatabaseInspect, Worker, 1, CancellationToken.None);

        Assert.DoesNotContain(
            await jobs.ReclaimAsync(CancellationToken.None), r => r.Id == made.Id);

        Assert.Equal(JobStatus.Running, (await FindAsync(jobs, made.Id, owner)).Status);
    }

    /// <remarks>
    /// <b>Harmless work is queued again once, and the second loss fails it</b> — otherwise a job
    /// whose work kills its process would be claimed and lost for as long as somebody restarts the
    /// server.
    /// </remarks>
    [Fact]
    public async Task A_lost_inspection_is_queued_again_once_and_then_failed()
    {
        PostgresJobStore jobs = await ReadyAsync();
        Guid owner = await OwnerAsync();

        JobRecord made = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseInspect, "hosted/twice-lost", null, CancellationToken.None);

        await jobs.ClaimAsync(JobKind.GeodatabaseInspect, Worker, 1, CancellationToken.None);
        await LapseAsync(made.Id);

        JobReclaim first = Assert.Single(
            await jobs.ReclaimAsync(CancellationToken.None), r => r.Id == made.Id);

        Assert.Equal(JobStatus.Queued, first.Became);
        Assert.Equal(Worker, first.LostBy);

        JobRecord requeued = await FindAsync(jobs, made.Id, owner);

        Assert.Equal(JobStatus.Queued, requeued.Status);
        Assert.Null(requeued.ClaimedBy);
        Assert.Null(requeued.Started);

        // Somebody else takes it, and loses it too.
        JobRecord? again = await jobs.ClaimAsync(
            JobKind.GeodatabaseInspect, Other, 1, CancellationToken.None);

        Assert.Equal(made.Id, again!.Id);

        // <b>The worker that lost it cannot finish it now that somebody else holds it.</b> Its late
        // answer would land on the new claimant's run — which is what the `claimed_by` test in
        // `FinishAsync` exists for, and what a finish on an already-failed row cannot show: the
        // first version of this file asserted only that, and passed with the test removed.
        await jobs.FinishAsync(
            made.Id, JobStatus.Done, "{\"late\":true}", null, CancellationToken.None, Worker);

        JobRecord stillTheirs = await FindAsync(jobs, made.Id, owner);

        Assert.Equal(JobStatus.Running, stillTheirs.Status);
        Assert.Equal(Other, stillTheirs.ClaimedBy);

        await LapseAsync(made.Id);

        JobReclaim second = Assert.Single(
            await jobs.ReclaimAsync(CancellationToken.None), r => r.Id == made.Id);

        Assert.Equal(JobStatus.Failed, second.Became);

        JobRecord failed = await FindAsync(jobs, made.Id, owner);

        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains(Other, failed.Failure!, StringComparison.Ordinal);
        Assert.Contains("tried 2 times", failed.Failure!, StringComparison.Ordinal);
        Assert.NotNull(failed.Finished);
    }

    /// <remarks>
    /// <b>An import is never run again on its own.</b> A second run is refused its layer only after
    /// it has created and filled a table, so the reclaim fails it and says what may be left behind.
    /// </remarks>
    [Fact]
    public async Task A_lost_import_is_failed_with_the_worker_that_lost_it_and_what_to_do()
    {
        PostgresJobStore jobs = await ReadyAsync();
        Guid owner = await OwnerAsync();

        JobRecord made = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseImport, "hosted/crashed", null, CancellationToken.None);

        await jobs.ClaimAsync(JobKind.GeodatabaseImport, Worker, 1, CancellationToken.None);
        await jobs.ProgressAsync(made.Id, 40, CancellationToken.None);
        await LapseAsync(made.Id);

        JobReclaim taken = Assert.Single(
            await jobs.ReclaimAsync(CancellationToken.None), r => r.Id == made.Id);

        Assert.Equal(JobStatus.Failed, taken.Became);

        JobRecord failed = await FindAsync(jobs, made.Id, owner);

        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains(Worker, failed.Failure!, StringComparison.Ordinal);
        Assert.Contains("not run twice", failed.Failure!, StringComparison.Ordinal);

        // The last honest figure survives, as it does for every other failure.
        Assert.Equal(40, failed.Progress);

        // And the worker that lost it cannot write over the answer.
        await jobs.FinishAsync(
            made.Id, JobStatus.Done, "{\"late\":true}", null, CancellationToken.None, Worker);

        Assert.Equal(JobStatus.Failed, (await FindAsync(jobs, made.Id, owner)).Status);
    }

    /// <remarks>
    /// <b>A row an older build claimed has no lease</b>, and that build may still be working on it:
    /// left alone for an hour from its start, then failed rather than retried.
    /// </remarks>
    [Fact]
    public async Task A_row_with_no_lease_is_given_an_hour_and_then_failed()
    {
        PostgresJobStore jobs = await ReadyAsync();
        Guid owner = await OwnerAsync();

        JobRecord recent = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseInspect, "hosted/old-build-recent", null, CancellationToken.None);
        JobRecord stale = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseInspect, "hosted/old-build-stale", null, CancellationToken.None);

        // What a build before migration 45 writes: running, claimed, no lease.
        await ExecuteAsync(
            "update job set status = 'running', claimed_by = 'graticula/import old#7', "
            + "started_at = now() - interval '10 minutes' where id = @id", recent.Id);
        await ExecuteAsync(
            "update job set status = 'running', claimed_by = 'graticula/import old#7', "
            + "started_at = now() - interval '3 days' where id = @id", stale.Id);

        IReadOnlyList<JobReclaim> taken = await jobs.ReclaimAsync(CancellationToken.None);

        Assert.DoesNotContain(taken, r => r.Id == recent.Id);

        // Harmless, and still failed rather than queued: nothing can say its worker is gone.
        JobReclaim old = Assert.Single(taken, r => r.Id == stale.Id);

        Assert.Equal(JobStatus.Failed, old.Became);
        Assert.Contains(
            "does not renew one", (await FindAsync(jobs, stale.Id, owner)).Failure!,
            StringComparison.Ordinal);
    }

    private async Task<PostgresJobStore> ReadyAsync()
    {
        await MigrateAsync();

        return new PostgresJobStore(DataSource);
    }

    private static async Task<JobRecord> FindAsync(PostgresJobStore jobs, Guid id, Guid owner) =>
        (await jobs.FindAsync(id, owner, false, CancellationToken.None))!;

    private async Task LapseAsync(Guid id) =>
        await ExecuteAsync("update job set lease_until = now() - interval '1 second' where id = @id", id);

    private async Task<(DateTimeOffset? Lease, int Attempts)> LeaseAsync(Guid id)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select lease_until, attempts from job where id = @id");
        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt32(1));
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> OwnerAsync()
    {
        Guid id = Guid.NewGuid();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name, user_type) values (@id, 'user', @name, 'creator')");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", "zz_lease_" + id.ToString("N")[..8]);
        await command.ExecuteNonQueryAsync();

        return id;
    }
}
