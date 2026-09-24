using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>
/// The OpenID Connect providers people may sign in through, and the accounts each names — ADR-088.
/// </summary>
/// <remarks>
/// <para>
/// <b>Apart from <see cref="IIdentityStore"/>, for the reason that interface gives about member administration</b>:
/// the request path reads a session, and this is a configuration surface plus the one lookup a sign-in needs.
/// </para>
/// <para>
/// <b>The secret is handed in sealed and handed out sealed.</b> The store does not hold the key; the host seals
/// with <c>SecretProtector</c>, as it does a data source's credential, and unseals only to call the provider.
/// </para>
/// </remarks>
public interface IIdentityProviderStore
{
    /// <summary>Every provider, by name.</summary>
    Task<IReadOnlyList<IdentityProvider>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One provider, or null.</summary>
    Task<IdentityProvider?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>A provider's sealed client secret and the key version that sealed it, or null when it has none.</summary>
    Task<(byte[] Secret, int KeyVersion)?> SecretOfAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Stores a new provider; null when its name is taken.</summary>
    Task<IdentityProvider?> CreateAsync(
        IdentityProviderSettings settings, (byte[] Secret, int KeyVersion)? secret, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a provider's settings; the secret only when one is given, or removed when
    /// <paramref name="clearSecret"/>. False when the name is another's.
    /// </summary>
    Task<bool> UpdateAsync(
        Guid id,
        IdentityProviderSettings settings,
        (byte[] Secret, int KeyVersion)? secret,
        CancellationToken cancellationToken,
        bool clearSecret = false);

    /// <summary>
    /// Removes a provider nobody signs in through; returns how many accounts it names when it was not removed.
    /// </summary>
    Task<int> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The account a provider's subject signs in to — found by the subject, or by the name for an account made
    /// before its owner's first sign-in, which this binds to the subject.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="subject">The <c>sub</c> claim.</param>
    /// <param name="username">The name the provider gives.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The principal, or null when the provider names no account here.</returns>
    Task<Principal?> FindPrincipalAsync(
        Guid providerId, string subject, string username, CancellationToken cancellationToken);

    /// <summary>
    /// Makes an account that signs in through a provider and has no password here — at a first sign-in, when the
    /// provider allows it, with <paramref name="subject"/>; or by an administrator ahead of one, without.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="subject">The <c>sub</c> claim, or null when an administrator makes the account first.</param>
    /// <param name="username">The name the provider gives.</param>
    /// <param name="accountName">The account's name here; the first free of it and its numbered variants is used.</param>
    /// <param name="displayName">A name to show, or null.</param>
    /// <param name="role">Its role.</param>
    /// <param name="userType">Its user type.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="numberIfTaken">
    /// True at a first sign-in, where nobody chose the name; false for an administrator, who typed it and is told
    /// it is taken.
    /// </param>
    /// <returns>
    /// The account, or null when the name is taken and may not be numbered, or the provider already names an account
    /// by this subject or name.
    /// </returns>
    Task<Principal?> CreateMemberAsync(
        Guid providerId,
        string? subject,
        string username,
        string accountName,
        string? displayName,
        string role,
        string userType,
        CancellationToken cancellationToken,
        bool numberIfTaken = true);

    /// <summary>Which provider an account signs in through, and the name it gives, or null for a local account.</summary>
    Task<(Guid ProviderId, string Username)?> ExternalOfAsync(Guid principalId, CancellationToken cancellationToken);
}

/// <summary>What an operator sets on a provider.</summary>
/// <param name="Name">What the sign-in button says.</param>
/// <param name="Issuer">The provider's issuer URL, whose <c>/.well-known/openid-configuration</c> describes it.</param>
/// <param name="ClientId">This server's client id at the provider.</param>
/// <param name="Scopes">The scopes asked for; <c>openid</c> is always among them.</param>
/// <param name="UsernameClaim">The claim that gives an account its name.</param>
/// <param name="AutoCreate">Whether a first sign-in with no account makes one.</param>
/// <param name="DefaultRole">The role such an account gets.</param>
/// <param name="DefaultUserType">The user type such an account gets.</param>
/// <param name="Enabled">Whether it is offered.</param>
public sealed record IdentityProviderSettings(
    string Name,
    string Issuer,
    string ClientId,
    string Scopes,
    string UsernameClaim,
    bool AutoCreate,
    string DefaultRole,
    string DefaultUserType,
    bool Enabled);

/// <summary>A provider as stored.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Settings">What it is set to.</param>
/// <param name="HasSecret">Whether a client secret is stored.</param>
/// <param name="Accounts">How many accounts it names.</param>
public sealed record IdentityProvider(Guid Id, IdentityProviderSettings Settings, bool HasSecret, int Accounts);
