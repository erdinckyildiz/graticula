using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>An API key's text: a prefix that says what it is, and 256 random bits — ADR-200.</summary>
/// <remarks>
/// <b>The prefix is for people, not for security.</b> A key pasted into a ticket or found in a repository is
/// recognisable as one, and a secret scanner can be taught it; the server looks a key up by its hash either way, and
/// a prefix on a session token would change nothing about what it opens.
/// </remarks>
public static class ApiKeyToken
{
    /// <summary>What every key begins with.</summary>
    public const string Prefix = "gak_";

    /// <summary>A new key, shown once and never stored.</summary>
    public static string Generate() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether a token is shaped as a key, so a session lookup that missed may try the keys.</summary>
    public static bool IsOne(string? token) =>
        token is { Length: > 40 } && token.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>An OAuth client secret's text — ADR-200 condition 2: what a service account's <c>client_credentials</c> sign-in sends.</summary>
/// <remarks>
/// <b>A different prefix from a key's, and the difference is load-bearing.</b> A key is a token; a secret is exchanged for
/// one at <c>oauth2/token</c> and is never a token itself. <see cref="ApiKeyToken.IsOne"/> never matches this prefix, so a
/// secret sent as <c>token=</c> is not even looked up among the keys, and the store's lookups filter by kind besides.
/// </remarks>
public static class ClientSecretToken
{
    /// <summary>What every client secret begins with.</summary>
    public const string Prefix = "gcs_";

    /// <summary>A new secret, shown once and never stored.</summary>
    public static string Generate() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A machine's account: no password, no items, roles only (ADR-015 §2) — ADR-200.</summary>
/// <param name="Id">The principal.</param>
/// <param name="Name">Its name, unique among every principal.</param>
/// <param name="Description">What it is for.</param>
/// <param name="UserType">Its ceiling, as a member's.</param>
/// <param name="Roles">What it holds.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="Disabled">Whether it is switched off, which stops every key it holds.</param>
/// <param name="Keys">How many keys it holds that are neither revoked nor expired.</param>
/// <param name="Secrets">How many OAuth client secrets it holds that are neither revoked nor expired (ADR-200 condition 2).</param>
public sealed record ServiceAccount(
    Guid Id,
    string Name,
    string? Description,
    string UserType,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt,
    bool Disabled,
    int Keys,
    int Secrets = 0);

/// <summary>One key or client secret, as listed: never its text, which is shown once when it is issued.</summary>
/// <param name="Id">The key.</param>
/// <param name="Name">What its holder called it.</param>
/// <param name="CreatedAt">When it was issued.</param>
/// <param name="ExpiresAt">When it stops working, or null for never.</param>
/// <param name="RevokedAt">When it was revoked, or null.</param>
/// <param name="LastUsedAt">When it last opened a request, to the minute, or null.</param>
public sealed record ApiKeyInfo(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>Service accounts and their keys — ADR-200.</summary>
public interface IApiKeyStore
{
    /// <summary>Makes a service account holding the roles given, or answers null when the name is taken.</summary>
    Task<ServiceAccount?> CreateServiceAccountAsync(
        string name, string? description, string userType, IReadOnlyList<string> roles, Guid createdBy,
        CancellationToken cancellationToken);

    /// <summary>Every service account.</summary>
    Task<IReadOnlyList<ServiceAccount>> ListServiceAccountsAsync(CancellationToken cancellationToken);

    /// <summary>Deletes a service account and its keys; false when there is none by that name.</summary>
    Task<bool> DeleteServiceAccountAsync(string name, CancellationToken cancellationToken);

    /// <summary>Switches a service account off or on; false when there is none by that name.</summary>
    Task<bool> SetDisabledAsync(string name, bool disabled, CancellationToken cancellationToken);

    /// <summary>Records a key by its hash, or answers null when the account is missing or the key's name is taken.</summary>
    Task<ApiKeyInfo?> IssueAsync(
        string account, string keyName, byte[] hash, DateTimeOffset? expiresAt, CancellationToken cancellationToken);

    /// <summary>An account's keys, or null when there is no such account.</summary>
    Task<IReadOnlyList<ApiKeyInfo>?> KeysAsync(string account, CancellationToken cancellationToken);

    /// <summary>Revokes a key; false when the account has no such key, or it was revoked already.</summary>
    Task<bool> RevokeAsync(string account, Guid key, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// The key a hash names and the account it opens, when the key is live and the account enabled; null otherwise,
    /// with no word about which, as a session lookup answers.
    /// </summary>
    Task<(Guid Key, Principal Principal, DateTimeOffset? ExpiresAt)?> FindAsync(
        byte[] hash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records that a key was used, at most once a minute per key.</summary>
    Task TouchAsync(Guid key, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Records an OAuth client secret by its hash (ADR-200 condition 2), or answers null when the account is missing or
    /// the name is taken among its keys and secrets.
    /// </summary>
    Task<ApiKeyInfo?> IssueSecretAsync(
        string account, string secretName, byte[] hash, DateTimeOffset? expiresAt, CancellationToken cancellationToken);

    /// <summary>An account's client secrets, or null when there is no such account.</summary>
    Task<IReadOnlyList<ApiKeyInfo>?> SecretsAsync(string account, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes a client secret and every token issued for it; false when the account has no such live secret.
    /// </summary>
    Task<bool> RevokeSecretAsync(string account, Guid secret, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// The secret a hash names, when it is the named account's, live, and the account enabled; null otherwise, with no
    /// word about which. Records the use, at most once a minute.
    /// </summary>
    Task<(Guid Secret, Principal Principal, DateTimeOffset? ExpiresAt)?> FindSecretAsync(
        string account, byte[] hash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records that a session was issued for a client secret, so revoking the secret ends it.</summary>
    Task LinkSessionAsync(Guid session, Guid secret, CancellationToken cancellationToken);
}
