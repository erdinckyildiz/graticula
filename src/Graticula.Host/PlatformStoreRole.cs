using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// Says whether the role this server reaches its platform store as is one row-level security binds.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-204](../../docs/adr/ADR-204-one-deployment-holds-several-organisations.md) condition 9, found by its
/// prototype on 2026-10-19.</b> The floor that keeps one organisation's rows from another is PostgreSQL's row-level
/// security, and a superuser is never bound by it — <c>FORCE</c> or not. A role with <c>BYPASSRLS</c> is not bound
/// either. <c>compose.yaml</c> connects as the image's <c>POSTGRES_USER</c>, which is a superuser, so on every
/// deployment shipped until then the floor would have been inert: measured, a superuser read 1,000 services as a
/// tenant that owns 50.
/// </para>
/// <para>
/// <b>A warning, the way <see cref="ConnectionCeilings"/> is, while there is one organisation.</b> One organisation
/// does not depend on the floor, and refusing to start would stop every deployment that works today. The refusal
/// belongs where it matters — to serving a second organisation over a connection that cannot keep them apart: the
/// tenant endpoint will not make one, and <see cref="RefusesToServe"/> stops a server starting over a store that
/// already holds several.
/// </para>
/// </remarks>
internal static class PlatformStoreRole
{
    /// <summary>
    /// Whether the server must refuse to start: the store holds more than one organisation and the role it is reached
    /// as is one the floor does not bind. ADR-204 condition 9.
    /// </summary>
    /// <param name="bound">What <see cref="ReportAsync"/> said; null when the database would not say.</param>
    /// <param name="organisations">How many organisations the store holds.</param>
    /// <returns>
    /// True only when the role is known not to be bound and there are several organisations. A database that would not
    /// say is not refused: the tenant endpoint asks again before it makes one, and the read just failed for a reason the
    /// handshake reports on its own.
    /// </returns>
    public static bool RefusesToServe(bool? bound, int organisations) => bound is false && organisations > 1;

    /// <summary>Reads the connected role's attributes and warns when the floor would not bind it.</summary>
    /// <param name="dataSource">The platform store, which is the connection we already have.</param>
    /// <param name="logger">Where to say it.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>
    /// Whether the role is bound, or null when the database would not say — an aside about the configuration is
    /// not a reason to stop.
    /// </returns>
    public static async Task<bool?> ReportAsync(
        NpgsqlDataSource dataSource, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        try
        {
            await using NpgsqlCommand command = dataSource.CreateCommand(
                "select rolname, rolsuper, rolbypassrls from pg_roles where rolname = current_user");
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            string role = reader.GetString(0);
            string? why = (reader.GetBoolean(1), reader.GetBoolean(2)) switch
            {
                (true, _) => "is a superuser",
                (false, true) => "has BYPASSRLS",
                _ => null,
            };

            if (why is null)
            {
                return true;
            }

            Log.PlatformStoreRoleIsNotBound(logger, role, why);
            return false;
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }
}
