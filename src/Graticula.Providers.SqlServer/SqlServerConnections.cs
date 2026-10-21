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
    /// <b>The server's certificate is trusted, not verified</b> (ADR-210 §6): the driver encrypts by default since
    /// version 4, and a SQL Server installed without a certificate from a trusted authority — LocalDB, Express, most
    /// development and many internal servers — presents a self-signed one. Encryption is kept; who answered is not
    /// proved. Recorded as a debt rather than offered as a setting nobody has asked for yet.
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
            TrustServerCertificate = true,
            ApplicationName = "Graticula",
        };

        if (!string.IsNullOrEmpty(source.Password))
        {
            builder.Password = source.Password;
        }

        return builder.ConnectionString;
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
