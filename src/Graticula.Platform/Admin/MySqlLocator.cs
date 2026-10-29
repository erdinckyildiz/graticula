using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graticula.Platform.Admin;

/// <summary>Where a registered MySQL database is, written as the fields that name it — ADR-208.</summary>
/// <param name="Host">The server's host.</param>
/// <param name="Port">Its port.</param>
/// <param name="Database">The database (MySQL's schema) whose tables are offered.</param>
/// <param name="User">Who to connect as.</param>
/// <param name="Password">The password; never shown.</param>
public sealed record MySqlSource(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("password")] string? Password)
{
    /// <summary>The port a MySQL server answers on when nobody says otherwise.</summary>
    public const int DefaultPort = 3306;

    /// <summary>Where it is, with nothing secret in it: <c>host:port/database</c>.</summary>
    public string Address => $"{Host}:{Port}/{Database}";
}

/// <summary>
/// How a registered MySQL database is written where a connection string would be — ADR-208, as
/// <see cref="OracleLocator"/> writes an Oracle one and for its reasons: a prefix no PostgreSQL connection string has,
/// and JSON after it, so the password crosses unchanged and is left out by choosing fields.
/// </summary>
public static class MySqlLocator
{
    /// <summary>The prefix.</summary>
    public const string Scheme = "mysql:";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Whether a stored locator names a MySQL database.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns>Whether it begins with <see cref="Scheme"/>.</returns>
    public static bool Is(string? stored) =>
        stored is not null && stored.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>The locator for a MySQL database.</summary>
    /// <param name="connection">Its fields.</param>
    /// <returns>The string to seal and store.</returns>
    public static string For(MySqlSource connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Scheme + JsonSerializer.Serialize(connection, Json);
    }

    /// <summary>The fields a locator carries, password included — never to be shown.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The fields.</returns>
    /// <exception cref="ArgumentException">When it is not a MySQL locator, or its JSON does not name a database.</exception>
    public static MySqlSource Of(string stored)
    {
        if (!Is(stored))
        {
            throw new ArgumentException("That locator does not name a MySQL database.", nameof(stored));
        }

        MySqlSource? read;

        try
        {
            read = JsonSerializer.Deserialize<MySqlSource>(stored[Scheme.Length..], Json);
        }
        catch (JsonException e)
        {
            throw new ArgumentException("That MySQL locator is not readable.", nameof(stored), e);
        }

        if (read is null || string.IsNullOrWhiteSpace(read.Host) || string.IsNullOrWhiteSpace(read.User)
            || read.Database is null || read.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("That MySQL locator does not name a host, a port, a database and a user.", nameof(stored));
        }

        return read;
    }

    /// <summary>The same locator without its password, for showing.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The locator with the password left out.</returns>
    public static string WithoutSecrets(string stored) => For(Of(stored) with { Password = null });

    /// <summary>The database a locator names, for telling two apart: <c>host:port/database</c>, lower-cased.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The address; a rotated password or a different user keeps it.</returns>
    public static string DatabaseKey(string stored) => Of(stored).Address.ToLowerInvariant();
}

/// <summary>
/// The registered SQL databases other than PostgreSQL — Oracle (ADR-205), MySQL (ADR-208) and SQL Server (ADR-210) —
/// asked as one, so a place that has only a locator answers for each without a branch each (ADR-208 §5.6).
/// </summary>
public static class SqlLocators
{
    /// <summary>The fields a registered SQL database is named by, without its password.</summary>
    /// <param name="Kind">Its kind (<see cref="DataSourceKinds"/>).</param>
    /// <param name="Host">The host.</param>
    /// <param name="Port">The port.</param>
    /// <param name="Database">The database — an Oracle service, a MySQL database.</param>
    /// <param name="User">The user.</param>
    /// <param name="HasPassword">Whether a password is stored.</param>
    /// <param name="VerifyCertificate">Whether the server's certificate is verified — SQL Server's only (D-313).</param>
    public sealed record Fields(string Kind, string Host, int? Port, string Database, string User, bool HasPassword, bool VerifyCertificate = false);

    /// <summary>Whether a stored locator names a registered SQL database other than PostgreSQL.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns>Whether it is Oracle's or MySQL's.</returns>
    public static bool Is(string? stored) => OracleLocator.Is(stored) || MySqlLocator.Is(stored) || SqlServerLocator.Is(stored);

    /// <summary>Its kind, or null when it is neither.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns><see cref="DataSourceKinds.Oracle"/>, <see cref="DataSourceKinds.MySql"/> or null.</returns>
    public static string? KindOf(string? stored) =>
        OracleLocator.Is(stored) ? DataSourceKinds.Oracle
        : MySqlLocator.Is(stored) ? DataSourceKinds.MySql
        : SqlServerLocator.Is(stored) ? DataSourceKinds.SqlServer
        : null;

    /// <summary>The engine's name, for a sentence.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns><c>Oracle</c> or <c>MySQL</c>.</returns>
    public static string NameOf(string stored) => MySqlLocator.Is(stored) ? "MySQL" : SqlServerLocator.Is(stored) ? "SQL Server" : "Oracle";

    /// <summary>The same locator without its password.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The locator, password left out.</returns>
    public static string WithoutSecrets(string stored) =>
        MySqlLocator.Is(stored) ? MySqlLocator.WithoutSecrets(stored)
        : SqlServerLocator.Is(stored) ? SqlServerLocator.WithoutSecrets(stored)
        : OracleLocator.WithoutSecrets(stored);

    /// <summary>Where it is, with nothing secret in it: <c>host:port/database</c>.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The address.</returns>
    public static string Address(string stored) =>
        MySqlLocator.Is(stored) ? MySqlLocator.Of(stored).Address
        : SqlServerLocator.Is(stored) ? SqlServerLocator.Of(stored).Address
        : OracleLocator.Of(stored).Address;

    /// <summary>The database it names, for telling two apart, prefixed with its kind.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The key.</returns>
    public static string DatabaseKey(string stored) =>
        MySqlLocator.Is(stored) ? "mysql:" + MySqlLocator.DatabaseKey(stored)
        : SqlServerLocator.Is(stored) ? "sqlserver:" + SqlServerLocator.DatabaseKey(stored)
        : "oracle:" + OracleLocator.DatabaseKey(stored);

    /// <summary>Its fields, for showing.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The fields, the password only as whether there is one.</returns>
    public static Fields FieldsOf(string stored)
    {
        if (MySqlLocator.Is(stored))
        {
            MySqlSource mysql = MySqlLocator.Of(stored);
            return new Fields(DataSourceKinds.MySql, mysql.Host, mysql.Port, mysql.Database, mysql.User, !string.IsNullOrEmpty(mysql.Password));
        }

        if (SqlServerLocator.Is(stored))
        {
            SqlServerSource sql = SqlServerLocator.Of(stored);
            return new Fields(DataSourceKinds.SqlServer, sql.Host, sql.Port, sql.Database, sql.User, !string.IsNullOrEmpty(sql.Password), sql.VerifyCertificate);
        }

        OracleSource oracle = OracleLocator.Of(stored);
        return new Fields(DataSourceKinds.Oracle, oracle.Host, oracle.Port, oracle.Service, oracle.User, !string.IsNullOrEmpty(oracle.Password));
    }
}
