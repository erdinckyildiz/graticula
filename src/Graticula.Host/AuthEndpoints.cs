using System;
using System.Text.Json;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
        app.MapPost("/rest/auth/password", ChangePasswordAsync);
    }

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
                context.Connection.RemoteIpAddress?.ToString(),
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
                "That setup token is not usable. It is single-use and time-limited, and one of "
                + "those has already happened to it. Restart the server to issue another; a "
                + "server that already has an administrator will not issue one at all.")
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

    /// <summary>The source address, or null when it cannot be determined.</summary>
    /// <remarks>
    /// <b>The socket's address, never a forwarded header.</b> Behind a proxy this
    /// is the proxy, which makes the per-address limit far too coarse — but
    /// trusting <c>X-Forwarded-For</c> without a configured trusted-proxy list
    /// lets any caller set their own rate-limit bucket, which makes the limit
    /// zero. Too coarse is recoverable; forgeable is not. Configuring trusted
    /// proxies is owed.
    /// </remarks>
    private static IPAddress? RemoteAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress;

    private static Task Refuse(HttpContext context, int status, string message) =>
        Results.Json(new { error = new { code = status, message } }, statusCode: status)
            .ExecuteAsync(context);
}
