using System;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Schema;
using Npgsql;

namespace GisServer.Platform.Postgres;

/// <summary>
/// The PostgreSQL implementation of <see cref="IPlatformSchemaStore"/>.
/// </summary>
/// <remarks>
/// Q-70 made PostgreSQL the only platform store there will be, so this is the
/// only implementation this port will ever have. The port exists because
/// <c>build-vs-adopt-policy.md</c> §4 forbids a driver type in a Tier 1
/// signature, not because a second one is expected.
/// </remarks>
public sealed class PostgresPlatformSchemaStore : IPlatformSchemaStore
{
    /// <summary>PostgreSQL <c>undefined_table</c>. The bootstrap signal.</summary>
    private const string UndefinedTable = "42P01";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a store over an existing data source.</summary>
    public PostgresPlatformSchemaStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<SchemaStamp?> ReadStampAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select applied_version, minimum_reader_version from platform_schema");

        try
        {
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // The table exists but holds no row. That is not "un-migrated" —
                // it is a store that lost its stamp, which would let the migrator
                // re-run migration 1 over a populated database. Refuse loudly.
                throw new InvalidOperationException(
                    "The platform_schema table exists but is empty. The store's schema level is "
                    + "unknown, so migrating it could re-run migrations against existing data. "
                    + "This needs an operator, not a retry.");
            }

            return new SchemaStamp(
                new SchemaVersion(reader.GetInt32(0)),
                new SchemaVersion(reader.GetInt32(1)));
        }
        catch (PostgresException e) when (e.SqlState == UndefinedTable)
        {
            // The bootstrap case, and the reason this is caught by SQL state
            // rather than by message text: the migrator cannot read a version
            // from the table its own first migration creates.
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task ApplyAsync(
        Migration migration, SchemaStamp resultingStamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(resultingStamp);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // The port's contract: statements and stamp are one atomic unit.
        // PostgreSQL has transactional DDL, so this is genuinely atomic rather
        // than best-effort. Without it a crash between the two leaves the store
        // either migrated while claiming it is not, or claiming a level it never
        // reached — corruption that presents as something else.
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (string statement in migration.Statements)
        {
            await using NpgsqlCommand command = new(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteStampAsync(connection, transaction, resultingStamp, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteStampAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SchemaStamp stamp,
        CancellationToken cancellationToken)
    {
        // The single-row constraint in the schema means the conflict target is
        // fixed, so this is an upsert that cannot accidentally add a second row.
        await using NpgsqlCommand command = new(
            """
            insert into platform_schema (only_row, applied_version, minimum_reader_version, applied_at)
            values (true, @applied, @reader, now())
            on conflict (only_row) do update
                set applied_version        = excluded.applied_version,
                    minimum_reader_version = excluded.minimum_reader_version,
                    applied_at             = excluded.applied_at
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("applied", stamp.Applied.Value);
        command.Parameters.AddWithValue("reader", stamp.MinimumReader.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
