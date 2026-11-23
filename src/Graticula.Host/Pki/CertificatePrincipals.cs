using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Oidc;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Graticula.Host.Pki;

/// <summary>
/// The member a request's client certificate names, for a request that carries no token — ADR-315 §5.5a, the web-tier
/// model the ArcGIS API for Python expects: given <c>cert_file</c> it sends the certificate on every request and never
/// asks for a token (measured 2026-11-23, arcgis 2.4.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>A certificate is an ambient credential</b>, as the session cookie is: a browser presents it on every connection,
/// including one a page on another site starts. So it is not read for a request that says it comes from another origin
/// (<c>Origin</c>, or <c>Sec-Fetch-Site: cross-site</c>), nor for JSONP; and, like a <c>generateToken</c> token, it opens
/// the ArcGIS faces and never the native administration API (ADR-015 §4).
/// </para>
/// <para>
/// <b>An answer is held for a minute per organisation's host and certificate</b>, so a script's thousand requests cost
/// one chain build and one lookup, not a thousand. A member disabled or a provider turned off reaches a certificate
/// holder within that minute.
/// </para>
/// </remarks>
internal sealed class CertificatePrincipals
{
    private static readonly TimeSpan Held = TimeSpan.FromMinutes(1);

    private readonly ClientCertificates _certificates;
    private readonly IIdentityProviderStore _providers;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, (Principal? Principal, DateTimeOffset Until)> _answers = new(StringComparer.Ordinal);

    /// <summary>Creates the resolver.</summary>
    public CertificatePrincipals(
        ClientCertificates certificates, IIdentityProviderStore providers, TimeProvider time, ILogger<CertificatePrincipals> log)
    {
        _certificates = certificates;
        _providers = providers;
        _time = time;
        _log = log;
    }

    /// <summary>The member the request's certificate names, or null.</summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellation">Cancellation.</param>
    public async Task<Principal?> ResolveAsync(HttpContext context, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_certificates.Settings.Enabled
            || Authentication.CrossOrigin(context.Request)
            || string.Equals(context.Request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase)
            || Jsonp.Asked(context.Request)
            || SessionScopes.IsNativeAdministration(context.Request.Path.Value))
        {
            return null;
        }

        (X509Certificate2? certificate, _) = await _certificates.PresentedAsync(context).ConfigureAwait(false);

        if (certificate is null)
        {
            return null;
        }

        string key = $"{context.Request.Host.Host.ToLowerInvariant()}|{certificate.Thumbprint}";
        DateTimeOffset now = _time.GetUtcNow();

        if (_answers.TryGetValue(key, out (Principal? Principal, DateTimeOffset Until) held) && held.Until > now)
        {
            return held.Principal;
        }

        Principal? principal = await LookUpAsync(certificate, cancellation).ConfigureAwait(false);

        if (_answers.Count > 10_000)
        {
            _answers.Clear();
        }

        _answers[key] = (principal, now + Held);
        return principal;
    }

    private async Task<Principal?> LookUpAsync(X509Certificate2 certificate, CancellationToken cancellation)
    {
        IdentityProvider? provider = (await _providers.ListAsync(cancellation).ConfigureAwait(false))
            .FirstOrDefault(p => p.Settings is { Enabled: true, Kind: "pki" });

        if (provider is null || _certificates.Refusal(certificate) is not null
            || ClientCertificates.Field(certificate, provider.Settings.UsernameClaim) is not { } value)
        {
            return null;
        }

        Principal? principal = await OidcEndpoints.FindOrMakeAsync(
            _providers, _log, provider, value.ToLowerInvariant(), value, ClientCertificates.Field(certificate, "cn"), cancellation,
            ClientCertificates.AccountName(value)).ConfigureAwait(false);

        return principal is { IsDisabled: false } ? principal : null;
    }
}
