using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Graticula.Host;

/// <summary>
/// The certificate this server presents: what it is, when it expires, and how it is replaced
/// without a restart.
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
/// <b>[ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) §2b — the load-bearing
/// requirement in that ADR, and it is not primarily a convenience.</b> ADR-007 §4.3–4.4
/// binds service contexts lazily and keeps them warm; restarting to load a certificate
/// evicts every warm context, so a rotation would trigger exactly the cold-start storm the
/// runtime is designed to avoid, on a schedule, for a reason unrelated to any service. So
/// the listener holds a *selector* rather than a certificate, this class holds the answer,
/// and replacing it is one interlocked write.
/// </para>
/// <para>
/// <b>Kestrel takes the certificate and does not lend it back.</b> `ConfigureKestrel` builds
/// it inside a listener callback, which is the only place it exists; the alternative to
/// remembering it here is reading the file again from the admin route, which would report
/// what is *on disk* rather than what is *being served* — and those differ for exactly the
/// window that matters, between a replacement being installed and it taking effect.
/// </para>
/// <para>
/// <b>Static, and that is a real cost stated rather than hidden.</b> One certificate per
/// process, which is what a single listener serves. It would be wrong for a server listening
/// on two addresses with two identities, and that deployment does not exist.
/// </para>
/// </remarks>
internal static class ServingCertificate
{
    private static X509Certificate2? _presented;

    /// <summary>What the listener presents right now.</summary>
    /// <remarks>
    /// Read on every handshake, so the read is a plain volatile one: it pairs with the
    /// interlocked exchange in <see cref="Rotate"/> and costs nothing measurable beside the
    /// RSA operation it precedes.
    /// </remarks>
    public static X509Certificate2? Current => Volatile.Read(ref _presented);

    /// <summary>Remembers what Kestrel was given at startup.</summary>
    /// <param name="certificate">The certificate, or null when this server is plain HTTP.</param>
    public static void Presenting(X509Certificate2? certificate) =>
        Volatile.Write(ref _presented, certificate);

    /// <summary>
    /// Replaces it. The next handshake uses the new one; connections in flight finish on the
    /// old one, which is §2b's sentence and is a property of TLS rather than of this code.
    /// </summary>
    /// <param name="replacement">The new certificate. It must have a private key.</param>
    /// <returns>The one it replaced, for the caller to let go of.</returns>
    /// <remarks>
    /// <b>The old one is returned rather than disposed here.</b> A handshake that started a
    /// microsecond ago may still be using it, and disposing a certificate out from under
    /// `SslStream` produces an error at the client that looks like a network fault. Letting
    /// the garbage collector take it is correct, and it is why this returns rather than
    /// cleans up.
    /// </remarks>
    public static X509Certificate2? Rotate(X509Certificate2 replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        if (!replacement.HasPrivateKey)
        {
            throw new ArgumentException(
                "A serving certificate without its private key cannot complete a handshake. "
                + "This would have replaced a working certificate with one that refuses every "
                + "connection, so it is refused here instead.",
                nameof(replacement));
        }

        return Interlocked.Exchange(ref _presented, replacement);
    }

    /// <summary>
    /// What to say about it, or null when there is nothing to say.
    /// </summary>
    /// <param name="now">The moment to measure against, so a test can choose one.</param>
    /// <returns>An object for the health document, or null on a plain-HTTP server.</returns>
    public static object? Describe(DateTimeOffset now) => Describe(Current, now);

    /// <summary>
    /// The same answer about a certificate handed in, so a test can hand in an expired one.
    /// </summary>
    /// <remarks>
    /// <b>Split from the no-argument overload so the sentence can be falsified.</b> The
    /// interesting states are *expired* and *critical*, and a test that waits for the
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

            // <b>`daysRemaining` can be negative, deliberately.</b> An expired certificate is
            // the case this exists for, and *0 days* would read as *expires today*. The sign
            // is the difference between a warning and a post-mortem.
            daysRemaining = Math.Floor(days),

            // <b>ADR-014 §2c's ladder, rather than a threshold of this file's choosing</b>:
            // *a warning at 30 days, escalating at 7, critical at 1.* §2c gives that duty to
            // a runtime supervisor that does not exist; the ladder itself does not need one
            // to be true, and the operator reading this page is the person §2c is written
            // for.
            // <b>`<=`, and the boundary is the whole reason this is tested rung by rung.</b>
            // §2c says *escalating at 7*, and `< 7` makes a certificate with exactly seven
            // days left a warning -- moving the page six days later than the ADR says. Found
            // by the test, not by reading it back.
            state =
                days < 0 ? "expired"
                : days <= 1 ? "critical"
                : days <= 7 ? "escalating"
                : days <= 30 ? "warning"
                : "valid",

            // Said in words as well as in a number, because §3.4 asks for a sentence. An
            // operator reading a dashboard at 03:20 should not have to subtract two dates to
            // find out that this is what happened.
            note = days < 0
                ? $"This server's certificate expired on {expires:yyyy-MM-dd HH:mm} UTC. "
                  + "Every client that checks it is being refused at the handshake, before any "
                  + "request reaches this server -- so its own logs will show the outage as "
                  + "silence rather than as errors."
                : days <= 30
                    ? $"This server's certificate expires on {expires:yyyy-MM-dd HH:mm} UTC, in "
                      + $"{Math.Floor(days)} days. Replacing the file it was loaded from is "
                      + "enough: the replacement is picked up on the next handshake, and no "
                      + "restart is needed (ADR-014 §2b)."
                    : null,
        };
    }
}
