using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// Which of a site's nodes runs each sweep that must run once per site — ADR-012 §5 phase 0 item 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>A PostgreSQL advisory lock per sweep, held on one connection of this node's own.</b> A sweep asks
/// <see cref="LeadsAsync"/> before each pass; the first node to ask takes the lock and keeps it for as long as its
/// connection lives, so the same node runs the sweep pass after pass, and every other node finds it held and skips its
/// pass. When the leading node stops or dies its connection closes, PostgreSQL releases its locks, and the next pass on
/// another node takes them. No new process and nothing to configure: the store every node already needs is the
/// coordinator (ADR-012 §2 A).
/// </para>
/// <para>
/// <b>Keyed by the store's schema as well as the sweep</b>, because advisory locks are database-wide and two
/// deployments may keep their stores in two schemas of one database; each must lead its own sweeps. The two-integer
/// form (<c>pg_try_advisory_lock(int, int)</c>) keeps these apart from the export budget's single-key lock.
/// </para>
/// <para>
/// <b>On one node nothing changes</b>: it takes every lock on its first pass and runs every sweep, as before. The cost
/// is one connection, opened with pooling off — a session-level lock cannot live in a pooled connection that other
/// work returns and reuses — and counted with the grants listener's in the connection budget.
/// </para>
/// <para>
/// <b>A node that cannot reach the store leads nothing.</b> If the connection fails, every lock it held is gone with
/// it, so the answer is no until a fresh connection takes them again — never a stale yes.
/// </para>
/// </remarks>
internal sealed partial class SiteSingletons : IAsyncDisposable
{
    /// <summary>The sweeps that run once per site, by the name each is locked under.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        Backup, LogRetention, HistoryRetention, AttachmentSweep, RecycleBinExpiry, CoveragePyramids,
    ];

    /// <summary>The scheduled backup (ADR-193): one per site per day, not one per node.</summary>
    public const string Backup = "backup-schedule";

    /// <summary>The request and studio log's thirty-day sweep.</summary>
    public const string LogRetention = "log-retention";

    /// <summary>The feature history's pruning.</summary>
    public const string HistoryRetention = "history-retention";

    /// <summary>Orphaned attachments of registered layers.</summary>
    public const string AttachmentSweep = "attachment-sweep";

    /// <summary>The recycle bin's expiry.</summary>
    public const string RecycleBinExpiry = "recycle-expiry";

    /// <summary>The image services' overviews, written beside their files.</summary>
    public const string CoveragePyramids = "coverage-pyramids";

    private readonly string _connectionString;
    private readonly ILogger<SiteSingletons> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private NpgsqlConnection? _connection;
    private string? _node;

    /// <summary>Creates the leases.</summary>
    /// <param name="connectionString">The platform store, with its search path; pooling is turned off here.</param>
    /// <param name="applicationName">What the connection is called in <c>pg_stat_activity</c>.</param>
    /// <param name="log">Where a lost connection is said.</param>
    public SiteSingletons(string connectionString, string applicationName, ILogger<SiteSingletons> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(log);

        _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = applicationName,
        }.ConnectionString;

        _log = log;
    }

    /// <summary>The sweeps this node leads now, as last found.</summary>
    public IReadOnlyList<string> Held
    {
        get
        {
            lock (_held)
            {
                return [.. _held.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>
    /// Whether this node is drained — stopped or under maintenance (ADR-012 §1c) — and so leads no sweep.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// Takes this node's name for the life of its connection — a second process under the same name finds it held.
    /// </summary>
    /// <param name="name">The node's name.</param>
    /// <param name="cancellation">The start's.</param>
    /// <returns>False when another running process holds the name.</returns>
    public async Task<bool> ClaimNodeAsync(string name, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await _gate.WaitAsync(cancellation).ConfigureAwait(false);

        try
        {
            _node = name;
            NpgsqlConnection connection = await ConnectionAsync(cancellation, takeName: false).ConfigureAwait(false);
            return await TakeAsync(connection, "graticula.node:" + name, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NpgsqlConnection> ConnectionAsync(CancellationToken cancellation, bool takeName = true)
    {
        if (_connection is { } open)
        {
            return open;
        }

        NpgsqlConnection opened = new(_connectionString);

        try
        {
            await opened.OpenAsync(cancellation).ConfigureAwait(false);
        }
        catch
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _connection = opened;

        // A connection made again after a failure takes this node's name back first: the locks went with the old one.
        if (takeName && _node is { } node && !await TakeAsync(opened, "graticula.node:" + node, cancellation).ConfigureAwait(false))
        {
            NameTaken(_log, node);
        }

        return opened;
    }

    private static async Task<bool> TakeAsync(NpgsqlConnection connection, string key, CancellationToken cancellation)
    {
        await using NpgsqlCommand take = new(
            "select pg_try_advisory_lock(hashtext(current_schema()), hashtext(@key))", connection);
        take.Parameters.AddWithValue("key", key);
        return (bool)(await take.ExecuteScalarAsync(cancellation).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Lets every sweep go, keeping this node's name — a node an administrator stopped, so another's next pass takes
    /// them at once rather than when this process ends.
    /// </summary>
    /// <returns>When released.</returns>
    public async Task ReleaseSweepsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            string[] held;

            lock (_held)
            {
                held = [.. _held];
                _held.Clear();
            }

            if (_connection is { } open)
            {
                foreach (string name in held)
                {
                    await using NpgsqlCommand release = new(
                        "select pg_advisory_unlock(hashtext(current_schema()), hashtext(@key))", open);
                    release.Parameters.AddWithValue("key", "graticula.singleton:" + name);
                    await release.ExecuteScalarAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (failure is NpgsqlException or System.IO.IOException or InvalidOperationException)
        {
            // A connection that fails here has released everything anyway.
            if (_connection is { } broken)
            {
                _connection = null;
                await broken.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether this node runs the named sweep's pass now — taking the lock if no node holds it.</summary>
    /// <param name="name">One of <see cref="Names"/>.</param>
    /// <param name="cancellation">The sweep's.</param>
    /// <returns>True when this node holds the sweep's lock.</returns>
    public async Task<bool> LeadsAsync(string name, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (Paused)
        {
            return false;
        }

        await _gate.WaitAsync(cancellation).ConfigureAwait(false);

        try
        {
            NpgsqlConnection connection = await ConnectionAsync(cancellation).ConfigureAwait(false);
            bool held;

            lock (_held)
            {
                held = _held.Contains(name);
            }

            if (held)
            {
                // Still ours while the session lives; a round trip proves it does.
                await using NpgsqlCommand alive = new("select 1", connection);
                await alive.ExecuteScalarAsync(cancellation).ConfigureAwait(false);
                return true;
            }

            bool taken = await TakeAsync(connection, "graticula.singleton:" + name, cancellation).ConfigureAwait(false);

            if (taken)
            {
                lock (_held)
                {
                    _held.Add(name);
                }

                Taken(_log, name);
            }

            return taken;
        }
        catch (Exception failure) when (failure is NpgsqlException or System.IO.IOException or InvalidOperationException)
        {
            // <b>Every lock went with the connection</b>, so nothing is held until a new one takes it again.
            bool lost;

            lock (_held)
            {
                lost = _held.Count > 0;
                _held.Clear();
            }

            if (_connection is { } broken)
            {
                _connection = null;
                await broken.DisposeAsync().ConfigureAwait(false);
            }

            if (lost)
            {
                Lost(_log, failure.Message);
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lets every lock go, so another node's next pass takes them — a node draining or stopping.</summary>
    /// <returns>When released.</returns>
    public async Task ReleaseAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            lock (_held)
            {
                _held.Clear();
            }

            if (_connection is { } open)
            {
                _connection = null;
                await open.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await ReleaseAllAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    [LoggerMessage(EventId = 2510, Level = LogLevel.Information,
        Message = "This node now runs the site's {Sweep} (ADR-012): no other node held it.")]
    private static partial void Taken(ILogger logger, string sweep);

    [LoggerMessage(EventId = 2520, Level = LogLevel.Error,
        Message = "This node reconnected to the platform store and found its name '{Node}' held by another process; two "
            + "processes now answer as one machine of the site. Give each its own Graticula:NodeName (ADR-012).")]
    private static partial void NameTaken(ILogger logger, string node);

    [LoggerMessage(EventId = 2511, Level = LogLevel.Warning,
        Message = "This node lost its connection to the platform store and with it every site sweep it ran ({Reason}); "
            + "another node's next pass takes them, or this one's once the store answers again (ADR-012).")]
    private static partial void Lost(ILogger logger, string reason);
}
