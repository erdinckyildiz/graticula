using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// A tile seed's record, in the platform store — migration 61, ADR-093.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every change is one statement or one transaction, conditional in SQL</b>, for the reason
/// <see cref="PostgresJobStore"/> gives: a read followed by a write is a race even with one worker,
/// because a retried request is a second caller.
/// </para>
/// <para>
/// <b>The job row is written here as well as by <see cref="PostgresJobStore.CreateAsync"/></b>, and
/// that is a second copy of one insert chosen on purpose: a seed is refused while another runs for
/// the same service, and the check and the insert must be one transaction. The columns are the ones
/// <c>CreateAsync</c> writes, and the job is claimed, renewed, reclaimed and listed by the job store
/// exactly as every other kind is.
/// </para>
/// </remarks>
public sealed class PostgresTileSeedStore : ITileSeedStore
{
    private const string SeedColumns =
        "s.service_id, s.min_zoom, s.max_zoom, s.min_x, s.min_y, s.max_x, s.max_y, s.whole, "
        + "s.concurrency, s.total, s.paused_until, s.paused_because";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store over a data source.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresTileSeedStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<TileSeedStart> StartAsync(
        Guid owner,
        TileSeedRequest request,
        string subject,
        string detail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        if (owner == Guid.Empty)
        {
            throw new ArgumentException("A seed belongs to somebody, as every job does.", nameof(owner));
        }

        if (request.Levels.Count == 0)
        {
            throw new ArgumentException("A seed covers at least one level.", nameof(request));
        }

        Guid id = Guid.NewGuid();

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // <b>An advisory lock on the service, held to the end of the transaction.</b> The question
        // *is a seed of this service queued or running* is answered from the job table, which no
        // constraint on this table can see; without the lock two operators pressing Start at once
        // would both find none and both insert. The key is the service's own id, hashed with a
        // prefix so it cannot collide with a lock anything else takes on the same number.
        await using (NpgsqlCommand lockService = new(
                         "select pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction))
        {
            lockService.Parameters.AddWithValue("key", "tile.seed:" + request.ServiceId.ToString("N"));
            await lockService.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand running = new(
                         """
                         select s.job_id
                           from tile_seed s
                           join job j on j.id = s.job_id
                          where s.service_id = @service and j.status in ('queued', 'running')
                          order by j.created_at
                          limit 1
                         """,
                         connection,
                         transaction))
        {
            running.Parameters.AddWithValue("service", request.ServiceId);

            if (await running.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid already)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new TileSeedStart(null, already);
            }
        }

        await using (NpgsqlCommand job = new(
                         """
                         insert into job (id, kind, status, owner_principal_id, subject, detail, protocol)
                         values (@id, @kind, 'queued', @owner, @subject, @detail, 1)
                         """,
                         connection,
                         transaction))
        {
            job.Parameters.AddWithValue("id", id);
            job.Parameters.AddWithValue("kind", PostgresJobStore.Wire(JobKind.TileSeed));
            job.Parameters.AddWithValue("owner", owner);
            job.Parameters.AddWithValue("subject", subject);
            job.Parameters.AddWithValue("detail", detail);
            await job.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long total = request.Levels.Sum(level => level.Total);

        await using (NpgsqlCommand seed = new(
                         """
                         insert into tile_seed (job_id, service_id, min_zoom, max_zoom, min_x, min_y, max_x, max_y,
                                                whole, concurrency, total)
                         values (@id, @service, @min, @max, @minx, @miny, @maxx, @maxy, @whole, @concurrency, @total)
                         """,
                         connection,
                         transaction))
        {
            seed.Parameters.AddWithValue("id", id);
            seed.Parameters.AddWithValue("service", request.ServiceId);
            seed.Parameters.AddWithValue("min", (short)request.Levels.Min(level => level.Zoom));
            seed.Parameters.AddWithValue("max", (short)request.Levels.Max(level => level.Zoom));
            seed.Parameters.AddWithValue("minx", request.Area.MinX);
            seed.Parameters.AddWithValue("miny", request.Area.MinY);
            seed.Parameters.AddWithValue("maxx", request.Area.MaxX);
            seed.Parameters.AddWithValue("maxy", request.Area.MaxY);
            seed.Parameters.AddWithValue("whole", request.Whole);
            seed.Parameters.AddWithValue("concurrency", (short)Math.Max(1, request.Concurrency));
            seed.Parameters.AddWithValue("total", total);
            await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand levels = new(
                         """
                         insert into tile_seed_level (job_id, zoom, total)
                         select @id, v.zoom, v.total from unnest(@zooms, @totals) as v(zoom, total)
                         """,
                         connection,
                         transaction))
        {
            levels.Parameters.AddWithValue("id", id);
            levels.Parameters.AddWithValue("zooms", request.Levels.Select(level => (short)level.Zoom).ToArray());
            levels.Parameters.AddWithValue("totals", request.Levels.Select(level => level.Total).ToArray());
            await levels.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new TileSeedStart(
            await FindAsync(id, cancellationToken).ConfigureAwait(false), null);
    }

    /// <inheritdoc/>
    public async Task<TileSeedState?> FindAsync(Guid job, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {PostgresJobStore.JobColumns}, {SeedColumns} "
            + "from tile_seed s join job j on j.id = s.job_id where s.job_id = @id");
        command.Parameters.AddWithValue("id", job);

        IReadOnlyList<TileSeedState> found =
            await ReadAsync(command, cancellationToken).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TileSeedState>> ListAsync(
        Guid service, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {PostgresJobStore.JobColumns}, {SeedColumns} "
            + "from tile_seed s join job j on j.id = s.job_id where s.service_id = @service "
            + "order by j.created_at desc limit @limit");
        command.Parameters.AddWithValue("service", service);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));

        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> CheckpointAsync(
        Guid job,
        string worker,
        IReadOnlyList<TileSeedLevel> levels,
        DateTimeOffset? pausedUntil,
        string? pausedBecause,
        int percent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        ArgumentNullException.ThrowIfNull(levels);

        // <b>One statement, and every part of it is conditional on the same row.</b> The job must
        // still be running and still this worker's; if it is not — cancelled, finished, or taken
        // back by the lease sweep — nothing is written and the answer is false, which is how the
        // worker learns to stop. A data-modifying CTE runs to completion whether or not the outer
        // select reads it, so the three updates land together or not at all.
        //
        // <b>The level's times are the store's clock, not the worker's.</b> A level is started when
        // its first tile is done and finished when its last is, and `coalesce` keeps the first
        // answer: a resumed seed does not restart a level's clock.
        const string Sql = """
            with mine as (
                select id from job
                 where id = @id and kind = 'tile.seed' and status = 'running' and claimed_by = @worker
                 for update
            ),
            paused as (
                update tile_seed set paused_until = @until, paused_because = @because
                 where job_id in (select id from mine)
                returning job_id
            ),
            moved as (
                update job set progress = @percent where id in (select id from mine) returning id
            ),
            counted as (
                update tile_seed_level l
                   set done = v.done, built = v.built, present = v.present, empty = v.empty,
                       failed = v.failed, skipped = v.skipped,
                       started_at = coalesce(l.started_at, case when v.done > 0 then now() end),
                       finished_at = coalesce(l.finished_at, case when v.done = l.total then now() end)
                  from unnest(@zoom, @done, @built, @present, @empty, @failed, @skipped)
                       as v(zoom, done, built, present, empty, failed, skipped)
                 where l.job_id in (select id from mine) and l.zoom = v.zoom
                returning l.zoom
            )
            select count(*) from mine
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", job);
        command.Parameters.AddWithValue("worker", worker);
        command.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz)
        {
            Value = pausedUntil is { } until ? until.ToUniversalTime() : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("because", NpgsqlDbType.Text)
        {
            Value = (object?)pausedBecause ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("percent", Math.Clamp(percent, 0, 100));
        command.Parameters.AddWithValue("zoom", levels.Select(level => (short)level.Zoom).ToArray());
        command.Parameters.AddWithValue("done", levels.Select(level => level.Done).ToArray());
        command.Parameters.AddWithValue("built", levels.Select(level => level.Built).ToArray());
        command.Parameters.AddWithValue("present", levels.Select(level => level.Present).ToArray());
        command.Parameters.AddWithValue("empty", levels.Select(level => level.Empty).ToArray());
        command.Parameters.AddWithValue("failed", levels.Select(level => level.Failed).ToArray());
        command.Parameters.AddWithValue("skipped", levels.Select(level => level.Skipped).ToArray());

        object? held = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return held is long count && count == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> CancelAsync(Guid job, CancellationToken cancellationToken)
    {
        // <b>Restricted to this kind in the statement, not only by the route that calls it.</b>
        // Cancelling an import leaves a half-filled table, and that decision has not been taken
        // (ADR-093 §5.4); a caller that found this method and passed an import's id must not be
        // able to take it.
        const string Sql = """
            with stopped as (
                update job
                   set status = 'cancelled', finished_at = now(), lease_until = null
                 where id = @id and kind = 'tile.seed' and status in ('queued', 'running')
                returning id
            )
            update tile_seed set paused_until = null, paused_because = null
             where job_id in (select id from stopped)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", job);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TileSeedZoom>> LastSeededAsync(
        Guid service, CancellationToken cancellationToken)
    {
        // <b>A level counts as seeded when some seed finished it</b>, whatever became of the rest of
        // that seed — a seed cancelled at level 12 still filled levels 0 to 11, and saying otherwise
        // would send an operator to seed them again.
        const string Sql = """
            select distinct on (l.zoom)
                   l.zoom, l.finished_at, s.job_id, s.min_x, s.min_y, s.max_x, s.max_y
              from tile_seed_level l
              join tile_seed s on s.job_id = l.job_id
             where s.service_id = @service and l.finished_at is not null
             order by l.zoom, l.finished_at desc
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("service", service);

        List<TileSeedZoom> answer = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            answer.Add(new TileSeedZoom(
                reader.GetInt16(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetGuid(2),
                new Envelope(reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6))));
        }

        return answer;
    }

    private async Task<IReadOnlyList<TileSeedState>> ReadAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<(JobRecord Job, Guid Service, int Min, int Max, Envelope Area, bool Whole, int Concurrency,
            long Total, DateTimeOffset? Until, string? Because)> seeds = [];

        await using (NpgsqlDataReader reader =
                         await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // The job's thirteen columns first, as `PostgresJobStore.Read` takes them.
                seeds.Add((
                    PostgresJobStore.Read(reader),
                    reader.GetGuid(13),
                    reader.GetInt16(14),
                    reader.GetInt16(15),
                    new Envelope(reader.GetDouble(16), reader.GetDouble(17), reader.GetDouble(18), reader.GetDouble(19)),
                    reader.GetBoolean(20),
                    reader.GetInt16(21),
                    reader.GetInt64(22),
                    reader.IsDBNull(23) ? null : reader.GetFieldValue<DateTimeOffset>(23),
                    reader.IsDBNull(24) ? null : reader.GetString(24)));
            }
        }

        if (seeds.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, List<TileSeedLevel>> levels = seeds.ToDictionary(seed => seed.Job.Id, _ => new List<TileSeedLevel>());

        await using (NpgsqlCommand read = _dataSource.CreateCommand(
                         """
                         select job_id, zoom, total, done, built, present, empty, failed, skipped,
                                started_at, finished_at
                           from tile_seed_level
                          where job_id = any(@ids)
                          order by job_id, zoom
                         """))
        {
            read.Parameters.AddWithValue("ids", seeds.Select(seed => seed.Job.Id).ToArray());

            await using NpgsqlDataReader reader =
                await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                levels[reader.GetGuid(0)].Add(new TileSeedLevel(
                    reader.GetInt16(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                    reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10)));
            }
        }

        return
        [
            .. seeds.Select(seed => new TileSeedState(
                seed.Job, seed.Service, seed.Min, seed.Max, seed.Area, seed.Whole, seed.Concurrency,
                seed.Total, seed.Until, seed.Because, levels[seed.Job.Id])),
        ];
    }
}
