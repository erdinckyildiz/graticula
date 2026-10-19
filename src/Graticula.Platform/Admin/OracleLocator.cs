using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graticula.Platform.Admin;

/// <summary>Where a registered Oracle database is, written as the fields that name it — ADR-205.</summary>
/// <param name="Host">The listener's host.</param>
/// <param name="Port">The listener's port.</param>
/// <param name="Service">The service name, a pluggable database's included.</param>
/// <param name="User">Who to connect as.</param>
/// <param name="Password">The password; never shown.</param>
public sealed record OracleSource(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("password")] string? Password)
{
    /// <summary>The port an Oracle listener answers on when nobody says otherwise.</summary>
    public const int DefaultPort = 1521;

    /// <summary>Where it is, with nothing secret in it: <c>host:port/service</c>.</summary>
    public string Address => $"{Host}:{Port}/{Service}";
}

/// <summary>
/// How a registered Oracle database is written where a connection string would be — ADR-205.
/// </summary>
/// <remarks>
/// <para>
/// <b>A prefix, as every engine other than PostGIS has one</b> (<see cref="GeoParquetLocator"/>'s remarks say why):
/// the sealed string reaches a dozen places that have only the string, and one that does not look would hand it to
/// Npgsql. <c>oracle:</c> is not how any PostgreSQL connection string begins.
/// </para>
/// <para>
/// <b>JSON after the prefix, not an ODP.NET connection string.</b> The fields are what the console sends; JSON
/// carries a password with a semicolon, an equals sign or a quote in it unchanged, where a connection string would
/// need Oracle's quoting rules got right on the way in and on the way out; and taking the password out for display
/// is choosing fields rather than parsing text. The provider builds the driver's string from these fields, so the
/// driver is referenced there and nowhere else.
/// </para>
/// </remarks>
public static class OracleLocator
{
    /// <summary>The prefix.</summary>
    public const string Scheme = "oracle:";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Whether a stored locator names an Oracle database.</summary>
    /// <param name="stored">The unsealed locator.</param>
    /// <returns>Whether it begins with <see cref="Scheme"/>.</returns>
    public static bool Is(string? stored) =>
        stored is not null && stored.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>The locator for an Oracle database.</summary>
    /// <param name="connection">Its fields.</param>
    /// <returns>The string to seal and store.</returns>
    public static string For(OracleSource connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Scheme + JsonSerializer.Serialize(connection, Json);
    }

    /// <summary>The fields a locator carries, password included — never to be shown.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The fields.</returns>
    /// <exception cref="ArgumentException">When it is not an Oracle locator, or its JSON does not name a database.</exception>
    public static OracleSource Of(string stored)
    {
        if (!Is(stored))
        {
            throw new ArgumentException("That locator does not name an Oracle database.", nameof(stored));
        }

        OracleSource? read;

        try
        {
            read = JsonSerializer.Deserialize<OracleSource>(stored[Scheme.Length..], Json);
        }
        catch (JsonException e)
        {
            throw new ArgumentException("That Oracle locator is not readable.", nameof(stored), e);
        }

        if (read is null || string.IsNullOrWhiteSpace(read.Host) || string.IsNullOrWhiteSpace(read.Service)
            || string.IsNullOrWhiteSpace(read.User) || read.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("That Oracle locator does not name a host, a port, a service and a user.", nameof(stored));
        }

        return read;
    }

    /// <summary>The same locator without its password, for showing.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The locator with the password left out.</returns>
    public static string WithoutSecrets(string stored) => For(Of(stored) with { Password = null });

    /// <summary>The database a locator names, for telling two apart: <c>host:port/service</c>, lower-cased.</summary>
    /// <param name="stored">A locator for which <see cref="Is"/> is true.</param>
    /// <returns>The address; a rotated password or a different user keeps it.</returns>
    public static string DatabaseKey(string stored) => Of(stored).Address.ToLowerInvariant();
}
