using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Oidc;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens.Saml2;

namespace Graticula.Host.Saml;

/// <summary>
/// Signing in through a SAML 2.0 identity provider — ADR-090: a request sent by HTTP redirect, a response posted back,
/// its signature checked by an established library, and what that library leaves unchecked checked here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a sign-in started here is finished here</b> (owner decision, 2026-09-24). The request's id rides in a cookie
/// sealed with the server's key, and a response is accepted only when it answers that id — in the response and in the
/// assertion's bearer confirmation, whose recipient must also be this server's address. A response a provider sends
/// unasked answers nothing, and is refused.
/// </para>
/// <para>
/// <b>The cookie is <c>SameSite=None</c></b>, unlike every other cookie here, because a SAML response arrives as a
/// cross-site POST from the provider's page and a <c>Lax</c> cookie is not sent on one. It carries nothing but the
/// sealed request; forging a sign-in with it would need a response signed by the provider for that request.
/// </para>
/// <para>
/// <b>An assertion is used once, on any node</b>: its id is recorded in the platform store until it would have
/// expired, because the library's own replay check lives in one process.
/// </para>
/// <para>
/// <b>What ends the sign-in is <see cref="OidcEndpoints.FinishAsync"/></b>, the door an OpenID Connect sign-in leaves
/// by: the same account rules, the same group mapping, the same session.
/// </para>
/// </remarks>
internal static class SamlEndpoints
{
    private const string StateCookie = "gis-saml";
    private const string CookiePath = "/rest/auth/saml";
    private const string AcsPath = "/rest/auth/saml/acs";
    private const string PostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";
    private const int LargestResponse = 1024 * 1024;
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The attributes a name to show is looked for in when the operator named none.</summary>
    private static readonly string[] DisplayAttributes =
    [
        "http://schemas.microsoft.com/identity/claims/displayname",
        "displayName",
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
        "cn",
    ];

    /// <summary>Maps the sign-in routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/rest/auth/saml/{id:guid}/start", StartAsync);
        app.MapPost(AcsPath, AssertionConsumerAsync).DisableAntiforgery();
        app.MapGet("/rest/auth/saml/{id:guid}/metadata", MetadataAsync);
    }

    /// <summary>Where a provider posts its response, for the operator to register with it.</summary>
    /// <param name="context">A request to this server.</param>
    /// <returns>The absolute address.</returns>
    public static string AcsUrl(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{AcsPath}";

    /// <summary>The entity id this server gives itself at a provider unless the operator chose another.</summary>
    /// <param name="context">A request to this server.</param>
    /// <returns>The entity id.</returns>
    public static string DefaultEntityId(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{CookiePath}";

    /// <summary>What an operator gives the provider about this server: its entity id and where responses go.</summary>
    private static async Task MetadataAsync(HttpContext context, Guid id, IIdentityProviderStore store, CancellationToken cancellation)
    {
        if (await store.FindAsync(id, cancellation).ConfigureAwait(false) is not { Settings.Kind: "saml" } provider)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string metadata =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<md:EntityDescriptor xmlns:md=\"urn:oasis:names:tc:SAML:2.0:metadata\" "
            + $"entityID=\"{SecurityElement.Escape(provider.Settings.ClientId)}\">"
            + "<md:SPSSODescriptor AuthnRequestsSigned=\"false\" WantAssertionsSigned=\"true\" "
            + "protocolSupportEnumeration=\"urn:oasis:names:tc:SAML:2.0:protocol\">"
            + $"<md:AssertionConsumerService Binding=\"{PostBinding}\" Location=\"{SecurityElement.Escape(AcsUrl(context))}\" "
            + "index=\"0\" isDefault=\"true\"/>"
            + "</md:SPSSODescriptor></md:EntityDescriptor>";

        context.Response.Headers.ContentDisposition = "inline; filename=\"graticula-saml-metadata.xml\"";
        await Results.Content(metadata, "application/samlmetadata+xml; charset=utf-8").ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task StartAsync(
        HttpContext context,
        Guid id,
        IIdentityProviderStore store,
        SamlMetadata metadata,
        SecretProtector protector,
        ILoggerFactory logs,
        CancellationToken cancellation)
    {
        if (await store.FindAsync(id, cancellation).ConfigureAwait(false) is not { Settings: { Enabled: true, Kind: "saml" } } provider)
        {
            await OidcEndpoints.PageAsync(context, 404, "No such sign-in", "This server offers no sign-in by that name.").ConfigureAwait(false);
            return;
        }

        SamlMetadata.Described idp;

        try
        {
            idp = await metadata.CurrentAsync(provider, cancellation).ConfigureAwait(false);
        }
        catch (SamlException e)
        {
            Log.OidcStartFailed(logs.CreateLogger("Graticula.Saml"), provider.Settings.Name, e.Message);
            await OidcEndpoints.PageAsync(context, 502, $"{provider.Settings.Name} cannot be used",
                $"{e.Message} Ask an administrator, or sign in with an account on this server.").ConfigureAwait(false);
            return;
        }

        string relay = OidcEndpoints.Random(32);
        Saml2AuthnRequest request = new(Configuration(provider, idp))
        {
            AssertionConsumerServiceUrl = new Uri(AcsUrl(context)),
            ProtocolBinding = new Uri(PostBinding),
        };

        Saml2RedirectBinding binding = new() { RelayState = relay };
        binding.Bind(request);

        StartedSignIn started = new(
            provider.Id, relay, request.IdAsString,
            AuthEndpoints.Safe(context.Request.Query["return"].ToString() is { Length: > 0 } r ? r : "/server/"),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            OAuthEndpoints.Carried(context, protector));

        context.Response.Cookies.Append(
            StateCookie,
            WebEncoders.Base64UrlEncode(protector.Protect(JsonSerializer.Serialize(started))),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Path = CookiePath,
                MaxAge = StateLifetime,
            });

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect(binding.RedirectLocation.OriginalString);
    }

    private static async Task AssertionConsumerAsync(
        HttpContext context,
        IIdentityProviderStore store,
        SamlMetadata metadata,
        SecretProtector protector,
        LoginService login,
        ILoggerFactory logs,
        CancellationToken cancellation)
    {
        context.Response.Headers.CacheControl = "no-store";
        ILogger log = logs.CreateLogger("Graticula.Saml");

        StartedSignIn? started = ReadState(context, protector);
        context.Response.Cookies.Delete(StateCookie, new CookieOptions
        {
            Path = CookiePath, Secure = true, HttpOnly = true, SameSite = SameSiteMode.None,
        });

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;
        string encoded = form["SAMLResponse"].ToString();

        if (started is null
            || !string.Equals(started.Relay, form["RelayState"].ToString(), StringComparison.Ordinal)
            || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - started.At > StateLifetime.TotalSeconds)
        {
            // Both a sign-in that took too long and one the provider sent unasked end here: neither answers a request
            // this browser made in the last ten minutes.
            await OidcEndpoints.PageAsync(context, 400, "This sign-in was not started here",
                "This server finishes only a sign-in started from its own sign-in page in the last ten minutes. "
                + "Start it again from there.").ConfigureAwait(false);
            return;
        }

        if (await store.FindAsync(started.Provider, cancellation).ConfigureAwait(false) is not { Settings: { Enabled: true, Kind: "saml" } } provider)
        {
            await OidcEndpoints.PageAsync(context, 404, "No such sign-in", "This server no longer offers that sign-in.").ConfigureAwait(false);
            return;
        }

        string name = provider.Settings.Name;
        string? refusal = null;
        ClaimsIdentity? claims = null;
        Saml2Assertion? assertion = null;
        string? declined = null;

        try
        {
            SamlMetadata.Described idp = await metadata.CurrentAsync(provider, cancellation).ConfigureAwait(false);
            string xml = encoded.Length is 0 or > LargestResponse ? string.Empty : Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

            if (xml.Length == 0 || xml.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
            {
                refusal = "The response is not a SAML response.";
            }
            else
            {
                Saml2PostBinding binding = new();
                Saml2AuthnResponse response = new(Configuration(provider, idp));
                ITfoxtec.Identity.Saml2.Http.HttpRequest request = new()
                {
                    Method = "POST",
                    Form = new() { ["SAMLResponse"] = encoded, ["RelayState"] = started.Relay },
                };

                binding.ReadSamlResponse(request, response);

                if (response.Status != Saml2StatusCodes.Success)
                {
                    declined = string.IsNullOrWhiteSpace(response.StatusMessage) ? response.Status.ToString() : response.StatusMessage;
                }
                else
                {
                    binding.Unbind(request, response);
                    assertion = response.Saml2SecurityToken?.Assertion;
                    claims = response.ClaimsIdentity;
                    refusal = Unanswered(response.InResponseToAsString, assertion, started.RequestId, AcsUrl(context));
                }
            }
        }
        catch (SamlException e)
        {
            refusal = e.Message;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Whatever the library or the XML reader threw, the response is not one this server can trust; its
            // message is for the log, where an operator can read it, and not for the page.
            refusal = $"{e.GetType().Name}: {e.Message}";
        }

        if (declined is not null)
        {
            await OidcEndpoints.PageAsync(context, 401, $"{name} did not sign you in", $"It said: {declined}", started.Return)
                .ConfigureAwait(false);
            return;
        }

        if (refusal is null && assertion is not null
            && !await store.ConsumeAssertionAsync(provider.Id, assertion.Id.Value, Until(assertion), cancellation).ConfigureAwait(false))
        {
            refusal = $"Assertion {assertion.Id.Value} was used before.";
        }

        if (refusal is not null || claims is null || assertion?.Subject?.NameId?.Value is not { Length: > 0 } subject)
        {
            Log.SamlRefused(log, name, refusal ?? "It named nobody.");
            await OidcEndpoints.PageAsync(context, 401, $"Signing in with {name} did not complete",
                $"This server could not accept what {name} sent back. The reason is in the server's log.", started.Return)
                .ConfigureAwait(false);
            return;
        }

        string attribute = provider.Settings.UsernameClaim;
        string username = attribute.Length == 0 || string.Equals(attribute, "NameID", StringComparison.OrdinalIgnoreCase)
            ? subject
            : claims.FindFirst(attribute)?.Value ?? subject;

        string? display = (provider.Settings.Saml?.DisplayAttribute is { Length: > 0 } chosen ? [chosen] : DisplayAttributes)
            .Select(a => claims.FindFirst(a)?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        await OidcEndpoints.FinishAsync(
            context, store, login, log, provider, subject, username, display,
            [.. claims.FindAll(provider.Settings.GroupsClaim).Select(c => c.Value)], started.Return, cancellation,
            started.OAuth)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// What the library leaves unchecked, checked: the response answers the request this browser made, and the
    /// assertion's bearer confirmation names that request and this server's address. Null when it does.
    /// </summary>
    internal static string? Unanswered(string? inResponseTo, Saml2Assertion? assertion, string requestId, string acs)
    {
        if (!string.Equals(inResponseTo, requestId, StringComparison.Ordinal))
        {
            return $"The response answers {inResponseTo ?? "no request"}, and this browser asked {requestId}.";
        }

        if (assertion?.Subject is null)
        {
            return "The response carries no assertion about anybody.";
        }

        bool confirmed = assertion.Subject.SubjectConfirmations.Any(c =>
            c.Method?.OriginalString == "urn:oasis:names:tc:SAML:2.0:cm:bearer"
            && c.SubjectConfirmationData is { } data
            && string.Equals(data.InResponseTo?.Value, requestId, StringComparison.Ordinal)
            && data.Recipient is not null
            && string.Equals(data.Recipient.OriginalString, acs, StringComparison.Ordinal)
            && (data.NotOnOrAfter is not { } until || until.ToUniversalTime() > DateTime.UtcNow));

        return confirmed
            ? null
            : $"The assertion is not confirmed for request {requestId} at {acs}: its bearer confirmation names another "
              + "request or another address, or has expired.";
    }

    /// <summary>When an assertion stops being usable, which is how long its id is kept.</summary>
    private static DateTimeOffset Until(Saml2Assertion assertion)
    {
        DateTime? until = assertion.Conditions?.NotOnOrAfter
            ?? assertion.Subject?.SubjectConfirmations.Select(c => c.SubjectConfirmationData?.NotOnOrAfter).Max();

        return until is { } at ? new DateTimeOffset(at.ToUniversalTime()).AddMinutes(5) : DateTimeOffset.UtcNow.AddHours(1);
    }

    /// <summary>The library's configuration for one provider: whose signatures, which issuer, which audience.</summary>
    private static Saml2Configuration Configuration(IdentityProvider provider, SamlMetadata.Described idp)
    {
        Saml2Configuration configuration = new()
        {
            Issuer = provider.Settings.ClientId,
            AllowedIssuer = idp.EntityId,
            SingleSignOnDestination = idp.SignOn,

            // The metadata names the certificates, and that is the trust (see SamlMetadata); a chain is not asked for.
            CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
            AudienceRestricted = true,

            // Replays are refused by the platform store, across nodes; the library's check is one process's memory.
            DetectReplayedTokens = false,
        };

        configuration.AllowedAudienceUris.Add(provider.Settings.ClientId);
        configuration.SignatureValidationCertificates.AddRange(idp.Certificates);
        return configuration;
    }

    /// <summary>What a sign-in carries from its start to the response, sealed in <see cref="StateCookie"/>.</summary>
    private sealed record StartedSignIn(Guid Provider, string Relay, string RequestId, string Return, long At, string? OAuth = null);

    private static StartedSignIn? ReadState(HttpContext context, SecretProtector protector)
    {
        if (!context.Request.Cookies.TryGetValue(StateCookie, out string? cookie) || string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StartedSignIn>(
                protector.Unprotect(WebEncoders.Base64UrlDecode(cookie), protector.KeyVersion));
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
