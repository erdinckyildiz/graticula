using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Identity;
using Microsoft.AspNetCore.Http;

namespace GisServer.Host;

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

        AuthenticatedSession? session =
            await FindSessionAsync(context, cancellationToken).ConfigureAwait(false);

        Principal principal = session?.Principal ?? Principal.Anonymous;

        (string userType, IReadOnlyList<string> roles) =
            await _store.GrantsOfAsync(principal.Id, cancellationToken).ConfigureAwait(false);

        return new RequestPrincipal(
            principal, session?.SessionId, Authorization.Resolve(userType, roles));
    }

    private async Task<AuthenticatedSession?> FindSessionAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        string? header = context.Request.Headers.Authorization;

        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string token = header[BearerPrefix.Length..].Trim();

        if (token.Length == 0)
        {
            return null;
        }

        return await _store
            .FindSessionAsync(SessionToken.HashOf(token), _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
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
    public RequestPrincipal(Principal principal, Guid? sessionId, Authorization authorization)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(authorization);

        Principal = principal;
        SessionId = sessionId;
        Authorization = authorization;
    }

    /// <summary>Who the request is from. Never null — anonymous is a principal.</summary>
    public Principal Principal { get; }

    /// <summary>Their session, or null for anonymous.</summary>
    public Guid? SessionId { get; }

    /// <summary>What they may do, resolved once for the request.</summary>
    public Authorization Authorization { get; }
}
