using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// A feature export's record, in the platform store — migration 68, ADR-106.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="PostgresTileExportStore"/>'s shape, deliberately.</b> Every change is one statement or one
/// transaction, conditional in SQL, and the job row is inserted here beside the export's, so the budget check and the
/// insert are one transaction. What differs is the row: rows counted and written instead of tiles, a phase, and the
/// caller — the job's owner — who is the only person besides an administrator who may see or download it.
/// </para>
/// <para>
/// <b>The lock and the sum are shared with the tile store</b> (<see cref="ExportBudget"/>), so one budget bounds both
/// kinds of file. <b>One export per caller is decided under the same lock</b>: a count made before the lock would let
/// two starts by the same caller each find none running.
/// </para>
/// </remarks>
public sealed class PostgresFeatureExportStore : IFeatureExportStore
{
    private const string ExportColumns =
        "e.service_id, e.format, e.layers, e.rows_total, e.rows_written, e.phase, e.estimated_bytes, e.bytes, "
        + "e.token, e.file_name, e.expires_at, e.removed_at";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store over a data source.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresFeatureExportStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>The stored name of a format, which is also its name on the wire.</summary>
    public static string Wire(FeatureExportFormat format) => format switch
    {
        FeatureExportFormat.GeoPackage => "gpkg",
        FeatureExportFormat.Shapefile => "shapefile",
        FeatureExportFormat.Excel => "xlsx",
        FeatureExportFormat.FileGeodatabase => "fgdb",
        FeatureExportFormat.Kml => "kml",
        FeatureExportFormat.Csv => "csv",
        FeatureExportFormat.GeoJson => "geojson",
        FeatureExportFormat.EsriJson => "esrijson",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "There is no stored name for this format."),
    };

    private static FeatureExportFormat ReadFormat(string stored) => stored switch
    {
        "gpkg" => FeatureExportFormat.GeoPackage,
        "shapefile" => FeatureExportFormat.Shapefile,
        "xlsx" => FeatureExportFormat.Excel,
        "fgdb" => FeatureExportFormat.FileGeodatabase,
        "kml" => FeatureExportFormat.Kml,
        "csv" => FeatureExportFormat.Csv,
        "geojson" => FeatureExportFormat.GeoJson,
        "esrijson" => FeatureExportFormat.EsriJson,
        _ => throw new InvalidOperationException(
            $"'{stored}' is not an export format this build knows; the check constraint should have refused it."),
    };

    private static string WirePhase(FeatureExportPhase phase) => phase switch
    {
        FeatureExportPhase.Reading => "reading",
        FeatureExportPhase.Writing => "writing",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "There is no stored name for this phase."),
    };

    // enum-default-is-deliberate: an unrecognised phase reads as the first, which claims nothing about the file.
    private static FeatureExportPhase ReadPhase(string stored) =>
        stored == "writing" ? FeatureExportPhase.Writing : FeatureExportPhase.Reading;

    /// <inheritdoc/>
    public async Task<FeatureExportStart> StartAsync(
        Guid owner,
        FeatureExportRequest request,
        string subject,
        string detail,
        long budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        if (owner == Guid.Empty)
        {
            throw new ArgumentException("An export belongs to somebody, as every job does.", nameof(owner));
        }

        if (request.Layers.Count == 0)
        {
            throw new ArgumentException("An export covers at least one layer.", nameof(request));
        }

        Guid id = Guid.NewGuid();

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand lockBudget = new(ExportBudget.LockSql, connection, transaction))
        {
            await lockBudget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // <b>One at a time per caller, decided under the lock</b> (ADR-106 §5.4). The row that is named is the caller's
        // own, so the refusal can say which export to wait for or cancel.
        await using (NpgsqlCommand running = new(
                         """
                         select f.job_id
                           from feature_export f
                           join job j on j.id = f.job_id
                          where j.owner_principal_id = @owner and j.status in ('queued', 'running')
                          order by j.created_at
                          limit 1
                         """,
                         connection,
                         transaction))
        {
            running.Parameters.AddWithValue("owner", owner);

            if (await running.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid already)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new FeatureExportStart(null, null, already);
            }
        }

        long held;

        await using (NpgsqlCommand used = new(ExportBudget.HeldSql, connection, transaction))
        {
            held = (long)(await used.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        if (held + request.EstimatedBytes > budget)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new FeatureExportStart(null, held, null);
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
            job.Parameters.AddWithValue("kind", PostgresJobStore.Wire(JobKind.FeatureExport));
            job.Parameters.AddWithValue("owner", owner);
            job.Parameters.AddWithValue("subject", subject);
            job.Parameters.AddWithValue("detail", detail);
            await job.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand export = new(
                         """
                         insert into feature_export (job_id, service_id, format, layers, rows_total, estimated_bytes,
                                                     token, file_name)
                         values (@id, @service, @format, @layers, @rows, @estimated, @token, @name)
                         """,
                         connection,
                         transaction))
        {
            export.Parameters.AddWithValue("id", id);
            export.Parameters.AddWithValue("service", request.ServiceId);
            export.Parameters.AddWithValue("format", Wire(request.Format));
            export.Parameters.AddWithValue("layers", request.Layers.ToArray());
            export.Parameters.AddWithValue("rows", Math.Max(0, request.RowsTotal));
            export.Parameters.AddWithValue("estimated", Math.Max(0, request.EstimatedBytes));
            export.Parameters.AddWithValue("token", request.Token);
            export.Parameters.AddWithValue("name", request.FileName);
            await export.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new FeatureExportStart(await FindAsync(id, cancellationToken).ConfigureAwait(false), null, null);
    }

    /// <inheritdoc/>
    public async Task<FeatureExportState?> FindAsync(Guid job, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {PostgresJobStore.JobColumns}, {ExportColumns} "
            + "from feature_export e join job j on j.id = e.job_id where e.job_id = @id");
        command.Parameters.AddWithValue("id", job);

        IReadOnlyList<FeatureExportState> found = await ReadAsync(command, cancellationToken).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FeatureExportState>> ListAsync(
        Guid service, Guid? owner, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {PostgresJobStore.JobColumns}, {ExportColumns} "
            + "from feature_export e join job j on j.id = e.job_id "
            + "where e.service_id = @service and (@owner::uuid is null or j.owner_principal_id = @owner::uuid) "
            + "order by j.created_at desc limit @limit");
        command.Parameters.AddWithValue("service", service);
        command.Parameters.Add(new NpgsqlParameter("owner", NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            Value = owner is { } who ? who : DBNull.Value,
        });
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));

        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<long> HeldBytesAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(ExportBudget.HeldSql);

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    /// <inheritdoc/>
    public async Task<bool> CheckpointAsync(
        Guid job, string worker, long rowsWritten, FeatureExportPhase phase, int percent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);

        // The tile export's one statement: nothing is written unless the job is still running and still this worker's.
        const string Sql = """
            with mine as (
                select id from job
                 where id = @id and kind = 'feature.export' and status = 'running' and claimed_by = @worker
                 for update
            ),
            counted as (
                update feature_export
                   set rows_written = @rows, phase = @phase
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
        command.Parameters.AddWithValue("rows", Math.Max(0, rowsWritten));
        command.Parameters.AddWithValue("phase", WirePhase(phase));
        command.Parameters.AddWithValue("percent", Math.Clamp(percent, 0, 100));

        object? held = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return held is long count && count == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> FinishAsync(
        Guid job, string worker, long bytes, long rowsWritten, TimeSpan retention, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);

        // <b>The job and the export in one statement</b>, so a file is never *done* without a size and an expiry, and
        // a cancel that landed first leaves both untouched and answers false.
        const string Sql = """
            with finished as (
                update job
                   set status = 'done', finished_at = now(), progress = 100, lease_until = null, failure = null
                 where id = @id and kind = 'feature.export' and status = 'running' and claimed_by = @worker
                returning id
            )
            update feature_export
               set bytes = @bytes, rows_written = @rows, expires_at = now() + @retention
             where job_id in (select id from finished)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", job);
        command.Parameters.AddWithValue("worker", worker);
        command.Parameters.AddWithValue("bytes", Math.Max(0, bytes));
        command.Parameters.AddWithValue("rows", Math.Max(0, rowsWritten));
        command.Parameters.AddWithValue("retention", retention);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> CancelAsync(Guid job, CancellationToken cancellationToken)
    {
        // Restricted to this kind in the statement, as the tile export's is to its own.
        const string Sql = """
            update job
               set status = 'cancelled', finished_at = now(), lease_until = null
             where id = @id and kind = 'feature.export' and status in ('queued', 'running')
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", job);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> MarkRemovedAsync(Guid job, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update feature_export set removed_at = now() where job_id = @id and removed_at is null");
        command.Parameters.AddWithValue("id", job);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(Guid Job, string Token)>> DueForRemovalAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        const string Sql = """
            select e.job_id, e.token
              from feature_export e
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
            "select token from feature_export where removed_at is null");

        HashSet<string> tokens = new(StringComparer.Ordinal);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tokens.Add(reader.GetString(0));
        }

        return tokens;
    }

    private static async Task<IReadOnlyList<FeatureExportState>> ReadAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<FeatureExportState> exports = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The job's thirteen columns first, as `PostgresJobStore.Read` takes them.
            const int At = 13;

            DateTimeOffset? When(int ordinal) =>
                reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

            exports.Add(new FeatureExportState(
                PostgresJobStore.Read(reader),
                reader.GetGuid(At),
                ReadFormat(reader.GetString(At + 1)),
                reader.GetFieldValue<int[]>(At + 2),
                reader.GetInt64(At + 3),
                reader.GetInt64(At + 4),
                ReadPhase(reader.GetString(At + 5)),
                reader.GetInt64(At + 6),
                reader.IsDBNull(At + 7) ? null : reader.GetInt64(At + 7),
                reader.GetString(At + 8),
                reader.GetString(At + 9),
                When(At + 10),
                When(At + 11)));
        }

        return exports;
    }
}
