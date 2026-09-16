using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>
/// An app registered to sign its users in through OAuth — ADR-076 §5.
/// </summary>
/// <param name="ClientId">What the app sends as <c>client_id</c>.</param>
/// <param name="Title">What the sign-in page shows a person, so they can tell who is asking.</param>
/// <param name="RedirectUris">The only addresses a code is ever sent to.</param>
/// <param name="Builtin">Whether it shipped registered (Field Maps) rather than being added here.</param>
/// <param name="CreatedAt">When it was registered.</param>
public sealed record OAuthApp(
    string ClientId, string Title, IReadOnlyList<string> RedirectUris, bool Builtin, DateTimeOffset CreatedAt);

/// <summary>A code as it was issued, returned when it is redeemed.</summary>
/// <param name="ClientId">The app it was issued to.</param>
/// <param name="Principal">Whose it is — null when the account is gone or disabled since.</param>
/// <param name="RedirectUri">The redirect it was issued for, which the exchange must repeat.</param>
/// <param name="Challenge">The PKCE challenge, or null.</param>
/// <param name="ChallengeMethod">`S256` or `plain`, or null.</param>
public sealed record OAuthCode(
    string ClientId, Principal? Principal, string RedirectUri, string? Challenge, string? ChallengeMethod);

/// <summary>What happened to a code presented for exchange.</summary>
public enum CodeRedemption
{
    /// <summary>Redeemed now, for the first and only time.</summary>
    Redeemed,

    /// <summary>No such code, or it expired.</summary>
    Unknown,

    /// <summary>Redeemed before — the refresh token that first use issued has been revoked.</summary>
    Replayed,
}

/// <summary>The rules of ADR-076 that need no database.</summary>
public static class OAuthRules
{
    /// <summary>How long a code lives: five minutes, one use.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>An access token's lifetime when the request names none — Esri's documented default for this grant.</summary>
    public static readonly TimeSpan DefaultAccessLifetime = TimeSpan.FromMinutes(30);

    /// <summary>A refresh token's lifetime — two weeks, Esri's documented default; the authorize request's `expiration` is not read (ADR-076 §3.4).</summary>
    public static readonly TimeSpan DefaultRefreshLifetime = TimeSpan.FromDays(14);

    /// <summary>The out-of-band redirect the native SDKs use.</summary>
    public const string OutOfBand = "urn:ietf:wg:oauth:2.0:oob";

    /// <summary>
    /// Whether a redirect URI may be registered, and why not.
    /// </summary>
    /// <remarks>
    /// <b>HTTPS anywhere, plain HTTP only to this machine, any custom scheme, or out-of-band.</b> A
    /// code sent over plain HTTP to another host is readable on the way, and with PKCE optional (as
    /// Esri documents it) the code alone is enough. A custom scheme is how a phone app receives it;
    /// <c>javascript:</c>, <c>data:</c> and <c>file:</c> are refused because a browser would run or
    /// open them rather than hand them to an app.
    /// </remarks>
    /// <param name="uri">The candidate.</param>
    /// <param name="error">Why not, when it may not.</param>
    /// <returns>Whether it may be registered.</returns>
    public static bool IsAcceptableRedirect(string? uri, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(uri) || uri != uri.Trim())
        {
            error = "A redirect URI is required and may not start or end with spaces.";
            return false;
        }

        if (string.Equals(uri, OutOfBand, StringComparison.Ordinal))
        {
            return true;
        }

        // <b>A scheme is required before the parser is asked</b>, because .NET on Linux reads
        // `/relative/path` as `file:///relative/path` — an absolute URI there and not on Windows —
        // so the same input was refused for two different reasons on the two machines. Found by CI.
        if (uri.IndexOf(':', StringComparison.Ordinal) <= 0
            || !Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
        {
            error = $"'{uri}' is not an absolute URI.";
            return false;
        }

        if (parsed.Fragment.Length > 0)
        {
            error = $"'{uri}' has a fragment; a code is delivered in the query and a fragment would be dropped.";
            return false;
        }

        switch (parsed.Scheme)
        {
            case "https":
                return true;

            case "http" when parsed.IsLoopback:
                return true;

            case "http":
                error = $"'{uri}' is plain HTTP to another machine, where the code can be read on the way. Use https.";
                return false;

            case "javascript" or "data" or "file" or "vbscript" or "about" or "blob":
                error = $"'{parsed.Scheme}:' is run or opened by a browser rather than handed to an app.";
                return false;

            default:
                return true;
        }
    }

    /// <summary>
    /// Whether a verifier answers a challenge — RFC 7636 §4.6.
    /// </summary>
    /// <remarks>
    /// <b>Constant time, and a challenge that was sent makes the verifier required.</b> An exchange
    /// without one is refused rather than treated as a client that did not use PKCE, because the
    /// code was issued under a promise that only the holder of the verifier could redeem it.
    /// </remarks>
    /// <param name="challenge">The challenge sent to authorize, or null.</param>
    /// <param name="method">`S256`, `plain`, or null for `plain`.</param>
    /// <param name="verifier">The verifier sent to the token endpoint, or null.</param>
    /// <returns>Whether the exchange may proceed.</returns>
    public static bool Verifies(string? challenge, string? method, string? verifier)
    {
        if (challenge is null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(verifier) || verifier.Length is < 43 or > 128)
        {
            return false;
        }

        string expected = string.Equals(method, "S256", StringComparison.Ordinal)
            ? Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            : verifier;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(challenge));
    }

    /// <summary>Whether a challenge method is one this server reads.</summary>
    /// <param name="method">The method, or null for the default.</param>
    /// <returns>Whether it is <c>S256</c>, <c>plain</c> or absent.</returns>
    public static bool IsKnownMethod(string? method) => method is null or "S256" or "plain";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Where registered apps, codes and refresh tokens are kept — ADR-076.
/// </summary>
/// <remarks>
/// <b>Hashes, never the values.</b> A code and a refresh token are bearer secrets; the store holds
/// their SHA-256, as it does a session's token, so a copy of the catalogue signs nobody in.
/// </remarks>
public interface IOAuthStore
{
    /// <summary>Every registered app.</summary>
    Task<IReadOnlyList<OAuthApp>> ListAppsAsync(CancellationToken cancellationToken);

    /// <summary>One app, or null.</summary>
    Task<OAuthApp?> FindAppAsync(string clientId, CancellationToken cancellationToken);

    /// <summary>Registers an app; false when the client id is taken.</summary>
    Task<bool> CreateAppAsync(
        string clientId, string title, IReadOnlyList<string> redirectUris, Guid createdBy, CancellationToken cancellationToken);

    /// <summary>Removes an app, its codes and its refresh tokens; false when there was none.</summary>
    Task<bool> DeleteAppAsync(string clientId, CancellationToken cancellationToken);

    /// <summary>Stores a code's hash.</summary>
    Task IssueCodeAsync(
        byte[] codeHash,
        string clientId,
        Guid principalId,
        string redirectUri,
        string? challenge,
        string? challengeMethod,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks a code used and returns what it was issued for — once. A second call revokes the
    /// refresh tokens the first issued and answers <see cref="CodeRedemption.Replayed"/>.
    /// </summary>
    Task<(CodeRedemption Outcome, OAuthCode? Code)> RedeemCodeAsync(
        byte[] codeHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Stores a refresh token's hash, remembering the code it came from, if any.</summary>
    Task IssueRefreshAsync(
        byte[] tokenHash,
        string clientId,
        Guid principalId,
        DateTimeOffset expiresAt,
        byte[]? fromCodeHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// The live refresh token's app and principal, or null when it is unknown, expired, revoked, or
    /// its account is gone or disabled.
    /// </summary>
    Task<(string ClientId, Principal Principal, DateTimeOffset ExpiresAt)?> FindRefreshAsync(
        byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Revokes a refresh token. Idempotent.</summary>
    Task RevokeRefreshAsync(byte[] tokenHash, CancellationToken cancellationToken);
}
