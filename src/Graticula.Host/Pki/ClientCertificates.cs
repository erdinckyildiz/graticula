using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Graticula.Host.Pki;

/// <summary>When Kestrel asks a connection for a client certificate — ADR-315 §5.2.</summary>
internal enum ClientCertificateMode
{
    /// <summary>Never: a certificate can only arrive in a trusted proxy's header.</summary>
    Off,

    /// <summary>Every connection is asked; one without a certificate still connects.</summary>
    Allow,

    /// <summary>Every connection must present a certificate the authorities vouch for, or the handshake fails.</summary>
    Require,
}

/// <summary>What a certificate whose revocation cannot be learned does — ADR-315 §5.6, ADR-014 §7 item 2.</summary>
internal enum RevocationPolicy
{
    /// <summary>A revoked certificate is refused; one whose status cannot be learned is accepted and logged.</summary>
    SoftFail,

    /// <summary>A certificate whose status cannot be learned is refused too.</summary>
    HardFail,

    /// <summary>Revocation is not checked.</summary>
    Off,
}

/// <summary>
/// How client certificates reach this server and which ones it believes — ADR-315 §5.2, the transport half ADR-014 §6
/// promised. Read once at start; a setting that cannot work refuses to start.
/// </summary>
/// <param name="Mode">When Kestrel asks a connection for a certificate.</param>
/// <param name="Authorities">The certificate authorities a client certificate must chain to.</param>
/// <param name="Header">The header a trusted proxy forwards the certificate in, or null when none is read.</param>
/// <param name="Revocation">The revocation policy.</param>
/// <param name="AuthoritiesPath">Where the authorities were read from, for the console to say.</param>
internal sealed record ClientCertificateSettings(
    ClientCertificateMode Mode,
    X509Certificate2Collection Authorities,
    string? Header,
    RevocationPolicy Revocation,
    string? AuthoritiesPath)
{
    /// <summary>No client certificate is ever read: the default.</summary>
    public static ClientCertificateSettings None { get; } =
        new(ClientCertificateMode.Off, [], null, RevocationPolicy.SoftFail, null);

    /// <summary>Whether a certificate can arrive at all.</summary>
    public bool Enabled => Mode != ClientCertificateMode.Off || Header is not null;

    /// <summary>Reads <c>Graticula:ClientCertificates</c>, refusing what cannot work.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="requireHttps">Whether this server ends TLS itself.</param>
    /// <param name="trustedProxies">The proxies the deployment trusts.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="InvalidOperationException">A setting is unusable — the operator is told which and why.</exception>
    public static ClientCertificateSettings Read(
        IConfiguration configuration, bool requireHttps, IReadOnlyList<IPNetwork> trustedProxies)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(trustedProxies);

        string? Text(string name) =>
            configuration[$"Graticula:ClientCertificates:{name}"] is { Length: > 0 } current ? current.Trim()
            : configuration[$"GisServer:ClientCertificates:{name}"] is { Length: > 0 } was ? was.Trim()
            : null;

        ClientCertificateMode mode = Parse(Text("Mode"), ClientCertificateMode.Off, "Mode", "Off, Allow or Require");
        RevocationPolicy revocation = Parse(Text("Revocation"), RevocationPolicy.SoftFail, "Revocation", "SoftFail, HardFail or Off");
        string? header = Text("Header");
        string? path = Text("Authorities");

        if (mode == ClientCertificateMode.Off && header is null)
        {
            return None with { Revocation = revocation };
        }

        if (mode != ClientCertificateMode.Off && !requireHttps)
        {
            throw new InvalidOperationException(
                $"Graticula:ClientCertificates:Mode is {mode}, but Graticula:RequireHttps is false, so this server makes no "
                + "TLS handshake to ask for a certificate in. Behind a proxy that ends TLS, set Mode to Off and have the proxy "
                + "forward the certificate in Graticula:ClientCertificates:Header (ADR-315 §5.2).");
        }

        if (header is not null && trustedProxies.Count == 0)
        {
            throw new InvalidOperationException(
                $"Graticula:ClientCertificates:Header is '{header}', but Graticula:TrustedProxies is empty, so no peer may "
                + "send it and it would never be read. Name the proxy that ends TLS in Graticula:TrustedProxies (ADR-315 §5.3).");
        }

        if (path is null)
        {
            throw new InvalidOperationException(
                "Client certificates are turned on (Graticula:ClientCertificates:Mode or :Header), but "
                + "Graticula:ClientCertificates:Authorities is not set. It is a PEM file of the certificate authorities a "
                + "client certificate must chain to; without it no certificate could be believed (ADR-315 §5.2).");
        }

        X509Certificate2Collection authorities = [];

        try
        {
            authorities.ImportFromPemFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidOperationException(
                $"Graticula:ClientCertificates:Authorities is '{path}', which could not be read as PEM certificates: {e.Message}", e);
        }

        if (authorities.Count == 0)
        {
            throw new InvalidOperationException(
                $"Graticula:ClientCertificates:Authorities is '{path}', which holds no certificate. It must hold the PEM "
                + "certificates of the authorities a client certificate chains to (ADR-315 §5.2).");
        }

        return new ClientCertificateSettings(mode, authorities, header, revocation, path);
    }

    private static T Parse<T>(string? text, T fallback, string name, string allowed)
        where T : struct, Enum =>
        text is null ? fallback
        : Enum.TryParse(text, ignoreCase: true, out T value) && Enum.IsDefined(value) ? value
        : throw new InvalidOperationException($"Graticula:ClientCertificates:{name} is '{text}'; it is one of {allowed}.");
}

/// <summary>
/// The client certificate a request carries, whether this server believes it, and whom it names — ADR-315 §5.3–§5.4.
/// </summary>
internal sealed partial class ClientCertificates
{
    /// <summary>The certificate fields a PKI provider may name an account by — ADR-315 §5.1.</summary>
    public static readonly IReadOnlyList<string> Fields = ["cn", "email", "upn"];

    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private const string UserPrincipalName = "1.3.6.1.4.1.311.20.2.3";
    private const string CommonName = "2.5.4.3";
    private const string EmailAddress = "1.2.840.113549.1.9.1";
    private const string SubjectAlternativeName = "2.5.29.17";

    private readonly ClientCertificateSettings _settings;
    private readonly IReadOnlyList<IPNetwork> _trustedProxies;
    private readonly ILogger _log;
    private readonly X509Certificate2Collection _roots = [];
    private readonly X509Certificate2Collection _intermediates = [];

    /// <summary>A reader over the deployment's settings.</summary>
    public ClientCertificates(ClientCertificateSettings settings, IReadOnlyList<IPNetwork> trustedProxies, ILogger<ClientCertificates> log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _trustedProxies = trustedProxies;
        _log = log;

        foreach (X509Certificate2 authority in settings.Authorities)
        {
            (authority.SubjectName.RawData.AsSpan().SequenceEqual(authority.IssuerName.RawData) ? _roots : _intermediates).Add(authority);
        }
    }

    /// <summary>The settings.</summary>
    public ClientCertificateSettings Settings => _settings;

    /// <summary>
    /// The certificate a request carries — the connection's own, or a trusted proxy's header — or null; never one from a
    /// header sent by anybody but a trusted proxy (ADR-315 §5.3).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The certificate, and why none was taken when a header was ignored.</returns>
    public async Task<(X509Certificate2? Certificate, string? Ignored)> PresentedAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_settings.Mode != ClientCertificateMode.Off
            && await context.Connection.GetClientCertificateAsync(context.RequestAborted).ConfigureAwait(false) is { } connection)
        {
            return (connection, null);
        }

        if (_settings.Header is not { } header
            || context.Request.Headers[header].ToString() is not { Length: > 0 } value)
        {
            return (null, null);
        }

        // The socket, not the resolved caller: who wrote the header is the peer this connection is with.
        if (context.Connection.RemoteIpAddress is not { } socket || !CallerAddress.IsTrusted(socket, _trustedProxies))
        {
            string ignored = $"A {header} header came from {context.Connection.RemoteIpAddress}, which is not a trusted proxy, "
                + "so it was ignored.";
            LogIgnored(_log, header, context.Connection.RemoteIpAddress?.ToString() ?? "an unknown address");
            return (null, ignored);
        }

        X509Certificate2? forwarded = FromHeader(value);

        if (forwarded is null)
        {
            LogUnreadable(_log, header);
            return (null, $"The {header} header the proxy sent is not a certificate.");
        }

        return (forwarded, null);
    }

    /// <summary>A certificate as a proxy forwards it: base64 DER, PEM, or URL-encoded PEM — or null.</summary>
    /// <param name="value">The header's value.</param>
    public static X509Certificate2? FromHeader(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string text = value.Trim();

        try
        {
            if (text.StartsWith('%') || text.Contains("%0A", StringComparison.OrdinalIgnoreCase))
            {
                text = Uri.UnescapeDataString(text);
            }

            if (text.StartsWith("-----BEGIN", StringComparison.Ordinal))
            {
                return X509Certificate2.CreateFromPem(text);
            }

            return text.Length == 0 ? null : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(text));
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why this server does not believe a certificate, or null when it does — ADR-315 §5.4 and §5.6: it chains to the
    /// configured authorities alone, it is within its dates, a listed extended key usage includes client
    /// authentication, and the revocation policy allows it.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>A sentence a person can act on, or null.</returns>
    public string? Refusal(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (_settings.Authorities.Count == 0)
        {
            return "This server is not configured with any certificate authority to trust.";
        }

        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension is X509EnhancedKeyUsageExtension usage && usage.EnhancedKeyUsages.Count > 0
                && !usage.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ClientAuthentication))
            {
                return "This certificate is not for signing in: its key usages do not include client authentication.";
            }
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_roots);
        chain.ChainPolicy.ExtraStore.AddRange(_intermediates);
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = _settings.Revocation == RevocationPolicy.Off ? X509RevocationMode.NoCheck : X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);

        // An intermediate configured but not a root: trust it as the anchor too, so a deployment may name an issuing CA.
        if (_roots.Count == 0)
        {
            chain.ChainPolicy.CustomTrustStore.AddRange(_intermediates);
        }

        chain.Build(certificate);

        X509ChainStatusFlags flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
        X509ChainStatusFlags unknown = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

        if (flags.HasFlag(X509ChainStatusFlags.Revoked))
        {
            return "This certificate has been revoked by the authority that issued it.";
        }

        if ((flags & (X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.NotTimeNested)) != 0)
        {
            return $"This certificate is not valid today: it is good from {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}.";
        }

        if ((flags & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.NotSignatureValid
                | X509ChainStatusFlags.ExplicitDistrust | X509ChainStatusFlags.Cyclic)) != 0
            || !AnchoredHere(chain))
        {
            return $"This certificate was issued by {certificate.Issuer}, which is not an authority this server trusts.";
        }

        if ((flags & unknown) != 0)
        {
            if (_settings.Revocation == RevocationPolicy.HardFail)
            {
                return "Whether this certificate has been revoked could not be learned, and this server refuses a certificate "
                    + "it cannot check.";
            }

            LogRevocationUnknown(_log, certificate.Subject);
            flags &= ~unknown;
        }

        // Anything else the chain engine says is a reason too: it is not ours to wave through what it did not like.
        flags &= ~(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation);

        return flags == X509ChainStatusFlags.NoError
            ? null
            : $"This certificate could not be checked: {string.Join(", ", chain.ChainStatus.Select(s => s.Status).Distinct())}.";
    }

    /// <summary>Whether the chain ends at one of the configured authorities — belt and braces over CustomRootTrust.</summary>
    private bool AnchoredHere(X509Chain chain) =>
        chain.ChainElements.Count > 0
        && _settings.Authorities.Cast<X509Certificate2>().Any(a =>
            chain.ChainElements.Cast<X509ChainElement>().Any(e => e.Certificate.RawData.AsSpan().SequenceEqual(a.RawData)));

    /// <summary>
    /// The value of a certificate's field that names its account — ADR-315 §5.1: <c>cn</c>, <c>email</c> or <c>upn</c>.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <param name="field">The field.</param>
    /// <returns>The value, or null when the certificate does not have it.</returns>
    public static string? Field(X509Certificate2 certificate, string field)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return field switch
        {
            "email" => AlternativeNames(certificate).Email ?? Subject(certificate, EmailAddress),
            "upn" => AlternativeNames(certificate).Upn,
            _ => Subject(certificate, CommonName),
        };
    }

    /// <summary>The account name a certificate's value makes — ADR-315 §5.4: before an @, spaces as dots.</summary>
    public static string AccountName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string name = Graticula.Host.Oidc.OidcEndpoints.AccountName(value);
        return string.Join('.', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? Subject(X509Certificate2 certificate, string oid)
    {
        foreach (X500RelativeDistinguishedName rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == oid
                && rdn.GetSingleElementValue() is { Length: > 0 } value)
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>The first RFC 822 name and the first user principal name in the subject alternative names.</summary>
    private static (string? Email, string? Upn) AlternativeNames(X509Certificate2 certificate)
    {
        if (certificate.Extensions[SubjectAlternativeName] is not { } extension)
        {
            return (null, null);
        }

        string? email = null;
        string? upn = null;

        try
        {
            AsnReader names = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();

            while (names.HasData)
            {
                Asn1Tag tag = names.PeekTag();

                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 1)
                {
                    string value = names.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 1));
                    email ??= value;
                }
                else if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 0)
                {
                    AsnReader other = names.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                    string type = other.ReadObjectIdentifier();
                    AsnReader explicitValue = other.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));

                    if (type == UserPrincipalName && explicitValue.PeekTag().TagValue == (int)UniversalTagNumber.UTF8String)
                    {
                        upn ??= explicitValue.ReadCharacterString(UniversalTagNumber.UTF8String);
                    }
                }
                else
                {
                    names.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException)
        {
            // A malformed extension names nobody; the sign-in says the field is missing.
        }

        return (email, upn);
    }

    [LoggerMessage(EventId = 3150, Level = LogLevel.Warning,
        Message = "A client certificate header {Header} came from {Peer}, which is not in Graticula:TrustedProxies, and was ignored (ADR-315).")]
    private static partial void LogIgnored(ILogger logger, string header, string peer);

    [LoggerMessage(EventId = 3151, Level = LogLevel.Warning,
        Message = "The client certificate header {Header} from a trusted proxy is not a certificate (ADR-315).")]
    private static partial void LogUnreadable(ILogger logger, string header);

    [LoggerMessage(EventId = 3152, Level = LogLevel.Warning,
        Message = "Whether the client certificate of {Subject} is revoked could not be learned; accepted, as Revocation is SoftFail (ADR-315 §5.6).")]
    private static partial void LogRevocationUnknown(ILogger logger, string subject);
}
