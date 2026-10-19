using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Oracle.ManagedDataAccess.Client;

namespace Graticula.Providers.Oracle;

/// <summary>Turns an <see cref="OracleLocator"/> into the driver's connection string — ADR-205.</summary>
/// <remarks>
/// <b>Here and only here</b>, because this is the one project that may name the driver. The locator carries fields;
/// the driver's builder quotes whatever needs quoting, so a password with a semicolon in it arrives intact.
/// </remarks>
internal static class OracleConnections
{
    /// <summary>The driver's connection string for a stored locator.</summary>
    /// <param name="stored">An <c>oracle:</c> locator, unsealed.</param>
    /// <param name="pooling">Whether to pool; a registration probe does not, a layer's source does.</param>
    /// <param name="timeoutSeconds">How long to wait for a connection.</param>
    /// <returns>The connection string.</returns>
    public static string For(string stored, bool pooling, int timeoutSeconds = 15)
    {
        OracleSource source = OracleLocator.Of(stored);

        OracleConnectionStringBuilder builder = new()
        {
            UserID = source.User,
            DataSource = $"//{source.Host}:{source.Port}/{source.Service}",
            Pooling = pooling,
            ConnectionTimeout = Math.Max(1, timeoutSeconds),
        };

        if (!string.IsNullOrEmpty(source.Password))
        {
            builder.Password = source.Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// A pooled connection with the session's formats pinned, so nothing depends on the server process's culture.
    /// </summary>
    /// <remarks>
    /// <b>The managed driver takes the session's language and territory from the .NET culture</b>, and a territory
    /// decides the decimal separator a <c>cast(… as varchar2)</c> writes and the order a text sort uses. Pinned here to
    /// a full stop and binary order — PostgreSQL's <c>C</c> collation's order. The reader and the writer both open here.
    /// </remarks>
    public static async Task<OracleConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        OracleConnection connection = new(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using OracleCommand session = new(
                "alter session set nls_territory = 'AMERICA' nls_language = 'AMERICAN' "
                + "nls_numeric_characters = '.,' nls_sort = 'BINARY' nls_comp = 'BINARY'",
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
    /// Whether the connected user may insert, update and delete rows of a table — ADR-205 phase 2 — so a layer is
    /// offered as editable only where an edit can land.
    /// </summary>
    /// <remarks>
    /// <b>The owner may; anybody else needs all three object grants, to them, to PUBLIC or to an enabled role
    /// (<c>ALL_TAB_PRIVS</c> lists those), or the three <c>ANY TABLE</c> system privileges.</b> Two of three is not
    /// editable: ArcGIS offers Create, Update and Delete together, and a layer that refuses one of them at the database
    /// would answer an edit its document invited with ORA-01031.
    /// </remarks>
    public static async Task<bool> CanWriteAsync(
        OracleConnection connection, string owner, string table, CancellationToken cancellationToken)
    {
        await using OracleCommand command = new(
            """
            select case
                     when :o = sys_context('USERENV', 'CURRENT_USER') then 1
                     when (select count(distinct privilege) from all_tab_privs
                            where table_schema = :o and table_name = :t
                              and privilege in ('INSERT', 'UPDATE', 'DELETE')) = 3 then 1
                     when (select count(*) from session_privs
                            where privilege in ('INSERT ANY TABLE', 'UPDATE ANY TABLE', 'DELETE ANY TABLE')) = 3 then 1
                     else 0
                   end
              from dual
            """, connection)
        {
            BindByName = true,
        };

        command.Parameters.Add("o", owner);
        command.Parameters.Add("t", table);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}
