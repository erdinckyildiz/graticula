using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using MySqlConnector;

namespace Graticula.Providers.MySql;

/// <summary>Turns a <see cref="MySqlLocator"/> into the driver's connection string — ADR-208.</summary>
/// <remarks>
/// <b>Here and only here</b>, because this is the one project that may name the driver. The locator carries fields;
/// the driver's builder quotes whatever needs quoting.
/// </remarks>
internal static class MySqlConnections
{
    /// <summary>The driver's connection string for a stored locator.</summary>
    /// <param name="stored">A <c>mysql:</c> locator, unsealed.</param>
    /// <param name="pooling">Whether to pool; a registration probe does not, a layer's source does.</param>
    /// <param name="timeoutSeconds">How long to wait for a connection.</param>
    /// <returns>The connection string.</returns>
    public static string For(string stored, bool pooling, int timeoutSeconds = 15)
    {
        MySqlSource source = MySqlLocator.Of(stored);

        MySqlConnectionStringBuilder builder = new()
        {
            Server = source.Host,
            Port = (uint)source.Port,
            Database = source.Database,
            UserID = source.User,
            Pooling = pooling,
            ConnectionTimeout = (uint)Math.Max(1, timeoutSeconds),

            // A row's identity and a GUID come back as the types their columns declare, and a zero date is a null.
            GuidFormat = MySqlGuidFormat.None,
            ConvertZeroDateTime = true,
        };

        if (!string.IsNullOrEmpty(source.Password))
        {
            builder.Password = source.Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// A connection with the session pinned to what every statement here is written for: <c>ANSI</c> mode, so a
    /// double-quoted name is a name and <c>||</c> concatenates as in PostgreSQL; strict writes; and UTC, so a
    /// <c>TIMESTAMP</c> reads back the instant it holds.
    /// </summary>
    public static async Task<MySqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        MySqlConnection connection = new(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using MySqlCommand session = new(
                "set session sql_mode = 'ANSI,STRICT_TRANS_TABLES,NO_ZERO_DATE,NO_ENGINE_SUBSTITUTION', time_zone = '+00:00'",
                connection);

            await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Whether the connected user may insert, update and delete rows of a table — all three, at the global, database or
    /// table level, as <c>CanWriteAsync</c> asks of Oracle; two of three is not editable, for its reason.
    /// </summary>
    public static async Task<bool> CanWriteAsync(
        MySqlConnection connection, string database, string table, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = new(
            """
            select count(distinct p.privilege_type) from (
              select privilege_type from information_schema.user_privileges where grantee = @who
              union all
              select privilege_type from information_schema.schema_privileges where grantee = @who and table_schema = @db
              union all
              select privilege_type from information_schema.table_privileges
               where grantee = @who and table_schema = @db and table_name = @tab
            ) p where p.privilege_type in ('INSERT', 'UPDATE', 'DELETE')
            """, connection);

        command.Parameters.AddWithValue("@who", await GranteeAsync(connection, cancellationToken).ConfigureAwait(false));
        command.Parameters.AddWithValue("@db", database);
        command.Parameters.AddWithValue("@tab", table);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 3;
    }

    /// <summary>The connected account as <c>information_schema</c> writes a grantee: <c>'user'@'host'</c>.</summary>
    private static async Task<string> GranteeAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = new("select current_user()", connection);
        string account = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        int at = account.LastIndexOf('@');

        return at < 0 ? $"'{account}'" : $"'{account[..at]}'@'{account[(at + 1)..]}'";
    }
}
