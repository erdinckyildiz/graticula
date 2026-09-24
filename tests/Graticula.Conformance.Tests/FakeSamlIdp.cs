using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A SAML 2.0 identity provider small enough to read: metadata at a URL, and responses to a request signed by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written with the base library's <see cref="SignedXml"/> and nothing else</b>, because a provider built on the
/// library the server validates with would agree with it by construction. The response is an XML document assembled
/// here and its assertion signed here, which is what makes the server's checks real ones.
/// </para>
/// <para>
/// <b>Each way a response can be wrong is a knob</b>: who signs it, what it answers, who it is for, where it is
/// confirmed for, and what is changed after it was signed.
/// </para>
/// </remarks>
internal sealed class FakeSamlIdp : IDisposable
{
    private const string Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private readonly X509Certificate2 _signing = Certificate("fake-idp");

    /// <summary>Starts a provider on a free loopback port.</summary>
    public FakeSamlIdp()
    {
        int port;
        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        Root = $"http://127.0.0.1:{port}";
        EntityId = $"{Root}/entity";
        _listener.Prefixes.Add($"{Root}/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    /// <summary>Where it listens.</summary>
    public string Root { get; }

    /// <summary>Its entity id, which its responses name as their issuer.</summary>
    public string EntityId { get; }

    /// <summary>Where its metadata is published.</summary>
    public string MetadataUrl => $"{Root}/metadata";

    /// <summary>Where a sign-in is sent.</summary>
    public string SignOn => $"{Root}/sso";

    /// <summary>The attribute its groups are listed in, as AD FS names it.</summary>
    public const string GroupsAttribute = "http://schemas.xmlsoap.org/claims/Group";

    /// <summary>Its metadata document.</summary>
    public string Metadata => $"""
        <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{EntityId}">
          <md:IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
            <md:KeyDescriptor use="signing"><ds:KeyInfo xmlns:ds="http://www.w3.org/2000/09/xmldsig#"><ds:X509Data><ds:X509Certificate>{Convert.ToBase64String(_signing.RawData)}</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>
            <md:SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect" Location="{SignOn}"/>
          </md:IDPSSODescriptor>
        </md:EntityDescriptor>
        """;

    /// <summary>The id of the request a redirect to this provider carries, read as the provider would read it.</summary>
    /// <param name="location">Where the server sent the browser.</param>
    /// <returns>The request's id, and the relay state to send back with the response.</returns>
    public (string RequestId, string RelayState) Read(Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (!location.GetLeftPart(UriPartial.Path).Equals(SignOn, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The server sent the browser to {location}, not to {SignOn}.");
        }

        string query = location.Query.TrimStart('?');
        string Param(string name) => Uri.UnescapeDataString(
            query.Split('&').Select(p => p.Split('=', 2)).First(p => p[0] == name)[1].Replace('+', ' '));

        using DeflateStream inflate = new(new MemoryStream(Convert.FromBase64String(Param("SAMLRequest"))), CompressionMode.Decompress);
        string request = new StreamReader(inflate).ReadToEnd();

        return (Regex.Match(request, "\\sID=\"([^\"]+)\"").Groups[1].Value, Param("RelayState"));
    }

    /// <summary>A response signing somebody in, base64 as it is posted.</summary>
    public string Respond(
        string inResponseTo,
        string subject,
        string audience,
        string recipient,
        string[]? groups = null,
        bool sign = true,
        bool signWithAnotherKey = false,
        string? confirmFor = null,
        Func<XmlDocument, XmlDocument>? afterSigning = null,
        string? prologue = null)
    {
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        string until = DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        string from = DateTime.UtcNow.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        string values = string.Concat((groups ?? []).Select(g => $"<saml:AttributeValue>{g}</saml:AttributeValue>"));

        string xml = (prologue ?? string.Empty)
            + $"<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" xmlns:saml=\"{Assertion}\" "
            + $"ID=\"_r{Guid.NewGuid():N}\" Version=\"2.0\" IssueInstant=\"{now}\" Destination=\"{recipient}\" InResponseTo=\"{inResponseTo}\">"
            + $"<saml:Issuer>{EntityId}</saml:Issuer>"
            + "<samlp:Status><samlp:StatusCode Value=\"urn:oasis:names:tc:SAML:2.0:status:Success\"/></samlp:Status>"
            + $"<saml:Assertion ID=\"_a{Guid.NewGuid():N}\" Version=\"2.0\" IssueInstant=\"{now}\">"
            + $"<saml:Issuer>{EntityId}</saml:Issuer>"
            + $"<saml:Subject><saml:NameID Format=\"urn:oasis:names:tc:SAML:2.0:nameid-format:persistent\">{subject}</saml:NameID>"
            + "<saml:SubjectConfirmation Method=\"urn:oasis:names:tc:SAML:2.0:cm:bearer\">"
            + $"<saml:SubjectConfirmationData InResponseTo=\"{confirmFor ?? inResponseTo}\" NotOnOrAfter=\"{until}\" Recipient=\"{recipient}\"/>"
            + "</saml:SubjectConfirmation></saml:Subject>"
            + $"<saml:Conditions NotBefore=\"{from}\" NotOnOrAfter=\"{until}\"><saml:AudienceRestriction><saml:Audience>{audience}</saml:Audience></saml:AudienceRestriction></saml:Conditions>"
            + $"<saml:AuthnStatement AuthnInstant=\"{now}\" SessionIndex=\"_s1\"><saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport</saml:AuthnContextClassRef></saml:AuthnContext></saml:AuthnStatement>"
            + "<saml:AttributeStatement>"
            + $"<saml:Attribute Name=\"{GroupsAttribute}\">{values}</saml:Attribute>"
            + "<saml:Attribute Name=\"http://schemas.microsoft.com/identity/claims/displayname\"><saml:AttributeValue>Jane Doe</saml:AttributeValue></saml:Attribute>"
            + "</saml:AttributeStatement></saml:Assertion></samlp:Response>";

        XmlDocument document = new() { PreserveWhitespace = true, XmlResolver = null };

        if (prologue is not null)
        {
            // A document type cannot be loaded with DTDs refused, and is what the test is about: send it as written.
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
        }

        document.LoadXml(xml);

        if (sign)
        {
            using X509Certificate2 other = Certificate("somebody-else");
            X509Certificate2 signer = signWithAnotherKey ? other : _signing;
            XmlElement assertion = (XmlElement)document.GetElementsByTagName("Assertion", Assertion)[0]!;

            SignedXml signed = new(assertion) { SigningKey = signer.GetRSAPrivateKey() };
            signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
            signed.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;

            Reference reference = new("#" + assertion.GetAttribute("ID")) { DigestMethod = SignedXml.XmlDsigSHA256Url };
            reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
            reference.AddTransform(new XmlDsigExcC14NTransform());
            signed.AddReference(reference);

            KeyInfo keyInfo = new();
            keyInfo.AddClause(new KeyInfoX509Data(signer));
            signed.KeyInfo = keyInfo;
            signed.ComputeSignature();

            // The signature goes after the assertion's issuer, where the schema puts it.
            assertion.InsertAfter(document.ImportNode(signed.GetXml(), true), assertion.GetElementsByTagName("Issuer", Assertion)[0]!);
        }

        if (afterSigning is not null)
        {
            document = afterSigning(document);
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(document.OuterXml));
    }

    private static X509Certificate2 Certificate(string name)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            byte[] body = context.Request.Url?.AbsolutePath == "/metadata" ? Encoding.UTF8.GetBytes(Metadata) : [];
            context.Response.StatusCode = body.Length > 0 ? 200 : 404;
            context.Response.ContentType = "application/samlmetadata+xml";
            await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();

        try
        {
            _serving.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
        _signing.Dispose();
    }
}
