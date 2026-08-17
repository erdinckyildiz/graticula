using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Resolves the principal for a request.
/// </summary>
/// <remarks>
/// <para>
/// ADR-015 §3: an opaque bearer token, looked up in the platform store. There is
/// no signature to check and no claims to trust — the store is the authority,
/// which is what makes revocation take effect on the next request rather than at
/// token expiry.
/// </para>
/// <para>
/// <b>An unresolvable token yields anonymous, not a 401.</b> Rejecting here
/// would put the authentication middleware in charge of a decision that belongs
/// to the endpoint: <c>/rest/services</c> on an open data portal is meant to work
/// with no credential at all. The middleware answers <em>who</em>; whether that
/// is enough is authorization's question. Since a bad token and no token both
/// mean "not authenticated", they resolve the same way.
/// </para>
/// <para>
/// <b>The ArcGIS <c>token=</c> query parameter is not accepted yet.</b> ADR-015
/// §4 requires it for unmodified clients and requires four mitigations with it,
/// one of which is that query strings are redacted before logging on those
/// routes. Accepting the parameter without the redaction would be the weakening
/// without the bound, so it waits for them.
/// </para>
/// </remarks>
internal sealed class Authentication
{
    private const string BearerPrefix = "Bearer ";

    private readonly IIdentityStore _store;
    private readonly TimeProvider _time;

    /// <summary>Creates the resolver.</summary>
    /// <param name="store">Where sessions live.</param>
    /// <param name="time">The clock.</param>
    public Authentication(IIdentityStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _time = time;
    }

    /// <summary>
    /// Resolves the principal and what it may do, defaulting to anonymous.
    /// </summary>
    /// <remarks>
    /// <b>Anonymous gets its grants looked up too.</b> ADR-015 §2a made it a real
    /// principal precisely so this path has no special case: whether a portal is
    /// public is then a row in <c>principal_role</c> rather than a branch here.
    /// </remarks>
    public async Task<RequestPrincipal> ResolveAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // <b>An unreachable store yields anonymous, not an error.</b> ADR-017 §6
        // requires a minimal admin surface to answer during a datastore outage,
        // and this middleware runs before every endpoint — so a throw here takes
        // down the surface that exists to be reachable when everything else is
        // not. Found by stopping the datastore: /admin/health never ran, and
        // neither did anything else.
        //
        // <b>It fails closed, not open.</b> A token that cannot be validated is
        // not honoured, and anonymous-with-no-grants holds no privilege at all.
        // The endpoints that need the store still refuse; the ones that do not,
        // answer.
        try
        {
            AuthenticatedSession? session =
                await FindSessionAsync(context, cancellationToken).ConfigureAwait(false);

            Principal principal = session?.Principal ?? Principal.Anonymous;

            (string userType, IReadOnlyList<string> roles) =
                await _store.GrantsOfAsync(principal.Id, cancellationToken).ConfigureAwait(false);

            return new RequestPrincipal(
                principal,
                session?.SessionId,
                Authorization.Resolve(userType, roles),
                session?.MustChangePassword ?? false);
        }
        catch (Npgsql.NpgsqlException)
        {
            return new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing);
        }
    }

    /// <summary>
    /// The cookie a browser carries, which is a <em>read-only</em> credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-08-15, because the browsable directory could never be
    /// anything but anonymous.</b> The only credential channel was
    /// <c>Authorization: Bearer</c>, which a browser following a link cannot
    /// send — so every page of the REST Services Directory saw a stranger, and
    /// any service shared with the organisation was invisible in the one surface
    /// built for browsing. The owner found it by opening
    /// <c>/rest/services/Utilities</c> and seeing nothing.
    /// </para>
    /// <para>
    /// <b>It holds the same opaque session token, and the cookie flags are the
    /// security.</b> <c>HttpOnly</c> so script cannot read it, <c>Secure</c> so
    /// it never crosses plaintext, <c>SameSite=Strict</c> so another origin
    /// cannot cause the browser to send it.
    /// </para>
    /// </remarks>
    public const string SessionCookie = "gis-session";

    private async Task<AuthenticatedSession?> FindSessionAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        string? token = BearerToken(context) ?? CookieToken(context);

        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return await _store
            .FindSessionAsync(SessionToken.HashOf(token), _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? BearerToken(HttpContext context)
    {
        string? header = context.Request.Headers.Authorization;

        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string token = header[BearerPrefix.Length..].Trim();

        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// The session cookie, and only for a request that cannot change anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Safe methods only, and this is what makes the cookie safe to have.</b>
    /// A cookie is sent by the browser whatever caused the request, which is the
    /// whole of cross-site request forgery: another site can make your browser
    /// POST here with your credentials attached. <c>SameSite=Strict</c> is the
    /// usual answer and it is set — but it is one flag, honoured by the browser,
    /// and browsers have had bugs.
    /// </para>
    /// <para>
    /// <b>So the cookie authenticates GET and HEAD and nothing else.</b> A
    /// forged cross-site request can then only read, and reading is what the
    /// directory is for. Every mutation — publish, applyEdits, sharing,
    /// import — still requires the bearer header, which a browser cannot be
    /// tricked into attaching. That is a stronger property than an antiforgery
    /// token, because there is no token to get wrong: the credential simply does
    /// not work for the requests that matter.
    /// </para>
    /// <para>
    /// <b>The cost is real and small.</b> An HTML form cannot POST to this
    /// server on a cookie, so any future write surface in the browser needs a
    /// deliberate design rather than a form tag. Recorded as the trade rather
    /// than discovered later.
    /// </para>
    /// </remarks>
    private static string? CookieToken(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method))
        {
            return null;
        }

        return context.Request.Cookies.TryGetValue(SessionCookie, out string? token)
            && token.Length > 0
                ? token
                : null;
    }
}

/// <summary>Where the resolved principal lives for the rest of the request.</summary>
/// <remarks>
/// A feature rather than <c>HttpContext.User</c>. The ASP.NET
/// <c>ClaimsPrincipal</c> is a claims bag, and ADR-015 §1a needs a stable name
/// that maps to a database role — converting to claims and back would make the
/// authorization code read claims that we invented one line earlier.
/// </remarks>
internal sealed class RequestPrincipal
{
    /// <summary>Creates the feature.</summary>
    /// <param name="principal">Who the request is from.</param>
    /// <param name="sessionId">Their session, or null for anonymous.</param>
    /// <param name="authorization">What they may do.</param>
    /// <param name="mustChangePassword">
    /// Whether the credential this session was opened with is one its owner must replace.
    /// </param>
    public RequestPrincipal(
        Principal principal,
        Guid? sessionId,
        Authorization authorization,
        bool mustChangePassword = false)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(authorization);

        Principal = principal;
        SessionId = sessionId;
        Authorization = authorization;
        MustChangePassword = mustChangePassword;
    }

    /// <summary>Who the request is from. Never null — anonymous is a principal.</summary>
    public Principal Principal { get; }

    /// <summary>Their session, or null for anonymous.</summary>
    public Guid? SessionId { get; }

    /// <summary>What they may do, resolved once for the request.</summary>
    public Authorization Authorization { get; }

    /// <summary>
    /// Whether this caller is holding a password the server issued and its owner has not replaced.
    /// </summary>
    /// <remarks>
    /// <b>Read from the store on every request, not stamped into the token.</b> Owner rule
    /// 2026-08-17: a password the system issued is dirty until its owner changes it. Resolving it
    /// per request is what makes the change take effect on the request *after* they set their own
    /// — and it is the rule three of this month's defects came from breaking, each time by caching
    /// a fact that governs what a caller may do.
    /// </remarks>
    public bool MustChangePassword { get; }
}
