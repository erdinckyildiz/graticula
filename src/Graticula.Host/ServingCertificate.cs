using System;
using System.Security.Cryptography.X509Certificates;

namespace Graticula.Host;

/// <summary>
/// The certificate this server presents, so that an operator can be told when it expires.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-017](../../docs/adr/ADR-017-admin-api.md) §3.4 — the scenario with a known date
/// in advance.</b> *"Everything stopped at 03:14"* is the only outage this server can see
/// coming, and §3.4's first step asks that `/admin/health` say **certificate expired at
/// 03:14** rather than leave it to be inferred from a TLS handshake error. Walked
/// 2026-08-27: the endpoint answered and carried no certificate at all. The process had one
/// loaded the whole time and nothing asked it.
/// </para>
/// <para>
/// <b>Kestrel takes the certificate and does not lend it back.</b> `ConfigureKestrel` builds
/// it inside a listener callback, which is the only place it exists; the alternative to
/// remembering it here is reading the file again from the admin route, which would report
/// what is *on disk* rather than what is *being served* — and those differ for exactly the
/// window that matters, between a replacement being installed and a restart.
/// </para>
/// <para>
/// <b>Static, and that is a real cost stated rather than hidden.</b> One certificate per
/// process, set once at startup. It is wrong the moment rotation without a restart exists
/// ([ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) §2b), and that is the same
/// change that would give it somewhere better to live.
/// </para>
/// </remarks>
internal static class ServingCertificate
{
    private static X509Certificate2? _presented;

    /// <summary>Remembers what Kestrel was given.</summary>
    /// <param name="certificate">The certificate, or null when this server is plain HTTP.</param>
    public static void Presenting(X509Certificate2? certificate) => _presented = certificate;

    /// <summary>
    /// What to say about it, or null when there is nothing to say.
    /// </summary>
    /// <param name="now">The moment to measure against, so a test can choose one.</param>
    /// <returns>An object for the health document, or null on a plain-HTTP server.</returns>
    /// <remarks>
    /// <b>`daysRemaining` can be negative, deliberately.</b> An expired certificate is the
    /// case this exists for, and *0 days* would read as *expires today*. The sign is the
    /// difference between a warning and a post-mortem.
    /// </remarks>
    public static object? Describe(DateTimeOffset now) => Describe(_presented, now);

    /// <summary>
    /// The same answer about a certificate handed in, so a test can hand in an expired one.
    /// </summary>
    /// <remarks>
    /// <b>Split from the no-argument overload so the sentence can be falsified.</b> The
    /// interesting states are *expired* and *expiring*, and a test that waits for the
    /// development certificate to reach either would take a year. Every branch below is
    /// reachable from here with a certificate built for the purpose.
    /// </remarks>
    public static object? Describe(X509Certificate2? presented, DateTimeOffset now)
    {
        if (presented is not { } certificate)
        {
            return null;
        }

        DateTimeOffset expires = certificate.NotAfter.ToUniversalTime();
        double days = (expires - now).TotalDays;

        return new
        {
            subject = certificate.Subject,
            issuer = certificate.Issuer,
            notBefore = certificate.NotBefore.ToUniversalTime(),
            notAfter = expires,
            daysRemaining = Math.Floor(days),

            // <b>Said in words as well as in a number, because §3.4 asks for a sentence.</b>
            // An operator reading a dashboard at 03:20 should not have to subtract two dates
            // to find out that this is what happened.
            state = days < 0 ? "expired" : days < 7 ? "expiring" : "valid",

            note = days < 0
                ? $"This server's certificate expired on {expires:yyyy-MM-dd HH:mm} UTC. "
                  + "Every client that checks it is being refused at the handshake, before any "
                  + "request reaches this server -- so its own logs will show the outage as "
                  + "silence rather than as errors."
                : days < 30
                    ? $"This server's certificate expires on {expires:yyyy-MM-dd HH:mm} UTC, in "
                      + $"{Math.Floor(days)} days. Replacing it needs a restart: rotation "
                      + "without one is not built (ADR-014 condition 1)."
                    : null,
        };
    }
}
