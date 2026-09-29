using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// A tile export's record, in the platform store — migration 64, ADR-098.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every change is one statement or one transaction, conditional in SQL</b>, for the reason
/// <see cref="PostgresJobStore"/> gives, and the job row is inserted here beside the export's for the reason
/// <see cref="PostgresTileSeedStore"/> gives: the budget check and the insert must be one transaction.
/// </para>
/// <para>
/// <b>The budget lock is one advisory lock for the whole server</b>, not one per service, because the export budget
/// is shared by every service: two starts on two services racing for the last gigabyte must see each other.
/// </para>
/// </remarks>
public sealed class PostgresTileExportStore : ITileExportStore
{
    private const string ExportColumns =
        "e.service_id, e.format, e.levels, e.min_x, e.min_y, e.max_x, e.max_y, e.whole, e.total, e.done, e.stored, "
        + "e.estimated_bytes, e.bytes, e.token, e.scheme, e.origin, e.expires_at, e.removed_at, e.paused_until, "
        + "e.paused_because";

    /// <summary>What a live export holds: its package once written, its estimate until then.</summary>
    /// <remarks>
    /// <b>Live</b> is not removed and not failed or cancelled — a failed or cancelled export's partial file is
    /// deleted by the worker or the sweep, so it holds nothing it should be charged for.
    /// </remarks>
    private const string HeldSql = """
        select coalesce(sum(coalesce(e.bytes, e.estimated_bytes)), 0)::bigint
          from tile_export e
          join job j on j.id = e.job_id
         where e.removed_at is null and j.status in ('queued', 'running', 'done')
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store over a data source.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresTileExportStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>The stored name of a format, which is also its name on the wire.</summary>
    public static string Wire(TileExportFormat format) => format switch
    {
        TileExportFormat.Vtpk => "vtpk",
        TileExportFormat.PmTiles => "pmtiles",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no stored name for this format."),
    };

    private static TileExportFormat ReadFormat(string stored) => stored switch
    {
        "vtpk" => TileExportFormat.Vtpk,
        "pmtiles" => TileExportFormat.PmTiles,
        _ => throw new InvalidOperationException(
            $"'{stored}' is not an export format this build knows; the check constraint should have refused it."),
    };

    /// <inheritdoc/>
    public async Task<TileExportStart> StartAsync(
        Guid owner,
        TileExportRequest request,
        string subject,
        string detail,
        long budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        if (owner == Guid.Empty)
        {
            throw new ArgumentException("An export belongs to somebody, as every job does.", nameof(owner));
        }

        if (request.Levels.Count == 0)
        {
            throw new ArgumentException("An export covers at least one level.", nameof(request));
        }

        Guid id = Guid.NewGuid();

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand lockBudget = new(
                         "select pg_advisory_xact_lock(hashtextextended('tile.export:budget', 0))", connection, transaction))
        {
            await lockBudget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long held;

        await using (NpgsqlCommand used = new(HeldSql, connection, transaction))
        {
            held = (long)(await used.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        if (held + request.EstimatedBytes > budget)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new TileExportStart(null, held);
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
            job.Parameters.AddWithValue("kind", PostgresJobStore.Wire(JobKind.TileExport));
            job.Parameters.AddWithValue("owner", owner);
            job.Parameters.AddWithValue("subject", subject);
            job.Parameters.AddWithValue("detail", detail);
            await job.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand export = new(
                         """
                         insert into tile_export (job_id, service_id, format, levels, min_x, min_y, max_x, max_y, whole,
                                                  total, estimated_bytes, token, scheme, origin)
                         values (@id, @service, @format, @levels, @minx, @miny, @maxx, @maxy, @whole,
                                 @total, @estimated, @token, @scheme, @origin)
                         """,
                         connection,
                         transaction))
        {
            export.Parameters.AddWithValue("id", id);
            export.Parameters.AddWithValue("service", request.ServiceId);
            export.Parameters.AddWithValue("format", Wire(request.Format));
            export.Parameters.AddWithValue("levels", request.Levels.Select(level => (short)level).ToArray());
            export.Parameters.AddWithValue("minx", request.Area.MinX);
            export.Parameters.AddWithValue("miny", request.Area.MinY);
            export.Parameters.AddWithValue("maxx", request.Area.MaxX);
            export.Parameters.AddWithValue("maxy", request.Area.MaxY);
            export.Parameters.AddWithValue("whole", request.Whole);
            export.Parameters.AddWithValue("total", request.Total);
            export.Parameters.AddWithValue("estimated", Math.Max(0, request.EstimatedBytes));
            export.Parameters.AddWithValue("token", request.Token);
            export.Parameters.AddWithValue("scheme", request.Scheme);
            export.Parameters.AddWithValue("origin", request.Origin);
            await export.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new TileExportStart(await FindAsync(id, cancellationToken).ConfigureAwait(false), null);
    }

    /// <inheritdoc/>
    public async Task<TileExportState?> FindAsync(Guid job, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {PostgresJobStore.JobColumns}, {ExportColumns} "
            + "from tile_export e join job j on j.id = e.job_id where e.job_id = @id");
        command.Parameters.AddWithValue("id", job);

        IReadOnlyList<TileExportState> found = await ReadAsync(command, cancellationToken).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TileExportState>> ListAsync(
        Guid service, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {PostgresJobStore.JobColumns}, {ExportColumns} "
            + "from tile_export e join job j on j.id = e.job_id where e.service_id = @service "
            + "order by j.created_at desc limit @limit");
        command.Parameters.AddWithValue("service", service);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));

        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<long> HeldBytesAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(HeldSql);

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    /// <inheritdoc/>
    public async Task<bool> CheckpointAsync(
        Guid job,
        string worker,
        long done,
        long stored,
        DateTimeOffset? pausedUntil,
        string? pausedBecause,
        int percent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);

        // The seed's one statement: nothing is written unless the job is still running and still this worker's.
        const string Sql = """
            with mine as (
                select id from job
                 where id = @id and kind = 'tile.export' and status = 'running' and claimed_by = @worker
                 for update
            ),
            counted as (
                update tile_export
                   set done = @done, stored = @stored, paused_until = @until, paused_because = @because
                 where job_id in (select id from mine)
                returning job_id
            ),
            moved as (
                update job set progress = @percent where id in (select id from mine) returning id
            )
            select count(*) from mine
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", job);
        command.Parameters.AddWithValue("worker", worker);
        command.Parameters.AddWithValue("done", Math.Max(0, done));
        command.Parameters.AddWithValue("stored", Math.Max(0, stored));
        command.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz)
        {
            Value = pausedUntil is { } until ? until.ToUniversalTime() : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("because", NpgsqlDbType.Text)
        {
            Value = (object?)pausedBecause ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("percent", Math.Clamp(percent, 0, 100));

        object? held = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return held is long count && count == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> FinishAsync(
        Guid job, string worker, long bytes, long stored, TimeSpan retention, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);

        // <b>The job and the export in one statement</b>, so a package is never *done* without a size and an
        // expiry, and a cancel that landed first leaves both untouched and answers false.
        const string Sql = """
            with finished as (
                update job
                   set status = 'done', finished_at = now(), progress = 100, lease_until = null, failure = null
                 where id = @id and kind = 'tile.export' and status = 'running' and claimed_by = @worker
                returning id
            )
            update tile_export
               set bytes = @bytes, stored = @stored, done = total, expires_at = now() + @retention,
                   paused_until = null, paused_because = null
             where job_id in (select id from finished)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", job);
        command.Parameters.AddWithValue("worker", worker);
        command.Parameters.AddWithValue("bytes", Math.Max(0, bytes));
        command.Parameters.AddWithValue("stored", Math.Max(0, stored));
        command.Parameters.AddWithValue("retention", retention);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> CancelAsync(Guid job, CancellationToken cancellationToken)
    {
        // Restricted to this kind in the statement, as the seed's is to its own.
        const string Sql = """
            with stopped as (
                update job
                   set status = 'cancelled', finished_at = now(), lease_until = null
                 where id = @id and kind = 'tile.export' and status in ('queued', 'running')
                returning id
            )
            update tile_export set paused_until = null, paused_because = null
             where job_id in (select id from stopped)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", job);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> MarkRemovedAsync(Guid job, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update tile_export set removed_at = now() where job_id = @id and removed_at is null");
        command.Parameters.AddWithValue("id", job);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(Guid Job, string Token)>> DueForRemovalAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        const string Sql = """
            select e.job_id, e.token
              from tile_export e
              join job j on j.id = e.job_id
             where e.removed_at is null
               and ((j.status = 'done' and e.expires_at <= @now) or j.status in ('failed', 'cancelled'))
             order by j.created_at
             limit @limit
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));

        List<(Guid, string)> due = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            due.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return due;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<string>> LiveTokensAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select token from tile_export where removed_at is null");

        HashSet<string> tokens = new(StringComparer.Ordinal);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tokens.Add(reader.GetString(0));
        }

        return tokens;
    }

    /// <inheritdoc/>
    public async Task<TileExportPolicy?> SetPolicyAsync(
        Guid service, TileExportPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // The old value from inside the statement that replaces it, for the audit record's before and after.
        const string Sql = """
            update service s
               set export_tiles_allowed = @allowed,
                   export_tiles_anonymous = @anonymous,
                   max_export_tiles = @maximum,
                   updated_at = now()
              from (select id, export_tiles_allowed, export_tiles_anonymous, max_export_tiles
                      from service where id = @id for update) old
             where s.id = old.id
            returning old.export_tiles_allowed, old.export_tiles_anonymous, old.max_export_tiles
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", service);
        command.Parameters.AddWithValue("allowed", policy.Allowed);
        command.Parameters.AddWithValue("anonymous", policy.Anonymous);
        command.Parameters.Add(new NpgsqlParameter("maximum", NpgsqlDbType.Integer)
        {
            Value = (object?)policy.MaximumTiles ?? DBNull.Value,
        });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TileExportPolicy(
            reader.GetBoolean(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetInt32(2));
    }

    private static async Task<IReadOnlyList<TileExportState>> ReadAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<TileExportState> exports = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The job's thirteen columns first, as `PostgresJobStore.Read` takes them.
            const int At = 13;

            DateTimeOffset? When(int ordinal) =>
                reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

            exports.Add(new TileExportState(
                PostgresJobStore.Read(reader),
                reader.GetGuid(At),
                ReadFormat(reader.GetString(At + 1)),
                [.. reader.GetFieldValue<short[]>(At + 2).Select(level => (int)level)],
                new Envelope(
                    reader.GetDouble(At + 3), reader.GetDouble(At + 4), reader.GetDouble(At + 5), reader.GetDouble(At + 6)),
                reader.GetBoolean(At + 7),
                reader.GetInt64(At + 8),
                reader.GetInt64(At + 9),
                reader.GetInt64(At + 10),
                reader.GetInt64(At + 11),
                reader.IsDBNull(At + 12) ? null : reader.GetInt64(At + 12),
                reader.GetString(At + 13),
                reader.GetString(At + 14),
                reader.GetString(At + 15),
                When(At + 16),
                When(At + 17),
                When(At + 18),
                reader.IsDBNull(At + 19) ? null : reader.GetString(At + 19)));
        }

        return exports;
    }
}
