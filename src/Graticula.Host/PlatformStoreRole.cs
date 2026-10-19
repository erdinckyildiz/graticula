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
/// <b>A warning, the way <see cref="ConnectionCeilings"/> is.</b> One organisation does not depend on the floor,
/// and refusing to start would stop every deployment that works today. The refusal belongs where it matters — to
/// serving a second organisation over a connection that cannot keep them apart — and that is ADR-204's to build.
/// </para>
/// </remarks>
internal static class PlatformStoreRole
{
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
