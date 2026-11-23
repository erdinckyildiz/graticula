using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Two real PostgreSQL clusters, one streaming from the other — ADR-319's primary and standby, made for one test and
/// stopped after it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Made from nothing by PostgreSQL's own tools</b>: <c>initdb</c>, <c>pg_basebackup -R</c>, <c>pg_ctl</c>,
/// <c>pg_rewind</c>, from the folder <c>GRATICULA_TEST_PG_BIN</c> names (with PostGIS installed beside them). Unset, the
/// tests that use this FAIL rather than skip, as every database test here does; CI runs none of them
/// (<c>Needs=StandbyStore</c>) and says so.
/// </para>
/// <para>
/// <b>The standby streams through a relay</b> (<see cref="Relay"/>), and so may a node: cutting a relay is a network
/// partition between two processes on one machine, which is the only way to make <i>the primary is alive but this
/// party cannot see it</i> happen here.
/// </para>
/// </remarks>
public sealed class StandbyStore : IAsyncDisposable
{
    /// <summary>The database the platform store is in.</summary>
    public const string DatabaseName = "gisg2h";

    private readonly string _bin;
    private readonly string _root;
    private readonly List<Relay> _relays = [];

    private StandbyStore(string bin, string root, int primaryPort, int standbyPort)
    {
        _bin = bin;
        _root = root;
        PrimaryPort = primaryPort;
        StandbyPort = standbyPort;
    }

    /// <summary>The first primary's port.</summary>
    public int PrimaryPort { get; }

    /// <summary>The first standby's port.</summary>
    public int StandbyPort { get; }

    /// <summary>The relay the standby streams from the primary through.</summary>
    public Relay StandbyLink { get; private set; } = null!;

    /// <summary>The primary's data folder.</summary>
    public string PrimaryData => Path.Combine(_root, "a");

    /// <summary>The standby's data folder.</summary>
    public string StandbyData => Path.Combine(_root, "b");

    /// <summary>Makes, starts and connects the two clusters.</summary>
    public static async Task<StandbyStore> StartAsync()
    {
        string bin = Environment.GetEnvironmentVariable("GRATICULA_TEST_PG_BIN") is { Length: > 0 } b
            ? b
            : throw new InvalidOperationException(
                "GRATICULA_TEST_PG_BIN is not set, so these tests FAIL rather than skip. It names a folder holding PostgreSQL's "
                + "initdb, pg_ctl, pg_basebackup and pg_rewind, with PostGIS installed, from which two clusters are made.");

        int[] ports = FreePorts(2);
        string root = Path.Combine(Path.GetTempPath(), "g2-ha-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        StandbyStore store = new(bin, root, ports[0], ports[1]);

        try
        {
            await store.MakeAsync();
            return store;
        }
        catch
        {
            // Nothing outside holds it yet, so nothing else would stop what was started.
            await store.DisposeAsync();
            throw;
        }
    }

    private async Task MakeAsync()
    {
        StandbyStore store = this;
        await store.RunAsync("initdb", "-D", store.PrimaryData, "-U", "postgres", "-A", "trust", "-E", "UTF8", "--locale=C");
        await File.AppendAllTextAsync(Path.Combine(store.PrimaryData, "postgresql.conf"), string.Create(CultureInfo.InvariantCulture,
            $"""

            port = {store.PrimaryPort}
            listen_addresses = '127.0.0.1'
            wal_level = replica
            wal_log_hints = on
            max_wal_senders = 10
            hot_standby = on
            wal_keep_size = 256MB
            max_connections = 300
            wal_receiver_timeout = 5s
            wal_retrieve_retry_interval = 1s

            """));
        await store.StartAsync(store.PrimaryData);

        await using (NpgsqlConnection admin = new(Direct(store.PrimaryPort, "postgres")))
        {
            await admin.OpenAsync();
            await using NpgsqlCommand create = new($"create database {DatabaseName}", admin);
            await create.ExecuteNonQueryAsync();
        }

        await using (NpgsqlConnection gis = new(Direct(store.PrimaryPort, DatabaseName)))
        {
            await gis.OpenAsync();
            await using NpgsqlCommand postgis = new("create extension if not exists postgis", gis);
            await postgis.ExecuteNonQueryAsync();
        }

        store.StandbyLink = store.RelayTo(store.PrimaryPort);
        await store.RunAsync("pg_basebackup", "-h", "127.0.0.1", "-p", store.PrimaryPort.ToString(CultureInfo.InvariantCulture),
            "-U", "postgres", "-D", store.StandbyData, "-R", "-X", "stream", "-c", "fast");
        await PointAsync(store.StandbyData, store.StandbyPort, store.StandbyLink.Port);
        await store.StartAsync(store.StandbyData);
        await StandbyStore.UntilStreamingAsync(store.StandbyPort, TimeSpan.FromSeconds(30));
    }

    /// <summary>The store as the server is given it: both hosts, the first primary first.</summary>
    public string Hosts(int? primaryVia = null) => string.Create(CultureInfo.InvariantCulture,
        $"Host=127.0.0.1:{primaryVia ?? PrimaryPort},127.0.0.1:{StandbyPort};Database={DatabaseName};Username=postgres;Target Session Attributes=read-write");

    /// <summary>One host, the way a test reads it directly.</summary>
    public static string Direct(int port, string database = DatabaseName) =>
        string.Create(CultureInfo.InvariantCulture, $"Host=127.0.0.1;Port={port};Database={database};Username=postgres;Pooling=false;Timeout=3");

    /// <summary>A TCP relay to a port of this machine, stopped with the store.</summary>
    public Relay RelayTo(int port)
    {
        Relay relay = new(port);
        _relays.Add(relay);
        return relay;
    }

    /// <summary>Stops a cluster at once, as a crash would.</summary>
    public Task KillAsync(string data) => RunAsync("pg_ctl", "-D", data, "stop", "-m", "immediate");

    /// <summary>Stops a cluster cleanly.</summary>
    public Task StopAsync(string data) => RunAsync("pg_ctl", "-D", data, "stop", "-m", "fast");

    /// <summary>Starts a cluster as its files say it is.</summary>
    public Task StartAsync(string data) =>
        RunAsync("pg_ctl", "-D", data, "-l", Path.Combine(data, "..", Path.GetFileName(data) + ".log"), "-w", "start");

    /// <summary>
    /// ADR-319 §5.8: rewinds an old primary against the new one and starts it as its standby, streaming through
    /// <paramref name="via"/>.
    /// </summary>
    public async Task RejoinAsync(string data, int port, int newPrimaryPort, Relay via)
    {
        await RunAsync("pg_rewind", "-D", data, "--source-server",
            string.Create(CultureInfo.InvariantCulture, $"host=127.0.0.1 port={newPrimaryPort} user=postgres dbname=postgres"),
            "--write-recovery-conf");
        await PointAsync(data, port, via.Port);
        await StartAsync(data);
    }

    /// <summary>A scalar read straight from one cluster.</summary>
    public static async Task<T> ScalarAsync<T>(int port, string sql, string database = DatabaseName)
    {
        await using NpgsqlConnection connection = new(Direct(port, database));
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Waits until a standby's receiver streams.</summary>
    public static async Task UntilStreamingAsync(int port, TimeSpan within)
    {
        Stopwatch clock = Stopwatch.StartNew();
        string? last = null;

        while (clock.Elapsed < within)
        {
            try
            {
                last = await ScalarAsync<object>(port, "select coalesce((select status from pg_stat_wal_receiver), 'none')", "postgres") as string;

                if (last == "streaming")
                {
                    return;
                }
            }
            catch (NpgsqlException e)
            {
                last = e.Message;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The standby on {port} did not stream within {within.TotalSeconds:0} s; last: {last}");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (Relay relay in _relays)
        {
            relay.Dispose();
        }

        foreach (string data in new[] { PrimaryData, StandbyData })
        {
            if (File.Exists(Path.Combine(data, "postmaster.pid")))
            {
                try
                {
                    await RunAsync("pg_ctl", "-D", data, "stop", "-m", "immediate");
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A log still held open; the temporary folder is the system's to clear.
        }
    }

    private static async Task PointAsync(string data, int port, int primaryVia)
    {
        string conf = Path.Combine(data, "postgresql.conf");
        string text = await File.ReadAllTextAsync(conf);
        await File.WriteAllTextAsync(conf, text + string.Create(CultureInfo.InvariantCulture, $"\nport = {port}\n"));

        // pg_basebackup -R and pg_rewind -R both write primary_conninfo into postgresql.auto.conf; it is replaced so the
        // standby streams through the relay.
        string auto = Path.Combine(data, "postgresql.auto.conf");
        List<string> lines = [.. (await File.ReadAllLinesAsync(auto)).Where(l => !l.StartsWith("primary_conninfo", StringComparison.Ordinal))];
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"primary_conninfo = 'host=127.0.0.1 port={primaryVia} user=postgres application_name=standby'"));
        await File.WriteAllLinesAsync(auto, lines);
    }

    private async Task RunAsync(string tool, params string[] arguments)
    {
        ProcessStartInfo start = new(Path.Combine(_bin, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException(tool + " did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();

        // pg_ctl start leaves the server holding the pipes it inherited; wait for the tool, not for the pipes.
        using CancellationTokenSource limit = new(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{tool} {string.Join(' ', arguments)} did not finish within three minutes.");
        }

        if (process.ExitCode != 0)
        {
            string said = (output.IsCompleted ? output.Result : "") + (error.IsCompleted ? error.Result : "");
            throw new InvalidOperationException($"{tool} {string.Join(' ', arguments)} exited {process.ExitCode}: {said}");
        }
    }

    /// <summary>Free ports in ADR-319's slot first (55512–55519), else any.</summary>
    private static int[] FreePorts(int count)
    {
        List<int> found = [];

        for (int port = 55512; port <= 55519 && found.Count < count; port++)
        {
            try
            {
                using TcpListener probe = new(IPAddress.Loopback, port);
                probe.Start();
                found.Add(port);
            }
            catch (SocketException)
            {
                // Taken.
            }
        }

        while (found.Count < count)
        {
            using TcpListener any = new(IPAddress.Loopback, 0);
            any.Start();
            found.Add(((IPEndPoint)any.LocalEndpoint).Port);
        }

        return [.. found];
    }

    /// <summary>
    /// A TCP relay to one port: what passes through it is a network path, and <see cref="Cut"/> is that path failing —
    /// every connection through it dropped and new ones refused — until <see cref="Mend"/>.
    /// </summary>
    public sealed class Relay : IDisposable
    {
        private readonly int _target;
        private readonly List<TcpClient> _open = [];
        private TcpListener? _listener;
        private CancellationTokenSource _stop = new();

        internal Relay(int target)
        {
            _target = target;
            TcpListener first = new(IPAddress.Loopback, 0);
            first.Start();
            Port = ((IPEndPoint)first.LocalEndpoint).Port;
            first.Stop();
            Mend();
        }

        /// <summary>The port it listens on.</summary>
        public int Port { get; }

        /// <summary>Drops every connection through it and refuses new ones.</summary>
        public void Cut()
        {
            _stop.Cancel();
            _listener?.Stop();
            _listener = null;

            lock (_open)
            {
                foreach (TcpClient client in _open)
                {
                    client.Dispose();
                }

                _open.Clear();
            }
        }

        /// <summary>Lets connections through again.</summary>
        public void Mend()
        {
            _stop = new CancellationTokenSource();
            TcpListener listener = new(IPAddress.Loopback, Port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            _listener = listener;
            CancellationToken stop = _stop.Token;
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient inbound;

                    try
                    {
                        inbound = await listener.AcceptTcpClientAsync(stop);
                    }
                    catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                    {
                        return;
                    }

                    _ = PassAsync(inbound, stop);
                }
            }, stop);
        }

        private async Task PassAsync(TcpClient inbound, CancellationToken stop)
        {
            TcpClient outbound = new();

            lock (_open)
            {
                _open.Add(inbound);
                _open.Add(outbound);
            }

            try
            {
                await outbound.ConnectAsync(IPAddress.Loopback, _target, stop);
                NetworkStream a = inbound.GetStream();
                NetworkStream b = outbound.GetStream();
                await Task.WhenAny(a.CopyToAsync(b, stop), b.CopyToAsync(a, stop));
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException
                or InvalidOperationException)
            {
                // The path failed, which is what a cut is.
            }
            finally
            {
                inbound.Dispose();
                outbound.Dispose();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Cut();
            _stop.Dispose();
        }
    }
}
