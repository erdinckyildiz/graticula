using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Oidc;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Microsoft.Extensions.Logging;

namespace Graticula.Host.Ldap;

/// <summary>
/// A password checked against an LDAP directory — Active Directory or OpenLDAP — ADR-089.
/// </summary>
/// <remarks>
/// <para>
/// <b>Search, then bind as the person.</b> The directory is searched for the name typed with the account an operator
/// configured, and the one entry found is bound with the password typed; only the directory ever compares it. The
/// search account's password is sealed with the server's key, as a provider's client secret is.
/// </para>
/// <para>
/// <b>One of the named outbound files</b> (<c>tools/registers-check.py</c>, D-274): it connects only to a directory
/// an operator configured, and only when somebody with no password here signs in or the operator checks it.
/// </para>
/// <para>
/// <b>A password never crosses the network in the clear</b>: <c>ldaps://</c>, or <c>ldap://</c> upgraded with
/// StartTLS, and plain <c>ldap://</c> only to this machine — the rule an OpenID Connect issuer has.
/// </para>
/// </remarks>
internal sealed class LdapDirectory : IDirectorySignIn
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IIdentityProviderStore _store;
    private readonly SecretProtector _protector;
    private readonly ILogger _log;

    /// <summary>
    /// <b>OpenLDAP 2.6 by the name its distributions give it.</b> The base library looks for
    /// <c>libldap-2.6.so.0</c>, <c>-2.5.so.0</c> and <c>-2.4.so.2</c>, and Ubuntu 24.04 — this image's base and CI's —
    /// ships 2.6 as <c>libldap.so.2</c>, which none of those is. Every directory call was a
    /// <see cref="DllNotFoundException"/> on Linux and nothing on Windows showed it; the first CI run did.
    /// </summary>
    static LdapDirectory()
    {
        Assembly protocols = typeof(LdapConnection).Assembly;

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, name) =>
            assembly == protocols
            && name.StartsWith("libldap", StringComparison.Ordinal)
            && NativeLibrary.TryLoad("libldap.so.2", out IntPtr handle)
                ? handle
                : IntPtr.Zero;
    }

    /// <summary>Creates the directory sign-in.</summary>
    public LdapDirectory(IIdentityProviderStore store, SecretProtector protector, ILoggerFactory logs)
    {
        ArgumentNullException.ThrowIfNull(logs);

        _store = store ?? throw new ArgumentNullException(nameof(store));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _log = logs.CreateLogger("Graticula.Ldap");
    }

    /// <summary>A person found in a directory: their entry, the subject that never changes, and their groups.</summary>
    internal sealed record Found(string Dn, string Subject, string? DisplayName, IReadOnlyList<string> Groups);

    /// <summary>Why this address cannot be used, or null when it can.</summary>
    /// <param name="address">The directory's URL.</param>
    /// <param name="startTls">Whether <c>ldap://</c> is upgraded.</param>
    /// <returns>The refusal.</returns>
    public static string? AddressRefusal(string address, bool startTls) =>
        !Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "ldap" && uri.Scheme != "ldaps")
            ? "A directory's address is ldaps://host or ldap://host, as in ldaps://dc01.contoso.com."
            : uri.Scheme == "ldaps" || startTls || uri.IsLoopback
                ? null
                : "Over plain ldap:// the password typed would cross the network in the clear. Use ldaps://, or turn "
                  + "on StartTLS. Plain ldap:// is accepted only for a directory on this machine.";

    /// <inheritdoc/>
    public async Task<Principal?> SignInAsync(string name, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        foreach (IdentityProvider provider in (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(p => p.Settings is { Kind: "ldap", Enabled: true, Ldap: not null }))
        {
            Found? found;

            try
            {
                string? bindPassword = await SearchPasswordAsync(provider.Id, cancellationToken).ConfigureAwait(false);
                found = await Task.Run(() => Authenticate(provider.Settings, bindPassword, name, password), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is LdapException or DirectoryOperationException or DirectoryException)
            {
                Log.LdapUnreachable(_log, provider.Settings.Name, e.Message);
                continue;
            }

            if (found is null)
            {
                continue;
            }

            string username = Unqualified(name);
            Principal? principal = await _store.FindPrincipalAsync(provider.Id, found.Subject, username, cancellationToken)
                .ConfigureAwait(false);

            if (principal is null && provider.Settings.AutoCreate)
            {
                principal = await _store.CreateMemberAsync(
                        provider.Id, found.Subject, username, OidcEndpoints.AccountName(username), found.DisplayName,
                        provider.Settings.DefaultRole, provider.Settings.DefaultUserType, cancellationToken)
                    .ConfigureAwait(false)
                    ?? await _store.FindPrincipalAsync(provider.Id, found.Subject, username, cancellationToken).ConfigureAwait(false);

                if (principal is not null)
                {
                    Log.OidcAccountMade(_log, principal.Name, username, provider.Settings.Name);
                }
            }

            if (principal is null)
            {
                // The directory knows them and this server has no account for them: a refusal, said as every
                // refused sign-in is said, with no second directory asked on their behalf.
                return null;
            }

            if (!principal.IsDisabled)
            {
                await _store.ApplyMappingsAsync(
                    principal.Id, provider.Id, found.Groups, RoleRank, provider.Settings.DefaultRole, cancellationToken)
                    .ConfigureAwait(false);
            }

            return principal;
        }

        return null;
    }

    /// <summary>Whether a directory can be reached and searched with the account configured — for Check.</summary>
    /// <returns>How many entries the search base holds at its top, or the refusal.</returns>
    public async Task<string?> CheckAsync(IdentityProvider provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        string? bindPassword = await SearchPasswordAsync(provider.Id, cancellationToken).ConfigureAwait(false);

        return await Task.Run(() =>
        {
            using LdapConnection connection = Connect(provider.Settings);
            BindSearcher(connection, provider.Settings, bindPassword);

            SearchResponse response = (SearchResponse)connection.SendRequest(
                new SearchRequest(provider.Settings.Ldap!.UserBase, "(objectClass=*)", SearchScope.Base, "1.1"));

            return response.Entries.Count == 1 ? null : $"The search base {provider.Settings.Ldap.UserBase} was not found.";
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How high a role is, for the highest a person's groups give: the built-in order, a role of the deployment's own below them.</summary>
    internal static int RoleRank(string role) => Roles.All.IndexOf(role);

    private async Task<string?> SearchPasswordAsync(Guid providerId, CancellationToken cancellationToken) =>
        await _store.SecretOfAsync(providerId, cancellationToken).ConfigureAwait(false) is { } sealedSecret
            ? _protector.Unprotect(sealedSecret.Secret, sealedSecret.KeyVersion)
            : null;

    private static Found? Authenticate(IdentityProviderSettings settings, string? bindPassword, string name, string password)
    {
        LdapSettings ldap = settings.Ldap!;
        string display = string.IsNullOrWhiteSpace(ldap.DisplayAttribute) ? "displayName" : ldap.DisplayAttribute;
        string groups = string.IsNullOrWhiteSpace(ldap.GroupAttribute) ? "memberOf" : ldap.GroupAttribute;

        List<string> attributes = [display, groups];
        if (!string.IsNullOrWhiteSpace(ldap.SubjectAttribute))
        {
            attributes.Add(ldap.SubjectAttribute);
        }

        SearchResultEntry entry;

        using (LdapConnection searcher = Connect(settings))
        {
            BindSearcher(searcher, settings, bindPassword);

            SearchResponse response = (SearchResponse)searcher.SendRequest(new SearchRequest(
                ldap.UserBase, ldap.UserFilter.Replace("{0}", Escape(Unqualified(name)), StringComparison.Ordinal),
                SearchScope.Subtree, [.. attributes]));

            // One person, or nobody: two entries for one name is a filter that does not identify, and guessing
            // which of them typed the password is how the wrong account is opened.
            if (response.Entries.Count != 1)
            {
                return null;
            }

            entry = response.Entries[0];
        }

        using (LdapConnection asThem = Connect(settings))
        {
            try
            {
                asThem.Bind(new NetworkCredential(entry.DistinguishedName, password));
            }
            catch (LdapException e) when (e.ErrorCode == 49)
            {
                return null;
            }
        }

        string subject = string.IsNullOrWhiteSpace(ldap.SubjectAttribute)
            ? entry.DistinguishedName
            : Value(entry, ldap.SubjectAttribute) ?? entry.DistinguishedName;

        return new Found(
            entry.DistinguishedName,
            subject,
            Value(entry, display),
            entry.Attributes.Contains(groups)
                ? [.. entry.Attributes[groups].GetValues(typeof(string)).Cast<string>()]
                : []);
    }

    private static LdapConnection Connect(IdentityProviderSettings settings)
    {
        Uri uri = new(settings.Issuer);
        bool secure = uri.Scheme == "ldaps";
        int port = uri.IsDefaultPort || uri.Port <= 0 ? (secure ? 636 : 389) : uri.Port;

        LdapConnection connection = new(new LdapDirectoryIdentifier(uri.Host, port))
        {
            AuthType = AuthType.Basic,
            Timeout = Timeout,
        };

        connection.SessionOptions.ProtocolVersion = 3;

        if (secure)
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }
        else if (settings.Ldap!.StartTls)
        {
            connection.SessionOptions.StartTransportLayerSecurity(null);
        }

        return connection;
    }

    private static void BindSearcher(LdapConnection connection, IdentityProviderSettings settings, string? bindPassword)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            connection.Bind(new NetworkCredential(string.Empty, string.Empty));
        }
        else
        {
            connection.Bind(new NetworkCredential(settings.ClientId, bindPassword ?? string.Empty));
        }
    }

    /// <summary>A value as text: a string as it is, and a binary one — AD's objectGUID — as hex.</summary>
    private static string? Value(SearchResultEntry entry, string attribute)
    {
        if (!entry.Attributes.Contains(attribute) || entry.Attributes[attribute].Count == 0)
        {
            return null;
        }

        object first = entry.Attributes[attribute][0];
        return first is byte[] bytes ? Convert.ToHexString(bytes) : first.ToString();
    }

    /// <summary>
    /// The name without a Windows domain before it — <c>jane</c> of <c>CONTOSO\jane</c> — which is how people type an
    /// Active Directory name and which no filter matches, since the backslash is escaped. Design review 2026-09-24.
    /// </summary>
    internal static string Unqualified(string name)
    {
        string trimmed = name.Trim();
        int slash = trimmed.IndexOf('\\', StringComparison.Ordinal);
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>A name as a filter may hold it — RFC 4515: a typed <c>*</c> or <c>)</c> is a character, not syntax.</summary>
    internal static string Escape(string value)
    {
        StringBuilder escaped = new(value.Length);

        foreach (char c in value)
        {
            escaped.Append(c switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => c.ToString(),
            });
        }

        return escaped.ToString();
    }
}
