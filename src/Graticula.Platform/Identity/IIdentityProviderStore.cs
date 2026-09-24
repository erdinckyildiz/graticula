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

    /// <summary>Every account that signs in through a provider, by principal id — for the members list.</summary>
    Task<IReadOnlyDictionary<Guid, ExternalMember>> ExternalMembersAsync(CancellationToken cancellationToken);

    /// <summary>A provider's group mappings — ADR-089.</summary>
    Task<IReadOnlyList<GroupMapping>> MappingsAsync(Guid providerId, CancellationToken cancellationToken);

    /// <summary>Replaces a provider's group mappings — ADR-089.</summary>
    Task SetMappingsAsync(Guid providerId, IReadOnlyList<GroupMapping> mappings, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a person's groups at their sign-in — ADR-089: the highest role any matched group gives, else the
    /// provider's default when its mappings give roles at all; membership of every group here a mapping names, as
    /// the matched groups say; and nothing a mapping does not name.
    /// </summary>
    /// <param name="principalId">The account.</param>
    /// <param name="providerId">The provider they signed in through.</param>
    /// <param name="groups">Their groups, as the directory or provider named them.</param>
    /// <param name="rank">How high a role is, for choosing between two a person's groups give.</param>
    /// <param name="defaultRole">The role for a person none of whose groups gives one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What changed.</returns>
    Task<MappingApplied> ApplyMappingsAsync(
        Guid principalId,
        Guid providerId,
        IReadOnlyCollection<string> groups,
        Func<string, int> rank,
        string defaultRole,
        CancellationToken cancellationToken);
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
/// <param name="Kind"><c>oidc</c>, or <c>ldap</c> for a directory — ADR-089, whose issuer is its address and whose
/// client id is the account it searches with.</param>
/// <param name="GroupsClaim">The claim an OpenID Connect provider lists a person's groups in — ADR-089.</param>
/// <param name="Ldap">A directory's search settings, or null for an OpenID Connect provider.</param>
public sealed record IdentityProviderSettings(
    string Name,
    string Issuer,
    string ClientId,
    string Scopes,
    string UsernameClaim,
    bool AutoCreate,
    string DefaultRole,
    string DefaultUserType,
    bool Enabled,
    string Kind = "oidc",
    string GroupsClaim = "groups",
    LdapSettings? Ldap = null);

/// <summary>How a directory is searched for a person — ADR-089.</summary>
/// <param name="UserBase">Where people are found, as in <c>ou=people,dc=example,dc=org</c>.</param>
/// <param name="UserFilter">The filter that finds one by the name typed, <c>{0}</c> standing for it.</param>
/// <param name="DisplayAttribute">The attribute that holds a name to show.</param>
/// <param name="GroupAttribute">The attribute that lists a person's groups.</param>
/// <param name="SubjectAttribute">The attribute that never changes for a person, or empty for their DN.</param>
/// <param name="StartTls">Whether an <c>ldap://</c> connection is upgraded before the password is sent.</param>
public sealed record LdapSettings(
    string UserBase,
    string UserFilter,
    string DisplayAttribute,
    string GroupAttribute,
    string SubjectAttribute,
    bool StartTls);

/// <summary>One of a directory's or provider's groups, and what it gives here — ADR-089.</summary>
/// <param name="ExternalGroup">The group as the directory or provider names it: a name, or a DN whose first part is.</param>
/// <param name="Role">The role it gives, or null.</param>
/// <param name="GroupId">The group here its members join, or null.</param>
/// <param name="GroupName">That group's name, for a screen.</param>
public sealed record GroupMapping(string ExternalGroup, string? Role, Guid? GroupId, string? GroupName = null);

/// <summary>What applying a person's groups did — ADR-089.</summary>
/// <param name="Role">The role they now hold because of it, or null when no mapping gives roles.</param>
/// <param name="Joined">Groups here they were added to.</param>
/// <param name="Left">Groups here they were taken out of.</param>
public sealed record MappingApplied(string? Role, IReadOnlyList<string> Joined, IReadOnlyList<string> Left);

/// <summary>An account that signs in through a provider, for the members list — ADR-088, ADR-089.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="Username">The name it gives them.</param>
/// <param name="RoleManaged">Whether their role is the provider's group mapping's, and not set by hand.</param>
public sealed record ExternalMember(string Provider, string Username, bool RoleManaged);

/// <summary>A provider as stored.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Settings">What it is set to.</param>
/// <param name="HasSecret">Whether a client secret is stored.</param>
/// <param name="Accounts">How many accounts it names.</param>
public sealed record IdentityProvider(Guid Id, IdentityProviderSettings Settings, bool HasSecret, int Accounts);
