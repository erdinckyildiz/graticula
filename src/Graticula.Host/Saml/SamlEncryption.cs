using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;

namespace Graticula.Host.Saml;

/// <summary>
/// This server's keys for encrypted SAML assertions — ADR-090 condition 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>A key per provider, made here when an operator turns encryption on</b>: an RSA key and a self-signed certificate
/// for it, which the provider is given through this server's SP metadata — as AD FS, Entra ID, Okta and Keycloak all
/// take an SP's encryption certificate. The private key is sealed with the server's secret key, as a client secret is,
/// and opened only to read a response.
/// </para>
/// <para>
/// <b>Rotation keeps one key behind.</b> A new key is published at once; the one before it still opens assertions until
/// the next rotation, so a provider that reads the metadata again later, or a sign-in already on its way, does not fail
/// in between. Nobody's sign-in depends on the certificate's dates: an SP's encryption certificate is a carrier for its
/// public key, and it is not checked against a chain by the providers that use it.
/// </para>
/// </remarks>
internal static class SamlEncryption
{
    /// <summary>How many keys are kept: the one published, and the one before it.</summary>
    private const int Kept = 2;

    /// <summary>How long a certificate made here says it is good for.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(5 * 365);

    /// <summary>The SAML 2.0 assertion namespace.</summary>
    private const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";

    /// <summary>The SAML 2.0 protocol namespace.</summary>
    private const string ProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";

    /// <summary>Makes a key, sealed, with its certificate.</summary>
    /// <param name="protector">The server's secret protector.</param>
    /// <param name="now">The time it is made.</param>
    /// <returns>The key.</returns>
    public static SamlEncryptionKey Make(SecretProtector protector, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(protector);

        using RSA key = RSA.Create(3072);
        CertificateRequest request = new(
            "CN=Graticula SAML encryption " + now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, critical: false));

        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddMinutes(-5), now + Lifetime);

        return new SamlEncryptionKey(
            certificate.ExportCertificatePem(),
            Convert.ToBase64String(protector.Protect(key.ExportPkcs8PrivateKeyPem())),
            protector.KeyVersion,
            now);
    }

    /// <summary>A provider's keys after a rotation: a new one first, and the one that was first behind it.</summary>
    /// <param name="keys">The keys held, or null.</param>
    /// <param name="protector">The server's secret protector.</param>
    /// <param name="now">The time.</param>
    /// <returns>The keys to keep.</returns>
    public static IReadOnlyList<SamlEncryptionKey> Rotate(
        IReadOnlyList<SamlEncryptionKey>? keys, SecretProtector protector, DateTimeOffset now) =>
        [Make(protector, now), .. (keys ?? []).Take(Kept - 1)];

    /// <summary>The certificate a provider encrypts to: the first key's, or null.</summary>
    /// <param name="saml">The provider's SAML settings.</param>
    /// <returns>The certificate, without its key.</returns>
    public static X509Certificate2? Published(SamlSettings saml) =>
        saml is { EncryptAssertions: true, EncryptionKeys: [{ } first, ..] } ? X509Certificate2.CreateFromPem(first.Certificate) : null;

    /// <summary>Every key held, opened, as certificates with their private keys for the library to decrypt with.</summary>
    /// <param name="saml">The provider's SAML settings.</param>
    /// <param name="protector">The server's secret protector.</param>
    /// <returns>The certificates.</returns>
    public static IReadOnlyList<X509Certificate2> DecryptionCertificates(SamlSettings? saml, SecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);

        List<X509Certificate2> opened = [];

        foreach (SamlEncryptionKey key in saml?.EncryptionKeys ?? [])
        {
            string pem = protector.Unprotect(Convert.FromBase64String(key.SealedKey), key.KeyVersion);
            using X509Certificate2 joined = X509Certificate2.CreateFromPem(key.Certificate, pem);

            // Through PKCS#12, so the key is one every platform's XML decryption can use rather than an ephemeral
            // handle; the bytes never leave this process.
            byte[] pkcs12 = joined.Export(X509ContentType.Pkcs12);
            opened.Add(X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.EphemeralKeySet));
            CryptographicOperations.ZeroMemory(pkcs12);
        }

        return opened;
    }

    /// <summary>
    /// The SP metadata's encryption key descriptor, or empty when the provider is not set to encrypt: the certificate,
    /// and the algorithms asked for, each one measured opening (ADR-090 condition 3): AES-256 in GCM mode with the key
    /// under XML Encryption 1.1's RSA-OAEP, which Keycloak 26.4 sends, and AES-256-CBC under RSA-OAEP-MGF1P, which the
    /// suite's provider sends.
    /// </summary>
    /// <param name="saml">The provider's SAML settings.</param>
    /// <returns>The XML fragment.</returns>
    public static string KeyDescriptor(SamlSettings? saml)
    {
        if (saml is null || Published(saml) is not { } certificate)
        {
            return string.Empty;
        }

        using (certificate)
        {
            return "<md:KeyDescriptor use=\"encryption\">"
                + "<ds:KeyInfo xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\"><ds:X509Data><ds:X509Certificate>"
                + Convert.ToBase64String(certificate.RawData)
                + "</ds:X509Certificate></ds:X509Data></ds:KeyInfo>"
                + "<md:EncryptionMethod Algorithm=\"http://www.w3.org/2009/xmlenc11#aes256-gcm\"/>"
                + "<md:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#aes256-cbc\"/>"
                + "<md:EncryptionMethod Algorithm=\"http://www.w3.org/2009/xmlenc11#rsa-oaep\"/>"
                + "<md:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#rsa-oaep-mgf1p\"/>"
                + "</md:KeyDescriptor>";
        }
    }

    /// <summary>
    /// Why a response cannot be accepted from a provider that is set to encrypt, or null: an assertion sent in the clear
    /// where an encrypted one is required — anybody who saw the response on its way would have read it.
    /// </summary>
    /// <param name="xml">The response, decoded.</param>
    /// <returns>The refusal.</returns>
    public static string? Unencrypted(string xml)
    {
        XmlDocument document = new() { XmlResolver = null, PreserveWhitespace = true };

        using (XmlReader reader = XmlReader.Create(
            new System.IO.StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            document.Load(reader);
        }

        if (document.DocumentElement is not { LocalName: "Response", NamespaceURI: ProtocolNamespace } response)
        {
            return "The response is not a SAML response.";
        }

        bool clear = response.ChildNodes.OfType<XmlElement>()
            .Any(e => e.LocalName == "Assertion" && e.NamespaceURI == AssertionNamespace);

        return clear
            ? "The assertion arrived unencrypted, and this provider is set to require it encrypted to this server's key. "
              + "Turn on assertion encryption at the provider, with the certificate in this server's SP metadata."
            : null;
    }

    /// <summary>What an operator reads about a key: whose certificate, made when, good until when.</summary>
    /// <param name="key">The key.</param>
    /// <param name="withPem">Whether to include the certificate itself.</param>
    /// <returns>The description.</returns>
    public static object Describe(SamlEncryptionKey key, bool withPem)
    {
        ArgumentNullException.ThrowIfNull(key);
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(key.Certificate);

        return new
        {
            subject = certificate.Subject,
            created = key.Created,
            notAfter = certificate.NotAfter.ToUniversalTime(),
            sha256 = Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            certificate = withPem ? key.Certificate : null,
        };
    }
}
