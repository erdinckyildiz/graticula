using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GisServer.Host;

/// <summary>
/// Produces the self-signed certificate the server serves with until an
/// administrator installs a real one.
/// </summary>
/// <remarks>
/// <para>
/// ADR-014 §2a: HTTPS from first start, with a generated certificate. The
/// alternative — plain HTTP by default — fails silently, where this fails
/// loudly in a browser and can be fixed.
/// </para>
/// <para>
/// <b>The cost is admitted rather than glossed:</b> a self-signed default means
/// clients show a warning, and warnings that appear routinely train people to
/// click through them. The mitigation is that installing a real certificate must
/// be easy, and that ADR-014 §2b requires it to take effect without a restart —
/// because restarting a worker evicts every warm service context, which is the
/// cold-start storm ADR-007 §4.4 exists to avoid.
/// </para>
/// <para>
/// <b>Generated fresh each start, deliberately, and this is a gap.</b>
/// ADR-016 §3 lists certificates as state that must survive a container
/// replacement. Persisting them belongs with the admin API's certificate
/// endpoints (ADR-017 §4), which do not exist yet — so today every restart
/// produces a new certificate and every client sees a new warning. Recorded
/// here rather than discovered.
/// </para>
/// </remarks>
internal static class ServerIdentity
{
    /// <summary>How long a generated certificate lasts.</summary>
    /// <remarks>
    /// A year, not ten. ADR-014 §2c makes expiry a monitored, warned-about
    /// event; a certificate that outlives the deployment would never exercise
    /// that path, and the first time anyone tested it would be the first time it
    /// mattered.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    /// <summary>Generates a self-signed certificate for <paramref name="hostName"/>.</summary>
    public static X509Certificate2 GenerateSelfSigned(string hostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);

        using RSA key = RSA.Create(2048);

        CertificateRequest request = new(
            $"CN={hostName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));   // server authentication

        // Modern clients ignore CN and require a subject alternative name, so a
        // certificate without one is rejected outright rather than merely
        // warned about — which would make the "generate and serve" default fail
        // instead of degrade.
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(hostName);
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Backdated five minutes: a client whose clock is slightly behind ours
        // would otherwise reject a certificate that is valid, and clock skew is
        // recorded as unwalked in failure-scenarios.
        using X509Certificate2 certificate = request.CreateSelfSigned(
            now.AddMinutes(-5), now.Add(Lifetime));

        // Round-tripped through PFX because Kestrel needs an exportable private
        // key on Windows; the ephemeral key from CreateSelfSigned is not usable
        // directly there.
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx), password: null);
    }
}
