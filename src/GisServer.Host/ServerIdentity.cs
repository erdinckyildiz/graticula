using System;
using System.IO;
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
    /// <summary>
    /// Loads the persisted self-signed certificate, generating one on first run.
    /// </summary>
    /// <param name="hostName">The name to put in the subject alternative name.</param>
    /// <param name="statePath">
    /// The directory that survives a container replacement.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>ADR-016 §3 lists certificates as state, and condition 4 says this is
    /// the entry most likely to be treated as configuration by whoever writes
    /// the compose file.</b> It was: until now the server generated a fresh
    /// certificate on every start, so every restart changed the server's
    /// identity and every client that had pinned or accepted it failed at once.
    /// </para>
    /// <para>
    /// <b>Written without a password, and the volume is the protection.</b> A
    /// password stored beside the file it protects is theatre; the honest
    /// statement is that anything able to read this directory holds the
    /// server's private key, which is equally true of the AES key that lives
    /// there too.
    /// </para>
    /// </remarks>
    public static X509Certificate2 LoadOrCreate(string hostName, string statePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);

        string file = Path.Combine(statePath, "serving-certificate.pfx");

        if (File.Exists(file))
        {
            X509Certificate2 existing = X509CertificateLoader.LoadPkcs12FromFile(file, password: null);

            // An expired certificate is replaced rather than served. Serving one
            // fails every client with an error that names expiry, which is at
            // least clear — but a server that can fix it and does not is just
            // waiting to be woken at 03:14 (ADR-017 §3.4).
            if (existing.NotAfter > DateTimeOffset.UtcNow.AddDays(1))
            {
                return existing;
            }

            existing.Dispose();
        }

        // <b>The bytes are written, not re-exported from the certificate.</b>
        // GenerateSelfSigned deliberately loads its result without
        // X509KeyStorageFlags.Exportable, so calling Export on what it returns
        // throws "Key not valid for use in specified state" — on Windows. It
        // does not throw on Linux, which is why the containerised verification
        // of ADR-016 condition 4 passed while the same code could not start on
        // a developer's machine.
        byte[] pkcs12 = CreatePkcs12(hostName);

        Directory.CreateDirectory(statePath);
        File.WriteAllBytes(file, pkcs12);

        return X509CertificateLoader.LoadPkcs12(pkcs12, password: null);
    }

    /// <summary>Generates a self-signed certificate as PKCS#12 bytes.</summary>
    /// <param name="hostName">The name to put in the subject alternative name.</param>
    /// <remarks>
    /// Returns bytes rather than a certificate because the bytes are what gets
    /// persisted, and exporting them a second time from a loaded certificate is
    /// what broke on Windows.
    /// </remarks>
    private static byte[] CreatePkcs12(string hostName)
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

        // Round-tripped through PFX because Kestrel needs a persisted private
        // key on Windows; the ephemeral key from CreateSelfSigned is not usable
        // directly there.
        return certificate.Export(X509ContentType.Pfx);
    }

    /// <summary>Generates a self-signed certificate ready for Kestrel.</summary>
    /// <param name="hostName">The name to put in the subject alternative name.</param>
    public static X509Certificate2 GenerateSelfSigned(string hostName) =>
        X509CertificateLoader.LoadPkcs12(CreatePkcs12(hostName), password: null);
}
