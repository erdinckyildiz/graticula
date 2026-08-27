using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Looks a second time at a database's certificate, after the handshake has already been
/// refused, so the refusal can be explained by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) condition 4</b>: *data-source
/// certificate expiry produces a named diagnosis, not a generic connection error — tested by
/// expiring one.* Tested 2026-08-27 and it did not. Npgsql throws
/// `NpgsqlException("Exception while performing SSL handshake")` with an
/// `AuthenticationException` inside it, and the platform's own wording underneath that is
/// *The remote certificate was rejected by the provided RemoteCertificateValidationCallback*
/// or an OS error number. None of those carry a date, so the probe's message became *"Could
/// not reach the server: Exception while performing SSL handshake"* — which sends an
/// administrator to look at the network for a problem that is a calendar entry.
/// </para>
/// <para>
/// <b>Why a second connection rather than a validation callback.</b> Installing a callback
/// on the real connection would mean this code deciding what is acceptable, and a diagnostic
/// that can accidentally accept a bad certificate is worse than a vague message. This runs
/// only after the connection has already been refused, takes the certificate without
/// trusting it, and returns a sentence. It changes no outcome — the probe still fails — and
/// it cannot make a rejected certificate work.
/// </para>
/// <para>
/// <b>The dates rather than the OS error, because the dates are portable and actionable.</b>
/// Windows reports an expired certificate as `0x800B0101` and Linux as OpenSSL's
/// `certificate has expired`; neither says *when*, and the operator's next move differs by
/// months. Reading `NotAfter` off the certificate answers both.
/// </para>
/// </remarks>
internal static class SourceCertificate
{
    /// <summary>The postgres SSLRequest packet: length 8, then the magic 80877103.</summary>
    private static readonly byte[] SslRequest = [0, 0, 0, 8, 4, 210, 22, 47];

    /// <summary>
    /// Why the handshake was refused, in one sentence, or null when nothing can be said.
    /// </summary>
    /// <param name="host">The host from the connection string.</param>
    /// <param name="port">Its port.</param>
    /// <param name="now">The moment to measure the dates against.</param>
    /// <param name="timeout">How long this diagnosis may take. It is a diagnosis, not a retry.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>
    /// A sentence naming the expiry, the trust failure or the name mismatch, or null when the
    /// second look failed too — in which case the caller keeps the generic message, because a
    /// wrong diagnosis is worse than a vague one.
    /// </returns>
    public static async Task<string?> WhyRefusedAsync(
        string host,
        int port,
        DateTimeOffset now,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource patience = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);

        patience.CancelAfter(timeout);

        X509Certificate2? presented = null;

        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(host, port, patience.Token).ConfigureAwait(false);

            await using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(SslRequest, patience.Token).ConfigureAwait(false);

            byte[] answer = new byte[1];

            if (await stream.ReadAsync(answer, patience.Token).ConfigureAwait(false) != 1
                || answer[0] != (byte)'S')
            {
                // The server declined TLS outright. That is a different fault with a
                // different fix, and it is not this function's to name.
                return null;
            }

            await using SslStream tls = new(
                stream,
                leaveInnerStreamOpen: true,

                // <b>Captures and refuses.</b> Returning false is what keeps this a
                // diagnosis: the handshake still fails, and no caller can ever end up with a
                // usable connection through this path.
                userCertificateValidationCallback: (_, certificate, _, _) =>
                {
                    if (certificate is not null)
                    {
                        presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    }

                    return false;
                });

            try
            {
                await tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = host },
                    patience.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException)
            {
                // Expected: we refused it ourselves. The certificate is what we came for.
            }
        }
        catch (Exception e) when (e is SocketException or System.IO.IOException
                                      or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }

        if (presented is null)
        {
            return null;
        }

        using (presented)
        {
            return Explain(presented, host, now);
        }
    }

    /// <summary>The sentence itself, separated so it can be tested without a socket.</summary>
    internal static string Explain(X509Certificate2 certificate, string host, DateTimeOffset now)
    {
        DateTimeOffset from = certificate.NotBefore.ToUniversalTime();
        DateTimeOffset until = certificate.NotAfter.ToUniversalTime();

        if (until < now)
        {
            return $"The database's TLS certificate expired on {until:yyyy-MM-dd HH:mm} UTC, "
                + $"{(int)(now - until).TotalDays} days ago. It is a certificate to replace on "
                + "the database, not a network fault, and nothing on this server will make it "
                + "connect again. Its subject is "
                + $"{certificate.Subject}.";
        }

        if (from > now)
        {
            return $"The database's TLS certificate is not valid until {from:yyyy-MM-dd HH:mm} "
                + "UTC. Either it was issued for a future date or one of the two machines has "
                + "the wrong clock.";
        }

        // <b>Not expired, so the refusal was trust or naming</b>, and those are the two
        // remaining reasons a handshake fails on the certificate itself. Both are stated
        // because a self-signed certificate is usually also issued to the wrong name, and an
        // operator told only one of the two fixes it and fails again.
        return $"The database presented a TLS certificate that this server would not accept, "
            + $"valid until {until:yyyy-MM-dd HH:mm} UTC so it has not expired. It is issued to "
            + $"{certificate.Subject} by {certificate.Issuer}"
            + (certificate.Subject == certificate.Issuer ? " -- it is self-signed" : string.Empty)
            + $". Either that issuer is not trusted by this machine, or the certificate is not "
            + $"issued to the name you connected to ({host}). "
            + "`Ssl Mode=VerifyFull` requires both; `Require` requires the first.";
    }
}
