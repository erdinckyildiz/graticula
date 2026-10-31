using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A site of real server processes over one platform store — ADR-012 condition 3: each phase lands with a test that fails
/// without it, <b>run against two processes</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Real processes, not two hosts in this one.</b> What ADR-012 is about is state one process holds and another does not
/// — memory, a disk, a session-level lock — and two <c>WebApplicationFactory</c> instances in one test process share
/// every static there is. So each node is <c>dotnet Graticula.Host.dll</c>, started from this suite's own output, on a
/// port of its own, with a state directory of its own, as two machines would be.
/// </para>
/// <para>
/// <b>Over a fresh schema of <c>GRATICULA_TEST_PG</c></b>, migrated once and dropped at the end, so nothing here meets
/// another suite's rows; the store is a schema, as the CI job's other stores are.
/// </para>
/// </remarks>
public sealed partial class TwoNodeSite : IAsyncLifetime
{
    /// <summary>The administrator every site is set up with.</summary>
    public const string Administrator = "siteadmin";

    /// <summary>Their password, for this throwaway store only.</summary>
    public const string Password = "two-node-site-password-1";

    private readonly List<Node> _nodes = [];

    /// <summary>The schema the store is in.</summary>
    public string Schema { get; } = "zz_site_" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>The key every node seals with, unless a test gives one another.</summary>
    public string Key { get; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>The raw connection string the tests read the store with.</summary>
    public string Database { get; } = Environment.GetEnvironmentVariable("GRATICULA_TEST_PG") is { Length: > 0 } pg
        ? pg
        : throw new InvalidOperationException(
            "GRATICULA_TEST_PG is not set, so these tests FAIL rather than skip. They start two servers over a schema of it.");

    /// <summary>The platform store as a node is given it.</summary>
    public string Store => new NpgsqlConnectionStringBuilder(Database) { SearchPath = Schema }.ConnectionString;

    private string? _setupToken;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        await using (NpgsqlConnection connection = new(Database))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand create = new($"create schema {Schema}", connection);
            await create.ExecuteNonQueryAsync();
        }

        (int exit, string said) = await RunAsync(["migrate", "--apply", "--no-backup"], new Dictionary<string, string>());
        Assert.True(exit == 0, "The store could not be migrated:\n" + said);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        foreach (Node node in _nodes)
        {
            node.Kill();
        }

        await using NpgsqlConnection connection = new(Database);
        await connection.OpenAsync();
        await using NpgsqlCommand drop = new($"drop schema if exists {Schema} cascade", connection);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>Starts a node and waits until it answers; the first one also sets the site up.</summary>
    /// <param name="name">Its node name.</param>
    /// <param name="extra">Settings beyond the ones every node is given, or ones that replace them.</param>
    /// <returns>The node.</returns>
    public async Task<Node> StartAsync(string name, IReadOnlyDictionary<string, string>? extra = null)
    {
        Node node = Launch(name, extra);
        _nodes.Add(node);

        await node.WaitLiveAsync(TimeSpan.FromSeconds(90));

        if (_setupToken is null)
        {
            _setupToken = await node.SetupTokenAsync(TimeSpan.FromSeconds(30));
            using HttpResponseMessage set = await node.Http.PostAsync(
                "/rest/setup", Json(new { token = _setupToken, name = Administrator, password = Password }));
            Assert.True(set.IsSuccessStatusCode, $"setup answered {(int)set.StatusCode}: {await set.Content.ReadAsStringAsync()}");
        }

        return node;
    }

    /// <summary>Starts a node that is expected to refuse to start, and returns what it said and its exit code.</summary>
    /// <param name="name">Its node name.</param>
    /// <param name="extra">Settings beyond the ones every node is given.</param>
    /// <returns>Its exit code and its log.</returns>
    public async Task<(int? Exit, string Said)> StartRefusedAsync(string name, IReadOnlyDictionary<string, string>? extra = null)
    {
        Node node = Launch(name, extra);
        _nodes.Add(node);

        bool exited = await node.WaitExitAsync(TimeSpan.FromSeconds(60));
        return (exited ? node.ExitCode : null, node.Log);
    }

    private Node Launch(string name, IReadOnlyDictionary<string, string>? extra)
    {
        int port = FreePort();
        string state = Path.Combine(Path.GetTempPath(), Schema, name);
        Directory.CreateDirectory(state);

        Dictionary<string, string> settings = new(StringComparer.Ordinal)
        {
            ["Graticula__PlatformStore"] = Store,
            ["Graticula__SecretKey"] = Key,
            ["Graticula__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Graticula__Listen"] = "127.0.0.1",
            ["Graticula__HostName"] = "127.0.0.1",
            ["Graticula__StatePath"] = Path.Combine(state, "state"),
            ["Graticula__TileCachePath"] = Path.Combine(state, "tilecache"),
            ["Graticula__ImportScratchPath"] = Path.Combine(state, "import"),
            ["Graticula__NodeName"] = name,
            ["Logging__LogLevel__Default"] = "Information",
        };

        foreach ((string key, string value) in extra ?? new Dictionary<string, string>())
        {
            settings[key] = value;
        }

        return new Node(name, port, Start([], settings));
    }

    private static Process Start(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> settings)
    {
        string host = Path.Combine(AppContext.BaseDirectory, "Graticula.Host.dll");
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet ? dotnet : "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        start.ArgumentList.Add(host);

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach ((string key, string value) in settings)
        {
            start.Environment[key] = value;
        }

        return Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
    }

    private async Task<(int Exit, string Said)> RunAsync(IReadOnlyList<string> arguments, Dictionary<string, string> settings)
    {
        settings["Graticula__PlatformStore"] = Store;
        settings["Graticula__SecretKey"] = Key;

        using Process process = Start(arguments, settings);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource limit = new(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(limit.Token);
        return (process.ExitCode, await output + await error);
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A JSON body.</summary>
    public static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>A one-row, one-column read of the store, in the site's schema.</summary>
    public async Task<object?> ScalarAsync(string sql)
    {
        await using NpgsqlConnection connection = new(Store);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>A statement against the store, in the site's schema.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = new(Store);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>One server process of the site.</summary>
    public sealed partial class Node : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _log = new();

        internal Node(string name, int port, Process process)
        {
            Name = name;
            Port = port;
            _process = process;
            _process.OutputDataReceived += (_, line) => { lock (_log) { _log.AppendLine(line.Data); } };
            _process.ErrorDataReceived += (_, line) => { lock (_log) { _log.AppendLine(line.Data); } };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

#pragma warning disable CA5400, CA2000
            HttpClientHandler handler = new()
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
#pragma warning restore CA5400, CA2000
            Http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>Its node name.</summary>
        public string Name { get; }

        /// <summary>Its port.</summary>
        public int Port { get; }

        /// <summary>A client of this node alone.</summary>
        public HttpClient Http { get; }

        /// <summary>Everything it has written so far.</summary>
        public string Log
        {
            get
            {
                lock (_log)
                {
                    return _log.ToString();
                }
            }
        }

        /// <summary>Its exit code, once it has exited.</summary>
        public int ExitCode => _process.ExitCode;

        /// <summary>Whether it is still running.</summary>
        public bool Running => !_process.HasExited;

        /// <summary>Ends it at once, as a crash would.</summary>
        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(10_000);
                }
            }
            catch (InvalidOperationException)
            {
                // Never started, or already gone.
            }
        }

        internal async Task WaitLiveAsync(TimeSpan within)
        {
            Stopwatch clock = Stopwatch.StartNew();

            while (clock.Elapsed < within)
            {
                Assert.False(_process.HasExited, $"Node {Name} exited with {(_process.HasExited ? _process.ExitCode : 0)}:\n{Log}");

                try
                {
                    using HttpResponseMessage live = await Http.GetAsync("/healthz/live");

                    if (live.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }

                await Task.Delay(250);
            }

            Assert.Fail($"Node {Name} did not answer within {within}:\n{Log}");
        }

        internal async Task<bool> WaitExitAsync(TimeSpan within)
        {
            using CancellationTokenSource limit = new(within);

            try
            {
                await _process.WaitForExitAsync(limit.Token);
                await Task.Delay(200);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        [GeneratedRegex(@"^\s+([A-Za-z0-9._-]{20,})\s*$", RegexOptions.Multiline)]
        private static partial Regex TokenLine();

        internal async Task<string> SetupTokenAsync(TimeSpan within)
        {
            Stopwatch clock = Stopwatch.StartNew();

            while (clock.Elapsed < within)
            {
                if (TokenLine().Match(Log) is { Success: true } found)
                {
                    return found.Groups[1].Value;
                }

                await Task.Delay(200);
            }

            throw new InvalidOperationException($"No setup token in node {Name}'s log:\n{Log}");
        }

        /// <summary>Signs in and returns a token.</summary>
        public async Task<string> SignInAsync()
        {
            using HttpResponseMessage answer = await Http.PostAsync(
                "/rest/auth/login", Json(new { name = Administrator, password = Password }));
            string body = await answer.Content.ReadAsStringAsync();
            Assert.True(answer.IsSuccessStatusCode, $"sign-in on {Name} answered {(int)answer.StatusCode}: {body}");
            return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
        }

        /// <summary>A request with a bearer token.</summary>
        public async Task<(int Status, string Body)> SendAsync(HttpMethod method, string path, string? token, object? body = null)
        {
            using HttpRequestMessage request = new(method, path);

            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (body is not null)
            {
                request.Content = Json(body);
            }

            using HttpResponseMessage answer = await Http.SendAsync(request);
            return ((int)answer.StatusCode, await answer.Content.ReadAsStringAsync());
        }

        /// <summary>Waits until a condition on this node holds, or fails with what it last saw.</summary>
        public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> holds, TimeSpan within, string what)
        {
            Stopwatch clock = Stopwatch.StartNew();
            T last = await read();

            while (!holds(last))
            {
                Assert.True(clock.Elapsed < within, $"{what} did not happen within {within.TotalSeconds:0} s; last seen: {last}");
                await Task.Delay(200);
                last = await read();
            }

            return last;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Kill();
            Http.Dispose();
            _process.Dispose();
        }
    }
}
