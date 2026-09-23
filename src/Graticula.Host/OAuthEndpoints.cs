using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// OAuth 2.0 authorization code with PKCE, for registered apps — ADR-076.
/// </summary>
/// <remarks>
/// <para>
/// <b>The protocol is Esri's published REST reference</b> — <c>/sharing/rest/oauth2/authorize</c> and
/// <c>/token</c> — so an app written against an Enterprise portal needs nothing changed to sign in here.
/// </para>
/// <para>
/// <b>Nothing new decides who a person is.</b> The password goes through
/// <see cref="LoginService.VerifyAsync"/>, with its throttle and its timing equalisation; an access token
/// is a session scoped to the ArcGIS surfaces (Q-154), checked by the same middleware as every other
/// token and refused by <c>/admin</c>. What is new is the registered app, the one-use code and the
/// refresh token.
/// </para>
/// </remarks>
internal static class OAuthEndpoints
{
    private const string Root = "/sharing/rest/oauth2";

    private const string LegacyRoot = "/sharing/oauth2";

    private const string FormCookie = "graticula_oauth_form";

    /// <summary>Maps the protocol and the administration of registered apps.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // <b>Under both prefixes, because the Maps SDK asks the one the reference does not name.</b>
        // Esri's REST reference documents `/sharing/rest/oauth2/…`; the ArcGIS Maps SDK for
        // JavaScript 4.30, given a portal URL, sends its user to `/sharing/oauth2/authorize` —
        // measured 2026-09-16 driving the SDK against a fixture, where the first attempt ended in a
        // 404. An Enterprise portal answers both, so this does too.
        foreach (string root in (string[])[Root, LegacyRoot])
        {
            app.MapGet($"{root}/authorize", AuthorizeAsync).Governed(SharingGovernedExtensions.Public);
            app.MapPost($"{root}/token", TokenAsync).Governed(SharingGovernedExtensions.Public).DisableAntiforgery();
            app.MapPost($"{root}/revokeToken", RevokeAsync).Governed(SharingGovernedExtensions.Public).DisableAntiforgery();
        }

        app.MapPost($"{Root}/signin", SignInAsync).Governed(SharingGovernedExtensions.Public).DisableAntiforgery();
        app.MapGet($"{Root}/approval", Approval).Governed(SharingGovernedExtensions.Public);

        app.MapGet("/admin/oauth/apps", ListAppsAsync);
        app.MapPost("/admin/oauth/apps", CreateAppAsync);
        app.MapDelete("/admin/oauth/apps/{clientId}", DeleteAppAsync);
    }

    // ---------------------------------------------------------------- authorize and sign in

    /// <summary>The parameters an authorize request carries, and the sign-in form carries back.</summary>
    private sealed record Request(
        string ClientId,
        string ResponseType,
        string RedirectUri,
        string? State,
        string? Challenge,
        string? ChallengeMethod);

    private static Request Read(Func<string, string?> value) =>
        new(
            value("client_id") ?? string.Empty,
            value("response_type") ?? string.Empty,
            value("redirect_uri") ?? string.Empty,
            value("state"),
            NullIfEmpty(value("code_challenge")),
            NullIfEmpty(value("code_challenge_method")));

    private static async Task AuthorizeAsync(HttpContext context, IOAuthStore store, CancellationToken cancellation)
    {
        Request request = Read(name => context.Request.Query[name].FirstOrDefault());

        if (await VerifiedAppAsync(context, store, request, cancellation).ConfigureAwait(false) is not { } app)
        {
            return;
        }

        // <b>From here the redirect is known to be the app's, so a mistake is sent back to it</b> in
        // OAuth's words rather than shown as a page the app never sees.
        if (!string.Equals(request.ResponseType, "code", StringComparison.Ordinal))
        {
            Redirect(context, request, ("error", "unsupported_response_type"),
                ("error_description", "This server issues authorization codes only; response_type=token is not offered (ADR-076)."));
            return;
        }

        if (!OAuthRules.IsKnownMethod(request.ChallengeMethod) || (request.ChallengeMethod is not null && request.Challenge is null))
        {
            Redirect(context, request, ("error", "invalid_request"),
                ("error_description", "code_challenge_method must be S256 or plain, and needs a code_challenge."));
            return;
        }

        await SignInPageAsync(context, app, request, message: null, status: 200).ConfigureAwait(false);
    }

    private static async Task SignInAsync(
        HttpContext context, IOAuthStore store, LoginService login, IAuditLog audit, CancellationToken cancellation)
    {
        if (!context.Request.HasFormContentType)
        {
            await ErrorPageAsync(context, 400, "This address takes the sign-in form this server shows, and nothing else.")
                .ConfigureAwait(false);
            return;
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        Request request = Read(name => form[name].FirstOrDefault());

        // <b>The form token against its cookie, before anything else is believed.</b> A page elsewhere
        // can post this form but cannot read or send the cookie, so it cannot sign a visitor in as
        // somebody of its choosing (ADR-076 §4).
        string? posted = form["form_token"].FirstOrDefault();
        string? kept = context.Request.Cookies[FormCookie];

        if (string.IsNullOrEmpty(posted) || string.IsNullOrEmpty(kept)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(posted), Encoding.ASCII.GetBytes(kept)))
        {
            await ErrorPageAsync(context, 400,
                "This sign-in form has expired, or did not come from this server. Go back to the app and start signing in again.")
                .ConfigureAwait(false);
            return;
        }

        // The client and redirect are checked again: the form's fields are the browser's to change.
        if (await VerifiedAppAsync(context, store, request, cancellation).ConfigureAwait(false) is not { } app)
        {
            return;
        }

        string name = form["username"].FirstOrDefault() ?? string.Empty;
        string password = form["password"].FirstOrDefault() ?? string.Empty;

        (LoginFailure failure, Principal? principal) = string.IsNullOrWhiteSpace(name)
            ? (LoginFailure.InvalidCredentials, null)
            : await login.VerifyAsync(name, password, CallerAddress.Of(context), cancellation).ConfigureAwait(false);

        if (failure != LoginFailure.None)
        {
            await RecordAsync(context, audit, Guid.Empty, name, "oauth.signin", app.ClientId, succeeded: false, cancellation)
                .ConfigureAwait(false);

            // The same two sentences the generateToken doors give, so this door tells nobody more.
            await SignInPageAsync(
                    context, app, request,
                    failure is LoginFailure.AddressThrottled or LoginFailure.AccountThrottled
                        ? "Too many failed sign-in attempts. Wait and try again."
                        : "The name or password is incorrect.",
                    failure is LoginFailure.AddressThrottled or LoginFailure.AccountThrottled ? 429 : 401)
                .ConfigureAwait(false);
            return;
        }

        string code = SessionToken.Generate();
        byte[] hash = SessionToken.HashOf(code);

        await store.IssueCodeAsync(
                hash, app.ClientId, principal!.Id, request.RedirectUri, request.Challenge, request.ChallengeMethod,
                DateTimeOffset.UtcNow + OAuthRules.CodeLifetime, cancellation)
            .ConfigureAwait(false);

        await RecordAsync(context, audit, principal.Id, principal.Name, "oauth.signin", app.ClientId, succeeded: true, cancellation)
            .ConfigureAwait(false);

        context.Response.Cookies.Delete(FormCookie, new CookieOptions { Path = Root });

        if (string.Equals(request.RedirectUri, OAuthRules.OutOfBand, StringComparison.Ordinal))
        {
            context.Response.Redirect($"{Root}/approval?code={Uri.EscapeDataString(code)}");
            return;
        }

        Redirect(context, request, ("code", code));
    }

    /// <summary>
    /// The out-of-band page: the code in the title and in the text, for an app that reads either.
    /// </summary>
    /// <remarks>
    /// <b>What a native SDK reads from it is not verified here</b> — ADR-076 condition 4 is a device.
    /// The code is in both places so a person can also copy it by hand.
    /// </remarks>
    private static async Task Approval(HttpContext context)
    {
        string code = context.Request.Query["code"].FirstOrDefault() ?? string.Empty;
        string safe = HtmlEncoder.Default.Encode(code);

        context.Response.Headers.CacheControl = "no-store";

        await HtmlAsync(context, 200, $"SUCCESS code={safe}",
            $"<h1>Signed in</h1><p>Return to the app. If it asks for a code, it is:</p><p class=\"code\">{safe}</p>",
            formTarget: null).ConfigureAwait(false);
    }

    /// <summary>
    /// The registered app, when the client id is registered and the redirect is one of its own; otherwise
    /// an error page, and never a redirect.
    /// </summary>
    /// <remarks>
    /// <b>Shown rather than redirected</b>, because sending a browser to an address the app has not
    /// registered is the open redirect OAuth 2.0 §4.1.2.1 forbids — and it is how a code would be
    /// delivered to somebody who merely typed a URL.
    /// </remarks>
    private static async Task<OAuthApp?> VerifiedAppAsync(
        HttpContext context, IOAuthStore store, Request request, CancellationToken cancellation)
    {
        OAuthApp? app = request.ClientId.Length == 0
            ? null
            : await store.FindAppAsync(request.ClientId, cancellation).ConfigureAwait(false);

        if (app is null)
        {
            await ErrorPageAsync(context, 400,
                $"No app is registered here as '{request.ClientId}'. An administrator of this server registers the apps that may sign people in.")
                .ConfigureAwait(false);
            return null;
        }

        if (!app.RedirectUris.Any(registered => SameRedirect(registered, request.RedirectUri)))
        {
            await ErrorPageAsync(context, 400,
                $"'{app.Title}' is not registered to receive sign-ins at '{request.RedirectUri}', so this server will not send one there.")
                .ConfigureAwait(false);
            return null;
        }

        return app;
    }

    /// <summary>
    /// A registered redirect and a requested one are the same address: allowing one trailing slash, and
    /// the requested one's own query when the registered one has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The query, because the Maps SDK sends the page it is on.</b> Measured 2026-09-16: an app at
    /// <c>http://localhost:8767/app.html?portal=…</c> sent that whole URL as <c>redirect_uri</c>, and an
    /// app cannot be expected to register every query string its page is ever opened with. The query is
    /// the app's own business at an address the app registered; the scheme, host, port and path still
    /// have to match exactly.
    /// </para>
    /// <para>
    /// <b>The slash, and nothing looser.</b> Esri's field apps are registered as
    /// <c>arcgis-fieldmaps://auth/</c> and a client building the URL itself may drop the slash; a path
    /// prefix match would let <c>https://app.example/cb/../elsewhere</c> through.
    /// </para>
    /// </remarks>
    private static bool SameRedirect(string registered, string requested)
    {
        if (requested.Length == 0)
        {
            return false;
        }

        if (string.Equals(registered, requested, StringComparison.Ordinal)
            || string.Equals(registered.TrimEnd('/'), requested.TrimEnd('/'), StringComparison.Ordinal))
        {
            return true;
        }

        int query = requested.IndexOf('?', StringComparison.Ordinal);

        return query > 0
            && !registered.Contains('?', StringComparison.Ordinal)
            && !requested.Contains('#', StringComparison.Ordinal)
            && string.Equals(registered.TrimEnd('/'), requested[..query].TrimEnd('/'), StringComparison.Ordinal);
    }

    private static void Redirect(HttpContext context, Request request, params (string Key, string Value)[] values)
    {
        StringBuilder target = new(request.RedirectUri);
        char joiner = request.RedirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        foreach ((string key, string value) in values.Append(("state", request.State ?? string.Empty)))
        {
            if (key == "state" && request.State is null)
            {
                continue;
            }

            target.Append(joiner).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            joiner = '&';
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect(target.ToString());
    }

    // ---------------------------------------------------------------- token and revoke

    private static async Task TokenAsync(
        HttpContext context, IOAuthStore store, LoginService login, HostSettings settings, CancellationToken cancellation)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (!context.Request.HasFormContentType)
        {
            await TokenErrorAsync(context, "invalid_request", "Send the parameters as a form, in the body of a POST.").ConfigureAwait(false);
            return;
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        string clientId = form["client_id"].FirstOrDefault() ?? string.Empty;
        string grant = form["grant_type"].FirstOrDefault() ?? string.Empty;

        if (clientId.Length == 0 || await store.FindAppAsync(clientId, cancellation).ConfigureAwait(false) is not { } app)
        {
            await TokenErrorAsync(context, "invalid_client", "client_id is not an app registered here.").ConfigureAwait(false);
            return;
        }

        TimeSpan? lifetime = int.TryParse(form["expiration"].FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            && minutes > 0
                ? TimeSpan.FromMinutes(minutes)
                : OAuthRules.DefaultAccessLifetime;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        switch (grant)
        {
            case "authorization_code":
            {
                string code = form["code"].FirstOrDefault() ?? string.Empty;
                byte[] codeHash = SessionToken.HashOf(code);

                (CodeRedemption outcome, OAuthCode? redeemed) = code.Length == 0
                    ? (CodeRedemption.Unknown, null)
                    : await store.RedeemCodeAsync(codeHash, now, cancellation).ConfigureAwait(false);

                if (outcome == CodeRedemption.Replayed)
                {
                    await TokenErrorAsync(context, "invalid_grant",
                        "This code was already exchanged. A code is used once; the refresh token its first use issued has been revoked, because a code held twice has leaked.")
                        .ConfigureAwait(false);
                    return;
                }

                if (outcome != CodeRedemption.Redeemed
                    || redeemed!.Principal is not { } principal
                    || !string.Equals(redeemed.ClientId, app.ClientId, StringComparison.Ordinal)
                    || !string.Equals(redeemed.RedirectUri, form["redirect_uri"].FirstOrDefault(), StringComparison.Ordinal))
                {
                    await TokenErrorAsync(context, "invalid_grant",
                        "The code is unknown, expired, issued to another app or another redirect_uri, or its account is disabled.")
                        .ConfigureAwait(false);
                    return;
                }

                if (!OAuthRules.Verifies(redeemed.Challenge, redeemed.ChallengeMethod, form["code_verifier"].FirstOrDefault()))
                {
                    await TokenErrorAsync(context, "invalid_grant",
                        "code_verifier does not answer the code_challenge this code was issued with.")
                        .ConfigureAwait(false);
                    return;
                }

                await IssueAsync(context, store, login, settings, app, principal, lifetime, now, withRefresh: true, codeHash, cancellation)
                    .ConfigureAwait(false);
                return;
            }

            case "refresh_token":
            case "exchange_refresh_token":
            {
                string refresh = form["refresh_token"].FirstOrDefault() ?? string.Empty;
                byte[] refreshHash = SessionToken.HashOf(refresh);

                bool exchange = grant == "exchange_refresh_token";

                if (refresh.Length == 0
                    || await store.FindRefreshAsync(refreshHash, now, cancellation).ConfigureAwait(false) is not { } found
                    || !string.Equals(found.ClientId, app.ClientId, StringComparison.Ordinal))
                {
                    await TokenErrorAsync(context, "invalid_grant",
                        "The refresh token is unknown, expired, revoked, issued to another app, or its account is disabled.")
                        .ConfigureAwait(false);
                    return;
                }

                if (exchange && !app.RedirectUris.Any(r => SameRedirect(r, form["redirect_uri"].FirstOrDefault() ?? string.Empty)))
                {
                    await TokenErrorAsync(context, "invalid_grant", "redirect_uri is not one registered for this app.").ConfigureAwait(false);
                    return;
                }

                if (exchange)
                {
                    await store.RevokeRefreshAsync(refreshHash, cancellation).ConfigureAwait(false);
                }

                await IssueAsync(context, store, login, settings, app, found.Principal, lifetime, now, withRefresh: exchange, fromCode: null, cancellation)
                    .ConfigureAwait(false);
                return;
            }

            case "client_credentials":
                await TokenErrorAsync(context, "unsupported_grant_type",
                    "client_credentials needs a client secret, and this server issues none (ADR-076). Sign a person in with authorization_code.")
                    .ConfigureAwait(false);
                return;

            default:
                await TokenErrorAsync(context, "unsupported_grant_type",
                    "grant_type must be authorization_code, refresh_token or exchange_refresh_token.")
                    .ConfigureAwait(false);
                return;
        }
    }

    private static async Task IssueAsync(
        HttpContext context,
        IOAuthStore store,
        LoginService login,
        HostSettings settings,
        OAuthApp app,
        Principal principal,
        TimeSpan? lifetime,
        DateTimeOffset now,
        bool withRefresh,
        byte[]? fromCode,
        CancellationToken cancellation)
    {
        (string token, AuthenticatedSession session) = await login
            .IssueAsync(principal, CallerAddress.Of(context), cancellation, lifetime, boundTo: null, scope: SessionScopes.ArcGis)
            .ConfigureAwait(false);

        Dictionary<string, object> answer = new()
        {
            ["access_token"] = token,
            ["expires_in"] = (long)Math.Max(0, (session.ExpiresAt - now).TotalSeconds),
            ["username"] = principal.Name,
            ["ssl"] = settings.RequireHttps,
        };

        if (withRefresh)
        {
            string refresh = SessionToken.Generate();
            DateTimeOffset refreshExpires = now + OAuthRules.DefaultRefreshLifetime;

            await store.IssueRefreshAsync(SessionToken.HashOf(refresh), app.ClientId, principal.Id, refreshExpires, fromCode, cancellation)
                .ConfigureAwait(false);

            answer["refresh_token"] = refresh;
            answer["refresh_token_expires_in"] = (long)OAuthRules.DefaultRefreshLifetime.TotalSeconds;
        }

        await Results.Json(answer).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RevokeAsync(HttpContext context, IOAuthStore store, IIdentityStore identities, CancellationToken cancellation)
    {
        // <b>The same answer whatever was sent</b>, so this endpoint is not a way to learn whether a
        // token exists.
        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
            string token = form["auth_token"].FirstOrDefault() ?? form["token"].FirstOrDefault() ?? string.Empty;

            if (token.Length > 0)
            {
                byte[] hash = SessionToken.HashOf(token);

                await store.RevokeRefreshAsync(hash, cancellation).ConfigureAwait(false);

                if (await identities.FindSessionAsync(hash, DateTimeOffset.UtcNow, cancellation).ConfigureAwait(false) is { } session
                    && string.Equals(session.Scope, SessionScopes.ArcGis, StringComparison.Ordinal))
                {
                    await identities.RevokeSessionAsync(session.SessionId, cancellation).ConfigureAwait(false);
                }
            }
        }

        await Results.Json(new { success = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static Task TokenErrorAsync(HttpContext context, string error, string description) =>
        Results.Json(
            new
            {
                error = new
                {
                    code = 400,
                    error,
                    error_description = description,
                    message = description,
                    details = Array.Empty<string>(),
                },
            },
            statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);

    // ---------------------------------------------------------------- registered apps

    /// <summary>A registration, as the admin API takes it.</summary>
    /// <param name="ClientId">The client id, or null to have one made.</param>
    /// <param name="Title">What the sign-in page shows.</param>
    /// <param name="RedirectUris">Where codes may be sent.</param>
    internal sealed record AppRequest(string? ClientId, string? Title, string[]? RedirectUris);

    private static async Task ListAppsAsync(HttpContext context, IOAuthStore store, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<OAuthApp> apps = await store.ListAppsAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new { apps = apps.Select(Wire) }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task CreateAppAsync(
        HttpContext context, AppRequest request, IOAuthStore store, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        string title = request.Title?.Trim() ?? string.Empty;

        if (title.Length is 0 or > 200)
        {
            await AdminErrorAsync(context, 400, "'title' is required, and is what a person sees on the sign-in page — at most 200 characters.").ConfigureAwait(false);
            return;
        }

        string[] redirects = request.RedirectUris ?? [];

        if (redirects.Length is 0 or > 20)
        {
            await AdminErrorAsync(context, 400, "'redirectUris' needs between one and twenty addresses: a code is only ever sent to one of them.").ConfigureAwait(false);
            return;
        }

        foreach (string redirect in redirects)
        {
            if (!OAuthRules.IsAcceptableRedirect(redirect, out string? why))
            {
                await AdminErrorAsync(context, 400, why!).ConfigureAwait(false);
                return;
            }
        }

        string clientId = string.IsNullOrWhiteSpace(request.ClientId)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()
            : request.ClientId.Trim();

        if (!System.Text.RegularExpressions.Regex.IsMatch(clientId, "^[A-Za-z0-9._-]{1,128}$"))
        {
            await AdminErrorAsync(context, 400, "'clientId' may use letters, digits, '.', '_' and '-', up to 128 characters.").ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!await store.CreateAppAsync(clientId, title, redirects, current.Principal.Id, cancellation).ConfigureAwait(false))
        {
            await AdminErrorAsync(context, 409, $"An app is already registered as '{clientId}'.").ConfigureAwait(false);
            return;
        }

        await RecordAsync(context, audit, current.Principal.Id, current.Principal.Name, "oauth.app.create", clientId, succeeded: true, cancellation)
            .ConfigureAwait(false);

        OAuthApp created = (await store.FindAppAsync(clientId, cancellation).ConfigureAwait(false))!;

        await Results.Json(Wire(created), statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteAppAsync(
        HttpContext context, string clientId, IOAuthStore store, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (!await store.DeleteAppAsync(clientId, cancellation).ConfigureAwait(false))
        {
            await AdminErrorAsync(context, 404, $"No app is registered as '{clientId}'.").ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        await RecordAsync(context, audit, current.Principal.Id, current.Principal.Name, "oauth.app.delete", clientId, succeeded: true, cancellation)
            .ConfigureAwait(false);

        await Results.Json(new
        {
            clientId,
            removed = true,
            note = "Its codes and refresh tokens are gone. Access tokens it already issued last out their own lifetime — thirty minutes unless the app asked for less.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static object Wire(OAuthApp app) => new
    {
        clientId = app.ClientId,
        title = app.Title,
        redirectUris = app.RedirectUris,
        builtin = app.Builtin,
        createdAt = app.CreatedAt,
    };

    private static Task AdminErrorAsync(HttpContext context, int status, string message) =>
        Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status).ExecuteAsync(context);

    private static Task RecordAsync(
        HttpContext context, IAuditLog audit, Guid principal, string name, string action, string resource, bool succeeded, CancellationToken cancellation) =>
        audit.RecordAsync(
            new AuditEvent(principal, name, CallerAddress.Of(context)?.ToString(), action, resource, "{}", succeeded),
            cancellation);

    // ---------------------------------------------------------------- pages

    private static async Task SignInPageAsync(HttpContext context, OAuthApp app, Request request, string? message, int status)
    {
        string formToken = SessionToken.Generate();

        context.Response.Cookies.Append(FormCookie, formToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = Root,
            MaxAge = TimeSpan.FromMinutes(15),
        });

        HtmlEncoder e = HtmlEncoder.Default;

        string Hidden(string name, string? value) =>
            value is null ? string.Empty : $"<input type=\"hidden\" name=\"{name}\" value=\"{e.Encode(value)}\">";

        string where = string.Equals(request.RedirectUri, OAuthRules.OutOfBand, StringComparison.Ordinal)
            ? "the app on this device"
            : request.RedirectUri;

        string body =
            $"<h1>Sign in</h1>"
            + $"<p class=\"who\"><b>{e.Encode(app.Title)}</b> is asking to use this server as you.</p>"
            + $"<p class=\"where\">You will be returned to <code>{e.Encode(where)}</code>.</p>"
            + (message is null ? string.Empty : $"<p class=\"bad\" role=\"alert\">{e.Encode(message)}</p>")
            + $"<form method=\"post\" action=\"{Root}/signin\">"
            + Hidden("form_token", formToken)
            + Hidden("client_id", request.ClientId)
            + Hidden("response_type", request.ResponseType)
            + Hidden("redirect_uri", request.RedirectUri)
            + Hidden("state", request.State)
            + Hidden("code_challenge", request.Challenge)
            + Hidden("code_challenge_method", request.ChallengeMethod)
            + "<label for=\"username\">Name</label><input id=\"username\" name=\"username\" autocomplete=\"username\" required autofocus>"
            + "<label for=\"password\">Password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>"
            + "<button type=\"submit\">Sign in</button></form>";

        await HtmlAsync(context, status, $"Sign in to {app.Title}", body, FormTarget(request.RedirectUri)).ConfigureAwait(false);
    }

    /// <summary>
    /// The CSP source the sign-in form's redirect will reach, because browsers apply <c>form-action</c> to
    /// the redirect that follows a form post (ADR-076 §4).
    /// </summary>
    private static string? FormTarget(string redirectUri)
    {
        if (string.Equals(redirectUri, OAuthRules.OutOfBand, StringComparison.Ordinal)
            || !Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https"
            ? $"{uri.Scheme}://{uri.Authority}"
            : $"{uri.Scheme}:";
    }

    private static Task ErrorPageAsync(HttpContext context, int status, string message) =>
        HtmlAsync(context, status, "Sign-in could not start", $"<h1>Sign-in could not start</h1><p>{HtmlEncoder.Default.Encode(message)}</p>", formTarget: null);

    private static async Task HtmlAsync(HttpContext context, int status, string title, string body, string? formTarget)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; img-src 'self' data:; "
            + $"form-action 'self'{(formTarget is null ? string.Empty : " " + formTarget)}; "
            + "frame-ancestors 'none'; base-uri 'none'";

        string page =
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + $"<title>{HtmlEncoder.Default.Encode(title).Replace("SUCCESS code=", "SUCCESS code=", StringComparison.Ordinal)}</title>"
            + "<style>"
            + ":root{--ground:#f4f6f5;--card:#fff;--ink:#17262b;--muted:#5a6b70;--line:#d3dcdb;--accent:#1d6a86;--bad:#a8321f}"
            + "@media (prefers-color-scheme:dark){:root{--ground:#0f171a;--card:#162226;--ink:#e1eaea;--muted:#93a5a9;--line:#2a3a3f;--accent:#72b8d0;--bad:#f08c78}}"
            + "*{box-sizing:border-box}body{margin:0;background:var(--ground);color:var(--ink);font:16px/1.5 system-ui,-apple-system,'Segoe UI',sans-serif;padding:32px 16px}"
            + "main{max-width:380px;margin:0 auto;background:var(--card);border:1px solid var(--line);border-radius:8px;padding:24px}"
            + "h1{font-size:1.4rem;margin:0 0 12px}p{margin:0 0 12px}.where,.code{color:var(--muted);overflow-wrap:anywhere}"
            + "code{font-size:.9em}.bad{color:var(--bad);font-weight:600}"
            + "label{display:block;font-weight:600;margin:14px 0 4px}input{width:100%;font:inherit;padding:8px 10px;border:1px solid var(--line);border-radius:6px;background:var(--ground);color:var(--ink)}"
            + "input:focus-visible,button:focus-visible{outline:2px solid var(--accent);outline-offset:2px}"
            + "button{margin-top:20px;width:100%;font:inherit;font-weight:600;padding:10px;border:0;border-radius:6px;background:var(--accent);color:var(--card);cursor:pointer}"
            + "</style></head><body><main>"
            + body
            + "</main></body></html>";

        await context.Response.WriteAsync(page).ConfigureAwait(false);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
