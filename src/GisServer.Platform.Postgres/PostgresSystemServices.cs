using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Identity;
using Npgsql;

namespace GisServer.Platform.Postgres;

/// <summary>A service that is not a layer.</summary>
/// <param name="Name">Its name within its folder.</param>
/// <param name="Kind">The ArcGIS service type, e.g. <c>GeometryServer</c>.</param>
/// <param name="Folder">Which folder it lives in, or null for the root.</param>
/// <param name="Sharing">Who may use it.</param>
public readonly record struct SystemService(
    string Name, string Kind, string? Folder, SharingScope Sharing);

/// <summary>
/// Services with no layer behind them, and their sharing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner correction, 2026-08-15: "geometry server is also a service."</b>
/// Sharing had been a property of a layer, so a service without one was governed
/// by nothing — GeometryServer was reachable anonymously, and that was a gap
/// rather than a decision. The authorization model was built around content, and
/// the geometry service is not content.
/// </para>
/// <para>
/// The same three scopes apply, so an administrator learns one concept.
/// </para>
/// </remarks>
public sealed class PostgresSystemServices
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the reader.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresSystemServices(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>Every system service.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The services.</returns>
    public async Task<IReadOnlyList<SystemService>> ListAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select name, kind, folder, sharing from system_service order by folder, name");

        List<SystemService> services = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            services.Add(new SystemService(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Parse(reader.GetString(3))));
        }

        return services;
    }

    /// <summary>One service by name, or null.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The service, or null.</returns>
    public async Task<SystemService?> FindAsync(string name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select name, kind, folder, sharing from system_service where name = @name");

        command.Parameters.AddWithValue("name", name);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SystemService(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Parse(reader.GetString(3)));
    }

    /// <summary>Changes who may use a service.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="sharing">The new scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it existed.</returns>
    public async Task<bool> SetSharingAsync(
        string name, SharingScope sharing, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "update system_service set sharing = @sharing, updated_at = now() where name = @name");

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("sharing", Wire(sharing));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static SharingScope Parse(string wire) => wire switch
    {
        "public" => SharingScope.Public,
        "organization" => SharingScope.Organization,
        _ => SharingScope.Private,
    };

    private static string Wire(SharingScope scope) => scope switch
    {
        SharingScope.Public => "public",
        SharingScope.Organization => "organization",
        _ => "private",
    };
}
