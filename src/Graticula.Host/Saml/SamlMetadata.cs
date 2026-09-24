using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Graticula.Platform.Identity;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using Microsoft.Extensions.Logging;

namespace Graticula.Host.Saml;

/// <summary>
/// A SAML identity provider's metadata: read from its URL, refreshed daily, and read for what a sign-in needs — ADR-090.
/// </summary>
/// <remarks>
/// <para>
/// <b>One of the named outbound files</b> (<c>tools/registers-check.py</c>, D-274): it connects only to the metadata URL
/// an operator configured, when they save or check the provider, and when a sign-in finds the copy held older than a
/// day. A provider whose metadata was uploaded is never asked anything.
/// </para>
/// <para>
/// <b>The certificates are trusted because the metadata names them</b>, which is how SAML establishes trust: a
/// provider's signing certificate is usually self-signed, and no chain would say more than the operator did by
/// configuring it. A metadata document that cannot be read keeps the copy held, so a provider's short outage is not
/// everybody's.
/// </para>
/// </remarks>
internal sealed class SamlMetadata
{
    /// <summary>The binding a sign-in is sent to the provider with.</summary>
    internal const string RedirectBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";

    private const int Largest = 2 * 1024 * 1024;
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(1);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = Largest,
    };

    private readonly IIdentityProviderStore _store;
    private readonly ILogger _log;

    /// <summary>Creates the reader.</summary>
    public SamlMetadata(IIdentityProviderStore store, ILoggerFactory logs)
    {
        ArgumentNullException.ThrowIfNull(logs);

        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = logs.CreateLogger("Graticula.Saml");
    }

    /// <summary>What a sign-in needs of a provider's metadata.</summary>
    /// <param name="EntityId">Its entity id, which its responses name as their issuer.</param>
    /// <param name="SignOn">Where a sign-in is sent, by HTTP redirect.</param>
    /// <param name="Certificates">What its signatures are checked with.</param>
    internal sealed record Described(string EntityId, Uri SignOn, IReadOnlyList<X509Certificate2> Certificates)
    {
        /// <summary>When the first of its certificates expires.</summary>
        public DateTimeOffset Expires => Certificates.Min(c => new DateTimeOffset(c.NotAfter.ToUniversalTime()));
    }

    /// <summary>Why a metadata URL cannot be used, or null when it can: HTTPS, or HTTP to this machine.</summary>
    public static string? UrlRefusal(string url) =>
        !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? "The metadata URL is a URL, as in https://login.microsoftonline.com/<tenant>/federationmetadata/2007-06/federationmetadata.xml."
            : uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
                ? null
                : "The metadata URL must be HTTPS: its certificates are what every sign-in is checked with, and over "
                  + "HTTP anybody on the way could replace them. Plain HTTP is accepted only for a provider on this machine.";

    /// <summary>Reads a metadata document for what a sign-in needs, or throws <see cref="SamlException"/> saying why not.</summary>
    public static Described Describe(string metadata)
    {
        // A document type is how an entity expansion is smuggled in, and no metadata needs one.
        if (string.IsNullOrWhiteSpace(metadata) || metadata.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            throw new SamlException("That is not a SAML metadata document.");
        }

        EntityDescriptor read;

        try
        {
            read = new EntityDescriptor().ReadIdPSsoDescriptor(metadata);
        }
        catch (Exception e) when (e is XmlException or FormatException or InvalidOperationException
            or ITfoxtec.Identity.Saml2.Saml2RequestException or System.Security.Cryptography.CryptographicException)
        {
            throw new SamlException($"The metadata could not be read: {e.Message}");
        }

        if (read.IdPSsoDescriptor is null)
        {
            throw new SamlException("The metadata describes no identity provider: it has no IDPSSODescriptor.");
        }

        List<X509Certificate2> certificates = [.. read.IdPSsoDescriptor.SigningCertificates ?? []];

        if (certificates.Count == 0)
        {
            throw new SamlException("The metadata names no signing certificate, so no response from it could be checked.");
        }

        Uri? signOn = (read.IdPSsoDescriptor.SingleSignOnServices ?? [])
            .FirstOrDefault(s => s.Binding?.OriginalString == RedirectBinding)?.Location;

        return signOn is null
            ? throw new SamlException("The metadata offers no sign-in by HTTP redirect, which is the one this server uses.")
            : new Described(read.EntityId, signOn, certificates);
    }

    /// <summary>Reads the metadata at a URL, or throws <see cref="SamlException"/> saying why not.</summary>
    public static async Task<string> FetchAsync(string url, CancellationToken cancellation)
    {
        if (UrlRefusal(url) is { } refusal)
        {
            throw new SamlException(refusal);
        }

        try
        {
            using HttpResponseMessage response = await Http.GetAsync(new Uri(url), cancellation).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

            return response.StatusCode == HttpStatusCode.OK
                ? body
                : throw new SamlException($"{url} answered {(int)response.StatusCode}.");
        }
        catch (HttpRequestException e)
        {
            throw new SamlException($"The metadata could not be read from {url}: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new SamlException($"{url} did not answer within 10 seconds.");
        }
    }

    /// <summary>
    /// A provider's metadata for a sign-in: the copy held, read again from its URL first when that copy is older than
    /// a day. A URL that cannot be read keeps the copy, and says so in the log.
    /// </summary>
    public async Task<Described> CurrentAsync(IdentityProvider provider, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(provider);

        SamlSettings saml = provider.Settings.Saml ?? throw new SamlException("This provider has no SAML metadata.");
        string metadata = saml.Metadata;

        if (saml.MetadataUrl.Length > 0 && (saml.FetchedAt is not { } at || DateTimeOffset.UtcNow - at > Fresh))
        {
            try
            {
                string fetched = await FetchAsync(saml.MetadataUrl, cancellation).ConfigureAwait(false);
                Describe(fetched);
                await _store.SetSamlMetadataAsync(provider.Id, fetched, DateTimeOffset.UtcNow, cancellation).ConfigureAwait(false);
                metadata = fetched;
            }
            catch (SamlException e)
            {
                Log.SamlMetadataKept(_log, provider.Settings.Name, e.Message);
            }
        }

        return Describe(metadata);
    }
}

/// <summary>Why a SAML provider could not be used, in words an operator or a person signing in can act on.</summary>
internal sealed class SamlException(string message) : Exception(message);
