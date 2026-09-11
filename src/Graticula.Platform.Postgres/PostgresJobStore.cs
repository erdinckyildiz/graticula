using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// The job record, in the platform store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Migration 28's table, a claim, and since migration 45 a lease.</b> No scheduler and no job
/// classes — see <see cref="IJobStore"/> for what of ADR-011 is still not here and why.
/// </para>
/// <para>
/// <b>Every state change is one conditional statement.</b> `StartAsync` moves queued to running and
/// reports whether it did; `FinishAsync` refuses a status that is not an ending. The conditions are in
/// SQL rather than read-then-write, because a read followed by a write is a race even with one worker —
/// a retried request is a second caller.
/// </para>
/// </remarks>
public sealed class PostgresJobStore : IJobStore
{
    /// <summary>
    /// The kinds a lost lease sends back to the queue: those whose work is harmless to repeat.
    /// </summary>
    /// <remarks>
    /// <b>Read from <see cref="JobKinds.RerunOf"/> rather than written here</b>, so ADR-011
    /// condition 2's declaration is the one place the answer lives — a kind declared harmless is
    /// retried, and a kind added without a declaration throws at start rather than being guessed at.
    /// </remarks>
    private static readonly string[] Harmless = Enum.GetValues<JobKind>()
        .Where(kind => JobKinds.RerunOf(kind) == JobRerun.Harmless)
        .Select(Wire)
        .ToArray();

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store over a data source.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresJobStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<JobRecord> CreateAsync(
        Guid owner,
        JobKind kind,
        string? subject,
        string? detail,
        CancellationToken cancellationToken,
        int protocol = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(protocol, 1);

        if (owner == Guid.Empty)
        {
            throw new ArgumentException(
                "A job belongs to somebody. An empty owner would make 'whose is this' unanswerable "
                + "for the rows where it matters most — an import that wrote into their content.",
                nameof(owner));
        }

        const string Sql = """
            insert into job (id, kind, status, owner_principal_id, subject, detail, protocol)
            values (@id, @kind, 'queued', @owner, @subject, @detail, @protocol)
            returning created_at
            """;

        Guid id = Guid.NewGuid();

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("kind", Wire(kind));
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("protocol", protocol);

        object? created = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // <b>Nobody has claimed it and the shape is the one that was written.</b> D-96: the
        // record this returns is built here rather than read back, so a field added to the row
        // and not added here is a field that is right in the store and wrong in the answer.
        return new JobRecord(
            id, kind, JobStatus.Queued, 0, owner, subject, detail, null,
            created is DateTimeOffset at ? at : DateTimeOffset.UtcNow, null, null,
            ClaimedBy: null, Protocol: protocol);
    }

    /// <inheritdoc/>
    public async Task<JobRecord?> FindAsync(
        Guid id, Guid asking, bool administrator, CancellationToken cancellationToken)
    {
        // <b>The ownership test is in the `where`, not in a branch after the read.</b> A read that
        // fetches the row and then decides whether the caller may have it has already fetched it, and
        // the difference shows up the first time somebody logs the query or profiles it.
        const string Sql = """
            select id, kind, status, progress, owner_principal_id, subject, detail, failure,
                   created_at, started_at, finished_at, claimed_by, protocol
              from job
             where id = @id
               and (@all or owner_principal_id = @who)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("who", asking);
        command.Parameters.AddWithValue("all", administrator);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobRecord>> ListAsync(
        Guid asking, bool all, bool unfinishedOnly, CancellationToken cancellationToken)
    {
        const string Sql = """
            select id, kind, status, progress, owner_principal_id, subject, detail, failure,
                   created_at, started_at, finished_at, claimed_by, protocol
              from job
             where (@all or owner_principal_id = @who)
               and (not @unfinished or status in ('queued', 'running'))
             order by created_at desc
             limit 200
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("who", asking);
        command.Parameters.AddWithValue("all", all);
        command.Parameters.AddWithValue("unfinished", unfinishedOnly);

        List<JobRecord> answer = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            answer.Add(Read(reader));
        }

        return answer;
    }

    /// <inheritdoc/>
    public async Task<bool> StartAsync(Guid id, CancellationToken cancellationToken)
    {
        // Conditional on still being queued, so two attempts cannot both believe they own it.
        const string Sql = """
            update job
               set status = 'running', started_at = now()
             where id = @id and status = 'queued'
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<JobRecord?> ClaimAsync(
        JobKind kind, string worker, int speaks, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        ArgumentOutOfRangeException.ThrowIfLessThan(speaks, 1);

        // <b>ADR-011 §3.2's statement, and `skip locked` is the whole reason it has this shape.</b>
        // Without it a second worker blocks on the first one's row lock, so a pool of four serialises
        // itself while every metric says it is running in parallel — the failure that looks like
        // success. The select and the update are one statement, so a crash between them is not a
        // state: either the row is taken and running, or it is untouched and still queued.
        //
        // <b>And the lease is taken in the same breath — D-243.</b> A claim that did not set one
        // would be a window in which the row is running and belongs to nobody the sweep can see.
        const string Sql = """
            with taken as (
                select id from job
                 where status = 'queued' and kind = @kind and protocol <= @speaks
                 order by created_at
                 limit 1
                 for update skip locked
            )
            update job
               set status = 'running', started_at = now(), claimed_by = @worker,
                   lease_until = now() + @lease, attempts = attempts + 1
             where id in (select id from taken)
            returning id, kind, status, progress, owner_principal_id, subject, detail, failure,
                      created_at, started_at, finished_at, claimed_by, protocol
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("kind", Wire(kind));
        command.Parameters.AddWithValue("worker", worker);
        command.Parameters.AddWithValue("speaks", speaks);
        command.Parameters.AddWithValue("lease", JobLease.Duration);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    /// <inheritdoc/>
    public async Task<bool> RenewAsync(Guid id, string worker, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);

        // <b>By the claimant, and only while it is still running.</b> A worker whose job was
        // reclaimed, or finished by somebody else, is told so by getting nothing back — which is
        // the only way a partitioned worker can find out it should stop.
        const string Sql = """
            update job set lease_until = now() + @lease
             where id = @id and status = 'running' and claimed_by = @worker
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("worker", worker);
        command.Parameters.AddWithValue("lease", JobLease.Duration);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobReclaim>> ReclaimAsync(CancellationToken cancellationToken)
    {
        // <b>One statement, two outcomes, and the outcome is the kind's own declaration.</b> A
        // harmless kind that lost a real lease and has not used its attempts goes back to the queue
        // with nobody's name on it; everything else is failed, keeps the name of the worker that lost
        // it, and says in the failure what happened and what to do. `skip locked`, so two workers
        // sweeping together take different rows rather than waiting on each other.
        //
        // <b>A row with no lease at all was claimed by an older build</b>, which may still be
        // working on it; it is given `@grace` from its start and then failed rather than retried,
        // because nothing can say whether that worker is gone.
        const string Sql = """
            with lost as (
                select id, kind, claimed_by, lease_until, attempts
                  from job
                 where status = 'running'
                   and coalesce(lease_until, started_at + @grace, created_at + @grace) < now()
                 for update skip locked
            ),
            decided as (
                select l.*,
                       (l.lease_until is not null
                        and l.kind = any(@harmless)
                        and l.attempts < @attempts) as again
                  from lost l
            )
            update job j
               set status      = case when d.again then 'queued' else 'failed' end,
                   started_at  = case when d.again then null else j.started_at end,
                   claimed_by  = case when d.again then null else j.claimed_by end,
                   progress    = case when d.again then 0 else j.progress end,
                   finished_at = case when d.again then null else now() end,
                   lease_until = null,
                   failure     = case when d.again then j.failure else
                       'The worker that took this job (' || coalesce(d.claimed_by, 'unnamed')
                       || ') stopped renewing its claim'
                       || case when d.lease_until is null
                               then ', and it was a build that does not renew one' else '' end
                       || ', so it was taken back at '
                       || to_char(now() at time zone 'utc', 'YYYY-MM-DD HH24:MI:SS') || ' UTC. '
                       || case when d.kind = any(@harmless)
                               then 'It had been tried ' || d.attempts || ' times, so it is not '
                                    || 'queued again.'
                               else 'Work of this kind is not run twice on its own, because a '
                                    || 'second run is refused only after it has written: ask for '
                                    || 'it again. A table it had begun may be left in the '
                                    || 'datastore without a layer.'
                          end
                   end
              from decided d
             where j.id = d.id
            returning j.id, j.kind, j.status, d.claimed_by
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("grace", JobLease.UnleasedGrace);
        command.Parameters.AddWithValue("harmless", Harmless);
        command.Parameters.AddWithValue("attempts", JobLease.Attempts);

        List<JobReclaim> taken = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            taken.Add(new JobReclaim(
                reader.GetGuid(0),
                ReadKind(reader.GetString(1)),
                ReadStatus(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return taken;
    }

    /// <inheritdoc/>
    public async Task ProgressAsync(Guid id, int percent, CancellationToken cancellationToken)
    {
        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percent), percent,
                "Progress is a percentage. Refused rather than clamped, because a worker reporting "
                + "140% has counted the wrong thing and storing 100 would make it look finished.");
        }

        // Only while running: a finished job's progress is history, and a late report from a worker
        // that has already been killed must not reopen it.
        const string Sql = """
            update job set progress = @percent
             where id = @id and status = 'running'
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("percent", percent);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task FinishAsync(
        Guid id,
        JobStatus status,
        string? detail,
        string? failure,
        CancellationToken cancellationToken,
        string? worker = null)
    {
        if (status is not (JobStatus.Done or JobStatus.Failed))
        {
            throw new ArgumentException(
                $"'{status}' is not an ending. A job finishes as Done or Failed; Queued and Running "
                + "are where it was, and Cancelled has no way to be reached yet.",
                nameof(status));
        }

        if (status == JobStatus.Failed && string.IsNullOrWhiteSpace(failure))
        {
            throw new ArgumentException(
                "A failure must say why. A job reporting only 'failed' is one nobody can act on, and "
                + "the store refuses it rather than trusting every caller to remember.",
                nameof(failure));
        }

        // <b>`progress` is set to 100 on success and left alone on failure.</b> A job that finished has
        // no more to do, and a failed one's last honest figure is where it stopped — overwriting that
        // with 100 would say it completed and with 0 would lose how far it got.
        //
        // <b>And a worker finishes only what it still holds — D-243.</b> Without the `claimed_by`
        // test, a worker whose lease lapsed would write its late answer over the one the next
        // claimant is producing.
        const string Sql = """
            update job
               set status      = @status,
                   finished_at = now(),
                   progress    = case when @status = 'done' then 100 else progress end,
                   detail      = coalesce(@detail, detail),
                   failure     = @failure,
                   lease_until = null
             where id = @id and status in ('queued', 'running')
               and (@worker is null or claimed_by = @worker)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", Wire(status));
        command.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("failure", (object?)failure ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("worker", NpgsqlDbType.Text)
        {
            Value = (object?)worker ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------ wire

    private static string Wire(JobKind kind) => kind switch
    {
        JobKind.GeodatabaseInspect => "geodatabase.inspect",
        JobKind.GeodatabaseImport => "geodatabase.import",

        // enum-default-is-deliberate: refused rather than defaulted. A kind this build does not know
        // has no check-constraint value, so guessing one would write a row the schema rejects — and
        // the exception here names the enum rather than leaving Postgres to complain about a string.
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "There is no stored name for this job kind."),
    };

    private static string Wire(JobStatus status) => status switch
    {
        JobStatus.Queued => "queued",
        JobStatus.Running => "running",
        JobStatus.Done => "done",
        JobStatus.Failed => "failed",
        JobStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "There is no stored name for this job status."),
    };

    // enum-default-is-deliberate: an unrecognised stored status reads as Failed, never as Done.
    //
    // <b>The direction is the assertion.</b> A row written by a newer build carrying a status this one
    // does not know must not read as *finished successfully* — the safe reading of *I do not understand
    // this* is the one that does not claim work was done. The opposite default is how a screen comes to
    // report a completed import that never happened.
    private static JobStatus ReadStatus(string stored) => stored switch
    {
        "queued" => JobStatus.Queued,
        "running" => JobStatus.Running,
        "done" => JobStatus.Done,
        "cancelled" => JobStatus.Cancelled,
        _ => JobStatus.Failed,
    };

    // enum-default-is-deliberate: the only kind there is, named rather than assumed.
    //
    // <b>A switch with one arm looks like a placeholder and is not.</b> Writing `_ =>
    // GeodatabaseImport` without naming the string would mean any stored value read as an import —
    // including a kind a newer build wrote — and the check constraint is the only thing standing
    // between that and a screen reporting work of a type it cannot perform. The named case is what
    // makes the second kind a compile-time question instead of a silent mis-read.
    private static JobKind ReadKind(string stored) => stored switch
    {
        "geodatabase.inspect" => JobKind.GeodatabaseInspect,
        "geodatabase.import" => JobKind.GeodatabaseImport,

        _ => throw new InvalidOperationException(
            $"'{stored}' is not a job kind this build knows. The schema's check constraint should "
            + "have refused it, so either a newer version wrote this row or the constraint was "
            + "dropped."),
    };

    private static JobRecord Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        ReadKind(reader.GetString(1)),
        ReadStatus(reader.GetString(2)),
        reader.GetInt32(3),
        reader.GetGuid(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),

        // <b>Which worker took it, and which request shape it was written in.</b> D-96: every
        // select that feeds this reader carries both, so the reader never has to work out from
        // the field count which query it is looking at.
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetInt32(12));
}
