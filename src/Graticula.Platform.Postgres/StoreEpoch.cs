using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// The newest timeline this process has seen for each PostgreSQL system, and the refusal of a primary below it —
/// ADR-319 §5.3. A promotion starts a new timeline, so a returned old primary is a primary on an older one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Checked on every physical connection a multi-host pool opens</b> (<see cref="StoreHosts.Build"/>), in one round trip:
/// whether the server is in recovery, its system identifier, and the timeline of its current WAL position. A standby is
/// let through — Npgsql's own target check turns it away from a read-write pool. A primary below the newest timeline
/// is refused with <see cref="StalePrimaryException"/>, and Npgsql then tries the next host (measured 2026-11-23).
/// </para>
/// <para>
/// <b>Remembered across a restart</b> in the file <see cref="Load"/> names, under the node's state path, so a node that
/// restarts while the old primary is back and unfenced still refuses it. A node that has never seen the newer timeline
/// is D-496.
/// </para>
/// </remarks>
public static class StoreEpoch
{
    private const string Ask =
        "select pg_is_in_recovery(), (select system_identifier from pg_control_system()), "
        + "case when pg_is_in_recovery() then 0 "
        + "else ('x' || substr(pg_walfile_name(pg_current_wal_lsn()), 1, 8))::bit(32)::int end";

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Suspects = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<long, int> _newest = [];
    private static string? _file;

    /// <summary>Said when a connection is refused, so the host can log it; null in a process that does not listen.</summary>
    public static Action<string>? Refused { get; set; }

    /// <summary>Said when a newer timeline is seen, so the host can log it.</summary>
    public static Action<long, int>? Raised { get; set; }

    /// <summary>Reads the remembered timelines from <paramref name="path"/> and writes there from now on.</summary>
    /// <param name="path">The file, under the node's state path.</param>
    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (Gate)
        {
            _file = path;

            try
            {
                if (File.Exists(path)
                    && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) is { } read)
                {
                    foreach ((string system, int timeline) in read)
                    {
                        long id = long.Parse(system, CultureInfo.InvariantCulture);
                        _newest[id] = Math.Max(_newest.GetValueOrDefault(id), timeline);
                    }
                }
            }
            catch (Exception e) when (e is IOException or JsonException or FormatException or UnauthorizedAccessException)
            {
                // An unreadable file remembers nothing, which is D-496's case and no worse.
            }
        }
    }

    /// <summary>The newest timeline seen for a system, or 0.</summary>
    /// <param name="system">The system identifier.</param>
    /// <returns>The timeline.</returns>
    public static int Newest(long system)
    {
        lock (Gate)
        {
            return _newest.GetValueOrDefault(system);
        }
    }

    /// <summary>Every system's newest timeline, for the store's report.</summary>
    /// <returns>A copy.</returns>
    public static IReadOnlyDictionary<long, int> All()
    {
        lock (Gate)
        {
            return new Dictionary<long, int>(_newest);
        }
    }

    /// <summary>Records a primary's timeline; raises and remembers the newest.</summary>
    /// <param name="system">The system identifier.</param>
    /// <param name="timeline">The timeline.</param>
    /// <returns>True when it was newer than any seen.</returns>
    public static bool Observe(long system, int timeline)
    {
        lock (Gate)
        {
            if (timeline <= _newest.GetValueOrDefault(system))
            {
                return false;
            }

            _newest[system] = timeline;
            Save();
        }

        Raised?.Invoke(system, timeline);
        return true;
    }

    /// <summary>
    /// ADR-319 §5.4: a host this node has voted down is refused for <paramref name="hold"/>, even if it answers again,
    /// so the node cannot write to it in the window before it learns of a promotion.
    /// </summary>
    /// <param name="address">The host, as <see cref="StoreHosts.Address"/> names it.</param>
    /// <param name="hold">How long.</param>
    public static void Suspect(string address, TimeSpan hold)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + hold;
        Suspects.AddOrUpdate(address, until, (_, was) => was > until ? was : until);
    }

    /// <summary>Lifts a suspicion — the host was promoted, or the hold lapsed with no promotion.</summary>
    /// <param name="address">The host.</param>
    public static void Clear(string address) => Suspects.TryRemove(address, out _);

    /// <summary>Whether a host is suspect now.</summary>
    /// <param name="address">The host.</param>
    /// <returns>True while held.</returns>
    public static bool IsSuspect(string address) =>
        Suspects.TryGetValue(address, out DateTimeOffset until) && until > DateTimeOffset.UtcNow;

    /// <summary>The physical connection check, for a synchronous open.</summary>
    /// <param name="connection">The connection just opened.</param>
    public static void Check(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Suspected(connection);

        using NpgsqlCommand ask = new(Ask, connection);
        using NpgsqlDataReader read = ask.ExecuteReader();
        read.Read();
        Judge(connection, read.GetBoolean(0), read.GetInt64(1), read.GetInt32(2));
    }

    /// <summary>The physical connection check, for an asynchronous open.</summary>
    /// <param name="connection">The connection just opened.</param>
    /// <returns>When checked.</returns>
    public static async Task CheckAsync(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Suspected(connection);

        await using NpgsqlCommand ask = new(Ask, connection);
        await using NpgsqlDataReader read = await ask.ExecuteReaderAsync().ConfigureAwait(false);
        await read.ReadAsync().ConfigureAwait(false);
        Judge(connection, read.GetBoolean(0), read.GetInt64(1), read.GetInt32(2));
    }

    private static void Suspected(NpgsqlConnection connection)
    {
        string address = StoreHosts.Address(connection.Host ?? "localhost", connection.Port);

        if (IsSuspect(address))
        {
            string said = $"The platform store at {address} is held out: this node voted that it could not reach it, and a "
                + "standby may be being promoted (ADR-319 §5.4).";
            Refused?.Invoke(said);
            throw new StalePrimaryException(said);
        }
    }

    private static void Judge(NpgsqlConnection connection, bool recovering, long system, int timeline)
    {
        if (recovering)
        {
            return;
        }

        int newest = Newest(system);

        if (timeline < newest)
        {
            string said = "The platform store at " + StoreHosts.Address(connection.Host ?? "localhost", connection.Port)
                + " is a primary on timeline " + timeline.ToString(CultureInfo.InvariantCulture) + ", and this node has seen timeline "
                + newest.ToString(CultureInfo.InvariantCulture) + ": it is an old primary that has come back, and nothing is "
                + "written to it (ADR-319 §5.3). Rewind it and start it as the standby (deployment.md §1.6).";
            Refused?.Invoke(said);
            throw new StalePrimaryException(said);
        }

        Observe(system, timeline);
    }

    private static void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            Dictionary<string, int> written = [];
            foreach ((long system, int timeline) in _newest)
            {
                written[system.ToString(CultureInfo.InvariantCulture)] = timeline;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            string temporary = _file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(written));
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept in memory still; the next raise tries again.
        }
    }

    /// <summary>A primary refused because it is older than the newest seen, or held out by this node's own vote.</summary>
    public sealed class StalePrimaryException : NpgsqlException
    {
        /// <summary>Creates the refusal.</summary>
        /// <param name="message">What it says.</param>
        public StalePrimaryException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the refusal.</summary>
        public StalePrimaryException()
        {
        }

        /// <summary>Creates the refusal.</summary>
        /// <param name="message">What it says.</param>
        /// <param name="inner">What it wraps.</param>
        public StalePrimaryException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
