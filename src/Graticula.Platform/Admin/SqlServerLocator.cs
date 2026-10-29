using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graticula.Platform.Admin;

/// <summary>Where a registered SQL Server database is, written as the fields that name it — ADR-210.</summary>
/// <param name="Host">
/// The server: a host name, or a host and an instance (<c>host\SQLEXPRESS</c>), or a LocalDB instance
/// (<c>(localdb)\MSSQLLocalDB</c>), which SQL Server's driver reaches by name rather than by port.
/// </param>
/// <param name="Port">Its TCP port, or null when the host names an instance the driver finds by itself.</param>
/// <param name="Database">The database whose tables are offered.</param>
/// <param name="User">Who to connect as, by SQL Server authentication.</param>
/// <param name="Password">The password; never shown.</param>
/// <param name="VerifyCertificate">
/// Whether the server's certificate must be issued by an authority this machine trusts and name the host — D-313. False,
/// the default and every locator written before the field existed, encrypts and accepts any certificate, which is what
/// LocalDB, Express and most internal servers need.
/// </param>
public sealed record SqlServerSource(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int? Port,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("verifyCertificate")] bool VerifyCertificate = false)
{
    /// <summary>The port a SQL Server answers on when nobody says otherwise.</summary>
    public const int DefaultPort = 1433;

    /// <summary>Where it is, with nothing secret in it: <c>host[:port]/database</c>.</summary>
    public string Address => Port is { } port ? $"{Host}:{port}/{Database}" : $"{Host}/{Database}";
}

/// <summary>
/// How a registered SQL Server database is written where a connection string would be — ADR-210, as
/// <see cref="OracleLocator"/> and <see cref="MySqlLocator"/> write theirs and for their reasons: a prefix no PostgreSQL
/// connection string has, and JSON after it, so the password crosses unchanged and is left out by choosing fields.
/// </summary>
public static class SqlServerLocator
{
    /// <summary>The prefix.</summary>
    public const string Scheme = "sqlserver:";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Whether a stored locator names a SQL Server database.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns>Whether it begins with <see cref="Scheme"/>.</returns>
    public static bool Is(string? stored) =>
        stored is not null && stored.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>The locator for a SQL Server database.</summary>
    /// <param name="connection">Its fields.</param>
    /// <returns>The string to seal and store.</returns>
    public static string For(SqlServerSource connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Scheme + JsonSerializer.Serialize(connection, Json);
    }

    /// <summary>The fields a locator carries, password included — never to be shown.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The fields.</returns>
    /// <exception cref="ArgumentException">When it is not a SQL Server locator, or it does not name a database.</exception>
    public static SqlServerSource Of(string stored)
    {
        if (!Is(stored))
        {
            throw new ArgumentException("That locator does not name a SQL Server database.", nameof(stored));
        }

        SqlServerSource? read;

        try
        {
            read = JsonSerializer.Deserialize<SqlServerSource>(stored[Scheme.Length..], Json);
        }
        catch (JsonException e)
        {
            throw new ArgumentException("That SQL Server locator is not readable.", nameof(stored), e);
        }

        if (read is null || string.IsNullOrWhiteSpace(read.Host) || string.IsNullOrWhiteSpace(read.User)
            || string.IsNullOrWhiteSpace(read.Database) || read.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("That SQL Server locator does not name a host, a database and a user.", nameof(stored));
        }

        return read;
    }

    /// <summary>The same locator without its password, for showing.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The locator with the password left out.</returns>
    public static string WithoutSecrets(string stored) => For(Of(stored) with { Password = null });

    /// <summary>The database a locator names, for telling two apart, lower-cased.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The address; a rotated password or a different user keeps it.</returns>
    public static string DatabaseKey(string stored) => Of(stored).Address.ToLowerInvariant();
}
