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
    string StatePath)
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
                ?? Path.Combine(AppContext.BaseDirectory, "state"));
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
