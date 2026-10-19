using System;
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
}
