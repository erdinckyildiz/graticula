using System;
using System.Security.Cryptography;
using System.Text;

namespace Graticula.Platform.Identity;

/// <summary>
/// Issues and hashes opaque session tokens.
/// </summary>
/// <remarks>
/// <para>
/// ADR-015 §3: tokens are opaque random strings with their state in the platform
/// store, not JWTs. The usual argument for JWT is statelessness, and Q-70 made
/// the platform store mandatory PostgreSQL, so statelessness buys nothing we do
/// not already have. What the store buys instead is revocation that actually
/// takes effect, and session listing.
/// </para>
/// <para>
/// <b>The token is never stored — only its SHA-256.</b> A dump of the session
/// table is then a list of useless digests rather than a set of live
/// credentials, which is the same reasoning as for passwords.
/// </para>
/// <para>
/// <b>SHA-256, not Argon2id, and the difference is not an oversight.</b> A
/// password is low-entropy and chosen by a human, so it needs a slow hash to
/// make guessing expensive. A token here is 256 bits from a CSPRNG: there is
/// nothing to guess, and a slow hash on the session lookup would put Argon2id in
/// the path of every authenticated request for no security gain at all.
/// </para>
/// </remarks>
public static class SessionToken
{
    /// <summary>Bytes of entropy in a token.</summary>
    /// <remarks>
    /// 256 bits. Larger than needed against any offline attack — there is no
    /// offline attack, since guessing requires a request per guess — and chosen
    /// so the margin is not a thing anyone has to think about again.
    /// </remarks>
    public const int EntropyBytes = 32;

    /// <summary>Generates a token.</summary>
    /// <returns>A URL-safe base64 string with no padding.</returns>
    /// <remarks>
    /// URL-safe because ADR-015 §4 accepts ArcGIS's <c>token=</c> query
    /// parameter, and a token containing <c>+</c> or <c>/</c> is one that
    /// survives or does not survive depending on which proxy it crosses.
    /// </remarks>
    public static string Generate() =>
        Base64Url(RandomNumberGenerator.GetBytes(EntropyBytes));

    /// <summary>Hashes a token for storage and lookup.</summary>
    /// <param name="token">The token as presented.</param>
    /// <returns>Its SHA-256.</returns>
    public static byte[] HashOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
