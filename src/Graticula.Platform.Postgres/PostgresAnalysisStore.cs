using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>Analysis runs in the platform store — ADR-222, migration 110.</summary>
public sealed class PostgresAnalysisStore : IAnalysisStore
{
    private const string RunColumns = "a.task, a.parameters::text, a.results::text, a.messages::text, a.cancel_asked";

    private readonly NpgsqlDataSource _root;

    private NpgsqlDataSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Creates the store.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresAnalysisStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<AnalysisRun> CreateAsync(Guid owner, string task, string parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters);

        Guid id = Guid.NewGuid();

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand job = new(
                         """
                         insert into job (id, kind, status, owner_principal_id, subject, detail, protocol)
                         values (@id, @kind, 'queued', @owner, @subject, null, 1)
                         """,
                         connection,
                         transaction))
        {
            job.Parameters.AddWithValue("id", id);
            job.Parameters.AddWithValue("kind", PostgresJobStore.Wire(JobKind.Analysis));
            job.Parameters.AddWithValue("owner", owner);
            job.Parameters.AddWithValue("subject", task);
            await job.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand run = new(
                         "insert into analysis_job (job_id, task, parameters) values (@id, @task, @parameters)",
                         connection,
                         transaction))
        {
            run.Parameters.AddWithValue("id", id);
            run.Parameters.AddWithValue("task", task);
            run.Parameters.AddWithValue("parameters", NpgsqlDbType.Jsonb, parameters);
            await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return (await FindAsync(id, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc/>
    public async Task<AnalysisRun?> FindAsync(Guid job, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {PostgresJobStore.JobColumns}, {RunColumns} from analysis_job a join job j on j.id = a.job_id "
            + "where a.job_id = @id");
        command.Parameters.AddWithValue("id", job);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // The job's thirteen columns first, as `PostgresJobStore.Read` takes them.
        return new AnalysisRun(
            PostgresJobStore.Read(reader),
            reader.GetString(13),
            reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetString(16),
            reader.GetBoolean(17));
    }

    /// <inheritdoc/>
    public async Task RecordAsync(Guid job, string? results, string messages, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messages);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update analysis_job set results = @results, messages = @messages where job_id = @id");
        command.Parameters.AddWithValue("id", job);
        command.Parameters.AddWithValue("results", NpgsqlDbType.Jsonb, (object?)results ?? DBNull.Value);
        command.Parameters.AddWithValue("messages", NpgsqlDbType.Jsonb, messages);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> AskCancelAsync(Guid job, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update analysis_job set cancel_asked = true where job_id = @id");
        command.Parameters.AddWithValue("id", job);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
