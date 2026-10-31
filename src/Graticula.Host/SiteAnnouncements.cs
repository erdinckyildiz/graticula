using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// Tells a site's other nodes that something one node holds in memory has changed, and hears them say the same —
/// ADR-012 §5 phase 0 item 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>PostgreSQL's <c>LISTEN</c>/<c>NOTIFY</c>, on a channel of its own beside the grants'.</b> A node that changes a
/// tenant, a table's remembered shape, the items' terms and categories, a pin or an image's overviews says so with
/// <see cref="Announce"/>; every node listening reloads the one thing named. The timers each of those already had stay
/// as the backstop for a change nothing announced — a DBA's <c>ALTER TABLE</c>, a missed notification while a listener
/// reconnects — so a lost announcement costs what it cost before this existed, never more.
/// </para>
/// <para>
/// <b>Each payload names the store's schema and the node that sent it.</b> The channel is database-wide, so another
/// deployment's store in the same database is heard too and ignored; and a node ignores its own, having already done
/// what it announced.
/// </para>
/// <para>
/// <b>One connection, outside every pool</b> — a <c>LISTEN</c> belongs to a session — counted with the site's lock
/// connection in the connection budget.
/// </para>
/// </remarks>
internal sealed partial class SiteAnnouncements : BackgroundService
{
    /// <summary>The channel.</summary>
    public const string Channel = "graticula_site";

    /// <summary>A tenant was made, changed or removed: every node reads the tenants again.</summary>
    public const string Tenants = "tenants";

    /// <summary>A layer's remembered shape was forgotten: the detail is its schema, table and geometry column.</summary>
    public const string Shape = "shape";

    /// <summary>Every remembered shape was forgotten.</summary>
    public const string Shapes = "shapes";

    /// <summary>The items' terms of use, categories or favourites changed.</summary>
    public const string Annotations = "annotations";

    /// <summary>A service was pinned or unpinned: every node warms the pinned services now.</summary>
    public const string Pins = "pins";

    /// <summary>An image was registered or changed: the node that builds overviews looks now.</summary>
    public const string Pyramids = "pyramids";

    /// <summary>An administrator set a machine's state: the detail is its name, and that machine takes it up now.</summary>
    public const string Machine = "machine";

    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    private readonly NpgsqlDataSource _store;
    private readonly string _listenConnection;
    private readonly string _node;
    private readonly ILogger<SiteAnnouncements> _log;
    private readonly ConcurrentDictionary<string, Func<string, Task>> _handlers = new(StringComparer.Ordinal);
    private string? _schema;
    private long _heard;

    /// <summary>Creates the announcer and its ear.</summary>
    /// <param name="store">The platform store's pool, which sends.</param>
    /// <param name="connectionString">The platform store, for the listening session.</param>
    /// <param name="node">This node's name, which every payload carries.</param>
    /// <param name="log">Where a lost subscription is said.</param>
    public SiteAnnouncements(NpgsqlDataSource store, string connectionString, string node, ILogger<SiteAnnouncements> log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _listenConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = PoolNames.Of(PoolNames.Site),
        }.ConnectionString;
        _node = node;
        _log = log;
    }

    /// <summary>Whether the ear is connected now.</summary>
    public bool Listening { get; private set; }

    /// <summary>How many announcements from other nodes this node has acted on — for the Site screen and the tests.</summary>
    public long Heard => Interlocked.Read(ref _heard);

    /// <summary>What to do when another node announces a kind of change.</summary>
    /// <param name="kind">One of this type's constants.</param>
    /// <param name="handler">Given the announcement's detail.</param>
    public void On(string kind, Func<string, Task> handler) => _handlers[kind] = handler;

    /// <summary>Says to every other node that something changed. Never throws: a lost announcement waits for a timer.</summary>
    /// <param name="kind">One of this type's constants.</param>
    /// <param name="detail">What, where the kind needs it; never a secret, since every listening session hears it.</param>
    public void Announce(string kind, string detail = "") => _ = SendAsync(kind, detail);

    private async Task SendAsync(string kind, string detail)
    {
        try
        {
            await using NpgsqlCommand command = _store.CreateCommand(
                "select pg_notify(@channel, current_schema() || '|' || @node || '|' || @kind || '|' || @detail)");
            command.Parameters.AddWithValue("channel", Channel);
            command.Parameters.AddWithValue("node", _node);
            command.Parameters.AddWithValue("kind", kind);
            command.Parameters.AddWithValue("detail", detail.Length > 1000 ? "" : detail);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception failure)
#pragma warning restore CA1031
        {
            // The other nodes' timers still bring them round; the change itself has already been made here.
            NotSent(_log, kind, failure.Message);
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool reported = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception failure)
#pragma warning restore CA1031
            {
                // Broad for the grants listener's reason: this guards the host, and a lost ear costs only freshness.
                if (!reported)
                {
                    Lost(_log, failure.Message);
                    reported = true;
                }
            }
            finally
            {
                Listening = false;
            }

            try
            {
                await Task.Delay(Retry, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        async Task ListenAsync(CancellationToken cancellation)
        {
            await using NpgsqlConnection connection = new(_listenConnection);
            await connection.OpenAsync(cancellation).ConfigureAwait(false);

            await using (NpgsqlCommand where = new("select current_schema()", connection))
            {
                _schema = (string)(await where.ExecuteScalarAsync(cancellation).ConfigureAwait(false))!;
            }

            connection.Notification += (_, announced) => Dispatch(announced.Payload);

            await using (NpgsqlCommand listen = new($"listen {Channel}", connection))
            {
                await listen.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            }

            Listening = true;

            if (reported)
            {
                Restored(_log);
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

    private void Dispatch(string payload)
    {
        string[] parts = payload.Split('|', 4);

        if (parts.Length < 4
            || !string.Equals(parts[0], _schema, StringComparison.Ordinal)
            || string.Equals(parts[1], _node, StringComparison.Ordinal)
            || !_handlers.TryGetValue(parts[2], out Func<string, Task>? handler))
        {
            return;
        }

        Interlocked.Increment(ref _heard);
        string kind = parts[2];

        _ = Task.Run(async () =>
        {
            try
            {
                await handler(parts[3]).ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception failure)
#pragma warning restore CA1031
            {
                NotActedOn(_log, kind, failure.Message);
            }
        });
    }

    [LoggerMessage(EventId = 2512, Level = LogLevel.Warning,
        Message = "Not hearing the site's announcements ({Reason}); what other nodes change reaches this one by its timers "
            + "until the subscription is back. Retrying every five seconds (ADR-012).")]
    private static partial void Lost(ILogger logger, string reason);

    [LoggerMessage(EventId = 2513, Level = LogLevel.Information, Message = "Hearing the site's announcements again (ADR-012).")]
    private static partial void Restored(ILogger logger);

    [LoggerMessage(EventId = 2514, Level = LogLevel.Warning,
        Message = "A '{Kind}' change could not be announced to the site's other nodes ({Reason}); they learn of it by their timers (ADR-012).")]
    private static partial void NotSent(ILogger logger, string kind, string reason);

    [LoggerMessage(EventId = 2515, Level = LogLevel.Warning,
        Message = "Another node's '{Kind}' change could not be taken up here ({Reason}); the timer brings it round (ADR-012).")]
    private static partial void NotActedOn(ILogger logger, string kind, string reason);
}
