using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// The job record: every rule migration 28 and <see cref="IJobStore"/> claim to keep.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-037]'s store, and the first increment of [ADR-011] rather than its implementation.</b> There
/// is no queue here and these tests do not look for one — no claim, no lease, no contention. What they
/// assert is that a request which cannot be answered now can be answered later, and that the answers
/// are the honest ones.
/// </para>
/// <para>
/// <b>Three of these are about a direction rather than a value</b>, which is the shape this repository
/// keeps finding matters: an unknown stored status must read as failed and never as done, a failure must
/// carry a reason, and a progress figure outside the range is refused rather than clamped. Each of them
/// is a way a screen could come to report work that did not happen.
/// </para>
/// </remarks>
public sealed class JobStoreTests : PostgresFixture
{
    /// <summary>
    /// A job is recorded queued, found by its owner, and hidden from everybody else.
    /// </summary>
    /// <remarks>
    /// <b>Null for *not yours* as well as for *not there*.</b> ADR-018's reasoning about private
    /// content, applied to an id: a 403 on a job id confirms the job exists, which turns the status
    /// endpoint into a way to learn what other people are doing. So the ownership test is in the
    /// <c>where</c> clause and the two cases are indistinguishable from outside — which is the point,
    /// and is why this test asserts both in one place.
    /// </remarks>
    [Fact]
    public async Task A_job_is_the_owners_and_a_stranger_cannot_tell_it_exists()
    {
        await MigrateAsync();

        PostgresJobStore jobs = new(DataSource);

        (Guid mine, _) = await MemberAsync("zz_job_owner");
        (Guid theirs, _) = await MemberAsync("zz_job_stranger");

        JobRecord made = await jobs.CreateAsync(
            mine, JobKind.GeodatabaseImport, "hosted/roads", null, CancellationToken.None);

        Assert.Equal(JobStatus.Queued, made.Status);
        Assert.Equal(0, made.Progress);
        Assert.Null(made.Started);
        Assert.Null(made.Finished);

        Assert.NotNull(await jobs.FindAsync(made.Id, mine, false, CancellationToken.None));

        // A stranger gets exactly what they would get for an id that never existed.
        Assert.Null(await jobs.FindAsync(made.Id, theirs, false, CancellationToken.None));
        Assert.Null(await jobs.FindAsync(Guid.NewGuid(), mine, false, CancellationToken.None));

        // And an administrator sees it, which is the second axis rather than a bypass.
        Assert.NotNull(await jobs.FindAsync(made.Id, theirs, true, CancellationToken.None));
    }

    /// <summary>
    /// Only one caller can take a queued job, and the loser is told rather than thrown at.
    /// </summary>
    /// <remarks>
    /// <b>This is not a claim protocol and the test says so.</b> There are no competing consumers —
    /// one process creates a job and the same process runs it. What is asserted is the property that
    /// makes a retried request safe: the transition is a conditional statement, so a second attempt
    /// reports *somebody already is* instead of quietly starting a second run. A read-then-write would
    /// pass a single-threaded test and fail a retry.
    /// </remarks>
    [Fact]
    public async Task Starting_a_job_twice_succeeds_once()
    {
        await MigrateAsync();

        PostgresJobStore jobs = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_job_start");

        JobRecord made = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseImport, "hosted/twice", null, CancellationToken.None);

        Assert.True(await jobs.StartAsync(made.Id, CancellationToken.None));
        Assert.False(await jobs.StartAsync(made.Id, CancellationToken.None));

        JobRecord? running = await jobs.FindAsync(
            made.Id, owner, false, CancellationToken.None);

        Assert.Equal(JobStatus.Running, running!.Status);
        Assert.NotNull(running.Started);
    }

    /// <summary>
    /// A finished job says how it finished, and a failure must say why.
    /// </summary>
    /// <remarks>
    /// <b>The refusal is the assertion.</b> A job reporting only *failed* is one nobody can act on, and
    /// this repository has recorded the general form of that mistake more than once — a state reported
    /// without the fact that makes it actionable. Refused in the store rather than left to every caller
    /// to remember.
    /// </remarks>
    [Fact]
    public async Task A_failure_must_carry_a_reason_and_success_completes_the_progress()
    {
        await MigrateAsync();

        PostgresJobStore jobs = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_job_finish");

        JobRecord ok = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseImport, "hosted/good", null, CancellationToken.None);

        await jobs.StartAsync(ok.Id, CancellationToken.None);
        await jobs.ProgressAsync(ok.Id, 40, CancellationToken.None);

        await jobs.FinishAsync(
            ok.Id, JobStatus.Done, "{\"rows\":50}", null, CancellationToken.None);

        JobRecord? done = await jobs.FindAsync(ok.Id, owner, false, CancellationToken.None);

        Assert.Equal(JobStatus.Done, done!.Status);

        // <b>100 on success, because a job that finished has nothing left to do.</b> Leaving it at 40
        // would show a completed import as two-fifths through for as long as the row survives.
        Assert.Equal(100, done.Progress);
        Assert.NotNull(done.Finished);
        Assert.Contains("50", done.Detail!, StringComparison.Ordinal);

        // ------------------------------------------------------------------ the refusals
        JobRecord bad = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseImport, "hosted/bad", null, CancellationToken.None);

        await jobs.StartAsync(bad.Id, CancellationToken.None);
        await jobs.ProgressAsync(bad.Id, 70, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            jobs.FinishAsync(bad.Id, JobStatus.Failed, null, null, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            jobs.FinishAsync(bad.Id, JobStatus.Failed, null, "   ", CancellationToken.None));

        // Not an ending, so not accepted as one.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            jobs.FinishAsync(bad.Id, JobStatus.Running, null, null, CancellationToken.None));

        await jobs.FinishAsync(
            bad.Id, JobStatus.Failed, null, "The archive held no feature class.",
            CancellationToken.None);

        JobRecord? failed = await jobs.FindAsync(bad.Id, owner, false, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, failed!.Status);
        Assert.Contains("no feature class", failed.Failure!, StringComparison.Ordinal);

        // <b>And the last honest figure survives a failure.</b> Overwriting it with 100 would claim
        // completion; with 0 would lose how far it got, which is the first thing somebody asks.
        Assert.Equal(70, failed.Progress);
    }

    /// <summary>
    /// Progress outside a percentage is refused, and a late report cannot reopen a finished job.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than clamped, because a clamp hides the bug.</b> A worker reporting 140% has
    /// counted features where it meant fractions, and silently storing 100 would make it look finished
    /// — the failure being that the wrong number is not the problem, the *plausible* number is.
    /// </remarks>
    [Fact]
    public async Task Progress_is_a_percentage_and_a_finished_job_stops_moving()
    {
        await MigrateAsync();

        PostgresJobStore jobs = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_job_progress");

        JobRecord made = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseImport, "hosted/percent", null, CancellationToken.None);

        await jobs.StartAsync(made.Id, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            jobs.ProgressAsync(made.Id, 140, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            jobs.ProgressAsync(made.Id, -1, CancellationToken.None));

        await jobs.ProgressAsync(made.Id, 55, CancellationToken.None);

        await jobs.FinishAsync(
            made.Id, JobStatus.Failed, null, "Stopped.", CancellationToken.None);

        // <b>A worker that was killed can still be mid-write.</b> Its last report must not move a job
        // that has already been recorded as finished, or a failed import reads as running again.
        await jobs.ProgressAsync(made.Id, 90, CancellationToken.None);

        JobRecord? after = await jobs.FindAsync(made.Id, owner, false, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, after!.Status);
        Assert.Equal(55, after.Progress);
    }

    /// <summary>
    /// An unrecognised stored status reads as failed, never as done.
    /// </summary>
    /// <remarks>
    /// <b>The direction is the whole assertion.</b> A row written by a newer build, carrying a status
    /// this one does not know, must not read as *finished successfully* — the safe reading of *I do not
    /// understand this* is the one that does not claim work was done. The opposite default is how a
    /// screen comes to report a completed import that never happened, and it is the same argument
    /// <c>ReadVisibility</c> makes about a group becoming public during a downgrade.
    /// </remarks>
    [Fact]
    public async Task An_unknown_stored_status_reads_as_failed()
    {
        await MigrateAsync();

        PostgresJobStore jobs = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_job_future");

        JobRecord made = await jobs.CreateAsync(
            owner, JobKind.GeodatabaseImport, "hosted/future", null, CancellationToken.None);

        // The check constraint is what stops this being writable through the port, so the row is
        // forged past it — which is the shape a downgrade produces without asking anybody.
        await using (Npgsql.NpgsqlCommand forge = DataSource.CreateCommand(
            "alter table job drop constraint job_status_known; "
            + "update job set status = 'reticulating_splines' where id = @id"))
        {
            forge.Parameters.AddWithValue("id", made.Id);
            await forge.ExecuteNonQueryAsync();
        }

        try
        {
            JobRecord? read = await jobs.FindAsync(made.Id, owner, false, CancellationToken.None);

            Assert.Equal(JobStatus.Failed, read!.Status);
        }
        finally
        {
            await using Npgsql.NpgsqlCommand restore = DataSource.CreateCommand(
                "update job set status = 'failed' "
                + "where status not in ('queued', 'running', 'done', 'failed', 'cancelled'); "
                + "alter table job add constraint job_status_known "
                + "check (status in ('queued', 'running', 'done', 'failed', 'cancelled'))");

            await restore.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// The listing shows a caller their own, an administrator everybody's, and can narrow to what runs.
    /// </summary>
    [Fact]
    public async Task The_listing_is_per_caller_and_can_narrow_to_the_unfinished()
    {
        await MigrateAsync();

        PostgresJobStore jobs = new(DataSource);

        (Guid mine, _) = await MemberAsync("zz_job_list_mine");
        (Guid theirs, _) = await MemberAsync("zz_job_list_theirs");

        JobRecord running = await jobs.CreateAsync(
            mine, JobKind.GeodatabaseImport, "hosted/a", null, CancellationToken.None);

        JobRecord finished = await jobs.CreateAsync(
            mine, JobKind.GeodatabaseImport, "hosted/b", null, CancellationToken.None);

        await jobs.StartAsync(running.Id, CancellationToken.None);
        await jobs.StartAsync(finished.Id, CancellationToken.None);
        await jobs.FinishAsync(finished.Id, JobStatus.Done, null, null, CancellationToken.None);

        await jobs.CreateAsync(
            theirs, JobKind.GeodatabaseImport, "hosted/c", null, CancellationToken.None);

        IReadOnlyList<JobRecord> ours = await jobs.ListAsync(
            mine, false, false, CancellationToken.None);

        Assert.Equal(2, ours.Count(j => j.Owner == mine));
        Assert.DoesNotContain(ours, j => j.Owner == theirs);

        // Newest first, because a job list is read from the top.
        Assert.True(ours[0].Created >= ours[^1].Created);

        IReadOnlyList<JobRecord> unfinished = await jobs.ListAsync(
            mine, false, true, CancellationToken.None);

        Assert.Contains(unfinished, j => j.Id == running.Id);
        Assert.DoesNotContain(unfinished, j => j.Id == finished.Id);

        IReadOnlyList<JobRecord> everybody = await jobs.ListAsync(
            mine, true, false, CancellationToken.None);

        Assert.Contains(everybody, j => j.Owner == theirs);
    }

    /// <summary>
    /// A job must belong to somebody.
    /// </summary>
    /// <remarks>
    /// <b>Refused in the port rather than left to the column.</b> The foreign key would refuse an empty
    /// guid too, with a message about a constraint; this refuses it with the reason — that *whose is
    /// this* has to be answerable for a job that wrote a table into somebody's content.
    /// </remarks>
    [Fact]
    public async Task A_job_without_an_owner_is_refused()
    {
        await MigrateAsync();

        PostgresJobStore jobs = new(DataSource);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            jobs.CreateAsync(
                Guid.Empty, JobKind.GeodatabaseImport, "hosted/nobody", null,
                CancellationToken.None));
    }

    private async Task<(Guid Id, string Name)> MemberAsync(string name)
    {
        await using Npgsql.NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name, user_type) "
            + "values (@id, 'user', @name, 'creator') "
            + "on conflict (name) do update set name = excluded.name "
            + "returning id");

        Guid id = Guid.NewGuid();
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);

        object? made = await command.ExecuteScalarAsync();

        return (made is Guid was ? was : id, name);
    }
}
