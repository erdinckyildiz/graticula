using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Why this server will not serve, as a named state and the sentence it says — ADR-318 §5.1.
/// </summary>
/// <param name="State">The state's name: <c>configuration</c>, <c>store-unreachable</c>, <c>store-not-initialised</c>,
/// <c>store-not-migrated</c>, <c>store-newer</c>, <c>unbound-role</c>, <c>node-refused</c>.</param>
/// <param name="Reason">The sentence, as the log says it.</param>
internal sealed record ServingRefusal(string State, string Reason)
{
    /// <summary>The configuration could not be read (D-171).</summary>
    public const string Configuration = "configuration";

    /// <summary>The platform store does not answer.</summary>
    public const string StoreUnreachable = "store-unreachable";

    /// <summary>The platform store has no schema.</summary>
    public const string StoreNotInitialised = "store-not-initialised";

    /// <summary>The platform store is older than this build: the migration has not been run.</summary>
    public const string StoreNotMigrated = "store-not-migrated";

    /// <summary>The platform store's minimum reader is above this build: a contract migration ran.</summary>
    public const string StoreNewer = "store-newer";

    /// <summary>Several organisations over a role the tenant floor does not bind (ADR-204 condition 9).</summary>
    public const string UnboundRole = "unbound-role";

    /// <summary>The site refused this node (ADR-012).</summary>
    public const string NodeRefused = "node-refused";

    /// <summary>How long a client is told to wait before asking again: the re-check's period.</summary>
    public static readonly TimeSpan Recheck = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Answers one request with the refusal: the health routes in their own shapes, everything else ArcGIS's error, all 503.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="refusal">Why.</param>
    /// <param name="status">What the health routes' <c>status</c> says: <c>refusing</c>, or a restore's <c>restoring</c> (ADR-317).</param>
    /// <returns>When written.</returns>
    public static Task WriteAsync(HttpContext context, ServingRefusal refusal, string status = "refusing")
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(refusal);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = ((int)Recheck.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";

        PathString path = context.Request.Path;

        object body = path.Equals("/healthz/live", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/healthz/ready", StringComparison.OrdinalIgnoreCase)
            ? new { status, state = refusal.State, reason = refusal.Reason }
            : path.Equals("/rest/info/healthcheck", StringComparison.OrdinalIgnoreCase)
                ? new { success = false, error = new { code = 503, message = refusal.Reason, details = new[] { refusal.State } } }
                : new { error = new { code = 503, message = refusal.Reason, details = new[] { status + ": " + refusal.State } } };

        return context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}

/// <summary>
/// Where a refusing server listens — ADR-318 §5.3: read from the settings when they were read, and leniently from the
/// configuration when they were not, so a missing secret key still leaves a port to say so on.
/// </summary>
internal sealed record RefusingListener(
    IPAddress Address, int Port, bool Https, string? CertificatePath, string? CertificatePassword, string HostName, string StatePath)
{
    /// <summary>The settings' own address, port and certificate.</summary>
    public static RefusingListener From(HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(settings.ListenAddress, settings.Port, settings.RequireHttps, settings.CertificatePath,
            settings.CertificatePassword, settings.HostName, settings.StatePath);
    }

    /// <summary>
    /// What can be read of the listening settings from a configuration that failed to read whole; each one that cannot be
    /// read takes its default.
    /// </summary>
    public static RefusingListener Lenient(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? Text(string name) =>
            configuration[$"Graticula:{name}"] is { Length: > 0 } value ? value
            : configuration[$"GisServer:{name}"] is { Length: > 0 } former ? former
            : null;

        bool https = !bool.TryParse(Text("RequireHttps"), out bool parsed) || parsed;
        IPAddress address = IPAddress.TryParse(Text("Listen"), out IPAddress? listen) ? listen : IPAddress.Any;
        int port = int.TryParse(Text("Port"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int p)
            && p is > 0 and < 65536 ? p : https ? 8443 : 8080;

        return new(address, port, https, Text("CertificatePath"), Text("CertificatePassword"),
            Text("HostName") ?? Dns.GetHostName(),
            Text("StatePath") ?? System.IO.Path.Combine(AppContext.BaseDirectory, "state"));
    }
}

/// <summary>
/// A server that refuses to serve and stays up — ADR-318. A small web application of its own, listening where the server
/// would, answering every request 503 with the refusal, until the refusal clears or the process is stopped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own application, not the server's pipeline with a gate in front.</b> The server's hosted services start with
/// it — the tenant directory, the portal's identity, the site's heartbeat — and each reads the store this one is refusing;
/// started against an un-migrated store they would fail in ways nobody asked them to survive. This one has nothing to
/// start but Kestrel.
/// </para>
/// <para>
/// <b>Closed before the server listens</b>: the caller stops and disposes it, which releases the port, and only then
/// runs the server's own application on the same address.
/// </para>
/// </remarks>
internal static partial class RefusingServer
{
    /// <summary>
    /// Listens and refuses until <paramref name="recheck"/> answers null (the refusal cleared: true) or the process is
    /// stopped (false). <paramref name="recheck"/> null is never re-checked.
    /// </summary>
    /// <param name="listener">Where to listen.</param>
    /// <param name="first">The refusal.</param>
    /// <param name="recheck">Asks again; null when the refusal is gone, or the refusal now.</param>
    /// <param name="logger">Where a change of state is said.</param>
    /// <returns>Whether the refusal cleared.</returns>
    /// <exception cref="InvalidOperationException">Nothing could be listened with — no certificate could be loaded or made.</exception>
    public static async Task<bool> HoldAsync(
        RefusingListener listener,
        ServingRefusal first,
        Func<CancellationToken, Task<ServingRefusal?>>? recheck,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(logger);

        ServingRefusal current = first;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        X509Certificate2? certificate = listener.Https ? Certificate(listener) : null;

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(listener.Address, listener.Port, listen =>
            {
                if (certificate is not null)
                {
                    listen.UseHttps(certificate);
                }

                listen.Protocols = HttpProtocols.Http1AndHttp2;
            });
        });

        await using WebApplication app = builder.Build();
        app.Run(context => ServingRefusal.WriteAsync(context, Volatile.Read(ref current)));

        await app.StartAsync().ConfigureAwait(false);
        Refusing(logger, current.State, listener.Https ? "https" : "http", listener.Port, current.Reason);

        CancellationToken stopping = app.Lifetime.ApplicationStopping;
        bool cleared = false;

        try
        {
            while (recheck is not null && !stopping.IsCancellationRequested)
            {
                await Task.Delay(ServingRefusal.Recheck, stopping).ConfigureAwait(false);

                ServingRefusal? now = await recheck(stopping).ConfigureAwait(false);

                if (now is null)
                {
                    cleared = true;
                    break;
                }

                if (!string.Equals(now.State, current.State, StringComparison.Ordinal)
                    || !string.Equals(now.Reason, current.Reason, StringComparison.Ordinal))
                {
                    Volatile.Write(ref current, now);
                    StillRefusing(logger, now.State, now.Reason);
                }
            }

            if (recheck is null)
            {
                await Task.Delay(Timeout.Infinite, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopped while refusing: the caller exits with the refusal's code.
        }

        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);

        if (cleared)
        {
            Cleared(logger, current.State);
        }

        return cleared;
    }

    /// <summary>The certificate the server would present, or a refusal to listen at all.</summary>
    private static X509Certificate2 Certificate(RefusingListener listener)
    {
        if (listener.CertificatePath is { } path)
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(path, listener.CertificatePassword);
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or System.IO.IOException or UnauthorizedAccessException)
            {
                // The configured one cannot be read; the generated one still says why on the same port.
            }
        }

        try
        {
            return ServerIdentity.LoadOrCreate(listener.HostName, listener.StatePath);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or System.IO.IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("No certificate could be loaded or made to refuse with: " + e.Message, e);
        }
    }

    [LoggerMessage(EventId = 2781, Level = LogLevel.Critical,
        Message = "This server refuses to serve ({State}) and stays up: every request on {Scheme} port {Port} is answered 503 "
            + "with the reason, until it is put right (ADR-318). {Reason}")]
    private static partial void Refusing(ILogger logger, string state, string scheme, int port, string reason);

    [LoggerMessage(EventId = 2782, Level = LogLevel.Critical,
        Message = "This server still refuses to serve, now for another reason ({State}) (ADR-318). {Reason}")]
    private static partial void StillRefusing(ILogger logger, string state, string reason);

    [LoggerMessage(EventId = 2783, Level = LogLevel.Warning,
        Message = "The refusal ({State}) has cleared: this server starts serving without a restart (ADR-318).")]
    private static partial void Cleared(ILogger logger, string state);
}
