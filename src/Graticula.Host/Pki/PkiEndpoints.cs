using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Oidc;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Graticula.Host.Pki;

/// <summary>
/// Signing in with a client certificate — ADR-315 §5.5: a route that reads the certificate the request carries, checks
/// it, maps it to a member by the provider's field, and ends in ADR-088's door.
/// </summary>
internal static class PkiEndpoints
{
    /// <summary>Maps the sign-in route.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/rest/auth/pki/{id:guid}/start", StartAsync);
    }

    /// <summary>Who a certificate signs in as, or why it does not — what the sign-in and <c>generateToken</c> share.</summary>
    /// <param name="Subject">The value the account is found by, without case.</param>
    /// <param name="Username">The value as the certificate writes it.</param>
    /// <param name="DisplayName">The certificate's common name, to show.</param>
    /// <param name="Status">The status to refuse with, when <paramref name="Refusal"/> is set.</param>
    /// <param name="Refusal">Why nobody is signed in, or null.</param>
    internal sealed record Reading(string Subject, string Username, string? DisplayName, int Status, string? Refusal);

    /// <summary>
    /// Reads the certificate a request carries for a PKI provider — ADR-315 §5.3–§5.4.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="provider">The PKI provider.</param>
    /// <returns>Who it names, or why nobody.</returns>
    internal static async Task<Reading> ReadAsync(HttpContext context, IdentityProvider provider)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(provider);

        ClientCertificates certificates = context.RequestServices.GetRequiredService<ClientCertificates>();

        if (!certificates.Settings.Enabled)
        {
            return new Reading("", "", null, 503,
                "This server is not configured to receive client certificates. An administrator sets "
                + "Graticula:ClientCertificates (ADR-315).");
        }

        (X509Certificate2? certificate, string? ignored) = await certificates.PresentedAsync(context).ConfigureAwait(false);

        if (certificate is null)
        {
            return new Reading("", "", null, 401, ignored
                ?? "Your browser sent no certificate. Insert your smart card, or install your certificate, and choose it when "
                   + "the browser asks; if it did not ask, close the browser and open this page again.");
        }

        if (certificates.Refusal(certificate) is { } refusal)
        {
            return new Reading("", "", null, 401, refusal);
        }

        string field = provider.Settings.UsernameClaim;

        if (ClientCertificates.Field(certificate, field) is not { } value)
        {
            return new Reading("", "", null, 401, field switch
            {
                "email" => "Your certificate carries no e-mail address, and this server names accounts by it.",
                "upn" => "Your certificate carries no user principal name, and this server names accounts by it.",
                _ => "Your certificate's subject has no common name (CN), and this server names accounts by it.",
            });
        }

        return new Reading(value.ToLowerInvariant(), value, ClientCertificates.Field(certificate, "cn"), 200, null);
    }

    private static async Task StartAsync(
        HttpContext context,
        Guid id,
        IIdentityProviderStore store,
        LoginService login,
        Graticula.Platform.Secrets.SecretProtector protector,
        ILoggerFactory logs,
        CancellationToken cancellation)
    {
        context.Response.Headers.CacheControl = "no-store";
        string returnTo = AuthEndpoints.Safe(context.Request.Query["return"].ToString() is { Length: > 0 } r ? r : "/server/");

        if (await store.FindAsync(id, cancellation).ConfigureAwait(false) is not { Settings: { Enabled: true, Kind: "pki" } } provider)
        {
            await OidcEndpoints.PageAsync(context, 404, "No such sign-in", "This server offers no sign-in by that name.").ConfigureAwait(false);
            return;
        }

        Reading reading = await ReadAsync(context, provider).ConfigureAwait(false);
        ILogger log = logs.CreateLogger("Graticula.Pki");

        if (reading.Refusal is { } refusal)
        {
            Log.OidcSignInFailed(log, provider.Settings.Name, refusal);
            await OidcEndpoints.PageAsync(context, reading.Status, "Your certificate did not sign you in", refusal, returnTo)
                .ConfigureAwait(false);
            return;
        }

        await OidcEndpoints.FinishAsync(
            context, store, login, log, provider, reading.Subject, reading.Username, reading.DisplayName, [], returnTo,
            cancellation, OAuthEndpoints.Carried(context, protector), ClientCertificates.AccountName(reading.Username))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The member a <c>generateToken</c> request's certificate signs in — ADR-315 §5.7 — or null with the refusal, when
    /// the request carries no name and no password. Null with a null refusal when this server takes no certificates or
    /// the organisation has no PKI provider, so the caller says what it said before.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellation">Cancellation.</param>
    internal static async Task<(Principal? Principal, int Status, string? Refusal)> TokenPrincipalAsync(
        HttpContext context, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.RequestServices.GetService<ClientCertificates>() is not { Settings.Enabled: true })
        {
            return (null, 0, null);
        }

        IIdentityProviderStore store = context.RequestServices.GetRequiredService<IIdentityProviderStore>();
        IdentityProvider? provider = (await store.ListAsync(cancellation).ConfigureAwait(false))
            .FirstOrDefault(p => p.Settings is { Enabled: true, Kind: "pki" });

        if (provider is null)
        {
            return (null, 0, null);
        }

        Reading reading = await ReadAsync(context, provider).ConfigureAwait(false);

        if (reading.Refusal is not null)
        {
            // No certificate at all is the password form's old answer; a certificate refused says why.
            return reading.Status == 401 && reading.Refusal.StartsWith("Your browser sent no certificate", StringComparison.Ordinal)
                ? (null, 0, null)
                : (null, reading.Status, reading.Refusal);
        }

        ILogger log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Graticula.Pki");
        Principal? principal = await OidcEndpoints.FindOrMakeAsync(
            store, log, provider, reading.Subject, reading.Username, reading.DisplayName, cancellation,
            ClientCertificates.AccountName(reading.Username)).ConfigureAwait(false);

        return principal switch
        {
            null => (null, 403, $"Your certificate names {reading.Username}, and this server has no account for that name."),
            { IsDisabled: true } => (null, 403, "This account has been disabled on this server."),
            _ => (principal, 200, null),
        };
    }

    /// <summary>What the console says about this server's certificate transport — ADR-315 §5.2.</summary>
    /// <param name="settings">The settings.</param>
    internal static object Describe(ClientCertificateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new
        {
            enabled = settings.Enabled,
            mode = settings.Mode.ToString(),
            header = settings.Header,
            revocation = settings.Revocation.ToString(),
            authorities = settings.Authorities.Cast<X509Certificate2>()
                .Select(a => new { subject = a.Subject, notAfter = a.NotAfter.ToUniversalTime() })
                .ToArray(),
        };
    }
}
