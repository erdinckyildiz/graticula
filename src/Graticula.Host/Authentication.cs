using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
/// mean "not authenticated", they resolve the same way. <b>They are no longer
/// indistinguishable</b> — ADR-015 §4a, 2026-09-15: a token the caller sent that finds no
/// session sets <see cref="RequestPrincipal.TokenWasRejected"/>, and the ArcGIS surface answers
/// it 498 so a client knows to sign in again.
/// </para>
/// <para>
/// <b>The ArcGIS <c>token=</c> query parameter is accepted, and the sentence here
/// said otherwise for a day.</b> It read *"not accepted yet … so it waits for
/// [the mitigations]"* while the code accepted it — the parameter was added on
/// 2026-08-20 with the ArcGIS token endpoints, and this remark was not. The
/// security gate found the consequence rather than the contradiction: live root
/// session tokens in the server's own log, harvested and replayed against a private
/// layer.
/// </para>
/// <para>
/// <b>The parameter is accepted because Q-17 requires unmodified Esri clients to
/// work</b> — ArcGIS Pro and every SDK put the token in the URL — and ADR-015 §4
/// permits it under four mitigations, all required. The first is that query strings
/// are redacted before logging, which is now <see cref="QueryRedaction"/> and is a
/// code path rather than a setting. The header form is tried first and is what a
/// client that offers the choice will use.
/// </para>
/// <para>
/// <b>What the channel still costs is recorded in
/// [D-120](../../docs/architecture-debt.md)</b>, and it is narrower than it was: the
/// credential is out of this server's own log, and what remains is every proxy and
/// browser history between the client and here.
/// </para>
/// </remarks>
internal sealed class Authentication
{
    private const string BearerPrefix = "Bearer ";

    private readonly IIdentityStore _store;

    /// <summary>What each role grants — ADR-035, a deployment's own answer.</summary>
    private readonly IRoleGrants _grants;
    private readonly TimeProvider _time;

    /// <summary>Creates the resolver.</summary>
    /// <param name="store">Where sessions live.</param>
    /// <param name="time">The clock.</param>
    /// <param name="grants">
    /// What each role grants — ADR-035. Optional so that a caller with no store keeps the compiled
    /// answer every build before 2026-08-18 gave, rather than silently resolving to nothing.
    /// </param>
    /// <param name="breaker">
    /// ADR-007 §4.8's N3, so a store that failed moments ago is not asked again. Optional,
    /// because a breaker is a property of a running server rather than of resolving a
    /// principal, and a test that has to build one to check a token is paying for
    /// something it is not about.
    /// </param>
    /// <param name="anonymous">
    /// The anonymous caller's grants, held while the store's announcements are heard —
    /// [D-249](../../docs/architecture-debt.md). Optional for the breaker's reason: without it
    /// every anonymous request reads the store, which is what every one did before it existed.
    /// </param>
    /// <param name="basic">HTTP Basic on the OGC faces — ADR-178. Optional: without it only tokens are read.</param>
    public Authentication(
        IIdentityStore store,
        TimeProvider time,
        IRoleGrants? grants = null,
        SourceBreaker? breaker = null,
        AnonymousGrants? anonymous = null,
        BasicCredentials? basic = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _grants = grants ?? CompiledRoleGrants.Instance;
        _time = time;
        _breaker = breaker;
        _anonymous = anonymous;
        _basic = basic;
    }

    private readonly SourceBreaker? _breaker;
    private readonly AnonymousGrants? _anonymous;

    /// <summary>HTTP Basic on the OGC faces — ADR-178. Optional, so a resolver built without it reads tokens only.</summary>
    private readonly BasicCredentials? _basic;

    /// <summary>
    /// Resolves the principal and what it may do, defaulting to anonymous.
    /// </summary>
    /// <remarks>
    /// <b>Anonymous gets its grants looked up too.</b> ADR-015 §2a made it a real
    /// principal precisely so this path has no special case: whether a portal is
    /// public is then a row in <c>principal_role</c> rather than a branch here.
    /// <b>Still a row, and since 2026-09-11 a held one</b> — ADR-015 §3a, D-249: the answer
    /// is kept in <see cref="AnonymousGrants"/> while the store's announcements are heard,
    /// so the row is read when it changes rather than on every request.
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
        /*
          <b>[D-131](../../docs/architecture-debt.md): this is the first of the two
          four-second waits.</b> Measured with the platform store stopped: `/rest/info`,
          which reads nothing, answered 200 in 4.03 s — all of it here. Every route pays
          it, because every route resolves a principal first, and a data request then pays
          it again against its own source, which is where the 8.0 s came from.

          <b>So a store that failed moments ago is not asked again.</b> The answer is the
          same either way — an unreachable store yields anonymous, as the comment above
          says — so skipping the attempt changes nothing about who the caller is and
          removes four seconds from every request during an outage. That is the difference
          between a degradation and a queue collapse: each of those waits holds a
          connection for its whole duration.
        */
        if (_breaker is { } breaker && breaker.IsOpen(SourceBreaker.PlatformStore))
        {
            // <b>The same reason as the catch below, and forgetting it here was the whole
            // defect surviving its own repair -- D-192.</b> The catch fires once, on the
            // request that discovers the outage; every request after it takes this line
            // instead, because that is what the breaker is for. So a repair that set the flag
            // only in the catch produced the right sentence for one caller and the misleading
            // one for everybody after -- and the outage rehearsal, which logs in before it
            // stops the database, read the second kind and showed no change at all.
            return new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing)
            {
                StoreWasUnreachable = true,
            };
        }

        try
        {
            await ReadBodyTokenAsync(context, cancellationToken).ConfigureAwait(false);

            AuthenticatedSession? session =
                await FindSessionAsync(context, cancellationToken).ConfigureAwait(false);

            // <b>HTTP Basic, on an OGC face and nowhere else — ADR-178.</b> Only when no token was sent, so a client that
            // has one is never asked for its password again, and only over HTTPS.
            BasicRefusal? basicRefusal = null;

            if (session is null
                && _basic is { } basic
                && BasicCredentials.AppliesTo(context.Request.Path)
                && (BearerToken(context) ?? EsriToken(context)) is null
                && BasicCredentials.TryRead(context, out string basicName, out string basicPassword))
            {
                (session, basicRefusal) = await BasicSessionAsync(context, basic, basicName, basicPassword, cancellationToken)
                    .ConfigureAwait(false);
            }

            // <b>A bound token used from somewhere it was not bound to is not a token — D-268.</b>
            // Treated exactly as an unrecognised one, so the ArcGIS surface answers 498 and nothing
            // tells the caller whether the token exists.
            if (session is { BoundTo: { } bound }
                && !TokenBinding.Admits(
                    bound,
                    CallerAddress.Of(context),
                    context.Request.Headers.Referer.ToString(),
                    context.Request.Headers.Origin.ToString()))
            {
                session = null;
            }

            // <b>An ArcGIS token does not open the native administration API — ADR-015 §4, Q-154
            // (owner decision 2026-09-15).</b> The caller is anonymous here and is told why, so a
            // script holding a token from generateToken learns to sign in rather than that it lacks a
            // privilege its account has.
            bool outsideScope = session is { Scope: SessionScopes.ArcGis }
                && SessionScopes.IsNativeAdministration(context.Request.Path.Value);

            if (outsideScope)
            {
                session = null;
            }

            // <b>A token the caller chose to send, and the store did not recognise — ADR-015 §4a.</b>
            // Still anonymous, as the remarks above say; what is added is that it is remembered, so
            // the ArcGIS surface can answer 498 rather than serve the caller as though it had sent
            // nothing. The cookie is not a token the caller sent in this sense: the console handles
            // its own expired session, and a browser attaches the cookie whether or not anybody meant
            // it to.
            //
            // The caller keeps the anonymous grants, so a face that does not answer 498 serves it
            // what it served before.
            bool rejected = !outsideScope && session is null && (BearerToken(context) ?? EsriToken(context)) is { Length: > 0 };

            Principal principal = session?.Principal ?? Principal.Anonymous;

            // <b>An anonymous caller's grants come from memory while the store's announcements
            // are heard — D-249, owner decision 2026-09-11.</b> That lookup was the whole of the
            // store traffic an anonymous request paid, measured at 2.6x to 3.9x of the pipeline's
            // ceiling. `AnonymousGrants` serves an answer only while a subscription is up, so a
            // server that cannot listen reads the store exactly as it always did. <b>A hit leaves
            // the breaker alone</b>: it is not evidence that the store answered, and telling the
            // breaker it was would close it on a request that never asked.
            AnonymousGrants.Snapshot? held = null;

            if (session is null && _anonymous is { } cache && cache.TryGet(out held))
            {
                if (_grants is PostgresRoleGrants heldLive)
                {
                    await heldLive.EnsureFreshAsync(cancellationToken).ConfigureAwait(false);
                }

                return new RequestPrincipal(
                    principal,
                    null,
                    Authorization.Resolve(
                        held!.UserType, held.Roles, _grants, held.Groups, held.EditableGroups))
                {
                    TokenWasRejected = rejected,
                    TokenOutsideScope = outsideScope,
                    BasicRefusal = basicRefusal,
                };
            }

            long readUnder = _anonymous?.Generation ?? 0;

            (string userType,
             IReadOnlyList<string> roles,
             IReadOnlyList<Guid> groups,
             IReadOnlyList<Guid> editableGroups) =
                await _store.GrantsOfAsync(principal.Id, cancellationToken).ConfigureAwait(false);

            if (session is null)
            {
                _anonymous?.Offer(
                    readUnder, new AnonymousGrants.Snapshot(userType, roles, groups, editableGroups));
            }

            // <b>What each role grants is read from the store now, not from a compiled table.</b>
            // ADR-035: a deployment edits its roles. The common case here is a clock comparison —
            // `PostgresRoleGrants` holds the answer for thirty seconds and is refreshed the moment
            // an administrator edits a role, so a revocation does not wait out a cache.
            if (_grants is PostgresRoleGrants live)
            {
                await live.EnsureFreshAsync(cancellationToken).ConfigureAwait(false);
            }

            _breaker?.Succeeded(SourceBreaker.PlatformStore);

            return new RequestPrincipal(
                principal,
                session?.SessionId,
                Authorization.Resolve(userType, roles, _grants, groups, editableGroups),
                session?.MustChangePassword ?? false)
            {
                TokenWasRejected = rejected,
                TokenOutsideScope = outsideScope,
                BasicRefusal = basicRefusal,
            };
        }
        catch (Npgsql.NpgsqlException unreachable)
        {
            // <b>Recorded, not only survived.</b> This catch is why the outage was
            // invisible to everything else: the request continued as anonymous and nothing
            // else in the server learnt that the store had just cost four seconds. The
            // breaker is the thing that learns it.
            _breaker?.Failed(SourceBreaker.PlatformStore, unreachable);

            // <b>Anonymous, and saying why -- D-192.</b> The request continues as anonymous
            // because a store this server cannot read is not a reason to trust a token. What
            // the caller is told about that changes: see `StoreWasUnreachable`.
            return new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing)
            {
                StoreWasUnreachable = true,
            };
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

    /// <summary>
    /// The session a Basic credential opens, or why it opens none — ADR-178.
    /// </summary>
    private async Task<(AuthenticatedSession? Session, BasicRefusal? Refusal)> BasicSessionAsync(
        HttpContext context, BasicCredentials basic, string name, string password, CancellationToken cancellationToken)
    {
        if (!context.Request.IsHttps)
        {
            return (null, BasicRefusal.Plaintext);
        }

        // Twice at most: a held token whose session the store has since forgotten — revoked, or its password changed —
        // is dropped and the credential is checked again.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            (string? token, string key, LoginFailure failure) =
                await basic.TokenAsync(name, password, context, cancellationToken).ConfigureAwait(false);

            if (token is null)
            {
                return (null, failure is LoginFailure.AccountThrottled or LoginFailure.AddressThrottled
                    ? BasicRefusal.Throttled
                    : BasicRefusal.Rejected);
            }

            if (await _store.FindSessionAsync(SessionToken.HashOf(token), _time.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false) is { } session)
            {
                return (session, null);
            }

            basic.Forget(key);
        }

        return (null, BasicRefusal.Rejected);
    }

    private async Task<AuthenticatedSession?> FindSessionAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        string? token = BearerToken(context) ?? EsriToken(context) ?? CookieToken(context);

        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return await _store
            .FindSessionAsync(SessionToken.HashOf(token), _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The token as an ArcGIS client sends it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two channels, because Esri clients use both.</b> The header is what
    /// newer ones send; the query parameter is what everything else has always
    /// sent, including the URLs a person pastes out of a browser.
    /// </para>
    /// <para>
    /// <b>Unlike the cookie, this is not restricted to safe methods, and the
    /// reason is that they are different risks.</b> A cookie is attached by the
    /// browser whether or not the caller meant it, which is what makes forgery
    /// possible; a token in a query string is put there deliberately by whoever
    /// made the request. Nothing can be tricked into adding it.
    /// </para>
    /// <para>
    /// <b>What it does cost is disclosure.</b> A token in a URL is written into
    /// this server's request log, and into any proxy's. That is real and it is
    /// [D-120](../../docs/architecture-debt.md); the header is the better channel
    /// and is tried first.
    /// </para>
    /// </remarks>
    private static string? EsriToken(HttpContext context)
    {
        string? header = context.Request.Headers["X-Esri-Authorization"];

        if (header is not null && header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            string bearer = header[BearerPrefix.Length..].Trim();

            if (bearer.Length > 0)
            {
                return bearer;
            }
        }

        // <b>The query channel is a deployment's choice, D-120.</b> This is the channel that
        // puts a live credential into every log between the caller and here, and the header
        // above is the one a client with a choice uses. A deployment that knows its clients
        // send the header sets `Graticula:AcceptTokenInQueryString` to false and the parameter
        // is not read at all, so a caller who sends only `?token=` is anonymous and meets the
        // ordinary refusal. <b>Deliberately not a distinct refusal</b>: *your token channel is
        // disabled* tells somebody who guessed a token that they guessed a real one.
        if (!context.RequestServices.GetRequiredService<HostSettings>().AcceptTokenInQueryString)
        {
            return context.Items[BodyTokenKey] as string;
        }

        string query = context.Request.Query["token"].ToString();

        return query.Length == 0 ? context.Items[BodyTokenKey] as string : query;
    }

    /// <summary>The largest form body searched for a token: past this it is not a request moved to POST for its length.</summary>
    private const long MaximumBodyTokenSearch = 8L * 1024 * 1024;

    /// <summary>Where <see cref="ReadBodyTokenAsync"/> leaves a token sent in a form body.</summary>
    private const string BodyTokenKey = "graticula.body-token";

    /// <summary>
    /// A token sent in a form-encoded POST body, as ArcGIS clients send it once a request is too long for a URL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The JS SDK moves a request to POST past 2,000 characters, and its token with it</b> — so a real polygon sent
    /// to the geometry service, a large query or applyEdits arrived anonymous and was answered as a service that is not
    /// there (the ArcGIS reviewer's GeometryServer pass, 2026-10-03).
    /// </para>
    /// <para>
    /// <b>Read regardless of the query-string setting.</b> A body is not written to request logs, which is the whole of
    /// D-120's cost; and like the query parameter it is put there deliberately by the caller, never attached by the
    /// browser, so it carries none of the cookie's forgery risk.
    /// </para>
    /// <para>
    /// <b>Form-encoded bodies only, buffered and rewound.</b> A multipart upload may be gigabytes and is not read here;
    /// an endpoint that reads its body itself still finds all of it.
    /// </para>
    /// </remarks>
    private static async Task ReadBodyTokenAsync(HttpContext context, CancellationToken cancellationToken)
    {
        HttpRequest request = context.Request;

        if (!HttpMethods.IsPost(request.Method)
            || request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) != true
            || request.Headers.ContainsKey("X-Esri-Authorization")
            || request.Headers.ContainsKey("Authorization")
            || request.Query.ContainsKey("token")
            // A body past this is not a form a client moved to POST for its length; it is the endpoint's to read, and
            // to refuse in its own words when it is past the ceiling (OversizedBodyTests).
            || request.ContentLength is not { } length || length > MaximumBodyTokenSearch)
        {
            return;
        }

        request.EnableBuffering();

        try
        {
            IFormCollection form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);

            if (form["token"].ToString() is { Length: > 0 } token)
            {
                context.Items[BodyTokenKey] = token;
            }
        }
        catch (Exception e) when (e is System.IO.InvalidDataException or BadHttpRequestException or System.IO.IOException)
        {
            // A form too large or malformed is the endpoint's to refuse, in its own words.
        }
        finally
        {
            request.Body.Position = 0;
        }
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
    /// <summary>
    /// Whether this request's credential, if it has one, is the session cookie alone — no bearer header, no ArcGIS
    /// header, and no <c>token=</c> this server reads.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>True when only the cookie could have signed the caller in.</returns>
    /// <remarks>
    /// <b>For the one <c>GET</c> that is a write — ArcGIS's <c>exportTiles</c>, ADR-098 §5.7.</b> The rule above is that
    /// the cookie authenticates reads only; the ArcGIS specification makes starting an export a <c>GET</c>, so the route
    /// asks this to hold the cookie to the rule the method no longer can. The query channel is counted only where the
    /// deployment reads it (D-120), so a <c>token=</c> the server ignores does not make a cookie request look signed.
    /// </remarks>
    internal static bool CookieOnly(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (BearerToken(context) is not null)
        {
            return false;
        }

        string? esri = context.Request.Headers["X-Esri-Authorization"];

        if (esri is not null && esri.StartsWith(BearerPrefix, StringComparison.Ordinal)
            && esri[BearerPrefix.Length..].Trim().Length > 0)
        {
            return false;
        }

        bool queryRead = context.RequestServices?.GetService(typeof(HostSettings)) is not HostSettings settings
            || settings.AcceptTokenInQueryString;

        if (queryRead && context.Request.Query["token"].ToString().Length > 0)
        {
            return false;
        }

        return CookieToken(context) is not null;
    }

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

    /// <summary>
    /// True when this caller is anonymous because the platform store could not be asked,
    /// rather than because they presented nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-192](../../docs/architecture-debt.md), found by the outage rehearsal on
    /// 2026-08-27.</b> Sessions live in the platform store, so when the store is down this
    /// server cannot tell a valid token from a forged one and correctly refuses both. What it
    /// said while refusing was *"you are not signed in. Sign in at /rest/auth/login"* — which
    /// is a sentence about the caller's credentials, sends an administrator to a login route
    /// that also cannot work, and is precisely the confusion
    /// [ADR-017](../../docs/adr/ADR-017-admin-api.md) §6 exists to prevent: *a data-plane
    /// failure must not blind the management plane.*
    /// </para>
    /// <para>
    /// <b>Still anonymous, and still 401.</b> The refusal is right -- an outage is not a
    /// reason to trust a token -- so what changes is the sentence and not the decision. A 503
    /// was the alternative and would have changed the contract of every authenticated route
    /// for a condition the caller cannot act on either way.
    /// </para>
    /// </remarks>
    public bool StoreWasUnreachable { get; init; }

    /// <summary>
    /// Whether the caller sent a token — as a header or as <c>token=</c> — that resolved to no
    /// session: expired, revoked, or never issued.
    /// </summary>
    /// <remarks>
    /// <b>Never set when the store could not be asked</b>, because an outage says nothing about the
    /// token, and answering 498 then would sign every client out for a condition signing in again
    /// cannot fix. <see cref="StoreWasUnreachable"/> is that case.
    /// </remarks>
    public bool TokenWasRejected { get; init; }

    /// <summary>
    /// Whether the caller sent a token issued by an ArcGIS token endpoint to the native administration
    /// API, which such a token does not open — ADR-015 §4, Q-154.
    /// </summary>
    public bool TokenOutsideScope { get; init; }

    /// <summary>Why a Basic credential on an OGC face opened no session, or null — ADR-178.</summary>
    public BasicRefusal? BasicRefusal { get; init; }

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
