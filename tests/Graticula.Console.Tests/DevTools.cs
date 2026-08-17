using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Console.Tests;

/// <summary>
/// A headless Chrome, and the three things this suite asks of one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Launch, navigate, evaluate.</b> That is the whole surface, and it is why
/// there is no package here: Chrome exposes the DevTools Protocol over a plain
/// WebSocket, request and response are JSON with a matching <c>id</c>, and
/// <see cref="ClientWebSocket"/> ships with the framework. What a browser
/// automation library adds on top — selector engines, auto-waiting, a second and
/// third browser — this suite does not use.
/// </para>
/// <para>
/// <b>One browser per test, deliberately.</b> Launching costs a few hundred
/// milliseconds and buys a clean <c>sessionStorage</c> and no cookies, which two
/// of these tests are entirely about: the difference between a token session and
/// a cookie-only one is invisible if a previous test left either behind.
/// </para>
/// <para>
/// <b>The port is chosen by Chrome, not by us.</b> <c>--remote-debugging-port=0</c>
/// makes it pick a free one and write it to <c>DevToolsActivePort</c> in the
/// profile directory. A fixed port is a suite that fails when a developer has a
/// debugger open, which is a failure about the machine.
/// </para>
/// </remarks>
public sealed class DevTools : IAsyncDisposable
{
    /// <summary>Where Chrome is, when it is not somewhere this class guesses.</summary>
    public const string ChromeVariable = "GRATICULA_TEST_CHROME";

    private readonly Process _chrome;
    private readonly ClientWebSocket _socket;
    private readonly string _profile;
    private readonly byte[] _buffer = new byte[1 << 20];
    private int _nextId;
    private bool _closed;

    private DevTools(Process chrome, ClientWebSocket socket, string profile)
    {
        _chrome = chrome;
        _socket = socket;
        _profile = profile;
    }

    /// <summary>
    /// Finds Chrome, or explains what to set.
    /// </summary>
    /// <returns>An executable path.</returns>
    /// <remarks>
    /// <b>This throws rather than returning null, and the suite fails rather than
    /// skipping</b> — the same rule <c>PostgresFixture</c> states for a database.
    /// A console suite that goes green because no browser was found reports that
    /// the console behaves, which is the one thing it exists to check.
    /// </remarks>
    public static string FindChrome()
    {
        if (Environment.GetEnvironmentVariable(ChromeVariable) is { Length: > 0 } named)
        {
            if (!File.Exists(named))
            {
                throw new FileNotFoundException(
                    $"{ChromeVariable} is set to '{named}' and there is nothing there.", named);
            }

            return named;
        }

        string[] candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\Application\chrome.exe"),
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            }
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? new[] { "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" }
                : new[]
                {
                    "/usr/bin/google-chrome",
                    "/usr/bin/google-chrome-stable",
                    "/usr/bin/chromium-browser",
                    "/usr/bin/chromium",
                };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "No Chrome or Chromium was found, so these tests FAIL rather than skip. Install one, "
            + $"or set {ChromeVariable} to its executable. They drive the operator console in a "
            + "real browser; passing them without one would assert that the console behaves.");
    }

    /// <summary>Starts a browser and attaches to its first tab.</summary>
    /// <returns>The connection.</returns>
    public static async Task<DevTools> LaunchAsync()
    {
        string chrome = FindChrome();

        string profile = Path.Combine(
            Path.GetTempPath(), "graticula-console-" + Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(profile);

        ProcessStartInfo start = new(chrome)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (string argument in new[]
        {
            "--headless=new",
            "--disable-gpu",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",

            // ADR-014 generates a self-signed certificate on start, so a browser
            // that validated the chain could never reach a development server.
            // Accepted here and nowhere else, exactly as ArcGisClient does it.
            "--ignore-certificate-errors",

            // Chrome picks the port and tells us; see the remarks.
            "--remote-debugging-port=0",
            "--user-data-dir=" + profile,

            // A fixed window, because these tests read computed styles and the
            // console's layout is responsive. A suite whose answer depends on the
            // developer's monitor is not an answer.
            "--window-size=1440,1180",
            "about:blank",
        })
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"'{chrome}' did not start.");

        try
        {
            int port = await ReadPortAsync(profile, process);
            string page = await FirstPageTargetAsync(port);

            ClientWebSocket socket = new();
            await socket.ConnectAsync(new Uri(page), CancellationToken.None);

            DevTools tools = new(process, socket, profile);
            await tools.CallAsync("Page.enable");
            await tools.CallAsync("Runtime.enable");
            return tools;
        }
        catch
        {
            Kill(process);
            Sweep(profile);
            throw;
        }
    }

    /// <summary>
    /// Waits for Chrome to publish the port it chose.
    /// </summary>
    /// <remarks>
    /// <b>The file appears before it is complete.</b> Chrome creates
    /// <c>DevToolsActivePort</c> and then writes two lines into it, so a reader
    /// that acts on existence alone gets an empty string — which is why this
    /// requires both lines rather than one.
    /// </remarks>
    private static async Task<int> ReadPortAsync(string profile, Process process)
    {
        string path = Path.Combine(profile, "DevToolsActivePort");

        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Chrome exited with {process.ExitCode} before publishing a debugging port. "
                    + await process.StandardError.ReadToEndAsync());
            }

            if (File.Exists(path))
            {
                // Chrome holds the file open; read it shared rather than exclusively.
                string text;

                try
                {
                    using FileStream stream = new(
                        path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    using StreamReader reader = new(stream);
                    text = await reader.ReadToEndAsync();
                }
                catch (IOException)
                {
                    text = string.Empty;
                }

                string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length >= 2 && int.TryParse(lines[0].Trim(), out int port))
                {
                    return port;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Chrome did not publish a debugging port within ten seconds.");
    }

    /// <summary>The WebSocket URL of the tab, past Chrome's own internal targets.</summary>
    private static async Task<string> FirstPageTargetAsync(int port)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

        for (int attempt = 0; attempt < 100; attempt++)
        {
            string body = await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/json/list"));

            foreach (JsonElement target in JsonDocument.Parse(body).RootElement.EnumerateArray())
            {
                // <b>Filtered by type, because a browser has more targets than
                // tabs.</b> Even a fresh profile reports background pages and
                // service workers, and attaching to one of those gets a socket that
                // answers Page.navigate and paints nothing.
                if (target.TryGetProperty("type", out JsonElement type)
                    && string.Equals(type.GetString(), "page", StringComparison.Ordinal)
                    && target.TryGetProperty("webSocketDebuggerUrl", out JsonElement url)
                    && url.GetString() is { Length: > 0 } socket)
                {
                    return socket;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Chrome reported no page target within five seconds.");
    }

    /// <summary>Sends one protocol command and returns its result.</summary>
    /// <param name="method">The domain and method, e.g. <c>Page.navigate</c>.</param>
    /// <param name="parameters">The command's parameters, or null.</param>
    /// <returns>The <c>result</c> object.</returns>
    /// <remarks>
    /// <b>Events arrive on the same socket as replies, and are dropped here.</b>
    /// A reply carries the <c>id</c> that was sent; anything without one is an
    /// event this suite has not asked for. Reading until the id matches is the
    /// whole of the correlation this client needs, because no two calls are ever
    /// in flight — the tests run one at a time and each awaits its answer.
    /// </remarks>
    public async Task<JsonElement> CallAsync(string method, object? parameters = null)
    {
        int id = ++_nextId;

        string request = JsonSerializer.Serialize(new
        {
            id,
            method,
            @params = parameters ?? new { },
        });

        await _socket.SendAsync(
            Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true,
            CancellationToken.None);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        while (true)
        {
            string message = await ReceiveAsync(deadline.Token);
            JsonElement frame = JsonDocument.Parse(message).RootElement;

            if (!frame.TryGetProperty("id", out JsonElement answered)
                || answered.GetInt32() != id)
            {
                continue;
            }

            if (frame.TryGetProperty("error", out JsonElement error))
            {
                throw new InvalidOperationException($"{method} failed: {error}");
            }

            return frame.TryGetProperty("result", out JsonElement result)
                ? result.Clone()
                : default;
        }
    }

    /// <summary>Reads one whole protocol message, however many frames it took.</summary>
    private async Task<string> ReceiveAsync(CancellationToken cancellation)
    {
        StringBuilder message = new();

        while (true)
        {
            ValueWebSocketReceiveResult received =
                await _socket.ReceiveAsync(_buffer.AsMemory(), cancellation);

            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Chrome closed the debugging socket.");
            }

            message.Append(Encoding.UTF8.GetString(_buffer, 0, received.Count));

            if (received.EndOfMessage)
            {
                return message.ToString();
            }
        }
    }

    /// <summary>Registers a script to run in every document before its own scripts do.</summary>
    /// <param name="source">The script.</param>
    /// <remarks>
    /// <b>This is how the session is planted, and why no file is written into
    /// <c>wwwroot</c>.</b> The console keeps its token in <c>sessionStorage</c>,
    /// which is per-origin and per-tab, so it cannot be planted from outside the
    /// tab — an earlier screenshot harness needed a bounce page served from the
    /// production directory to do it. This runs inside the tab, on the right
    /// origin, before <c>console.js</c> reads the key.
    /// </remarks>
    public Task PlantAsync(string source) =>
        CallAsync("Page.addScriptToEvaluateOnNewDocument", new { source });

    /// <summary>Goes to a URL and waits for the document to finish loading.</summary>
    /// <param name="url">Where to go.</param>
    public async Task NavigateAsync(string url)
    {
        await CallAsync("Page.navigate", new { url });

        // <b>Polled rather than waited on an event.</b> Correlating Page.loadEventFired
        // would mean buffering events this client deliberately discards, and the
        // console is a single-page application whose interesting state arrives
        // after the load event anyway — every caller has a condition of its own to
        // wait for, so this only needs the document to exist.
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (await EvaluateAsync<string>("document.readyState") == "complete")
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"{url} did not finish loading within ten seconds.");
    }

    /// <summary>Evaluates an expression in the page and returns its value.</summary>
    /// <typeparam name="T">What the expression yields.</typeparam>
    /// <param name="expression">JavaScript.</param>
    /// <returns>The value.</returns>
    public async Task<T?> EvaluateAsync<T>(string expression)
    {
        JsonElement answer = await CallAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = true,
        });

        if (answer.TryGetProperty("exceptionDetails", out JsonElement thrown))
        {
            throw new InvalidOperationException(
                $"The page threw evaluating '{Shorten(expression)}': "
                + (thrown.TryGetProperty("exception", out JsonElement exception)
                    && exception.TryGetProperty("description", out JsonElement description)
                        ? description.GetString()
                        : thrown.ToString()));
        }

        JsonElement value = answer.GetProperty("result");

        return value.TryGetProperty("value", out JsonElement got)
            ? got.Deserialize<T>()
            : default;
    }

    private static string Shorten(string expression) =>
        expression.Length <= 120 ? expression : expression[..120] + "…";

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        try
        {
            await CallAsync("Browser.close");
        }
        catch (Exception e) when (e is InvalidOperationException or WebSocketException
            or OperationCanceledException or JsonException)
        {
            // A browser that has already gone is the outcome this asked for.
        }

        _socket.Dispose();

        if (!_chrome.WaitForExit(5000))
        {
            Kill(_chrome);
        }

        _chrome.Dispose();
        Sweep(_profile);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Removes the profile, and does not fail the test if it cannot.
    /// </summary>
    /// <remarks>
    /// A leaked profile directory is a few megabytes in the temporary directory; a
    /// test that fails because Chrome still held a lock on its cache is a false
    /// report about the console. The first is the cheaper mistake.
    /// </remarks>
    private static void Sweep(string profile)
    {
        try
        {
            Directory.Delete(profile, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind; the temporary directory is swept by the operating system.
        }
    }
}
