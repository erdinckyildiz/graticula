using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Microsoft.Data.SqlClient;

namespace Graticula.Providers.SqlServer;

/// <summary>Turns a <see cref="SqlServerLocator"/> into the driver's connection string — ADR-210.</summary>
/// <remarks>
/// <b>Here and only here</b>, because this is the one project that may name the driver. The locator carries fields;
/// the driver's builder quotes whatever needs quoting.
/// </remarks>
internal static class SqlServerConnections
{
    /// <summary>The driver's connection string for a stored locator.</summary>
    /// <param name="stored">A <c>sqlserver:</c> locator, unsealed.</param>
    /// <param name="pooling">Whether to pool; a registration probe does not, a layer's source does.</param>
    /// <param name="timeoutSeconds">How long to wait for a connection.</param>
    /// <returns>The connection string.</returns>
    /// <remarks>
    /// <b>The server's certificate is trusted unless the registration asks for it to be verified</b> (ADR-210 §5.7,
    /// D-313): the driver encrypts by default since version 4, and a SQL Server installed without a certificate from a
    /// trusted authority — LocalDB, Express, most development and many internal servers — presents a self-signed one, so
    /// trusting it is the default that lets them connect at all. <see cref="SqlServerSource.VerifyCertificate"/> makes
    /// encryption mandatory and the certificate checked — its chain against this machine's trusted authorities and its
    /// name against the host — so a server impersonating the database is refused rather than read from.
    /// </remarks>
    public static string For(string stored, bool pooling, int timeoutSeconds = 15)
    {
        SqlServerSource source = SqlServerLocator.Of(stored);

        SqlConnectionStringBuilder builder = new()
        {
            DataSource = source.Port is { } port ? $"{source.Host},{port}" : source.Host,
            InitialCatalog = source.Database,
            UserID = source.User,
            Pooling = pooling,
            ConnectTimeout = Math.Max(1, timeoutSeconds),
            TrustServerCertificate = !source.VerifyCertificate,
            ApplicationName = "Graticula",
        };

        if (source.VerifyCertificate)
        {
            builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
        }

        if (!string.IsNullOrEmpty(source.Password))
        {
            builder.Password = source.Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Whether this credential may change rows of a base table — its <c>UPDATE</c> permission, on a table rather than a
    /// view, which is what every edit but an add needs; a refusal of the rest is said per edit.
    /// </summary>
    public static async Task<bool> CanWriteAsync(SqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        await using SqlCommand command = new(
            "select case when exists (select 1 from INFORMATION_SCHEMA.TABLES "
            + "where TABLE_SCHEMA = @s and TABLE_NAME = @t and TABLE_TYPE = 'BASE TABLE') "
            + "and has_perms_by_name(quotename(@s) + '.' + quotename(@t), 'OBJECT', 'UPDATE') = 1 then 1 else 0 end",
            connection);
        command.Parameters.AddWithValue("@s", schema);
        command.Parameters.AddWithValue("@t", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// An open connection. Every statement here is written for SQL Server's defaults through this driver —
    /// <c>QUOTED_IDENTIFIER ON</c>, so a double-quoted name is a name, and <c>ANSI_NULLS ON</c>.
    /// </summary>
    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        SqlConnection connection = new(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
