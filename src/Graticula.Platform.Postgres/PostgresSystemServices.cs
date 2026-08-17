using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>A service that is not a layer.</summary>
/// <param name="Name">Its name within its folder.</param>
/// <param name="Kind">The ArcGIS service type, e.g. <c>GeometryServer</c>.</param>
/// <param name="Folder">Which folder it lives in, or null for the root.</param>
/// <param name="Sharing">Who may use it.</param>
/// <param name="Status">
/// Whether it answers. <b>Added 2026-08-17 on the owner's question</b> — *"geometry server'in,
/// startı stop'u, timeout'u vs si yok mu?"* — and it had neither: this record carried sharing
/// and nothing else, while the console drew a <c>started</c> pill it had invented. Sharing and
/// status are different questions and an operator needs both: sharing is *who*, status is
/// *whether*, and stopping a compute endpoint under load without changing who may call it once
/// it is back is exactly what a stop is for.
/// </param>
/// <param name="DeadlineSeconds">
/// How long one operation on this service may run, or null for the configured default.
/// <b>Settable since 2026-08-17</b>, on the owner's objection to being told it was a
/// configuration-file value: *"iyi de neden yok. yani ben neden max timeout süresi
/// tanımlayamıyorum?"*
/// </param>
/// <param name="PreflightPairs">
/// The pre-flight threshold in candidate segment pairs — zero meaning no pre-flight — or null for
/// the configured default. Kept beside the deadline because they are the same kind of choice: how
/// much of a caller's request this deployment is willing to spend.
/// </param>
/// <param name="WaitSeconds">
/// How long a request may queue for a free worker, or null for the configured default. <b>Its own
/// budget rather than the work's deadline</b>, which is the split ArcGIS Server Manager's Pooling
/// page makes and this server did not.
/// </param>
/// <param name="IdleSeconds">
/// How long a worker may sit unused before it is disposed — zero meaning keep it for ever — or
/// null for the configured default.
/// </param>
public readonly record struct SystemService(
    string Name,
    string Kind,
    string? Folder,
    SharingScope Sharing,
    ServiceStatus Status,
    int? DeadlineSeconds = null,
    long? PreflightPairs = null,
    int? WaitSeconds = null,
    int? IdleSeconds = null);

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
            "select name, kind, folder, sharing, status, deadline_seconds, preflight_pairs, "
            + "wait_seconds, idle_seconds "
            + "from system_service order by folder, name");

        List<SystemService> services = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            services.Add(new SystemService(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Parse(reader.GetString(3)),
                ParseStatus(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8)));
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
            "select name, kind, folder, sharing, status, deadline_seconds, preflight_pairs, "
            + "wait_seconds, idle_seconds "
            + "from system_service where name = @name");

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
            Parse(reader.GetString(3)),
            ParseStatus(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8));
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

    /// <summary>Starts or stops a service.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="status">The new status.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What it was before, or null when there is no such service.</returns>
    /// <remarks>
    /// <b>Returns the previous status rather than a bool</b>, for the same reason the layer
    /// setter does: an operator wants to know whether their stop was the one that stopped it, and
    /// *it was already stopped* is a different answer from *it is stopped now*.
    /// </remarks>
    public async Task<ServiceStatus?> SetStatusAsync(
        string name, ServiceStatus status, CancellationToken cancellationToken)
    {
        // <b>One statement, and it reads the old value in the same statement it writes the new
        // one.</b> Reading first and writing second would report a status somebody else changed
        // in between — and the *reason* this is written carefully is D-57: the layer version of
        // this method wrote a column nothing read, and answered 200 as though it had worked.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update system_service set status = @status, updated_at = now()
             where name = @name
             returning (select y.status from system_service y where y.name = @name)
            """);

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("status", Wire(status));

        object? before = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return before is string wire ? ParseStatus(wire) : null;
    }

    /// <summary>Sets, or clears, what this service is willing to spend on one operation.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="deadlineSeconds">The deadline, or null to fall back to the configured default.</param>
    /// <param name="preflightPairs">
    /// The pre-flight threshold, zero meaning none, or null to fall back to the configured default.
    /// </param>
    /// <param name="waitSeconds">The queue-wait budget, or null for the configured default.</param>
    /// <param name="idleSeconds">
    /// How long a worker may sit unused — zero for never reclaiming one — or null for the
    /// configured default.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the service existed.</returns>
    /// <remarks>
    /// <para>
    /// <b>Both in one call, because they are one decision.</b> A deployment choosing to refuse
    /// heavy work quickly sets a short deadline and a pre-flight together; setting one and leaving
    /// the other is how a configuration ends up half-applied and read as broken.
    /// </para>
    /// <para>
    /// <b>Null clears rather than being ignored</b>, which is the whole point of the three-way
    /// rule: an administrator who wants the server's default back must be able to say so without
    /// looking it up and typing it in, because a typed-in copy of a default stops tracking it the
    /// moment the default changes.
    /// </para>
    /// </remarks>
    public async Task<bool> SetBoundsAsync(
        string name,
        int? deadlineSeconds,
        long? preflightPairs,
        int? waitSeconds,
        int? idleSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            update system_service
               set deadline_seconds = @deadline,
                   preflight_pairs = @preflight,
                   wait_seconds = @wait,
                   idle_seconds = @idle,
                   updated_at = now()
             where name = @name
            """);

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("deadline", (object?)deadlineSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("preflight", (object?)preflightPairs ?? DBNull.Value);
        command.Parameters.AddWithValue("wait", (object?)waitSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("idle", (object?)idleSeconds ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static ServiceStatus ParseStatus(string wire) =>
        wire == "stopped" ? ServiceStatus.Stopped : ServiceStatus.Started;

    private static string Wire(ServiceStatus status) =>
        status == ServiceStatus.Stopped ? "stopped" : "started";

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
