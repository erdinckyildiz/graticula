using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Holds one connection subscribed to the store's grant announcements, and tells
/// <see cref="AnonymousGrants"/> when the anonymous caller's may have changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-249](../../../docs/architecture-debt.md): the half of the owner's decision that makes a
/// held answer safe.</b> Migration 44's triggers announce every change to a principal's roles,
/// user type or groups on one channel; this is the ear. It is the only thing that turns
/// <see cref="AnonymousGrants"/> on, so a server whose listener cannot connect behaves exactly as
/// it did before the cache existed.
/// </para>
/// <para>
/// <b>One connection, outside every pool, and it is counted.</b> A <c>LISTEN</c> belongs to a
/// session, so it cannot share a pooled connection that other work returns and reuses; it is opened
/// with pooling off and held. The job signal (`JobSignal`) declined exactly this for its pollers,
/// because it would cost a connection per worker for ever to save a poll nobody was waiting on;
/// here it is one per server against a round trip on every anonymous request, which is the cost
/// D-249 measured.
/// </para>
/// <para>
/// <b>A silent death is looked for, not assumed away.</b> A connection whose peer vanished can sit
/// in a wait for ever with nothing to say it is gone, and a listener that is not hearing anything
/// is indistinguishable from one with nothing to hear. So a wait that times out is followed by a
/// round trip; if that fails the subscription is dropped, the cache turns off, and the loop
/// reconnects.
/// </para>
/// </remarks>
public sealed partial class PostgresGrantsListener
{
    /// <summary>The channel migration 44's triggers announce on.</summary>
    public const string Channel = "graticula_grants";

    /// <summary>How long a quiet subscription goes before it proves it is still there.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait before subscribing again after a failure.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    private readonly string _connectionString;
    private readonly AnonymousGrants _anonymous;
    private readonly ILogger<PostgresGrantsListener> _log;

    /// <summary>Creates the listener.</summary>
    /// <param name="connectionString">
    /// The platform store, with its search path. Pooling is turned off here whatever it says.
    /// </param>
    /// <param name="anonymous">What to tell.</param>
    /// <param name="log">Where a lost subscription is reported.</param>
    public PostgresGrantsListener(
        string connectionString, AnonymousGrants anonymous, ILogger<PostgresGrantsListener> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(anonymous);
        ArgumentNullException.ThrowIfNull(log);

        _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _anonymous = anonymous;
        _log = log;
    }

    /// <summary>Subscribes, and keeps subscribing, until stopped.</summary>
    /// <param name="stopping">Stops the listener.</param>
    /// <returns>When stopped.</returns>
    public async Task RunAsync(CancellationToken stopping)
    {
        bool reported = false;

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception failure)
#pragma warning restore CA1031
            {
                // <b>Broad, because what it guards is the host.</b> An exception leaving a
                // background service stops the server, and a listener that dies of a surprise
                // should cost the cache rather than the process — the `finally` below turns the
                // cache off either way, which is the state that is always correct.
                //
                // <b>Once per outage rather than once per retry</b>: the retry is every five seconds and
                // the store may be gone for an hour.
                if (!reported)
                {
                    Log.SubscriptionLost(_log, failure.Message);
                    reported = true;
                }
            }
            finally
            {
                _anonymous.Subscribed(false);
            }

            try
            {
                await Task.Delay(Retry, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return;

        async Task ListenAsync(CancellationToken cancellation)
        {
            await using NpgsqlConnection connection = new(_connectionString);
            await connection.OpenAsync(cancellation).ConfigureAwait(false);

            string schema;

            await using (NpgsqlCommand where = new("select current_schema()", connection))
            {
                schema = (string)(await where.ExecuteScalarAsync(cancellation).ConfigureAwait(false))!;
            }

            // <b>Only this store's anonymous principal.</b> The channel is database-wide, so
            // another store in the same database announces here too; its changes are not ours.
            string mine = string.Create(
                CultureInfo.InvariantCulture, $"{schema}:{Principal.AnonymousId}");

            connection.Notification += (_, announced) =>
            {
                if (string.Equals(announced.Payload, mine, StringComparison.OrdinalIgnoreCase))
                {
                    _anonymous.Changed();
                }
            };

            await using (NpgsqlCommand listen = new($"listen {Channel}", connection))
            {
                await listen.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            }

            _anonymous.Subscribed(true);

            if (reported)
            {
                Log.SubscriptionRestored(_log);
                reported = false;
            }

            while (!cancellation.IsCancellationRequested)
            {
                if (!await connection.WaitAsync(Heartbeat, cancellation).ConfigureAwait(false))
                {
                    await using NpgsqlCommand alive = new("select 1", connection);
                    await alive.ExecuteScalarAsync(cancellation).ConfigureAwait(false);
                }
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1061,
            Level = LogLevel.Warning,
            Message = "Not subscribed to the platform store's grant announcements ({Reason}). "
                    + "Anonymous requests read their grants from the store on every request until "
                    + "the subscription is back, which is correct and slower; retrying every five "
                    + "seconds.")]
        public static partial void SubscriptionLost(ILogger logger, string reason);

        [LoggerMessage(
            EventId = 1062,
            Level = LogLevel.Information,
            Message = "Subscribed to the platform store's grant announcements again.")]
        public static partial void SubscriptionRestored(ILogger logger);
    }
}
