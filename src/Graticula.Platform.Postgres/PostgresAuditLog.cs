using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary><see cref="IAuditLog"/> over the platform store.</summary>
/// <remarks>
/// <b>A failed write here fails the request.</b> The tempting alternative — log
/// the audit failure and carry on — makes the audit trail silently incomplete
/// exactly when the store is under stress, which is when its completeness
/// matters. An action that cannot be recorded has not been authorised to happen.
/// </remarks>
public sealed class PostgresAuditLog : IAuditLog
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the log.</summary>
    /// <param name="dataSource">The platform store pool.</param>
    public PostgresAuditLog(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task RecordAsync(AuditEvent entry, CancellationToken cancellationToken)
    {
        const string Sql = """
            insert into audit_event
              (id, principal_id, principal_name, source_address, action, resource, detail, succeeded)
            values (@id, @principal, @name, @address, @action, @resource, @detail::jsonb, @succeeded)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("name", entry.PrincipalName);
        command.Parameters.AddWithValue("action", entry.Action);
        command.Parameters.AddWithValue("detail", entry.Detail);
        command.Parameters.AddWithValue("succeeded", entry.Succeeded);

        // The anonymous principal has a real row, so its id is a real reference.
        // Only a principal that has since been deleted arrives as null here, and
        // the column keeps the name for exactly that case.
        command.Parameters.Add(new NpgsqlParameter("principal", NpgsqlDbType.Uuid)
        {
            Value = entry.Principal == Guid.Empty ? DBNull.Value : entry.Principal,
        });

        command.Parameters.Add(new NpgsqlParameter("address", NpgsqlDbType.Inet)
        {
            Value = entry.SourceAddress is null
                ? DBNull.Value
                : System.Net.IPAddress.Parse(entry.SourceAddress),
        });

        command.Parameters.Add(new NpgsqlParameter("resource", NpgsqlDbType.Text)
        {
            Value = (object?)entry.Resource ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
