using System;
using System.Text.Json;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>What a client sends to log in.</summary>
/// <param name="Name">The principal name.</param>
/// <param name="Password">The password.</param>
internal sealed record LoginRequest(string? Name, string? Password);

/// <summary>What a member sends to change their own password.</summary>
/// <param name="CurrentPassword">The one they have now.</param>
/// <param name="NewPassword">The one they want.</param>
internal sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);

/// <summary>What a client sends to complete first-start setup.</summary>
/// <param name="Token">The setup token from the server log.</param>
/// <param name="Name">The administrator's principal name.</param>
/// <param name="DisplayName">A human label, or null.</param>
/// <param name="Password">The administrator's password.</param>
internal sealed record SetupRequest(
    string? Token, string? Name, string? DisplayName, string? Password);

/// <summary>The authentication endpoints.</summary>
internal static class AuthEndpoints
{
    /// <summary>
    /// Shortest password we accept.
    /// </summary>
    /// <remarks>
    /// <b>Length only, and no composition rules.</b> Requiring an uppercase, a
    /// digit and a symbol measurably pushes people toward <c>Password1!</c>,
    /// which is in every wordlist. NIST SP 800-63B dropped composition rules for
    /// exactly this reason. Length is the property that actually helps.
    /// <b>What is missing:</b> a check against known-breached passwords, which
    /// is worth more than either and needs a corpus we do not ship.
    /// <para>
    /// <b>Lowered from 12 to 8 on 2026-08-14.</b> 8 is the floor NIST SP 800-63B
    /// sets for a user-chosen secret; 12 was our own invention with no reasoning
    /// recorded behind it, and the first real password anybody tried to set was
    /// refused by it. A rule nobody can state a reason for, that refuses the
    /// server's own root account, is a rule people route around — and the route
    /// around this one was going to be a direct write to the store, which would
    /// have left the policy in place and untrue.
    /// </para>
    /// <para>
    /// <b>The honest cost.</b> 8 characters is weak against an offline attack on
    /// a stolen hash. What carries that weight here is Argon2id at 19 MiB per
    /// guess (ADR-015) and the rate limit in
    /// <see cref="Platform.Identity.LoginService"/> — not the length rule, which
    /// was never doing that job at 12 either.
    /// </para>
    /// </remarks>
    public const int MinimumPasswordLength = 8;

    /// <summary>Maps login and logout.</summary>
    public static void MapAuth(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/rest/auth/login", LoginAsync);
        app.MapPost("/rest/auth/logout", LogoutAsync);
        app.MapPost("/rest/auth/session", ExchangeAsync);
        app.MapPost("/rest/auth/password", ChangePasswordAsync);

        // <b>The token endpoint every Esri client looks for, and it was advertised
        // before it existed.</b> `/rest/info` has told clients since ADR-015 §4
        // that this server uses token security, and pointed them at
        // `/rest/auth/login` — which speaks JSON and wants a field called `name`,
        // where an ArcGIS client posts a form with `username`. So ArcGIS Pro could
        // read public services and could not sign in, and the failure arrived as a
        // credential rejection rather than as a missing feature. Found by pointing
        // Pro at the server: *user pass kabul etmedi*.
        //
        // Both methods, because clients differ and the specification does not say.
        app.MapPost("/rest/generateToken", GenerateTokenAsync);
        app.MapGet("/rest/generateToken", GenerateTokenAsync);
    }

    /// <summary>
    /// Issues a token in the shape an ArcGIS client expects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second door onto one lock.</b> It calls the same
    /// <see cref="LoginService"/> as <c>/rest/auth/login</c>, so the throttle, the
    /// audit record and the *one message for every failure* rule are shared rather
    /// than reimplemented. What differs is only the vocabulary: <c>username</c>
    /// instead of <c>name</c>, minutes instead of a fixed lifetime, and an error
    /// shape an Esri client renders instead of ignoring.
    /// </para>
    /// <para>
    /// <b>The expiry the caller asks for is a ceiling request, not a grant.</b>
    /// The session's own lifetime is what the store issued; this reports that and
    /// does not extend it because a client asked nicely.
    /// </para>
    /// </remarks>
    private static async Task GenerateTokenAsync(
        HttpContext context,
        LoginService login,
        CancellationToken cancellation)
    {
        // A portal token exchanged for this server's — owningSystemUrl, 2026-09-15.
        if (await TryExchangeAsync(context, cancellation).ConfigureAwait(false) is { Asked: true } exchanged)
        {
            if (exchanged.Error is { } refusal)
            {
                await EsriTokenError(context, exchanged.Status, refusal).ConfigureAwait(false);
                return;
            }

            await Results.Json(new
            {
                token = exchanged.Token,
                expires = exchanged.Expires.ToUnixTimeMilliseconds(),
                ssl = context.RequestServices.GetRequiredService<HostSettings>().RequireHttps,
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        string? name = null;
        string? password = null;

        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation)
                .ConfigureAwait(false);

            name = form["username"].ToString();
            password = form["password"].ToString();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = context.Request.Query["username"].ToString();
        }

        if (string.IsNullOrEmpty(password))
        {
            password = context.Request.Query["password"].ToString();
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password))
        {
            await EsriTokenError(
                    context,
                    StatusCodes.Status400BadRequest,
                    "'username' and 'password' are required.")
                .ConfigureAwait(false);

            return;
        }

        (bool bindable, string? bound, string? unbindable) =
            await RequestedBindingAsync(context, cancellation).ConfigureAwait(false);

        if (!bindable)
        {
            await EsriTokenError(context, StatusCodes.Status400BadRequest, unbindable!).ConfigureAwait(false);
            return;
        }

        LoginResult result = await login
            .AuthenticateAsync(
                name, password, RemoteAddress(context), cancellation,
                await RequestedLifetimeAsync(context, cancellation).ConfigureAwait(false),
                bound, SessionScopes.ArcGis)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // The same single message as the JSON door, for the same reason:
            // distinguishing wrong-name from wrong-password is an account
            // enumeration oracle. A throttle is still told apart, because a
            // locked-out administrator cannot learn that any other way.
            (int status, string detail) = result.Failure switch
            {
                LoginFailure.AddressThrottled or LoginFailure.AccountThrottled => (
                    StatusCodes.Status429TooManyRequests,
                    "Too many failed sign-in attempts. Wait and try again."),
                _ => (
                    StatusCodes.Status401Unauthorized,
                    "The name or password is incorrect."),
            };

            await EsriTokenError(context, status, detail).ConfigureAwait(false);
            return;
        }

        AuthenticatedSession session = result.Session!.Value;

        await Results.Json(new
        {
            token = result.Token!,

            // Milliseconds since the epoch, which is what an Esri client reads.
            expires = session.ExpiresAt.ToUnixTimeMilliseconds(),

            // <b>Whether this server answers only over HTTPS, which is what an ArcGIS client
            // reads the field as.</b> This said `false` always while `/sharing/rest/generateToken`
            // said `true` always and `portals/self` said `allSSL: true` — three answers to one
            // question. It is now the one fact that decides it: `RequireHttps`.
            ssl = context.RequestServices.GetRequiredService<HostSettings>().RequireHttps,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The error shape an ArcGIS client renders.</summary>
    /// <remarks>
    /// <b>The HTTP status is still the truthful one.</b> Esri's own token service
    /// answers 200 with an error object inside, which makes a failure invisible to
    /// anything that reads status codes — a proxy, a log, a monitor. The body is
    /// theirs so the client shows a real message; the status is ours so everything
    /// else can still tell.
    /// </remarks>
    private static Task EsriTokenError(HttpContext context, int status, string detail) =>
        Results.Json(
            new
            {
                error = new
                {
                    code = status,
                    message = "Unable to generate token.",
                    details = new[] { detail },
                },
            },
            statusCode: status).ExecuteAsync(context);

    private static async Task LoginAsync(
        HttpContext context,
        LoginService login,
        CancellationToken cancellation)
    {
        // <b>JSON for a client, a form for a browser.</b> The endpoint took a
        // JSON body only, so the sign-in page's form got 415 Unsupported Media
        // Type — a browser cannot set a JSON content type on a form post. Read
        // by hand rather than by two routes, because two routes is two places
        // for the throttle and the audit record to diverge.
        LoginRequest request;

        // Where a browser wants to be afterwards. Captured here rather than read
        // back off the request twice, because the two failure paths below both
        // need it and a re-read is a second thing to get wrong.
        string? returnTo = null;
        bool fromForm = context.Request.HasFormContentType;

        if (fromForm)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation)
                .ConfigureAwait(false);

            request = new LoginRequest(form["name"].ToString(), form["password"].ToString());
            returnTo = form["return"].ToString();
        }
        else
        {
            try
            {
                request = await context.Request
                    .ReadFromJsonAsync<LoginRequest>(cancellation)
                    .ConfigureAwait(false)
                    ?? new LoginRequest(null, null);
            }
            catch (JsonException)
            {
                await Refuse(context, 400, "The request body is not valid JSON.")
                    .ConfigureAwait(false);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrEmpty(request.Password))
        {
            await Refuse(context, 400, "name and password are required.").ConfigureAwait(false);
            return;
        }

        LoginResult result = await login
            .AuthenticateAsync(request.Name, request.Password, RemoteAddress(context), cancellation)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // 401 for a bad credential, 429 for a throttle. The distinction is
            // safe to make: it tells an attacker their attempts are being
            // counted, which is information they can get by counting their own
            // attempts, and it tells a locked-out administrator why — which they
            // cannot get any other way.
            (int status, string message) = result.Failure switch
            {
                LoginFailure.AddressThrottled => (
                    StatusCodes.Status429TooManyRequests,
                    "Too many failed sign-in attempts from this address. Wait and try again."),
                LoginFailure.AccountThrottled => (
                    StatusCodes.Status429TooManyRequests,
                    "Too many failed sign-in attempts for this account. Wait and try again. The "
                    + "account is not locked: the correct password still works."),
                _ => (
                    StatusCodes.Status401Unauthorized,

                    // One message for wrong-name, wrong-password and disabled.
                    // Distinguishing them is an account-enumeration oracle, which
                    // is the step before every credential-stuffing run.
                    "The name or password is incorrect."),
            };

            if (fromForm)
            {
                // Back to the form with a message, not a JSON error a browser
                // renders as a wall of text. The reason is deliberately the same
                // for every failure — see the message above.
                context.Response.Redirect(
                    "/rest/login?failed=1&return="
                    + Uri.EscapeDataString(Safe(returnTo)));
                return;
            }

            await Refuse(context, status, message).ConfigureAwait(false);
            return;
        }

        AuthenticatedSession session = result.Session!.Value;

        // <b>The same token, also as a cookie, so a browser can browse.</b> The
        // directory could never show anything but public content before this:
        // the only credential channel was the Authorization header, which a
        // browser following a link cannot send. The cookie authenticates GET and
        // HEAD only (Authentication.CookieToken), so it cannot be used to change
        // anything even if another origin manages to send it.
        SetSessionCookie(context, result.Token!, session.ExpiresAt);

        // A browser that posted the sign-in form wants the directory back, not
        // a JSON document it has no way to read.
        if (fromForm)
        {
            context.Response.Redirect(Safe(returnTo));
            return;
        }

        await Results.Json(new
        {
            token = result.Token,
            expiresAt = session.ExpiresAt,
            principal = new { name = session.Principal.Name, kind = session.Principal.Kind.ToString() },

            // Said in the response as well as the documentation, because this is
            // the one moment a client author is definitely reading. ADR-015 §4's
            // second mitigation is that the header form is preferred and
            // advertised, and advertising it only in a manual is not advertising.
            usage = "Send this as 'Authorization: Bearer <token>'. Do not put it in a URL.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the browsing cookie into a bearer token of its own, for a page on this server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner decision, 2026-09-13</b> — ADR-023 §4c amended. Signed in through the services
    /// directory, the owner pressed <em>Server</em> and was asked to sign in again: the cookie
    /// authenticates reading only, and the console writes, so it needed a token and had no way to
    /// get one but the password. §4c had refused both obvious accommodations — a <c>GET</c> that
    /// hands out credentials, and a cookie that authenticates a <c>POST</c> behind an antiforgery
    /// token — and neither is what this is.
    /// </para>
    /// <para>
    /// <b>The control is a header no page can write.</b> <c>Sec-Fetch-Site</c> is set by the
    /// browser itself, is a forbidden header name to script, and says <c>same-origin</c> only when
    /// the request came from a page on this origin. A page elsewhere that makes the browser post here
    /// gets <c>cross-site</c> — and the cookie is <c>SameSite=Strict</c>, so it would not have been
    /// sent anyway — and even a request that got through would return a token that page cannot read.
    /// A client that is not a browser can set the header, and needs the cookie's value to use it, at
    /// which point it already holds the credential.
    /// </para>
    /// <para>
    /// <b>What same-origin still admits is script on this origin</b>, and the answer is where script
    /// can run: every directory page is served with <c>default-src 'none'</c>, so the pages that
    /// render catalogue text run none at all, and the pages that do run script — the console, the
    /// map and the viewer — load it only from this server and hold a bearer token already.
    /// </para>
    /// <para>
    /// <b>A new session, not the cookie's own token.</b> The cookie stays <c>HttpOnly</c> in the
    /// sense that matters: its value never reaches script. The new session ends when the cookie's
    /// does — it is not a way to extend one — and signing out of the console revokes it and clears
    /// the cookie.
    /// </para>
    /// </remarks>
    private static async Task ExchangeAsync(
        HttpContext context,
        IIdentityStore store,
        TimeProvider time,
        CancellationToken cancellation)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (!FromThisOrigin(context.Request))
        {
            await Refuse(
                context, 403,
                "A browsing session is exchanged for a token only by a page on this server, and this "
                + "request did not come from one. Sign in with a name and password instead.")
                .ConfigureAwait(false);

            return;
        }

        if (!context.Request.Cookies.TryGetValue(Authentication.SessionCookie, out string? cookie)
            || string.IsNullOrEmpty(cookie))
        {
            await Refuse(context, 401, "There is no browsing session to exchange. Sign in.")
                .ConfigureAwait(false);

            return;
        }

        DateTimeOffset now = time.GetUtcNow();

        if (await store.FindSessionAsync(SessionToken.HashOf(cookie), now, cancellation)
                .ConfigureAwait(false) is not { } session)
        {
            // Expired or revoked: the browser is holding a cookie for nothing, so it goes too.
            ClearSessionCookie(context);

            await Refuse(context, 401, "The browsing session has ended. Sign in.")
                .ConfigureAwait(false);

            return;
        }

        string token = SessionToken.Generate();

        await store
            .CreateSessionAsync(
                session.Principal.Id,
                SessionToken.HashOf(token),
                session.ExpiresAt,
                RemoteAddress(context),
                cancellation)
            .ConfigureAwait(false);

        await Results.Json(new
        {
            token,
            expiresAt = session.ExpiresAt,
            principal = new { name = session.Principal.Name, kind = session.Principal.Kind.ToString() },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Whether a request came from a page on this origin, as the browser says.</summary>
    /// <param name="request">The request.</param>
    /// <returns>True only for <c>Sec-Fetch-Site: same-origin</c>.</returns>
    /// <remarks>
    /// <b>Absent is refused, not assumed.</b> Every browser this console supports sends the header;
    /// one that does not is a client whose origin cannot be told, and the answer for that client is
    /// the password it would otherwise have typed.
    /// </remarks>
    internal static bool FromThisOrigin(HttpRequest request) =>
        string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "same-origin", StringComparison.OrdinalIgnoreCase);

    private static async Task LogoutAsync(
        HttpContext context, IIdentityStore store, CancellationToken cancellation)
    {
        RequestPrincipal? current = context.Features.Get<RequestPrincipal>();

        if (current?.SessionId is not { } sessionId)
        {
            // <b>The cookie is cleared on this path too, and it used to be the one
            // path that did not.</b> Arriving here means the session behind the
            // credential is already gone — expired, or revoked from elsewhere — and
            // the browser is still holding a cookie for it. Returning without
            // clearing left that stale cookie in place, so the caller most in need
            // of being signed out was the one left carrying a credential.
            ClearSessionCookie(context);

            // Not an error. Logging out when not logged in has already achieved
            // what the caller asked for, and a 400 here makes a client that
            // clears its own state first look broken.
            await Results.Json(new { revoked = false }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await store.RevokeSessionAsync(sessionId, cancellation).ConfigureAwait(false);

        ClearSessionCookie(context);

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            context.Response.Redirect("/rest/services");
            return;
        }

        await Results.Json(new { revoked = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the browsing cookie.
    /// </summary>
    /// <remarks>
    /// <b>Three flags, and each closes something.</b> <c>HttpOnly</c> keeps it
    /// away from script, so an XSS in the directory cannot read a session — and
    /// the directory renders user-supplied layer names, which is exactly where
    /// an XSS would come from. <c>Secure</c> keeps it off plaintext.
    /// <c>SameSite=Strict</c> stops another origin causing the browser to send
    /// it at all. The fourth control is not a flag: the cookie only
    /// authenticates GET and HEAD.
    /// </remarks>
    private static void SetSessionCookie(
        HttpContext context, string token, DateTimeOffset expires) =>
        context.Response.Cookies.Append(
            Authentication.SessionCookie,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = expires,
                Path = "/",
            });

    private static void ClearSessionCookie(HttpContext context) =>
        context.Response.Cookies.Delete(
            Authentication.SessionCookie,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
            });

    /// <summary>
    /// A return path that cannot leave this server.
    /// </summary>
    /// <remarks>
    /// <b>An open redirect is what this prevents.</b> A sign-in page that
    /// forwards to whatever <c>return</c> says is a phishing primitive: the link
    /// is genuinely ours, the credential prompt is genuinely ours, and the
    /// landing page is the attacker's. Only a path beginning with a single
    /// slash is honoured, and <c>//host</c> is rejected because a
    /// protocol-relative URL also begins with one.
    /// </remarks>
    private static string Safe(string? target) =>
        !string.IsNullOrEmpty(target)
        && target.StartsWith('/')
        && !target.StartsWith("//", StringComparison.Ordinal)
            ? target
            : "/rest/services";

    /// <summary>
    /// Changes the caller's own password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The current password is required, and a valid session is not enough.</b>
    /// A stolen session token would otherwise be sufficient to change the
    /// password and lock the real owner out — turning a temporary compromise
    /// into a permanent one. Knowing the current password is the thing an
    /// attacker with a token does not have.
    /// </para>
    /// <para>
    /// <b>Every other session is revoked.</b> If the password was changed
    /// because it leaked, leaving the attacker signed in makes the change
    /// theatre. ADR-015 §3 chose server-side sessions so that revocation takes
    /// effect on the next request, and this is the case that most needs it. The
    /// current session survives, so changing a password does not sign you out of
    /// the screen you changed it on.
    /// </para>
    /// <para>
    /// <b>Self-service only.</b> An administrator resetting somebody else's
    /// password is a different operation with a different risk — it needs
    /// <c>admin:manageMembers</c> and an audit trail that says who reset whose,
    /// and it does not exist yet.
    /// </para>
    /// </remarks>
    private static async Task ChangePasswordAsync(
        HttpContext context,
        PasswordChangeRequest request,
        IIdentityStore store,
        IPasswordHasher hasher,
        IAuditLog audit,
        HostSettings settings,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in before changing a password.").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
        {
            await Refuse(context, 400, "currentPassword and newPassword are required.")
                .ConfigureAwait(false);
            return;
        }

        if (request.NewPassword.Length < MinimumPasswordLength)
        {
            await Refuse(
                context,
                400,
                $"The new password is {request.NewPassword.Length} characters and the minimum is "
                + $"{MinimumPasswordLength}. Length is the only rule: composition requirements "
                + "push people toward predictable passwords, so this is the one that is enforced.")
                .ConfigureAwait(false);
            return;
        }

        if (await RefusedAsCommonAsync(context, request.NewPassword, settings)
            .ConfigureAwait(false))
        {
            return;
        }

        (Principal Principal, PasswordHash? Credential)? found = await store
            .FindForLoginAsync(current.Principal.Name, cancellation).ConfigureAwait(false);

        if (found is not { Credential: { } credential }
            || !hasher.Verify(request.CurrentPassword, credential))
        {
            await AuditAsync(context, audit, "principal.password", current.Principal.Name,
                "{\"outcome\":\"wrong-current-password\"}", succeeded: false, cancellation)
                .ConfigureAwait(false);

            await Refuse(context, 403, "The current password is incorrect.").ConfigureAwait(false);
            return;
        }

        await store.SetPasswordAsync(
            current.Principal.Id, hasher.Hash(request.NewPassword), cancellation).ConfigureAwait(false);

        int revoked = await store.RevokeOtherSessionsAsync(
            current.Principal.Id, current.SessionId, cancellation).ConfigureAwait(false);

        // The password itself never reaches the audit record, obviously — but
        // nor does its length, which would narrow a guess.
        await AuditAsync(context, audit, "principal.password", current.Principal.Name,
            $"{{\"revokedSessions\":{revoked}}}", succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            changed = true,
            revokedSessions = revoked,
            note = revoked > 0
                ? $"{revoked} other session(s) were signed out. This one is still valid."
                : "No other sessions were open. This one is still valid.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Records an administrative act.</summary>
    private static Task AuditAsync(
        HttpContext context,
        IAuditLog audit,
        string action,
        string resource,
        string detail,
        bool succeeded,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                CallerAddress.Of(context)?.ToString(),
                action,
                resource,
                detail,
                succeeded),
            cancellation);
    }

    /// <summary>Completes first-start setup. Reachable only while setup is pending.</summary>
    public static async Task SetupAsync(
        HttpContext context,
        SetupRequest request,
        ISetupStore setup,
        IPasswordHasher hasher,
        ServerState state,
        TimeProvider time,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(request.Token)
            || string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrEmpty(request.Password))
        {
            await Refuse(context, 400, "token, name and password are required.").ConfigureAwait(false);
            return;
        }

        if (request.Password.Length < MinimumPasswordLength)
        {
            await Refuse(
                context,
                400,
                $"The password must be at least {MinimumPasswordLength} characters. Length is the "
                + "only rule: composition requirements push people toward predictable passwords.")
                .ConfigureAwait(false);
            return;
        }

        if (await RefusedAsCommonAsync(context, request.Password, settings).ConfigureAwait(false))
        {
            return;
        }

        Principal? administrator = await setup.RedeemAsync(
            request.Token,
            request.Name,
            request.DisplayName,
            hasher.Hash(request.Password),

            // ADR-018 §4. The first account is a platform administrator or the
            // server has nobody who can grant anything to anybody.
            Roles.Administrator,
            time.GetUtcNow(),
            cancellation).ConfigureAwait(false);

        if (administrator is null)
        {
            await Refuse(
                context,
                403,
                // <b>It names what it knows, which is three possibilities rather than two —
                // [D-257](../../docs/architecture-debt.md).</b> This said the token had been
                // used or had expired, and *one of those has already happened to it*. Neither
                // is true of a token that was mistyped, and the sentence then sent the reader
                // to restart the server — which issues a fresh token and never tells them they
                // had a copy error. Measured on 2026-09-10 by walking the quickstart: a token
                // carrying the log's `server-1  |` prefix was refused with this sentence, and
                // the same token pasted cleanly a moment later worked.
                //
                // <b>The three cannot be told apart from here, and saying so is the repair.</b>
                // The register holds live tokens only, so an unknown one and a spent one are
                // both *not found*; keeping spent tokens would be state kept for a message.
                // What costs nothing is checking the token before restarting, and this is the
                // only place that can say so — this is the fourth command of the quickstart and
                // the first whose input is not literal.
                "That setup token is not one this server is holding. It was mistyped, or it "
                + "has already been used, or it has expired — this server cannot tell which. "
                + "Check the token first: it is the last line of the SETUP REQUIRED block and "
                + "nothing else on that line. Restarting issues a fresh one, and a server that "
                + "already has an administrator will not issue one at all.")
                .ConfigureAwait(false);
            return;
        }

        state.SetupCompleted();

        await Results.Json(new
        {
            principal = new { name = administrator.Name, role = Roles.Administrator },
            next = "Sign in at /rest/auth/login.",

            // ADR-018 §3 is a behaviour change from every previous build, and
            // this is the moment the person who will be surprised by it is
            // reading output.
            note =
                "Layers are private to their owner by default. To publish openly, set a layer's "
                + "sharing to 'public'; to expose it to signed-in members only, 'organization'.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a password that is already published, as far as this deployment can tell.
    /// </summary>
    /// <param name="context">The request, for the answer.</param>
    /// <param name="password">What was chosen.</param>
    /// <param name="settings">Where the list lives.</param>
    /// <returns>True when the request was refused and the caller should stop.</returns>
    /// <remarks>
    /// <para>
    /// <b>[D-23](../../docs/architecture-debt.md), repaid 2026-08-24.</b> Length was the only
    /// rule, so <c>Passw0rd!</c> and a random nine characters were treated alike. The realistic
    /// attack on a small deployment is credential stuffing, and Argon2id and the rate limit do
    /// nothing at all about a password that is already published.
    /// </para>
    /// <para>
    /// <b>The refusal says what to do, and does not say what was matched.</b> Naming the entry
    /// would teach an attacker the list; naming the shape of the fix is what the person choosing
    /// a password needs.
    /// </para>
    /// </remarks>
    private static async Task<bool> RefusedAsCommonAsync(
        HttpContext context, string password, HostSettings settings)
    {
        if (!CommonPasswords.Known(password, settings.CommonPasswords))
        {
            return false;
        }

        await Refuse(
            context,
            400,
            "That password is one of the ones attackers try first, or a decorated version of one "
            + "— capitals, a trailing year and letter-for-digit swaps are all in every list. "
            + "Length is the only other rule: composition requirements push people toward exactly "
            + "these, which is why they are not enforced. Three unrelated words are stronger than "
            + "anything a rule can ask for.")
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>The source address, or null when it cannot be determined.</summary>
    /// <remarks>
    /// <para>
    /// <b>The socket's address, unless a proxy this deployment named says otherwise</b> —
    /// [D-12](../../docs/architecture-debt.md), repaid 2026-08-24. It used to be the socket and
    /// nothing else, which behind a reverse proxy made the per-address limit one shared bucket:
    /// fifty failures anywhere disabled sign-in for everybody.
    /// </para>
    /// <para>
    /// <b>The reason it could not simply read the header</b> was that trusting
    /// <c>X-Forwarded-For</c> from anybody lets every caller choose their own rate-limit bucket,
    /// which makes the limit zero. Too coarse is recoverable; forgeable is not. So
    /// <see cref="CallerAddress"/> reads it only from a peer in `Graticula:TrustedProxies`, which
    /// is empty by default — and with it empty this is what it always was.
    /// </para>
    /// </remarks>
    private static IPAddress? RemoteAddress(HttpContext context) =>
        CallerAddress.Of(context);

    /// <summary>
    /// How long an ArcGIS token lives when its client does not say — sixty minutes, ArcGIS's own
    /// default for <c>expiration</c>.
    /// </summary>
    /// <remarks>
    /// <b>ADR-015 §4 mitigation 3: short-lived by default.</b> Until 2026-09-15 a token requested
    /// without <c>expiration</c> lived the deployment's whole session lifetime, twelve hours by default,
    /// so one that leaked into a log or a <c>Referer</c> stayed useful for a working day. The console's
    /// own sign-in is not an ArcGIS token and keeps the session lifetime.
    /// </remarks>
    internal static readonly TimeSpan CompatibilityTokenLifetime = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The lifetime an ArcGIS client asks for with <c>expiration</c>, in minutes — from the form or
    /// the query — or <see cref="CompatibilityTokenLifetime"/> when it asks for none or for something
    /// that is not a number.
    /// </summary>
    /// <remarks>
    /// <b>One reading for all three token doors</b> (<c>/rest</c>, <c>/sharing/rest</c>,
    /// <c>/admin</c>), so they cannot come to grant different lifetimes for the same request.
    /// <see cref="LoginService"/> keeps the deployment's lifetime as the ceiling.
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The lifetime to ask <see cref="LoginService"/> for.</returns>
    internal static async Task<TimeSpan?> RequestedLifetimeAsync(HttpContext context, CancellationToken cancellation)
    {
        string? value = null;

        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
            value = form["expiration"].ToString();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = context.Request.Query["expiration"].ToString();
        }

        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double minutes)
            && double.IsFinite(minutes)
            && minutes < TimeSpan.MaxValue.TotalMinutes / 2
                ? TimeSpan.FromMinutes(minutes)
                : CompatibilityTokenLifetime;
    }

    /// <summary>
    /// What an ArcGIS client asks its token to be bound to, from <c>client</c>, <c>referer</c> and
    /// <c>ip</c> in the form or the query — <see cref="TokenBinding"/>.
    /// </summary>
    /// <remarks>One reading for all three token doors, for the reason
    /// <see cref="RequestedLifetimeAsync"/> is.</remarks>
    /// <param name="context">The request.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>Whether a token may be issued, the binding to store, and why not.</returns>
    internal static async Task<(bool Ok, string? Bound, string? Error)> RequestedBindingAsync(
        HttpContext context, CancellationToken cancellation)
    {
        IFormCollection? form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : null;

        string? Read(string name)
        {
            string? value = form?[name].ToString();
            return string.IsNullOrWhiteSpace(value) ? context.Request.Query[name].ToString() : value;
        }

        bool ok = TokenBinding.TryRead(
            Read("client"), Read("referer"), Read("ip"), CallerAddress.Of(context), out string? bound, out string? error);

        return (ok, bound, error);
    }

    /// <summary>The outcome of a token exchange: whether one was asked for, and its answer.</summary>
    /// <param name="Asked">Whether the request carried a token to exchange rather than a password.</param>
    /// <param name="Status">The status to answer a refusal with.</param>
    /// <param name="Error">Why it was refused, or null.</param>
    /// <param name="Token">The token for the server.</param>
    /// <param name="Expires">When it ends.</param>
    internal readonly record struct Exchange(bool Asked, int Status, string? Error, string? Token, DateTimeOffset Expires);

    /// <summary>
    /// A token for this server in exchange for a token this portal issued — what an ArcGIS client asks for
    /// once <c>/rest/info</c> names an <c>owningSystemUrl</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The request, as the ArcGIS Maps SDK 4.30 sends it</b> — measured 2026-09-15 against a probe that
    /// logged it: <c>POST {tokenServicesUrl}</c> with <c>request=getToken</c>, <c>serverUrl</c> naming the
    /// resource it was refused (a layer's URL, not the server's root), the portal <c>token</c>, and no
    /// username. The SDK sends it to the <c>tokenServicesUrl</c> <c>/rest/info</c> advertises.
    /// </para>
    /// <para>
    /// <b>The answer is the same token.</b> The portal and the server here are one process with one session
    /// store, so the portal's token already opens the server; minting a second session would double what
    /// a sign-out has to revoke and what a leak exposes. Its expiry, binding and scope go with it unchanged,
    /// which is what an exchange must not widen.
    /// </para>
    /// <para>
    /// <b>Refused for a server that is not this one</b>: a portal that answered for another host would be
    /// vouching for a server it does not run. And a token that is expired, revoked or used from outside its
    /// binding is 498, the answer an ArcGIS client already reads as <i>sign in again</i>.
    /// </para>
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The exchange, or <c>Asked = false</c> when the request is an ordinary sign-in.</returns>
    internal static async Task<Exchange> TryExchangeAsync(HttpContext context, CancellationToken cancellation)
    {
        IFormCollection? form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : null;

        string Read(string name)
        {
            string? value = form?[name].ToString();
            return string.IsNullOrWhiteSpace(value) ? context.Request.Query[name].ToString() : value;
        }

        string presented = Read("token").Trim();

        if (presented.Length == 0 || Read("username").Trim().Length > 0)
        {
            return new Exchange(false, 0, null, null, default);
        }

        string serverUrl = Read("serverUrl").Trim();
        string origin = $"{context.Request.Scheme}://{context.Request.Host}";

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? server)
            || !string.Equals(server.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
        {
            return new Exchange(
                true, StatusCodes.Status400BadRequest,
                serverUrl.Length == 0
                    ? "A token is exchanged for a server named in 'serverUrl', and none was named."
                    : $"'{serverUrl}' is not a server this portal runs, so it issues no token for it.",
                null, default);
        }

        IIdentityStore store = context.RequestServices.GetRequiredService<IIdentityStore>();
        TimeProvider time = context.RequestServices.GetRequiredService<TimeProvider>();

        AuthenticatedSession? session = await store
            .FindSessionAsync(SessionToken.HashOf(presented), time.GetUtcNow(), cancellation)
            .ConfigureAwait(false);

        if (session is not { } found
            || !TokenBinding.Admits(
                found.BoundTo,
                CallerAddress.Of(context),
                context.Request.Headers.Referer.ToString(),
                context.Request.Headers.Origin.ToString()))
        {
            return new Exchange(true, 498, "Invalid token.", null, default);
        }

        return new Exchange(true, 0, null, presented, found.ExpiresAt);
    }

    private static Task Refuse(HttpContext context, int status, string message) =>
        Results.Json(new { error = new { code = status, message } }, statusCode: status)
            .ExecuteAsync(context);
}
