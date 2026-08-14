using System;
using System.IO;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace GisServer.Host;

/// <summary>
/// What the server needs in order to start.
/// </summary>
/// <remarks>
/// <para>
/// ADR-017 §5c: <b>configuration holds what is needed to start; everything else
/// is state and goes through the admin API.</b> Certificates, migrations and
/// pinning are all operations rather than file edits. If this class grows a
/// setting that changes behaviour at runtime, that setting is in the wrong
/// place.
/// </para>
/// <para>
/// Validated once, here, at startup. A server that starts and then fails on the
/// first request because a setting was wrong is harder to diagnose than one that
/// refuses with a reason.
/// </para>
/// </remarks>
internal sealed record HostSettings(
    string PlatformStore,
    string SecretKeyBase64,
    int SecretKeyVersion,
    IPAddress ListenAddress,
    int Port,
    string HostName,
    bool RequireHttps,
    string? CertificatePath,
    string? CertificatePassword,
    TimeSpan SessionLifetime,
    string StatePath,
    string TileCachePath,
    long TileCacheBudgetBytes,
    long TileCacheLayerBudgetBytes,
    TimeSpan TileCacheLifetime)
{
    /// <summary>Reads and validates settings.</summary>
    /// <exception cref="InvalidOperationException">A setting is missing or unusable.</exception>
    public static HostSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string platformStore = Require(configuration, "GisServer:PlatformStore",
            "the connection string for the platform store");

        string key = Require(configuration, "GisServer:SecretKey",
            "the base64 AES-256 key that seals data source credentials (ADR-002 §4.7)");

        try
        {
            int length = Convert.FromBase64String(key).Length;
            if (length != 32)
            {
                throw new InvalidOperationException(
                    $"GisServer:SecretKey decodes to {length} bytes; AES-256 needs 32.");
            }
        }
        catch (FormatException e)
        {
            throw new InvalidOperationException("GisServer:SecretKey is not valid base64.", e);
        }

        string listen = configuration["GisServer:Listen"] ?? "0.0.0.0";
        if (!IPAddress.TryParse(listen, out IPAddress? address))
        {
            throw new InvalidOperationException($"GisServer:Listen '{listen}' is not an IP address.");
        }

        // HTTPS unless explicitly disabled — ADR-014 §2a. The default is the
        // secure one, and turning it off is a deliberate act that warns at every
        // startup rather than once.
        bool requireHttps = configuration.GetValue("GisServer:RequireHttps", defaultValue: true);

        // <b>A budget with a default, because N6's finding was that there was
        // none at all.</b> 2 GB holds a useful seeded pyramid for a handful of
        // layers and will not surprise anybody running this on a laptop; a
        // deployment that wants more sets it, and the number is visible in one
        // place rather than implied by whatever the disk happened to have.
        long budget = configuration.GetValue("GisServer:TileCacheBudgetMB", defaultValue: 2048L);

        // <b>Per-layer quota, so one layer cannot take the whole cache.</b>
        // A quarter of the total: enough that a single busy layer is not
        // artificially starved, small enough that four of them cannot crowd
        // everything else out between them.
        long layerBudget = configuration.GetValue(
            "GisServer:TileCacheLayerBudgetMB", defaultValue: Math.Max(1, budget / 4));

        return new HostSettings(
            platformStore,
            key,
            configuration.GetValue("GisServer:SecretKeyVersion", defaultValue: 1),
            address,
            configuration.GetValue("GisServer:Port", defaultValue: requireHttps ? 8443 : 8080),
            configuration["GisServer:HostName"] ?? Dns.GetHostName(),
            requireHttps,
            configuration["GisServer:CertificatePath"],
            configuration["GisServer:CertificatePassword"],

            // Twelve hours: long enough that a working day does not need a
            // second sign-in, short enough that a token copied out of a browser
            // is not useful next week. ADR-015 3 makes this cheap to change --
            // sessions are server-side, so shortening it takes effect at once
            // rather than waiting for issued tokens to expire.
            TimeSpan.FromHours(configuration.GetValue("GisServer:SessionHours", defaultValue: 12)),

            // ADR-016 §3's secret volume. Defaults to a directory beside the
            // process for a local run, and is mounted in a container. Anything
            // that must survive a container replacement and is not in the
            // platform database lives here — today that is the serving
            // certificate.
            configuration["GisServer:StatePath"]
                ?? Path.Combine(AppContext.BaseDirectory, "state"),

            // <b>Not under StatePath, deliberately.</b> StatePath holds things
            // that must survive a container replacement — the serving
            // certificate. A tile cache must not: it is derived data, it is the
            // largest thing this server writes, and putting it on the volume
            // that must be backed up would make every backup carry gigabytes of
            // tiles that can be rebuilt from the database in seconds.
            configuration["GisServer:TileCachePath"]
                ?? Path.Combine(AppContext.BaseDirectory, "tilecache"),

            budget * 1024 * 1024,
            layerBudget * 1024 * 1024,

            // <b>One hour, and it is a placeholder for a per-layer setting.</b>
            // ADR-010 §5.3 is explicit that TTL cannot be one global number — a
            // cadastral layer that changes twice a year and an incident layer
            // that changes every minute need opposite answers, and the
            // administrator is the only person who knows which is which (A-028).
            // Volatility is not in the schema yet, so this is the floor until it
            // is, and it is written here rather than left at some library
            // default so the gap is visible.
            TimeSpan.FromMinutes(
                configuration.GetValue("GisServer:TileCacheMinutes", defaultValue: 60)));
    }

    private static string Require(IConfiguration configuration, string key, string what) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. It is {what}. Set it in appsettings.json, in user "
                + "secrets, or as the environment variable "

                // Two underscores, not one. The environment provider maps '__'
                // to ':'; a single underscore produces a variable that is set,
                // looks right, and is never read. The first version of this
                // message said one underscore and cost a startup to find.
                + $"{key.Replace(":", "__", StringComparison.Ordinal)}.");
}
