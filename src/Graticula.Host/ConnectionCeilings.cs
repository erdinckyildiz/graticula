using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// Compares what this server may open against what its database will give.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-196](../../docs/architecture-debt.md), measured 2026-08-27.</b> While the
/// admission-control conformance test saturates one server, the peak is **129 connections —
/// 100 of them the platform store's pool and 29 everything else.** A stock PostgreSQL allows
/// **100**, three reserved for superusers. On a default database that pairing does not fit,
/// and the way it announces itself is `53300: sorry, too many clients already` on whichever
/// request happens to be next — which opens `SourceBreaker` and turns the following ten
/// seconds of unrelated requests into 503s.
/// </para>
/// <para>
/// <b>Two bounds were designed and neither is this one.</b>
/// [ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md)
/// bounds concurrent *data-source operations*, correctly. Q-04 counted backends per client
/// count, correctly. Nothing compared the total against `max_connections`, and the platform
/// store's pool is outside the admission budget entirely — it is read on **every** request,
/// including requests that touch no data source.
/// </para>
/// <para>
/// <b>This says, it does not decide.</b> What the ceilings should be is a decision with
/// several defensible answers and it belongs to whoever owns ADR-046; what is not a decision
/// is that a server can be configured to want more connections than its database will give
/// and find out under load. Never degrade silently, applied to a number nobody was comparing.
/// </para>
/// <para>
/// <b>A warning rather than a refusal to start.</b> The arithmetic is a worst case: pools
/// reach their ceiling only under load, and a deployment that never saturates never notices.
/// Refusing to start would stop servers that work.
/// </para>
/// </remarks>
internal static class ConnectionCeilings
{
    /// <summary>
    /// PostgreSQL keeps some slots for superusers, and they are not ours to use.
    /// </summary>
    private const int ReservedByDefault = 3;

    /// <summary>
    /// Reads the database's limit and says whether this server's ceilings fit inside it.
    /// </summary>
    /// <param name="dataSource">The platform store, which is the connection we already have.</param>
    /// <param name="ceiling">
    /// The largest number of connections this server may hold open at once, added up across
    /// its pools.
    /// </param>
    /// <param name="logger">Where to say it.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>
    /// Nothing. A database that will not say is not an error: this is an aside about the
    /// configuration, and refusing to start over a failed `SHOW` would make a diagnostic into
    /// an outage.
    /// </returns>
    public static async Task CompareAsync(
        NpgsqlDataSource dataSource,
        int ceiling,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        int allowed;
        int reserved;

        try
        {
            await using NpgsqlCommand command = dataSource.CreateCommand(
                "select current_setting('max_connections')::int, "
                + "current_setting('superuser_reserved_connections')::int");

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            allowed = reader.GetInt32(0);
            reserved = reader.GetInt32(1);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException
                                      or OperationCanceledException)
        {
            return;
        }

        int usable = allowed - (reserved > 0 ? reserved : ReservedByDefault);

        if (ceiling <= usable)
        {
            Log.ConnectionCeilingFits(logger, ceiling, usable, allowed);

            return;
        }

        Log.ConnectionCeilingExceedsTheDatabase(logger, ceiling, usable, allowed);
    }
}
