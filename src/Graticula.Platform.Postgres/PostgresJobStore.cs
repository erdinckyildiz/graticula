using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// The job record, in the platform store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Migration 28's table, and nothing more.</b> No queue, no lease, no claim protocol — see
/// <see cref="IJobStore"/> for why the absence is load-bearing rather than unfinished.
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
        CancellationToken cancellationToken)
    {
        if (owner == Guid.Empty)
        {
            throw new ArgumentException(
                "A job belongs to somebody. An empty owner would make 'whose is this' unanswerable "
                + "for the rows where it matters most — an import that wrote into their content.",
                nameof(owner));
        }

        const string Sql = """
            insert into job (id, kind, status, owner_principal_id, subject, detail)
            values (@id, @kind, 'queued', @owner, @subject, @detail)
            returning created_at
            """;

        Guid id = Guid.NewGuid();

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("kind", Wire(kind));
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);

        object? created = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return new JobRecord(
            id, kind, JobStatus.Queued, 0, owner, subject, detail, null,
            created is DateTimeOffset at ? at : DateTimeOffset.UtcNow, null, null);
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
                   created_at, started_at, finished_at
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
                   created_at, started_at, finished_at
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
        CancellationToken cancellationToken)
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
        const string Sql = """
            update job
               set status      = @status,
                   finished_at = now(),
                   progress    = case when @status = 'done' then 100 else progress end,
                   detail      = coalesce(@detail, detail),
                   failure     = @failure
             where id = @id and status in ('queued', 'running')
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", Wire(status));
        command.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("failure", (object?)failure ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------ wire

    private static string Wire(JobKind kind) => kind switch
    {
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
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));
}
