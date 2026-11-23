using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// The platform store's primary and standby, watched from this node; the standby promoted when the primary is gone
/// and every witness agrees; a returned old primary fenced — ADR-319 §5.4–§5.7.
/// </summary>
/// <remarks>
/// <para>
/// <b>Active only when <c>Graticula:PlatformStore</c> names more than one host.</b> On the baseline deployment it is
/// constructed, says it is inactive, and does nothing.
/// </para>
/// <para>
/// <b>What it decides is when, never how.</b> Promotion is PostgreSQL's <c>pg_promote()</c>; the epoch is PostgreSQL's
/// timeline (<see cref="StoreEpoch"/>); the fence is PostgreSQL's <c>default_transaction_read_only</c>, which Npgsql's
/// read-write selection honours (measured 2026-11-23).
/// </para>
/// </remarks>
internal sealed partial class StoreFailover : BackgroundService
{
    /// <summary>How often each host is probed.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    /// <summary>Consecutive failed probes before this node calls the primary unreachable — ArcGIS's documented three.</summary>
    public const int Misses = 3;

    /// <summary>How long a host this node voted down stays refused by this node, whatever it answers (§5.4).</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(30);

    /// <summary>The route the nodes ask each other on.</summary>
    public const string ViewPath = "/healthz/store";

    /// <summary>The header that proves a question comes from a node of this site.</summary>
    public const string ProofHeader = "X-Graticula-Site-Proof";

    /// <summary>The state <c>/healthz/ready</c> names while the store has no primary this node can write to (§5.5).</summary>
    public const string ReadinessState = "store-failover";

    private const string ProbeSql =
        "select pg_is_in_recovery(), (select system_identifier from pg_control_system()), "
        + "case when pg_is_in_recovery() then (select timeline_id from pg_control_checkpoint()) "
        + "else ('x' || substr(pg_walfile_name(pg_current_wal_lsn()), 1, 8))::bit(32)::int end, "
        + "current_setting('default_transaction_read_only') = 'on', "
        + "(select status from pg_stat_wal_receiver limit 1), "
        + "coalesce((select rolsuper from pg_roles where rolname = current_user), false) or pg_has_role('pg_monitor', 'member')";

    private const string LagSql =
        "select coalesce(max(pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn)), 0)::bigint, "
        + "max(extract(epoch from replay_lag))::float8, count(*)::int from pg_stat_replication";

    private readonly NpgsqlDataSource _store;
    private readonly SiteNode _node;
    private readonly ILogger<StoreFailover> _log;
    private readonly IReadOnlyList<(string Address, string Probe)> _hosts;
    private readonly byte[] _proofKey;
    private readonly ConcurrentDictionary<string, HostView> _views = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _promoting = new(1, 1);
    private IReadOnlyList<(string Name, string Address)> _peers = [];
    private DateTimeOffset _peersRead = DateTimeOffset.MinValue;
    private string? _primary;
    private DateTimeOffset? _lostSince;
    private volatile string? _held;
    private Failover? _lastFailover;

    /// <summary>Creates the watcher.</summary>
    /// <param name="store">The platform store's request pool, through which a failover is recorded.</param>
    /// <param name="node">This node, for its name and the site's other machines.</param>
    /// <param name="settings">The host's settings.</param>
    /// <param name="configuration">Where <c>Graticula:StoreFailover</c> and <c>Graticula:MaintenanceStore</c> are read.</param>
    /// <param name="log">Where it says what it saw and did.</param>
    public StoreFailover(
        NpgsqlDataSource store, SiteNode node, HostSettings settings, IConfiguration configuration, ILogger<StoreFailover> log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);

        _store = store ?? throw new ArgumentNullException(nameof(store));
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Active = StoreHosts.IsMultiHost(settings.PlatformStore);
        Mode = string.Equals(configuration["Graticula:StoreFailover"], "manual", StringComparison.OrdinalIgnoreCase)
            ? "manual"
            : "automatic";
        _proofKey = HMACSHA256.HashData(Convert.FromBase64String(settings.SecretKeyBase64), "graticula.store-view"u8);

        // The probe's role: the maintenance connection's where set (reading the standby's receiver and promoting need a
        // superuser or pg_monitor and pg_promote), else the platform store's own. Each host on a connection of its own.
        string login = configuration["Graticula:MaintenanceStore"] is { Length: > 0 } maintenance ? maintenance : settings.PlatformStore;
        _hosts = Active
            ? StoreHosts.Of(settings.PlatformStore)
                .Select(h => (StoreHosts.Address(h.Host, h.Port), ProbeString(login, h.Host, h.Port)))
                .ToList()
            : [];

        if (Active)
        {
            StoreEpoch.Refused = said => Refused(_log, said);
        }
    }

    /// <summary>Whether the store names a primary and a standby.</summary>
    public bool Active { get; }

    /// <summary><c>automatic</c> or <c>manual</c> — <c>Graticula:StoreFailover</c>.</summary>
    public string Mode { get; }

    /// <summary>
    /// Why this node should not be sent requests because of the store's failover, or null — §5.5's named state, beside
    /// the others <c>/healthz/ready</c> has.
    /// </summary>
    public string? NotReadyReason
    {
        get
        {
            if (!Active || _views.IsEmpty || CurrentPrimary() is not null)
            {
                return null;
            }

            return _held is { } held
                ? $"The platform store has no primary this node can write to; failover is held: {held} (ADR-319)."
                : $"The platform store has no primary this node can write to (the last was {_primary ?? "not seen"}); the standby "
                    + "is promoted when it and every node of the site agree the primary is gone (ADR-319).";
        }
    }

    /// <summary>What one probe of one host saw.</summary>
    internal sealed record HostView(
        string Address,
        bool Reachable,
        bool Recovering,
        long System,
        int Timeline,
        bool Fenced,
        string? Receiver,
        bool Privileged,
        long? LagBytes,
        double? ReplayLagSeconds,
        int Standbys,
        int Misses,
        DateTimeOffset At,
        string? Error);

    /// <summary>A promotion, as <c>store_failover</c> keeps it.</summary>
    internal sealed record Failover(DateTimeOffset At, string From, string To, int Timeline, string DecidedBy, string How, int? UnreachableMs);

    /// <summary>The hosts as last probed.</summary>
    internal IReadOnlyList<HostView> Views => [.. _hosts.Select(h => _views.TryGetValue(h.Address, out HostView? v) ? v : Unknown(h.Address))];

    /// <summary>Why automatic failover is held, or null.</summary>
    internal string? Held => _held;

    /// <summary>The host writes go to now: a reachable, unfenced primary on the newest timeline, or null.</summary>
    internal string? CurrentPrimary()
    {
        foreach ((string address, _) in _hosts)
        {
            if (_views.TryGetValue(address, out HostView? v) && v.Reachable && !v.Recovering && !v.Fenced
                && v.Timeline >= StoreEpoch.Newest(v.System))
            {
                return address;
            }
        }

        return null;
    }

    /// <summary>The last failover, from the store; what this node did itself while the store cannot answer.</summary>
    internal async Task<Failover?> LastFailoverAsync(CancellationToken cancellation)
    {
        // Read every time: another node may have promoted since. What this node saw itself answers while the store cannot.
        try
        {
            await using NpgsqlCommand read = _store.CreateCommand(
                "select at, from_host, to_host, timeline, decided_by, how, unreachable_ms from store_failover order by id desc limit 1");
            await using NpgsqlDataReader row = await read.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
            _lastFailover = await row.ReadAsync(cancellation).ConfigureAwait(false)
                ? new Failover(row.GetFieldValue<DateTimeOffset>(0), row.GetString(1), row.GetString(2), row.GetInt32(3),
                    row.GetString(4), row.GetString(5), row.IsDBNull(6) ? null : row.GetInt32(6))
                : null;
        }
        catch (PostgresException missing) when (missing.SqlState == "42P01")
        {
            // A store before migration 148 remembers none.
        }
        catch (Exception e) when (StoreOutage.Is(e) || e is InvalidOperationException)
        {
            // While the store is failing over, what this node last knew.
        }

        return _lastFailover;
    }

    /// <summary>One round of probes, before the node serves — so even a node that remembers no timeline fences an old
    /// primary it finds beside a newer one before it reads anything (ADR-319 §5.6, D-496).</summary>
    /// <param name="cancellation">The start's.</param>
    /// <returns>When probed.</returns>
    public async Task ObserveAsync(CancellationToken cancellation)
    {
        if (Active)
        {
            await RoundAsync(decide: false, cancellation).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Active)
        {
            return;
        }

        string named = string.Join(", ", _hosts.Select(h => h.Address));
        Watching(_log, named, Mode);
        using PeriodicTimer timer = new(Every);

        do
        {
            try
            {
                await RoundAsync(decide: true, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031
            catch (Exception failure)
#pragma warning restore CA1031
            {
                // A round that fails is a round; the next is a second away. Never the process.
                RoundFailed(_log, failure.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RoundAsync(bool decide, CancellationToken cancellation)
    {
        HostView[] seen = await Task.WhenAll(_hosts.Select(h => ProbeAsync(h.Address, h.Probe, cancellation))).ConfigureAwait(false);

        foreach (HostView view in seen)
        {
            _views[view.Address] = view;

            if (view.Reachable && !view.Recovering)
            {
                StoreEpoch.Observe(view.System, view.Timeline);
            }
        }

        // §5.6: a read-write host below its system's newest timeline is an old primary come back. Fence it.
        foreach (HostView stale in seen.Where(v => v.Reachable && !v.Recovering && !v.Fenced && v.Timeline < StoreEpoch.Newest(v.System)))
        {
            await FenceAsync(stale, cancellation).ConfigureAwait(false);
        }

        if (CurrentPrimary() is { } primary)
        {
            if (!string.Equals(primary, _primary, StringComparison.OrdinalIgnoreCase) && _primary is not null)
            {
                PrimaryIs(_log, primary);
            }

            _primary = primary;
            _lostSince = null;
            _held = null;

            if (decide && DateTimeOffset.UtcNow - _peersRead > SiteNode.Beat)
            {
                await ReadPeersAsync(cancellation).ConfigureAwait(false);
            }

            return;
        }

        _lostSince ??= DateTimeOffset.UtcNow;

        if (!decide || _primary is null || !_views.TryGetValue(_primary, out HostView? old) || old.Misses < Misses)
        {
            return;
        }

        // §5.4: before this node votes, it stops writing to the primary it votes against.
        StoreEpoch.Suspect(_primary, Hold);

        if (Mode != "automatic")
        {
            _held = "this site's failover is manual (Graticula:StoreFailover); an administrator promotes the standby";
            return;
        }

        if (await WhyNotAsync(_primary, PrimaryKey(old), cancellation).ConfigureAwait(false) is { } reason)
        {
            if (!string.Equals(reason, _held, StringComparison.Ordinal))
            {
                HeldBecause(_log, _primary, reason);
            }

            _held = reason;
            return;
        }

        _held = null;
        HostView standby = Standby(_primary)!;
        await PromoteAsync(standby, "automatic", cancellation).ConfigureAwait(false);
    }

    /// <summary>The host that could be promoted in place of <paramref name="primary"/>: reachable, in recovery, the same system.</summary>
    private HostView? Standby(string primary)
    {
        long system = _views.TryGetValue(primary, out HostView? p) ? p.System : 0;

        return _views.Values.FirstOrDefault(v =>
            !string.Equals(v.Address, primary, StringComparison.OrdinalIgnoreCase)
            && v.Reachable && v.Recovering && (system == 0 || v.System == system));
    }

    /// <summary>Why the standby may not be promoted now in place of <paramref name="primary"/>, or null when it may.</summary>
    private async Task<string?> WhyNotAsync(string primary, string key, CancellationToken cancellation)
    {
        if (Standby(primary) is not { } standby)
        {
            return "no standby is reachable from this node";
        }

        if (!standby.Privileged)
        {
            return "the role this node probes with may not read the standby's receiver or promote it; set "
                + "Graticula:MaintenanceStore to a superuser's connection";
        }

        if (string.Equals(standby.Receiver, "streaming", StringComparison.OrdinalIgnoreCase))
        {
            return $"the standby {standby.Address} still streams from the primary, so the primary is alive";
        }

        foreach ((string name, string address) in _peers)
        {
            bool? reaches = await AskAsync(address, key, cancellation).ConfigureAwait(false);

            if (reaches is null)
            {
                return $"node '{name}' did not answer, and it may still be writing to the primary (D-497)";
            }

            if (reaches.Value)
            {
                return $"node '{name}' still reaches the primary";
            }
        }

        return null;
    }

    /// <summary>
    /// Promotes the standby — automatically when every witness agrees, or because an administrator asked (§5.7).
    /// </summary>
    /// <param name="standby">The host.</param>
    /// <param name="how"><c>automatic</c> or <c>manual</c>.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The failover, or null when another promotion was already under way.</returns>
    internal async Task<Failover?> PromoteAsync(HostView standby, string how, CancellationToken cancellation)
    {
        if (!await _promoting.WaitAsync(0, cancellation).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            string from = _primary
                ?? _views.Values.FirstOrDefault(v => !string.Equals(v.Address, standby.Address, StringComparison.OrdinalIgnoreCase))?.Address
                ?? "(none)";
            int? unreachable = _lostSince is { } since ? (int)(DateTimeOffset.UtcNow - since).TotalMilliseconds : null;
            string probe = _hosts.First(h => string.Equals(h.Address, standby.Address, StringComparison.OrdinalIgnoreCase)).Probe;
            Promoting(_log, standby.Address, from, how);

            await using (NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(probe) { Timeout = 10, CommandTimeout = 90 }.ConnectionString))
            {
                await connection.OpenAsync(cancellation).ConfigureAwait(false);

                await using (NpgsqlCommand promote = new("select pg_promote(true, 60)", connection))
                {
                    await promote.ExecuteScalarAsync(cancellation).ConfigureAwait(false);
                }

                // A host fenced once (§5.6) carries the fence until it is rewound; promoted, it must take writes.
                await using (NpgsqlCommand unfence = new("alter system reset default_transaction_read_only", connection))
                {
                    await unfence.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
                }

                await using (NpgsqlCommand reload = new("select pg_reload_conf()", connection))
                {
                    await reload.ExecuteScalarAsync(cancellation).ConfigureAwait(false);
                }
            }

            HostView promoted = await ProbeAsync(standby.Address, probe, cancellation).ConfigureAwait(false);
            _views[promoted.Address] = promoted;
            StoreEpoch.Observe(promoted.System, promoted.Timeline);
            _primary = promoted.Address;
            StoreEpoch.Clear(promoted.Address);

            Failover done = new(DateTimeOffset.UtcNow, from, promoted.Address, promoted.Timeline, _node.Name, how, unreachable);
            _lastFailover = done;
            Promoted(_log, promoted.Address, promoted.Timeline, from, how);
            await RecordAsync(done, cancellation).ConfigureAwait(false);
            return done;
        }
        finally
        {
            _promoting.Release();
        }
    }

    /// <summary>
    /// §5.7: <c>graticula store promote</c> — ArcGIS Data Store's <c>makePrimary</c>, as a command on a machine of the site.
    /// </summary>
    /// <remarks>
    /// <b>A command and not an admin route, and a test found why.</b> The route was written first, and
    /// <c>A_manual_site_waits_for_an_administrator_to_promote_the_standby</c> showed it could never succeed: it is needed
    /// exactly when the primary is gone, and then the server cannot read the session that would prove the caller is an
    /// administrator (401, by design — Authentication refuses rather than assume). Whoever can run this binary with the
    /// site's settings already holds the store's credentials, which is the authority promoting needs.
    /// </remarks>
    /// <param name="output">Where it says what it did.</param>
    /// <param name="cancellation">The command's.</param>
    /// <returns>The process's exit code: 0 when promoted.</returns>
    internal async Task<int> PromoteFromCommandLineAsync(System.IO.TextWriter output, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(output);
        await ObserveAsync(cancellation).ConfigureAwait(false);
        (HostView? standby, string? refusal) = ManualCandidate();

        if (standby is null)
        {
            await output.WriteLineAsync(refusal).ConfigureAwait(false);
            return 1;
        }

        Failover? done = await PromoteAsync(standby, "manual", cancellation).ConfigureAwait(false);

        if (done is null)
        {
            await output.WriteLineAsync("A promotion is already under way.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync($"{done.To} is the primary now, on timeline {done.Timeline}, in place of {done.From}. "
            + "Rewind the old primary and start it as the standby (deployment.md §1.6).").ConfigureAwait(false);
        return 0;
    }

    /// <summary>§5.7: the standby an administrator may promote now, or why not.</summary>
    internal (HostView? Standby, string? Refusal) ManualCandidate()
    {
        if (!Active)
        {
            return (null, "The platform store names one host: there is no standby to promote (ADR-319).");
        }

        if (CurrentPrimary() is { } primary)
        {
            return (null, $"This node can still write to the primary {primary}. Promoting a standby beside a live primary is "
                + "the split brain ADR-319 prevents; stop the primary first, then promote.");
        }

        HostView? standby = _views.Values.FirstOrDefault(v => v.Reachable && v.Recovering);
        return standby is null
            ? (null, "No standby is reachable from this node.")
            : standby.Privileged
                ? (standby, null)
                : (null, "The role this node probes with may not promote; set Graticula:MaintenanceStore to a superuser's connection.");
    }

    /// <summary>The primary as the nodes name it to each other: its system and timeline, never an address.</summary>
    /// <remarks>
    /// <b>Not an address, and a test found why.</b> Two nodes may reach one host by two names — an address and a name,
    /// a proxy and the host — and a node asked about a name it does not use answered <i>no</i>, so the other promoted
    /// beside a primary it could still reach (<c>A_node_that_still_reaches_the_primary_stops_the_other_from_promoting</c>,
    /// 2026-11-23). PostgreSQL names the primary the same way to everyone.
    /// </remarks>
    internal static string PrimaryKey(HostView view) =>
        string.Create(CultureInfo.InvariantCulture, $"{view.System}/{view.Timeline}");

    /// <summary>
    /// The answer to another node's question (§5.4): whether this node reaches the primary <paramref name="key"/> names
    /// (<see cref="PrimaryKey"/>) now, by whatever address it uses. A node that answers <i>no</i> stops writing to it for
    /// <see cref="Hold"/> — its vote binds itself.
    /// </summary>
    internal bool Reaches(string key)
    {
        string[] parts = key.Split('/');

        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long system)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int timeline))
        {
            return false;
        }

        List<HostView> named = [.. _views.Values.Where(v => v.System == system && !v.Recovering && v.Timeline >= timeline)];
        bool reaches = named.Any(v => v.Reachable && v.Misses == 0);

        if (!reaches)
        {
            foreach (HostView v in named)
            {
                StoreEpoch.Suspect(v.Address, Hold);
            }
        }

        return reaches;
    }

    /// <summary>The proof a node sends with a question, or signs an answer with: a keyed hash of the site's key.</summary>
    internal string Proof(string text) =>
        Convert.ToHexString(HMACSHA256.HashData(_proofKey, Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Whether a proof is this site's, compared in constant time.</summary>
    internal bool Proves(string text, string? proof) =>
        proof is { Length: 64 }
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Proof(text)), Encoding.ASCII.GetBytes(proof.ToLowerInvariant()));

    private async Task<bool?> AskAsync(string peer, string primary, CancellationToken cancellation)
    {
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        try
        {
            using SocketsHttpHandler handler = new()
            {
                // The site's nodes present certificates made for their own names; the question and the answer are both
                // proved by the site's key instead.
#pragma warning disable CA5359
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
#pragma warning restore CA5359
            };
            using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(2) };
            using HttpRequestMessage request = new(HttpMethod.Get,
                new Uri($"{peer}{ViewPath}?primary={Uri.EscapeDataString(primary)}&nonce={nonce}"));
            request.Headers.Add(ProofHeader, Proof("ask:" + primary + ":" + nonce));
            using HttpResponseMessage answer = await http.SendAsync(request, cancellation).ConfigureAwait(false);

            if (!answer.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument body = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            bool reaches = body.RootElement.GetProperty("reaches").GetBoolean();
            string? signed = body.RootElement.GetProperty("proof").GetString();
            return Proves("answer:" + primary + ":" + nonce + ":" + (reaches ? "yes" : "no"), signed) ? reaches : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task ReadPeersAsync(CancellationToken cancellation)
    {
        try
        {
            List<(string, string)> peers = [];
            await using NpgsqlCommand read = _store.CreateCommand(
                "select name, address from site_node where name <> @name and last_seen > now() - @silence order by name");
            read.Parameters.AddWithValue("name", _node.Name);
            read.Parameters.AddWithValue("silence", SiteNode.Silence);
            await using NpgsqlDataReader row = await read.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            while (await row.ReadAsync(cancellation).ConfigureAwait(false))
            {
                peers.Add((row.GetString(0), row.GetString(1)));
            }

            _peers = peers;
            _peersRead = DateTimeOffset.UtcNow;
        }
        catch (Exception e) when (StoreOutage.Is(e) || e is InvalidOperationException)
        {
            // Kept as last read: the list a failover is decided by is the one from while the primary answered.
        }
    }

    private async Task FenceAsync(HostView stale, CancellationToken cancellation)
    {
        string probe = _hosts.First(h => string.Equals(h.Address, stale.Address, StringComparison.OrdinalIgnoreCase)).Probe;

        try
        {
            await using NpgsqlConnection connection = new(probe);
            await connection.OpenAsync(cancellation).ConfigureAwait(false);

            await using (NpgsqlCommand fence = new("alter system set default_transaction_read_only = on", connection))
            {
                await fence.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
            }

            await using (NpgsqlCommand reload = new("select pg_reload_conf()", connection))
            {
                await reload.ExecuteScalarAsync(cancellation).ConfigureAwait(false);
            }

            _views[stale.Address] = stale with { Fenced = true };
            Fenced(_log, stale.Address, stale.Timeline, StoreEpoch.Newest(stale.System));
        }
        catch (Exception e) when (StoreOutage.Is(e))
        {
            // Refused by every node that knows the newer timeline anyway (§5.3); fenced at the next round.
            FenceFailed(_log, stale.Address, e.Message);
        }
    }

    /// <remarks>
    /// <b>Once per promotion, whoever records it.</b> Two nodes that agree in the same second both call <c>pg_promote</c>
    /// while the standby is still in recovery, and both are told it worked — observed in
    /// <c>Two_nodes_that_both_lose_the_primary_agree_and_promote_the_standby</c>, which found two rows. One promotion is
    /// one new timeline on one host, so the second node's row is the same failover and is not written.
    /// </remarks>
    private async Task RecordAsync(Failover done, CancellationToken cancellation)
    {
        // The pools re-check a host's role every second (StoreHosts), so the new primary may take a moment to be chosen.
        Stopwatch clock = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                await using NpgsqlCommand write = _store.CreateCommand(
                    """
                    insert into store_failover (at, from_host, to_host, timeline, decided_by, how, unreachable_ms)
                    select @at, @from, @to, @timeline, @by, @how, @ms
                     where not exists (select 1 from store_failover
                                        where to_host = @to and timeline = @timeline and at > now() - interval '10 minutes')
                    """);
                write.Parameters.AddWithValue("at", done.At);
                write.Parameters.AddWithValue("from", done.From);
                write.Parameters.AddWithValue("to", done.To);
                write.Parameters.AddWithValue("timeline", done.Timeline);
                write.Parameters.AddWithValue("by", done.DecidedBy);
                write.Parameters.AddWithValue("how", done.How);
                write.Parameters.Add(new NpgsqlParameter("ms", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)done.UnreachableMs ?? DBNull.Value });
                await write.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
                return;
            }
            catch (PostgresException missing) when (missing.SqlState == "42P01")
            {
                return;
            }
            catch (Exception e) when ((StoreOutage.Is(e) || e is InvalidOperationException) && clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(250, cancellation).ConfigureAwait(false);
            }
        }
    }

    private async Task<HostView> ProbeAsync(string address, string probe, CancellationToken cancellation)
    {
        int misses = _views.TryGetValue(address, out HostView? was) ? was.Misses : 0;

        try
        {
            await using NpgsqlConnection connection = new(probe);
            await connection.OpenAsync(cancellation).ConfigureAwait(false);

            bool recovering;
            long system;
            int timeline;
            bool fenced;
            string? receiver;
            bool privileged;

            await using (NpgsqlCommand ask = new(ProbeSql, connection))
            await using (NpgsqlDataReader row = await ask.ExecuteReaderAsync(cancellation).ConfigureAwait(false))
            {
                await row.ReadAsync(cancellation).ConfigureAwait(false);
                recovering = row.GetBoolean(0);
                system = row.GetInt64(1);
                timeline = row.GetInt32(2);
                fenced = row.GetBoolean(3);
                receiver = row.IsDBNull(4) ? null : row.GetString(4);
                privileged = row.GetBoolean(5);
            }

            long? lagBytes = null;
            double? replayLag = null;
            int standbys = 0;

            if (!recovering)
            {
                await using NpgsqlCommand lag = new(LagSql, connection);
                await using NpgsqlDataReader row = await lag.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
                await row.ReadAsync(cancellation).ConfigureAwait(false);
                standbys = row.GetInt32(2);
                lagBytes = standbys > 0 ? row.GetInt64(0) : null;
                replayLag = row.IsDBNull(1) ? null : row.GetDouble(1);
            }

            return new HostView(address, true, recovering, system, timeline, fenced, receiver, privileged, lagBytes, replayLag,
                standbys, 0, DateTimeOffset.UtcNow, null);
        }
        catch (Exception e) when (StoreOutage.Is(e) || e is InvalidOperationException)
        {
            return (was ?? Unknown(address)) with
            {
                Reachable = false,
                Misses = misses + 1,
                At = DateTimeOffset.UtcNow,
                Error = e.Message,
                LagBytes = null,
                ReplayLagSeconds = null,
            };
        }
    }

    private static HostView Unknown(string address) =>
        new(address, false, false, 0, 0, false, null, false, null, null, 0, 0, DateTimeOffset.MinValue, "not probed yet");

    private static string ProbeString(string login, string host, int port) =>
        new NpgsqlConnectionStringBuilder(login)
        {
            Host = host,
            Port = port,
            TargetSessionAttributes = null,
            Pooling = false,
            Timeout = 2,
            CommandTimeout = 5,
            ApplicationName = "graticula-store-failover",
            SearchPath = null,
        }.ConnectionString;

    [LoggerMessage(EventId = 2560, Level = LogLevel.Information,
        Message = "The platform store names {Hosts}; this node watches them and its failover is {Mode} (ADR-319).")]
    private static partial void Watching(ILogger logger, string hosts, string mode);

    [LoggerMessage(EventId = 2561, Level = LogLevel.Warning,
        Message = "The platform store's primary {Primary} is unreachable from this node, and failover is held: {Reason} (ADR-319).")]
    private static partial void HeldBecause(ILogger logger, string primary, string reason);

    [LoggerMessage(EventId = 2562, Level = LogLevel.Warning,
        Message = "Promoting the platform store's standby {Standby} in place of {Primary} ({How}) (ADR-319).")]
    private static partial void Promoting(ILogger logger, string standby, string primary, string how);

    [LoggerMessage(EventId = 2563, Level = LogLevel.Warning,
        Message = "The platform store failed over: {Standby} is the primary on timeline {Timeline}, in place of {Primary} ({How}) (ADR-319).")]
    private static partial void Promoted(ILogger logger, string standby, int timeline, string primary, string how);

    [LoggerMessage(EventId = 2564, Level = LogLevel.Warning,
        Message = "The platform store at {Host} came back as a primary on timeline {Timeline}, below the newest, {Newest}; it is "
            + "fenced read-only and nothing is written to it. Rewind it and start it as the standby (deployment.md §1.6, ADR-319).")]
    private static partial void Fenced(ILogger logger, string host, int timeline, int newest);

    [LoggerMessage(EventId = 2565, Level = LogLevel.Warning,
        Message = "The old primary {Host} could not be fenced ({Reason}); every node that knows the newer timeline refuses it anyway (ADR-319).")]
    private static partial void FenceFailed(ILogger logger, string host, string reason);

    [LoggerMessage(EventId = 2566, Level = LogLevel.Warning, Message = "{Said}")]
    private static partial void Refused(ILogger logger, string said);

    [LoggerMessage(EventId = 2567, Level = LogLevel.Information,
        Message = "The platform store's primary is {Primary} (ADR-319).")]
    private static partial void PrimaryIs(ILogger logger, string primary);

    [LoggerMessage(EventId = 2568, Level = LogLevel.Warning,
        Message = "A round of the platform store's failover watch failed ({Reason}); the next is a second away (ADR-319).")]
    private static partial void RoundFailed(ILogger logger, string reason);
}
