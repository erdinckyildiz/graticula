using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Graticula.Host.Oidc;

/// <summary>
/// Signing in through an OpenID Connect provider — ADR-088: authorization code with PKCE, a nonce, and a state
/// bound to the browser that started it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the sign-in ends in is this server's own session</b>, the one a password sign-in makes (ADR-015 §3): an
/// opaque token, revocable on the next request. The provider's ID token is validated once and discarded; it is
/// never a credential here.
/// </para>
/// <para>
/// <b>The state rides in a cookie sealed with the server's key</b>, so nothing is stored for a sign-in somebody
/// abandons, any node can finish a sign-in another started, and a callback carrying a state this browser did not
/// start is refused. The cookie is <c>SameSite=Lax</c> — the one cookie here that must be — because the callback
/// arrives as a top-level navigation from the provider's site, which a <c>Strict</c> cookie is never sent on.
/// </para>
/// </remarks>
internal static class OidcEndpoints
{
    private const string StateCookie = "gis-oidc";
    private const string CallbackPath = "/rest/auth/oidc/callback";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Maps the sign-in routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/rest/auth/providers", ProvidersAsync);
        app.MapGet("/rest/auth/oidc/{id:guid}/start", StartAsync);
        app.MapGet(CallbackPath, CallbackAsync);
    }

    /// <summary>Where a sign-in through a provider with a button starts — ADR-088, ADR-090.</summary>
    internal static string StartPath(IdentityProvider provider) =>
        provider.Settings.Kind == "saml" ? $"/rest/auth/saml/{provider.Id}/start" : $"/rest/auth/oidc/{provider.Id}/start";

    /// <summary>Where a provider sends a person back to, for the operator to register with it.</summary>
    /// <param name="context">A request to this server.</param>
    /// <returns>The absolute redirect URI.</returns>
    public static string RedirectUri(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{CallbackPath}";

    /// <summary>The providers a person may sign in through: public, because a sign-in page must list them.</summary>
    private static async Task ProvidersAsync(HttpContext context, IIdentityProviderStore store, CancellationToken cancellation)
    {
        IReadOnlyList<IdentityProvider> all = await store.ListAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            // ADR-089: a directory is signed in to with the password form, not a button.
            // ADR-090: a SAML provider is a button as an OpenID Connect one is, and starts at its own route.
            providers = all.Where(p => p.Settings is { Enabled: true, Kind: "oidc" or "saml" })
                .Select(p => new { id = p.Id, name = p.Settings.Name, start = StartPath(p) })
                .ToArray(),

            // ADR-089: a directory's people use the password form, and the form has to say that it is theirs.
            directories = all.Where(p => p.Settings is { Enabled: true, Kind: "ldap" }).Select(p => p.Settings.Name).ToArray(),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task StartAsync(
        HttpContext context,
        Guid id,
        IIdentityProviderStore store,
        OidcClient client,
        SecretProtector protector,
        ILoggerFactory logs,
        CancellationToken cancellation)
    {
        if (await store.FindAsync(id, cancellation).ConfigureAwait(false) is not { Settings: { Enabled: true, Kind: "oidc" } } provider)
        {
            await PageAsync(context, 404, "No such sign-in", "This server offers no sign-in by that name.").ConfigureAwait(false);
            return;
        }

        OpenIdConnectConfiguration configuration;

        try
        {
            configuration = await client.DiscoverAsync(provider.Settings.Issuer, cancellation).ConfigureAwait(false);
        }
        catch (OidcException e)
        {
            Log.OidcStartFailed(logs.CreateLogger("Graticula.Oidc"), provider.Settings.Name, e.Message);
            await PageAsync(context, 502, $"{provider.Settings.Name} cannot be reached",
                $"{e.Message} Try again later, or sign in with an account on this server.").ConfigureAwait(false);
            return;
        }

        string state = Random(32);
        string nonce = Random(32);
        string verifier = Random(48);
        string challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        StartedSignIn started = new(
            provider.Id, state, nonce, verifier,
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
                SameSite = SameSiteMode.Lax,
                Path = "/rest/auth/oidc",
                MaxAge = StateLifetime,
            });

        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = provider.Settings.ClientId,
            ["redirect_uri"] = RedirectUri(context),
            ["scope"] = Scopes(provider.Settings.Scopes),
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect(QueryHelpers.AddQueryString(configuration.AuthorizationEndpoint, query));
    }

    private static async Task CallbackAsync(
        HttpContext context,
        IIdentityProviderStore store,
        OidcClient client,
        SecretProtector protector,
        LoginService login,
        ILoggerFactory logs,
        CancellationToken cancellation)
    {
        context.Response.Headers.CacheControl = "no-store";
        ILogger log = logs.CreateLogger("Graticula.Oidc");

        StartedSignIn? started = ReadState(context, protector);
        context.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/rest/auth/oidc", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax });

        if (started is null
            || !string.Equals(started.State, context.Request.Query["state"].ToString(), StringComparison.Ordinal)
            || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - started.At > StateLifetime.TotalSeconds)
        {
            await PageAsync(context, 400, "This sign-in has expired",
                "It was not started from this browser in the last ten minutes. Start it again.").ConfigureAwait(false);
            return;
        }

        if (await store.FindAsync(started.Provider, cancellation).ConfigureAwait(false) is not { Settings.Enabled: true } provider)
        {
            await PageAsync(context, 404, "No such sign-in", "This server no longer offers that sign-in.").ConfigureAwait(false);
            return;
        }

        string name = provider.Settings.Name;

        if (context.Request.Query["error"].ToString() is { Length: > 0 } refused)
        {
            string said = context.Request.Query["error_description"].ToString();
            await PageAsync(context, 401, $"{name} did not sign you in",
                string.IsNullOrWhiteSpace(said) ? $"It said: {refused}." : $"It said: {said}", started.Return).ConfigureAwait(false);
            return;
        }

        ClaimsIdentity claims;

        try
        {
            OpenIdConnectConfiguration configuration =
                await client.DiscoverAsync(provider.Settings.Issuer, cancellation).ConfigureAwait(false);

            string? secret = await store.SecretOfAsync(provider.Id, cancellation).ConfigureAwait(false) is { } sealedSecret
                ? protector.Unprotect(sealedSecret.Secret, sealedSecret.KeyVersion)
                : null;

            string idToken = await OidcClient.RedeemAsync(
                configuration, provider.Settings.ClientId, secret, context.Request.Query["code"].ToString(),
                started.Verifier, RedirectUri(context), cancellation).ConfigureAwait(false);

            claims = await client.ValidateAsync(
                provider.Settings.Issuer, configuration, idToken, provider.Settings.ClientId, started.Nonce, cancellation)
                .ConfigureAwait(false);
        }
        catch (OidcException e)
        {
            Log.OidcSignInFailed(log, name, e.Message);
            await PageAsync(context, 502, $"Signing in with {name} did not complete", e.Message, started.Return).ConfigureAwait(false);
            return;
        }

        string? subject = claims.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(subject))
        {
            await PageAsync(context, 502, $"{name} did not say who you are", "Its ID token carries no subject.").ConfigureAwait(false);
            return;
        }

        string username = claims.FindFirst(provider.Settings.UsernameClaim)?.Value
            ?? claims.FindFirst("email")?.Value
            ?? subject;

        await FinishAsync(
            context, store, login, log, provider, subject, username, claims.FindFirst("name")?.Value,
            [.. claims.FindAll(provider.Settings.GroupsClaim).Select(c => c.Value)], started.Return, cancellation,
            started.OAuth)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ends a sign-in a provider vouched for — ADR-088, and ADR-090's SAML through the same door: the account the
    /// provider's subject signs in to, made at a first sign-in when the provider allows it; the provider's groups
    /// applied (ADR-089); and this server's own session.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The providers.</param>
    /// <param name="login">What issues a session.</param>
    /// <param name="log">Where an account made is said.</param>
    /// <param name="provider">The provider that vouched.</param>
    /// <param name="subject">What it never reuses for another person.</param>
    /// <param name="username">The name it gives them.</param>
    /// <param name="displayName">A name to show, or null.</param>
    /// <param name="groups">Their groups there.</param>
    /// <param name="returnTo">Where the sign-in was started from.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <param name="oauth">An app's sealed request, when the sign-in was started from the OAuth sign-in page: then
    /// the app gets a code instead of the browser getting a session — ADR-088 condition 2.</param>
    internal static async Task FinishAsync(
        HttpContext context,
        IIdentityProviderStore store,
        LoginService login,
        ILogger log,
        IdentityProvider provider,
        string subject,
        string username,
        string? displayName,
        IReadOnlyCollection<string> groups,
        string returnTo,
        CancellationToken cancellation,
        string? oauth = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(provider);

        string name = provider.Settings.Name;
        Principal? principal = await store.FindPrincipalAsync(provider.Id, subject, username, cancellation).ConfigureAwait(false);

        if (principal is null && provider.Settings.AutoCreate)
        {
            principal = await store.CreateMemberAsync(
                provider.Id, subject, username, AccountName(username), displayName,
                provider.Settings.DefaultRole, provider.Settings.DefaultUserType, cancellation).ConfigureAwait(false)
                ?? await store.FindPrincipalAsync(provider.Id, subject, username, cancellation).ConfigureAwait(false);

            if (principal is not null)
            {
                Log.OidcAccountMade(log, principal.Name, username, name);
            }
        }

        if (principal is null)
        {
            await PageAsync(context, 403, "There is no account for you here",
                $"{name} signed you in as {username}, and this server has no account for that name. "
                + "Ask an administrator to add one.", returnTo).ConfigureAwait(false);
            return;
        }

        // <b>ADR-089: the provider's groups, through the same mapping a directory's go through</b>, at every sign-in.
        if (!principal.IsDisabled)
        {
            await store.ApplyMappingsAsync(
                principal.Id,
                provider.Id,
                groups,
                Graticula.Host.Ldap.LdapDirectory.RoleRank,
                provider.Settings.DefaultRole,
                cancellation).ConfigureAwait(false);
        }

        if (principal.IsDisabled)
        {
            await PageAsync(context, 403, "This account cannot sign in",
                "It has been disabled on this server. Ask an administrator.", returnTo).ConfigureAwait(false);
            return;
        }

        if (oauth is not null)
        {
            await OAuthEndpoints.CompleteExternalAsync(context, oauth, principal, cancellation).ConfigureAwait(false);
            return;
        }

        (string token, AuthenticatedSession session) = await login
            .IssueAsync(principal, context.Connection.RemoteIpAddress, cancellation).ConfigureAwait(false);

        AuthEndpoints.SetSessionCookie(context, token, session.ExpiresAt);
        context.Response.Redirect(returnTo);
    }

    /// <summary>What a sign-in carries from its start to its callback, sealed in <see cref="StateCookie"/>.</summary>
    /// <param name="Provider">The provider.</param>
    /// <param name="State">The state sent, which the callback must bring back.</param>
    /// <param name="Nonce">The nonce the ID token must carry.</param>
    /// <param name="Verifier">The PKCE verifier.</param>
    /// <param name="Return">Where the sign-in was started from.</param>
    /// <param name="At">When, in Unix seconds.</param>
    /// <param name="OAuth">An app's request, when the sign-in was started from the OAuth sign-in page — ADR-088
    /// condition 2 — sealed.</param>
    private sealed record StartedSignIn(Guid Provider, string State, string Nonce, string Verifier, string Return, long At, string? OAuth = null);

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

    /// <summary>The scopes asked for, <c>openid</c> always among them.</summary>
    private static string Scopes(string configured)
    {
        List<string> scopes = [.. configured.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        if (!scopes.Contains("openid", StringComparer.Ordinal))
        {
            scopes.Insert(0, "openid");
        }

        return string.Join(' ', scopes);
    }

    /// <summary>An account name made from the name a provider gives: its part before an @, if it has one.</summary>
    internal static string AccountName(string username)
    {
        string name = username.Contains('@', StringComparison.Ordinal) ? username[..username.IndexOf('@', StringComparison.Ordinal)] : username;
        return name.Trim().Length == 0 ? username.Trim() : name.Trim();
    }

    internal static string Random(int bytes) => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    internal static Task PageAsync(HttpContext context, int status, string heading, string sentence, string? back = null) =>
        Results.Content(
                RestDirectory.SignInMessage(heading, sentence, back ?? "/rest/login"), "text/html; charset=utf-8", statusCode: status)
            .ExecuteAsync(context);
}
