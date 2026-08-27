using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Postgres;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A data source whose certificate expired last night is told so by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) condition 4</b>: *data-source
/// certificate expiry produces a named diagnosis, not a generic connection error — tested by
/// expiring one.* The connection string is the operator's, so `Ssl Mode=Require` and
/// `VerifyFull` are both reachable, and until this test a failed handshake fell to the last
/// arm of the probe's `Describe` — *"Could not reach the server: …"* — which sends an
/// administrator to look at the network for a problem that is a date.
/// </para>
/// <para>
/// <b>Expired for real, against Npgsql's real code path, without a database.</b> The
/// alternative was a PostgreSQL container built with TLS and a backdated certificate, which
/// is a heavy dependency for one exception shape. Postgres announces TLS before it
/// authenticates anything: the client sends an eight-byte SSLRequest and the server answers
/// with a single `S`, and everything after that is an ordinary TLS handshake. So the server
/// below is those two steps and a certificate that expired yesterday. Npgsql does its own
/// validation, throws its own exception, and the probe classifies it — which is the whole of
/// what this condition is about.
/// </para>
/// <para>
/// <b>No fixture, deliberately.</b> This is the one probe test that must not need a
/// database, because the point is what happens when a database cannot be reached.
/// </para>
/// </remarks>
public sealed class ExpiredSourceCertificateTests
{
    /// <summary>A certificate that stopped being valid yesterday.</summary>
    private static X509Certificate2 ExpiredYesterday()
    {
        using RSA key = RSA.Create(2048);

        CertificateRequest request = new(
            "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Exported and reloaded: a certificate built here and handed straight to SslStream
        // has no private key on Windows in the way the handshake needs it.
        using X509Certificate2 built =
            request.CreateSelfSigned(now.AddDays(-400), now.AddDays(-1));

        return X509CertificateLoader.LoadPkcs12(built.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>
    /// Answers the postgres SSLRequest with <c>S</c>, then offers the expired certificate.
    /// </summary>
    /// <remarks>
    /// <b>Every connection, not one.</b> Written to serve a single handshake at first, which
    /// made the probe's diagnostic second look hang until its timeout -- so the first run of
    /// this test failed in ten seconds rather than in the tenth of a second the fault takes,
    /// and the shape of the failure was the fixture's rather than the code's. A real database
    /// answers twice.
    /// </remarks>
    private static async Task ServeHandshakesAsync(TcpListener listener, X509Certificate2 certificate)
    {
        while (true)
        {
            if (!await ServeOneHandshakeAsync(listener, certificate))
            {
                return;
            }
        }
    }

    private static async Task<bool> ServeOneHandshakeAsync(TcpListener listener, X509Certificate2 certificate)
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = client.GetStream();

            // The SSLRequest packet: four bytes of length, four of the magic 80877103.
            byte[] request = new byte[8];
            int read = 0;
            while (read < request.Length)
            {
                int got = await stream.ReadAsync(request.AsMemory(read));
                if (got == 0)
                {
                    return true;
                }

                read += got;
            }

            await stream.WriteAsync(new byte[] { (byte)'S' });

            await using SslStream tls = new(stream, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(certificate);
        }
        catch (AuthenticationException)
        {
            // The client rejecting our certificate, which is the point of the test.
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            // A connection torn down mid-handshake, likewise expected.
        }
        catch (ObjectDisposedException)
        {
            // The listener was stopped: the test is over.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return true;
    }

    [Theory]

    // <b>The two modes that authenticate the server, and there are exactly two.</b> Written
    // first with `Require` beside them, on the belief that it validates. It does not: Npgsql
    // 9 follows libpq, where `Require` means *encrypt* and nothing more. The run proved it --
    // the expired certificate was accepted and the failure came later, from the startup
    // packet -- and that silence is now [D-190](../../docs/architecture-debt.md) rather than
    // a line in this comment.
    [InlineData("VerifyFull")]
    [InlineData("VerifyCA")]
    public async Task An_expired_source_certificate_is_named_rather_than_called_unreachable(string mode)
    {
        using X509Certificate2 certificate = ExpiredYesterday();

        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task serving = ServeHandshakesAsync(listener, certificate);

        try
        {
            PostgresDataSourceProbe probe = new();

            ProbeResult result = await probe.ProbeAsync(
                $"Host=127.0.0.1;Port={port};Username=anyone;Password=anything;"
                + $"Database=anything;Ssl Mode={mode}",
                CancellationToken.None);

            Assert.Equal(ProbeOutcome.CannotConnect, result.Outcome);

            // The sentence an administrator reads at 03:20. It must contain the word, or
            // they are looking at firewalls for a problem that is a date.
            Assert.Contains("certificate", result.Message, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(
                "Could not reach the server", result.Message, StringComparison.Ordinal);

            // <b>The date itself, which is the whole of the condition.</b> A message that
            // says *the certificate was rejected* is still a message an administrator cannot
            // act on: the fix is a replacement on the database and the question is when it
            // lapsed. Asserting only the word `certificate` would pass on Npgsql's own
            // wording, which is why this line is here.
            Assert.Contains(
                certificate.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd"),
                result.Message,
                StringComparison.Ordinal);

            Assert.Contains("expired", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

}
