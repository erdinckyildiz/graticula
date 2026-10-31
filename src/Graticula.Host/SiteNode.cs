using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// This process as one machine of a site — ADR-012 §5 phase 0 items 4, 5, 6 and 11, after ArcGIS Server's
/// multiple-machine site (§1c): it registers itself in <c>site_node</c>, keeps a heartbeat, honours the state an
/// administrator sets it to, refuses to join a site whose key or settings differ from its own, and says when it is
/// ready to be sent requests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its name</b> is <c>Graticula:NodeName</c>, or the host name and port — so two processes on one machine are two
/// nodes without configuration. A second process started under a name a running node holds is refused at start: the
/// name is an advisory lock held for the process's life, so a crashed node's name is free at once rather than after a
/// heartbeat times out.
/// </para>
/// <para>
/// <b>The site's key</b> is ArcGIS's <i>same account on every machine</i>: a node whose <c>Graticula:SecretKey</c> differs
/// cannot open what the others sealed, so it is refused. What is compared is a keyed hash of a constant, never the key.
/// </para>
/// <para>
/// <b>The site's settings</b> are every <c>Graticula:*</c> key but those that are a machine's own by nature (its port,
/// its address, its paths, its certificate, its connection strings). A node that sets one differently from a running
/// node is refused at start, with the key and both values named; a secret's value is compared by hash and never shown.
/// What is compared is what is <i>set</i>: a key set to its default on one node and left unset on another counts as a
/// difference, and the message says which, so it is a line to delete rather than a hunt.
/// </para>
/// <para>
/// <b>On one node</b> all of this is a list of one, which is still the right thing to show; nothing is refused.
/// </para>
/// </remarks>
internal sealed partial class SiteNode : BackgroundService
{
    /// <summary>How often a node says it is alive.</summary>
    public static readonly TimeSpan Beat = TimeSpan.FromSeconds(10);

    /// <summary>How long without a heartbeat before a node is shown down.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(30);

    /// <summary>The configured states an administrator may set — ArcGIS's <c>configuredState</c> and <c>underMaintenance</c>.</summary>
    public static readonly IReadOnlyList<string> States = ["started", "stopped", "maintenance"];

    /// <summary>Keys that are a machine's own, never compared across a site.</summary>
    private static readonly HashSet<string> PerNode = new(StringComparer.OrdinalIgnoreCase)
    {
        "Port", "Listen", "HostName", "NodeName", "StatePath", "CertificatePath", "CertificatePassword", "PlatformStore",
        "MaintenanceStore", "SecretKey", "SecretKeyVersion", "TileCachePath", "ImportScratchPath", "TileExportPath",
        "GeoParquetRoot", "ServerDirectories", "ProPublishCaptureDirectory", "CommonPasswordFile",
    };

    private readonly NpgsqlDataSource _store;
    private readonly SiteSingletons _singletons;
    private readonly PinnedServices? _pinned;
    private readonly ILogger<SiteNode> _log;
    private readonly IReadOnlyDictionary<string, string> _settings;
    private readonly string _keyCheck;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private volatile string _state = "started";
    private volatile bool _warm;
    private volatile bool _registered;
    private readonly string _loopback;

    /// <summary>Creates the node.</summary>
    /// <param name="store">The platform store's pool.</param>
    /// <param name="singletons">The site's sweep locks, which hold this node's name too.</param>
    /// <param name="settings">The host's settings.</param>
    /// <param name="configuration">The configuration the site's settings are read from.</param>
    /// <param name="log">Where a refusal or a state change is said.</param>
    /// <param name="pinned">The pinned services, whose warm times are kept per node; absent in tests that build this alone.</param>
    public SiteNode(
        NpgsqlDataSource store, SiteSingletons singletons, HostSettings settings, IConfiguration configuration,
        ILogger<SiteNode> log, PinnedServices? pinned = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(singletons);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _singletons = singletons;
        _pinned = pinned;
        _log = log;
        Name = NameOf(configuration, settings);
        string host = settings.ListenAddress.Equals(System.Net.IPAddress.Any) ? settings.HostName : settings.ListenAddress.ToString();
        Address = string.Create(CultureInfo.InvariantCulture, $"{(settings.RequireHttps ? "https" : "http")}://{host}:{settings.Port}");
        _settings = AgreedSettings(configuration);
        string loop = settings.ListenAddress.Equals(System.Net.IPAddress.Any) || settings.ListenAddress.Equals(System.Net.IPAddress.IPv6Any)
            ? "127.0.0.1"
            : settings.ListenAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{settings.ListenAddress}]"
                : settings.ListenAddress.ToString();
        _loopback = string.Create(CultureInfo.InvariantCulture, $"{(settings.RequireHttps ? "https" : "http")}://{loop}:{settings.Port}");
        _keyCheck = KeyCheck(settings.SecretKeyBase64);
    }

    /// <summary>This node's name in the site.</summary>
    public string Name { get; }

    /// <summary>Where this node listens, as it says itself.</summary>
    public string Address { get; }

    /// <summary>What an administrator set this node to: started, stopped or maintenance.</summary>
    public string ConfiguredState => _state;

    /// <summary>Whether this node is to be sent no requests — stopped or under maintenance (§1c).</summary>
    public bool Draining => _state != "started";

    /// <summary>Whether the first round of warming is done — the pinned services and one request to itself (item 4).</summary>
    public bool Warm => _warm;

    /// <summary>Whether this node has written itself into the site — false on a store before migration 119.</summary>
    public bool Registered => _registered;

    /// <summary>The build this node runs.</summary>
    public static string Version { get; } =
        typeof(SiteNode).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    /// <summary>A node's name: <c>Graticula:NodeName</c>, else its host name and port.</summary>
    internal static string NameOf(IConfiguration configuration, HostSettings settings) =>
        (configuration["Graticula:NodeName"] ?? configuration["GisServer:NodeName"]) is { Length: > 0 } named
            ? named.Trim()
            : string.Create(CultureInfo.InvariantCulture, $"{settings.HostName}:{settings.Port}");

    /// <summary>A keyed hash of a constant: equal on two nodes exactly when their keys are, and says nothing of the key.</summary>
    internal static string KeyCheck(string keyBase64)
    {
        byte[] mac = HMACSHA256.HashData(Convert.FromBase64String(keyBase64), "graticula.site.key-check"u8);
        return Convert.ToHexString(mac, 0, 12).ToLowerInvariant();
    }

    /// <summary>The settings a site's nodes must agree on, as this node has them set; secrets by hash.</summary>
    internal static IReadOnlyDictionary<string, string> AgreedSettings(IConfiguration configuration)
    {
        SortedDictionary<string, string> agreed = new(StringComparer.OrdinalIgnoreCase);

        foreach (string section in (string[])["GisServer", "Graticula"])
        {
            foreach ((string key, string? value) in configuration.GetSection(section).AsEnumerable(makePathsRelative: true))
            {
                if (value is null)
                {
                    continue;
                }

                string top = key.Split(':')[0];

                if (PerNode.Contains(top) || top.EndsWith("Path", StringComparison.OrdinalIgnoreCase)
                    || top.EndsWith("Directory", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                agreed[key] = Secret(key)
                    ? "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 8).ToLowerInvariant()
                    : value;
            }
        }

        return agreed;
    }

    private static bool Secret(string key) =>
        key.Contains("Password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Connection", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Store", StringComparison.OrdinalIgnoreCase);

    /// <summary>The settings in which two nodes differ, each with both values — empty when they agree.</summary>
    internal static IReadOnlyList<string> Differences(
        IReadOnlyDictionary<string, string> mine, IReadOnlyDictionary<string, string> theirs, string other)
    {
        List<string> differ = [];

        foreach (string key in mine.Keys.Union(theirs.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            string? a = mine.GetValueOrDefault(key);
            string? b = theirs.GetValueOrDefault(key);

            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                differ.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Graticula:{key} is {(a is null ? "unset" : $"'{a}'")} here and {(b is null ? "unset" : $"'{b}'")} on {other}"));
            }
        }

        return differ;
    }

    /// <summary>
    /// Joins the site: takes this node's name, refuses a site whose key or settings differ, and writes this node's row.
    /// Called once, before anything is served.
    /// </summary>
    /// <param name="cancellation">The start's.</param>
    /// <returns>Null when joined (or when the store predates the site's tables); otherwise why this node may not start.</returns>
    public async Task<string?> JoinAsync(CancellationToken cancellation)
    {
        try
        {
            if (!await _singletons.ClaimNodeAsync(Name, cancellation).ConfigureAwait(false))
            {
                return "A node named '" + Name + "' is already running on this platform store. Two processes under one "
                    + "name would be one machine to the site; give this one its own Graticula:NodeName, or stop the other.";
            }

            await using (NpgsqlCommand others = _store.CreateCommand(
                "select name, key_check, settings from site_node where name <> @name and last_seen > now() - @silence"))
            {
                others.Parameters.AddWithValue("name", Name);
                others.Parameters.AddWithValue("silence", Silence);
                await using NpgsqlDataReader reader = await others.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
                {
                    string other = reader.GetString(0);

                    if (!string.Equals(reader.GetString(1), _keyCheck, StringComparison.Ordinal))
                    {
                        return "Node '" + other + "' of this site runs with a different Graticula:SecretKey. Every node "
                            + "of a site must have the same key — it seals every registered credential, and a node with "
                            + "another could open none of them. This node was not started (ADR-012).";
                    }

                    Dictionary<string, string> theirs = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2)) ?? [];
                    IReadOnlyList<string> differ = Differences(_settings, theirs, other);

                    if (differ.Count > 0)
                    {
                        return "This node's settings differ from the site's, so it was not started (ADR-012): "
                            + string.Join("; ", differ)
                            + ". Every node of a site must behave alike; a node that differs answers differently and says nothing.";
                    }
                }
            }

            await using (NpgsqlCommand join = _store.CreateCommand(
                """
                insert into site_node (name, address, version, started_at, last_seen, settings, key_check)
                values (@name, @address, @version, @started, now(), @settings, @key)
                on conflict (name) do update
                   set address = excluded.address, version = excluded.version, started_at = excluded.started_at,
                       last_seen = now(), settings = excluded.settings, key_check = excluded.key_check,
                       ready = false, leads = '{}'
                returning configured_state
                """))
            {
                join.Parameters.AddWithValue("name", Name);
                join.Parameters.AddWithValue("address", Address);
                join.Parameters.AddWithValue("version", Version);
                join.Parameters.AddWithValue("started", _started);
                join.Parameters.AddWithValue("settings", JsonSerializer.Serialize(_settings));
                join.Parameters.AddWithValue("key", _keyCheck);
                _state = (string)(await join.ExecuteScalarAsync(cancellation).ConfigureAwait(false))!;
            }

            _registered = true;
            _singletons.Paused = Draining;
            Joined(_log, Name, _state);
            return null;
        }
        catch (PostgresException missing) when (missing.SqlState == "42P01")
        {
            // A store before migration 119: no site to join, and one node behaves as it always did.
            return null;
        }
    }

    /// <summary>Sets a node's configured state, here or for another node.</summary>
    /// <param name="store">The platform store.</param>
    /// <param name="name">The node.</param>
    /// <param name="state">One of <see cref="States"/>.</param>
    /// <param name="cancellation">The request's.</param>
    /// <returns>Whether the node exists.</returns>
    public static async Task<bool> SetStateAsync(NpgsqlDataSource store, string name, string state, CancellationToken cancellation)
    {
        await using NpgsqlCommand set = store.CreateCommand("update site_node set configured_state = @state where name = @name");
        set.Parameters.AddWithValue("state", state);
        set.Parameters.AddWithValue("name", name);
        return await set.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false) > 0;
    }

    /// <summary>Reads this node's configured state and takes it up — when an administrator set it from another node.</summary>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>When taken up.</returns>
    public async Task RefreshStateAsync(CancellationToken cancellation)
    {
        await using NpgsqlCommand read = _store.CreateCommand("select configured_state from site_node where name = @name");
        read.Parameters.AddWithValue("name", Name);

        if (await read.ExecuteScalarAsync(cancellation).ConfigureAwait(false) is string state)
        {
            await ApplyAsync(state).ConfigureAwait(false);
        }
    }

    /// <summary>Takes up a state just set on this node at once, rather than at the next heartbeat.</summary>
    /// <param name="state">The state.</param>
    public async Task ApplyAsync(string state)
    {
        bool was = Draining;
        _state = state;
        _singletons.Paused = Draining;

        if (Draining && !was)
        {
            await _singletons.ReleaseSweepsAsync().ConfigureAwait(false);
            StateTaken(_log, Name, state);
        }
        else if (!Draining && was)
        {
            Resumed(_log, Name);
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = WarmUpAsync(stoppingToken);

        using PeriodicTimer timer = new(Beat);

        do
        {
            if (!_registered)
            {
                continue;
            }

            try
            {
                await BeatAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure) when (failure is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                // The store may be gone for a while; the node keeps serving what it can and beats again in ten seconds.
                BeatFailed(_log, failure.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Item 4: the pinned services' first round, then one request to this node itself — the ~240 ms a new process pays
    /// on its first request (ADR-218 condition 1) — before it says it is ready.
    /// </summary>
    private async Task WarmUpAsync(CancellationToken cancellation)
    {
        try
        {
            if (_pinned is not null)
            {
                await _pinned.FirstRound.WaitAsync(TimeSpan.FromMinutes(2), cancellation).ConfigureAwait(false);
            }

            using System.Net.Http.SocketsHttpHandler handler = new()
            {
                // Itself, at its loopback address, presenting a certificate made for its public name.
#pragma warning disable CA5359
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
#pragma warning restore CA5359
            };
            using System.Net.Http.HttpClient self = new(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using System.Net.Http.HttpResponseMessage answered = await self
                .GetAsync(new Uri(_loopback + "/rest/info?f=json"), cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031
        catch (Exception failure)
#pragma warning restore CA1031
        {
            // A warm-up that fails is a slower first request, not a node that never becomes ready.
            WarmUpFailed(_log, failure.Message);
        }

        _warm = true;
    }

    private async Task BeatAsync(CancellationToken cancellation)
    {
        string state;

        await using (NpgsqlCommand beat = _store.CreateCommand(
            """
            update site_node set last_seen = now(), leads = @leads, ready = @ready
             where name = @name
            returning configured_state
            """))
        {
            beat.Parameters.AddWithValue("name", Name);
            beat.Parameters.AddWithValue("leads", _singletons.Held.ToArray());
            beat.Parameters.AddWithValue("ready", _warm && !Draining);
            object? read = await beat.ExecuteScalarAsync(cancellation).ConfigureAwait(false);

            if (read is not string found)
            {
                // Removed by an administrator while running: it writes itself back, as a machine that is still there.
                await JoinAsync(cancellation).ConfigureAwait(false);
                return;
            }

            state = found;
        }

        if (!string.Equals(state, _state, StringComparison.Ordinal))
        {
            await ApplyAsync(state).ConfigureAwait(false);
        }

        // Item 5: when this node last warmed each pinned service, as its own rows.
        if (_pinned is not null && _pinned.WarmedTimes() is { Count: > 0 } warmed)
        {
            await using NpgsqlCommand keep = _store.CreateCommand(
                """
                insert into site_node_warm (node, service_id, warmed_at)
                select @name, w.id, w.at from unnest(@ids, @ats) as w(id, at)
                 where exists (select 1 from service s where s.id = w.id)
                on conflict (node, service_id) do update set warmed_at = excluded.warmed_at
                """);
            keep.Parameters.AddWithValue("name", Name);
            keep.Parameters.AddWithValue("ids", warmed.Keys.ToArray());
            keep.Parameters.AddWithValue("ats", warmed.Values.Select(v => v.UtcDateTime).ToArray());
            await keep.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
        }

        await using (NpgsqlCommand gone = _store.CreateCommand(
            "delete from site_node_warm w where w.node = @name and not (w.service_id = any(@ids))"))
        {
            gone.Parameters.AddWithValue("name", Name);
            gone.Parameters.AddWithValue("ids", (_pinned?.WarmedTimes().Keys ?? []).ToArray());
            await gone.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 2516, Level = LogLevel.Information,
        Message = "Node '{Node}' joined its site; an administrator has it {State} (ADR-012).")]
    private static partial void Joined(ILogger logger, string node, string state);

    [LoggerMessage(EventId = 2517, Level = LogLevel.Warning,
        Message = "Node '{Node}' is now {State}: it answers not ready, so a load balancer sends it nothing new, and it runs "
            + "none of the site's sweeps (ADR-012).")]
    private static partial void StateTaken(ILogger logger, string node, string state);

    [LoggerMessage(EventId = 2519, Level = LogLevel.Information,
        Message = "Node '{Node}' is started again: it answers ready and runs its share of the site's sweeps (ADR-012).")]
    private static partial void Resumed(ILogger logger, string node);

    [LoggerMessage(EventId = 2524, Level = LogLevel.Warning,
        Message = "This node's warm-up did not finish ({Reason}); it says it is ready anyway, and its first requests pay the warm-up (ADR-012).")]
    private static partial void WarmUpFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 2518, Level = LogLevel.Warning,
        Message = "This node could not write its heartbeat to the platform store ({Reason}); it is shown down until it can.")]
    private static partial void BeatFailed(ILogger logger, string reason);
}
