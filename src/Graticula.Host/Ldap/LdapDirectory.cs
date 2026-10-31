using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
/// <para>
/// <b>A domain's own certificate authority is trusted where the operator gives it</b> (ADR-089 condition 1): a
/// directory's <c>TrustedCertificate</c> is checked besides the machine's store — by a chain built here on Windows,
/// whose LDAP client hands the server's certificate to a callback, and on Linux by a directory OpenLDAP is told to read
/// for that connection, written under the state directory and named by the certificate's hash.
/// </para>
/// <para>
/// <b>Groups inside groups</b> (ADR-089 condition 3) are one more search when a directory asks for them: Active
/// Directory's <c>LDAP_MATCHING_RULE_IN_CHAIN</c>, made with the search account after the person's own bind has
/// succeeded, so a wrong password costs the directory nothing more than before.
/// </para>
/// </remarks>
internal sealed class LdapDirectory : IDirectorySignIn
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Active Directory's transitive-membership rule, <c>LDAP_MATCHING_RULE_IN_CHAIN</c>.</summary>
    internal const string InChain = "1.2.840.113556.1.4.1941";

    /// <summary>The most nested groups read for one person: past it the search is cut short, not refused.</summary>
    private const int MostGroups = 1000;

    private readonly IIdentityProviderStore _store;
    private readonly SecretProtector _protector;
    private readonly ILogger _log;
    private readonly string _trustRoot;

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
    public LdapDirectory(IIdentityProviderStore store, SecretProtector protector, ILoggerFactory logs, HostSettings host)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(host);

        _trustRoot = Path.Combine(host.StatePath, "ldap-trust");

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
                found = await Task.Run(() => Authenticate(provider.Settings, bindPassword, name, password, _trustRoot), cancellationToken)
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
            using LdapConnection connection = Connect(provider.Settings, _trustRoot);
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

    private static Found? Authenticate(
        IdentityProviderSettings settings, string? bindPassword, string name, string password, string trustRoot)
    {
        LdapSettings ldap = settings.Ldap!;
        string display = string.IsNullOrWhiteSpace(ldap.DisplayAttribute) ? "displayName" : ldap.DisplayAttribute;
        string groups = string.IsNullOrWhiteSpace(ldap.GroupAttribute) ? "memberOf" : ldap.GroupAttribute;

        List<string> attributes = [display, groups];
        if (!string.IsNullOrWhiteSpace(ldap.SubjectAttribute))
        {
            attributes.Add(ldap.SubjectAttribute);
        }

        using LdapConnection searcher = Connect(settings, trustRoot);
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

        SearchResultEntry entry = response.Entries[0];

        using (LdapConnection asThem = Connect(settings, trustRoot))
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

        List<string> memberOf = entry.Attributes.Contains(groups)
            ? [.. entry.Attributes[groups].GetValues(typeof(string)).Cast<string>()]
            : [];

        if (ldap.NestedGroups)
        {
            memberOf = [.. memberOf.Union(NestedGroupsOf(searcher, ldap, entry.DistinguishedName), StringComparer.OrdinalIgnoreCase)];
        }

        return new Found(entry.DistinguishedName, subject, Value(entry, display), memberOf);
    }

    /// <summary>
    /// Every group a person is in, directly or through other groups — ADR-089 condition 3: one search with Active
    /// Directory's <c>LDAP_MATCHING_RULE_IN_CHAIN</c> on <c>member</c>, which the directory walks itself, so a chain
    /// of any depth costs one round trip and a cycle is the directory's to stop.
    /// </summary>
    private static List<string> NestedGroupsOf(LdapConnection searcher, LdapSettings ldap, string personDn)
    {
        SearchRequest request = new(
            GroupBaseOf(ldap), $"(member:{InChain}:={Escape(personDn)})", SearchScope.Subtree, "1.1")
        {
            SizeLimit = MostGroups,
        };

        try
        {
            return [.. ((SearchResponse)searcher.SendRequest(request)).Entries.Cast<SearchResultEntry>().Select(e => e.DistinguishedName)];
        }
        catch (DirectoryOperationException e) when (e.Response is SearchResponse partial
            && e.Response.ResultCode == ResultCode.SizeLimitExceeded)
        {
            return [.. partial.Entries.Cast<SearchResultEntry>().Select(g => g.DistinguishedName)];
        }
    }

    /// <summary>Where groups are looked for: the base given, else the domain the user base is in.</summary>
    internal static string GroupBaseOf(LdapSettings ldap)
    {
        if (!string.IsNullOrWhiteSpace(ldap.GroupBase))
        {
            return ldap.GroupBase.Trim();
        }

        string[] domain = [.. ldap.UserBase.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.StartsWith("dc=", StringComparison.OrdinalIgnoreCase))];

        return domain.Length > 0 ? string.Join(",", domain) : ldap.UserBase;
    }

    /// <summary>Why a certificate given to trust cannot be used, or null when it can — for the admin surface.</summary>
    /// <param name="pem">The PEM given.</param>
    /// <returns>The refusal.</returns>
    public static string? CertificateRefusal(string pem)
    {
        try
        {
            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);
            return certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow
                ? $"The certificate {certificate.Subject} expired on {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}."
                : null;
        }
        catch (CryptographicException)
        {
            return "The certificate to trust is one certificate in PEM: -----BEGIN CERTIFICATE----- … -----END CERTIFICATE-----.";
        }
    }

    /// <summary>What an operator reads about a certificate given to trust: whose, and until when.</summary>
    /// <param name="pem">The PEM held, or empty.</param>
    /// <returns>Its subject, expiry and PEM, or null.</returns>
    public static object? DescribeCertificate(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        try
        {
            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);
            return new { subject = certificate.Subject, notAfter = certificate.NotAfter.ToUniversalTime(), pem };
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static LdapConnection Connect(IdentityProviderSettings settings, string trustRoot)
    {
        Uri uri = new(settings.Issuer);
        bool secure = uri.Scheme == "ldaps";
        int port = uri.IsDefaultPort || uri.Port <= 0 ? (secure ? 636 : 389) : uri.Port;
        LdapSettings ldap = settings.Ldap!;

        LdapConnection connection = new(new LdapDirectoryIdentifier(uri.Host, port))
        {
            AuthType = AuthType.Basic,
            Timeout = Timeout,
        };

        connection.SessionOptions.ProtocolVersion = 3;

        if ((secure || ldap.StartTls) && !string.IsNullOrWhiteSpace(ldap.TrustedCertificate))
        {
            Trust(connection, ldap.TrustedCertificate, uri.Host, trustRoot);
        }

        if (secure)
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }
        else if (ldap.StartTls)
        {
            connection.SessionOptions.StartTransportLayerSecurity(null);
        }

        return connection;
    }

    /// <summary>
    /// Makes a connection trust the directory's certificate when it chains to the authority given, as well as when the
    /// machine trusts it — before the TLS handshake, which <c>SecureSocketLayer</c> or StartTLS begins.
    /// </summary>
    private static void Trust(LdapConnection connection, string pem, string host, string trustRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            X509Certificate2 authority = X509Certificate2.CreateFromPem(pem);
            connection.SessionOptions.VerifyServerCertificate = (_, presented) => Verifies(presented, authority, host);
        }
        else
        {
            TrustOnUnix(connection, pem, trustRoot);
        }
    }

    /// <summary>
    /// Whether a directory's certificate is one to talk to: it names the host the directory was configured by, and it
    /// chains, for a server and with no revocation looked up, to the authority given or to the machine's own roots.
    /// Windows' LDAP client asks this instead of its own check once a callback is set, so the host is checked here.
    /// </summary>
    internal static bool Verifies(X509Certificate presented, X509Certificate2 authority, string host)
    {
        ArgumentNullException.ThrowIfNull(presented);
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());

        if (!certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: true))
        {
            return false;
        }

        foreach (bool custom in new[] { true, false })
        {
            using X509Chain chain = new();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));

            if (custom)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(authority);
            }

            if (chain.Build(certificate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// OpenLDAP is told to read a directory of trusted certificates for this connection and to start a new TLS context
    /// with it. The directory is written once per certificate, named by its SHA-256, holding the PEM as <c>ca.pem</c>
    /// — GnuTLS, which Debian's and Ubuntu's libldap are built with and this image's is, reads every file there.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static void TrustOnUnix(LdapConnection connection, string pem, string trustRoot)
    {
        string directory = Path.Combine(trustRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pem.Trim())))[..16]);
        string file = Path.Combine(directory, "ca.pem");

        if (!File.Exists(file))
        {
            Directory.CreateDirectory(directory);
            string written = file + "." + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(written, pem.Trim() + "\n");
            File.Move(written, file, overwrite: true);
        }

        // <b>And under OpenSSL's name for it</b> — found 2026-10-31 when CI's Ubuntu runner refused the authority the
        // Windows build had trusted: that libldap is built with OpenSSL, whose trust directory is looked up by the
        // subject's hash (`<hash>.0`, as `c_rehash` names it) and never read file by file as GnuTLS reads it.
        string hashed = Path.Combine(directory, SubjectHash(pem) + ".0");

        if (!File.Exists(hashed))
        {
            string written = hashed + "." + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(written, pem.Trim() + "\n");
            File.Move(written, hashed, overwrite: true);
        }

        connection.SessionOptions.TrustedCertificatesDirectory = directory;
        connection.SessionOptions.StartNewTlsSessionContext();
    }

    /// <summary>
    /// OpenSSL's subject hash of a PEM certificate (<c>X509_NAME_hash</c>, what <c>openssl x509 -hash</c> prints): the
    /// first four bytes, little-endian, of the SHA-1 of the subject's canonical encoding — every RDN's set re-encoded
    /// with its string values as UTF8String, trimmed, inner whitespace collapsed to one space and ASCII lowercased,
    /// concatenated without the outer sequence.
    /// </summary>
    internal static string SubjectHash(string pem)
    {
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem.Trim());
        System.Formats.Asn1.AsnReader name = new(certificate.SubjectName.RawData, System.Formats.Asn1.AsnEncodingRules.DER);
        System.Formats.Asn1.AsnReader rdns = name.ReadSequence();
        using MemoryStream canonical = new();

        while (rdns.HasData)
        {
            System.Formats.Asn1.AsnReader set = rdns.ReadSetOf();
            System.Formats.Asn1.AsnWriter writer = new(System.Formats.Asn1.AsnEncodingRules.DER);

            using (writer.PushSetOf())
            {
                while (set.HasData)
                {
                    System.Formats.Asn1.AsnReader attribute = set.ReadSequence();
                    string oid = attribute.ReadObjectIdentifier();
                    System.Formats.Asn1.Asn1Tag tag = attribute.PeekTag();

                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier(oid);

                        if (tag.TagClass == System.Formats.Asn1.TagClass.Universal && IsString((System.Formats.Asn1.UniversalTagNumber)tag.TagValue))
                        {
                            string value = attribute.ReadCharacterString((System.Formats.Asn1.UniversalTagNumber)tag.TagValue);
                            writer.WriteCharacterString(System.Formats.Asn1.UniversalTagNumber.UTF8String, Canonical(value));
                        }
                        else
                        {
                            writer.WriteEncodedValue(attribute.ReadEncodedValue().Span);
                        }
                    }
                }
            }

            canonical.Write(writer.Encode());
        }

#pragma warning disable CA5350 // A file name OpenSSL defines as SHA-1's, not a security check.
        byte[] digest = SHA1.HashData(canonical.ToArray());
#pragma warning restore CA5350
        uint hash = digest[0] | ((uint)digest[1] << 8) | ((uint)digest[2] << 16) | ((uint)digest[3] << 24);
        return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

        static bool IsString(System.Formats.Asn1.UniversalTagNumber number) => number is
            System.Formats.Asn1.UniversalTagNumber.UTF8String or System.Formats.Asn1.UniversalTagNumber.PrintableString
            or System.Formats.Asn1.UniversalTagNumber.IA5String or System.Formats.Asn1.UniversalTagNumber.T61String
            or System.Formats.Asn1.UniversalTagNumber.BMPString or System.Formats.Asn1.UniversalTagNumber.UniversalString
            or System.Formats.Asn1.UniversalTagNumber.VisibleString;

        static string Canonical(string value)
        {
            StringBuilder text = new(value.Length);
            bool space = false;

            foreach (char c in value.Trim(' ', '\t', '\n', '\r', '\f', '\v'))
            {
                if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
                {
                    space = true;
                    continue;
                }

                if (space)
                {
                    text.Append(' ');
                    space = false;
                }

                text.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
            }

            return text.ToString();
        }
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
