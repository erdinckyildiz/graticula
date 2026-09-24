using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Server settings in the platform store's <c>server_setting</c> table — migration 54, ADR-084.
/// </summary>
public sealed class PostgresServerSettingStore : IServerSettingStore
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Reads and writes settings in a platform store.</summary>
    /// <param name="dataSource">The store.</param>
    public PostgresServerSettingStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<StoredSetting?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select value, changed_at from server_setting where name = @name");
        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredSetting(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1))
            : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>One statement each way, returning what was there</b>, so the audit record's *before* is the row
    /// this write replaced rather than a read that another writer could have raced.
    /// </remarks>
    public async Task<StoredSetting?> WriteAsync(
        string name, string? value, Guid? changedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        StoredSetting? before = null;

        await using (NpgsqlCommand read = new(
            "select value, changed_at from server_setting where name = @name for update", connection, transaction))
        {
            read.Parameters.AddWithValue("name", name);

            await using NpgsqlDataReader reader =
                await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                before = new StoredSetting(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));
            }
        }

        await using (NpgsqlCommand write = new(
            value is null
                ? "delete from server_setting where name = @name"
                : """
                  insert into server_setting (name, value, changed_at, changed_by)
                  values (@name, @value, now(), @by)
                  on conflict (name) do update
                     set value = excluded.value, changed_at = excluded.changed_at, changed_by = excluded.changed_by
                  """,
            connection,
            transaction))
        {
            write.Parameters.AddWithValue("name", name);

            if (value is not null)
            {
                write.Parameters.AddWithValue("value", value);
                write.Parameters.AddWithValue("by", (object?)changedBy ?? DBNull.Value);
            }

            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return before;
    }
}
