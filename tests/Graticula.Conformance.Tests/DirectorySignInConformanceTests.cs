using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Signing in with a password a directory checks, and a directory's or provider's groups mapped to a role and a group
/// here — ADR-089, against a directory and a provider this suite runs.
/// </summary>
/// <remarks>
/// <b>The owner's four answers are each asserted</b>: groups map to a role and to a group; the mapping is applied at
/// every sign-in, so leaving a group in the directory is leaving it here at the next; a role the mapping gives is not
/// changed by hand; and an OpenID Connect provider's <c>groups</c> claim goes through the same mapping.
/// </remarks>
public sealed class DirectorySignInConformanceTests : ArcGisClient
{
    private static HttpClient Plain() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    /// <summary>Signs in with a name and password on this server's own form; returns the token, or null.</summary>
    private static async Task<(HttpStatusCode Status, string? Token)> PasswordAsync(string root, string name, string password)
    {
        using HttpClient http = Plain();
        using StringContent body = new(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.PostAsync(new Uri($"{root}/rest/auth/login"), body);
        string text = await response.Content.ReadAsStringAsync();

        return (response.StatusCode,
            response.IsSuccessStatusCode ? JsonDocument.Parse(text).RootElement.GetProperty("token").GetString() : null);
    }

    private async Task<JsonElement> MemberAsync(string name)
    {
        (int _, string members) = await AdminAsync(HttpMethod.Get, "/admin/members");
        return JsonDocument.Parse(members).RootElement.GetProperty("members").EnumerateArray()
            .Single(m => string.Equals(m.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)).Clone();
    }

    private async Task<bool> InGroupAsync(string group, string member)
    {
        (int _, string described) = await AdminAsync(HttpMethod.Get, $"/admin/groups/{Uri.EscapeDataString(group)}");
        return JsonDocument.Parse(described).RootElement.GetProperty("members").EnumerateArray()
            .Any(m => string.Equals(m.GetProperty("name").GetString(), member, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_directorys_password_signs_in_and_its_groups_decide_the_role_and_the_groups_at_every_sign_in()
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeLdapServer ldap = new($"dc=t{suffix}");

        string uid = $"carol{suffix}";
        string password = $"pw-{suffix}";
        string dn = ldap.AddPerson(uid, password, "Carol Test",
            $"cn=GIS-Publishers,ou=groups,{ldap.BaseDn}", $"cn=Planners,ou=groups,{ldap.BaseDn}");

        string group = $"zz Planning {suffix}";
        string? providerId = null;

        try
        {
            (int madeGroup, string groupSaid) = await AdminAsync(HttpMethod.Post, "/admin/groups",
                JsonSerializer.Serialize(new { name = group }));
            Assert.True(madeGroup is 200 or 201, $"Making the group answered {madeGroup}: {groupSaid}");

            (int madeProvider, string providerSaid) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers",
                JsonSerializer.Serialize(new
                {
                    kind = "ldap",
                    name = $"zz Directory {suffix}",
                    issuer = ldap.Address,
                    clientId = ldap.SearchDn,
                    clientSecret = ldap.SearchPassword,
                    userBase = $"ou=people,{ldap.BaseDn}",
                    userFilter = "(&(objectClass=person)(uid={0}))",
                    autoCreate = true,
                    defaultRole = "viewer",
                }));
            Assert.True(madeProvider == 201, $"Adding the directory answered {madeProvider}: {providerSaid}");
            providerId = JsonDocument.Parse(providerSaid).RootElement.GetProperty("id").GetString()!;

            (int checkedIt, string checkSaid) = await AdminAsync(HttpMethod.Post, $"/admin/identity-providers/{providerId}/check");
            Assert.True(checkedIt == 200, $"Checking the directory answered {checkedIt}: {checkSaid}");

            (int mapped, string mapSaid) = await AdminAsync(HttpMethod.Put, $"/admin/identity-providers/{providerId}/groups",
                JsonSerializer.Serialize(new
                {
                    mappings = new object[]
                    {
                        new { externalGroup = "GIS-Publishers", role = "publisher" },
                        new { externalGroup = $"cn=Planners,ou=groups,{ldap.BaseDn}", group },
                    },
                }));
            Assert.True(mapped == 200, $"Mapping the groups answered {mapped}: {mapSaid}");

            // ---- a wrong password and an unknown name are refused alike ----
            Assert.Equal(HttpStatusCode.Unauthorized, (await PasswordAsync(root, uid, "wrong")).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PasswordAsync(root, $"nobody{suffix}", password)).Status);

            // ---- the directory's password signs in, and makes the account ----
            (HttpStatusCode first, string? token) = await PasswordAsync(root, uid, password);
            Assert.True(first == HttpStatusCode.OK, $"Signing in with the directory's password answered {(int)first}.");
            Assert.NotNull(token);

            JsonElement member = await MemberAsync(uid);
            Assert.Contains("publisher", member.GetProperty("roles").ToString(), StringComparison.Ordinal);
            Assert.True(member.GetProperty("roleManaged").GetBoolean(), "A role the mapping gave is not marked as the mapping's.");
            Assert.Equal($"zz Directory {suffix}", member.GetProperty("signsInWith").GetString());
            Assert.True(await InGroupAsync(group, uid), "The directory's Planners did not join the group it maps to.");

            // ---- a role the mapping gives is not changed by hand ----
            (int byHand, string byHandSaid) = await AdminAsync(HttpMethod.Put, $"/admin/members/{uid}/role",
                JsonSerializer.Serialize(new { role = "user" }));
            Assert.Equal(409, byHand);
            Assert.Contains("groups", byHandSaid, StringComparison.Ordinal);

            // ---- out of both groups in the directory: at the next sign-in, the default role and out of the group ----
            ldap.SetGroups(dn, $"cn=Somebody-else,ou=groups,{ldap.BaseDn}");

            Assert.Equal(HttpStatusCode.OK, (await PasswordAsync(root, uid, password)).Status);

            member = await MemberAsync(uid);
            Assert.Contains("viewer", member.GetProperty("roles").ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("publisher", member.GetProperty("roles").ToString(), StringComparison.Ordinal);
            Assert.False(await InGroupAsync(group, uid), "Leaving Planners in the directory did not leave the group here.");
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{uid}");
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Uri.EscapeDataString(group)}");
            if (providerId is not null) await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{providerId}");
        }
    }

    /// <summary>
    /// ADR-089 condition 3: a person in <c>Planners</c>, which is itself in <c>All-GIS</c>, is not in <c>All-GIS</c> by
    /// <c>memberOf</c>, which lists direct groups only; with the directory's <c>nestedGroups</c> on, the server asks the
    /// directory for transitive membership — Active Directory's <c>LDAP_MATCHING_RULE_IN_CHAIN</c> — and the mapping
    /// on <c>All-GIS</c> applies at the next sign-in. Off, as before, nothing more is asked.
    /// </summary>
    [Fact]
    public async Task Groups_inside_groups_are_followed_when_the_directory_is_set_to()
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeLdapServer ldap = new($"dc=n{suffix}");

        string uid = $"nadia{suffix}";
        string password = $"pw-{suffix}";
        string planners = $"cn=Planners,ou=groups,{ldap.BaseDn}";
        string dn = ldap.AddPerson(uid, password, "Nadia Nested", planners);
        ldap.AddGroup("Planners", dn);
        string allGis = ldap.AddGroup("All-GIS", planners);

        string group = $"zz All GIS {suffix}";
        string? providerId = null;
        object Settings(bool nested) => new
        {
            kind = "ldap", name = $"zz Nested {suffix}", issuer = ldap.Address, clientId = ldap.SearchDn,
            clientSecret = ldap.SearchPassword, userBase = $"ou=people,{ldap.BaseDn}", userFilter = "(uid={0})",
            autoCreate = true, defaultRole = "viewer", nestedGroups = nested,
        };

        try
        {
            (int madeGroup, string groupSaid) = await AdminAsync(HttpMethod.Post, "/admin/groups", JsonSerializer.Serialize(new { name = group }));
            Assert.True(madeGroup is 200 or 201, $"Making the group answered {madeGroup}: {groupSaid}");

            (int made, string said) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers", JsonSerializer.Serialize(Settings(false)));
            Assert.True(made == 201, $"Adding the directory answered {made}: {said}");
            providerId = JsonDocument.Parse(said).RootElement.GetProperty("id").GetString()!;

            (int mapped, string mapSaid) = await AdminAsync(HttpMethod.Put, $"/admin/identity-providers/{providerId}/groups",
                JsonSerializer.Serialize(new { mappings = new object[] { new { externalGroup = allGis, role = "publisher", group } } }));
            Assert.True(mapped == 200, $"Mapping answered {mapped}: {mapSaid}");

            // ---- off: memberOf alone, so the group around Planners is not reached ----
            Assert.Equal(HttpStatusCode.OK, (await PasswordAsync(root, uid, password)).Status);
            Assert.False(await InGroupAsync(group, uid), "With nested groups off, a group inside a group was followed.");
            Assert.Contains("viewer", (await MemberAsync(uid)).GetProperty("roles").ToString(), StringComparison.Ordinal);
            Assert.Equal(0, ldap.ChainSearches);

            // ---- on: the directory is asked for the chain, and All-GIS's mapping applies ----
            (int changed, string changeSaid) = await AdminAsync(HttpMethod.Put, $"/admin/identity-providers/{providerId}",
                JsonSerializer.Serialize(Settings(true)));
            Assert.True(changed == 200, $"Turning nested groups on answered {changed}: {changeSaid}");
            Assert.True(JsonDocument.Parse(changeSaid).RootElement.GetProperty("nestedGroups").GetBoolean());

            Assert.Equal(HttpStatusCode.OK, (await PasswordAsync(root, uid, password)).Status);
            Assert.True(await InGroupAsync(group, uid), "With nested groups on, All-GIS around Planners did not join its group here.");
            Assert.Contains("publisher", (await MemberAsync(uid)).GetProperty("roles").ToString(), StringComparison.Ordinal);
            Assert.True(ldap.ChainSearches > 0, "The directory was never asked for transitive membership.");

            // ---- a wrong password asks nothing more of it ----
            int before = ldap.ChainSearches;
            Assert.Equal(HttpStatusCode.Unauthorized, (await PasswordAsync(root, uid, "wrong")).Status);
            Assert.Equal(before, ldap.ChainSearches);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{uid}");
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Uri.EscapeDataString(group)}");
            if (providerId is not null) await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{providerId}");
        }
    }

    /// <summary>
    /// ADR-089 condition 1: a directory over <c>ldaps://</c> whose certificate comes from an authority nobody trusts —
    /// one made for this run, as a domain's own CA is to a server outside the domain — is refused until that authority
    /// is given to the server as the directory's certificate to trust, and then checks and signs somebody in. Taking it
    /// away refuses it again: nothing but the certificate decides.
    /// </summary>
    [Fact]
    public async Task A_directory_over_ldaps_is_trusted_by_the_authority_it_is_given_and_by_nothing_else()
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeLdapServer ldap = new($"dc=s{suffix}", tls: true);

        string uid = $"tess{suffix}";
        string password = $"pw-{suffix}";
        ldap.AddPerson(uid, password, "Tess Tls");
        string? providerId = null;
        object Settings(string trusted) => new
        {
            kind = "ldap", name = $"zz Ldaps {suffix}", issuer = ldap.Address, clientId = ldap.SearchDn,
            clientSecret = ldap.SearchPassword, userBase = $"ou=people,{ldap.BaseDn}", userFilter = "(uid={0})",
            autoCreate = true, defaultRole = "viewer", trustedCertificate = trusted,
        };

        try
        {
            Assert.StartsWith("ldaps://", ldap.Address, StringComparison.Ordinal);

            // ---- a certificate that is not one is refused before it is stored ----
            (int bad, string badSaid) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers",
                JsonSerializer.Serialize(Settings("not a certificate")));
            Assert.True(bad == 400, $"A certificate that is not one answered {bad}: {badSaid}");

            // ---- without the authority: the handshake is refused, so the check fails and nobody signs in ----
            (int made, string said) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers", JsonSerializer.Serialize(Settings("")));
            Assert.True(made == 201, $"Adding the directory answered {made}: {said}");
            providerId = JsonDocument.Parse(said).RootElement.GetProperty("id").GetString()!;

            (int untrusted, string untrustedSaid) = await AdminAsync(HttpMethod.Post, $"/admin/identity-providers/{providerId}/check");
            Assert.True(untrusted != 200, $"A directory whose certificate nothing trusts was checked as fine: {untrustedSaid}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await PasswordAsync(root, uid, password)).Status);
            Assert.Equal(0, ldap.PersonBinds);
            Assert.True(ldap.RefusedHandshakes > 0, "The refusal was not the TLS handshake's.");

            // ---- given the authority: the check passes and the person signs in over TLS ----
            (int trusted, string trustedSaid) = await AdminAsync(HttpMethod.Put, $"/admin/identity-providers/{providerId}",
                JsonSerializer.Serialize(Settings(ldap.Tls!)));
            Assert.True(trusted == 200, $"Giving the authority answered {trusted}: {trustedSaid}");
            Assert.Contains("Graticula test directory CA",
                JsonDocument.Parse(trustedSaid).RootElement.GetProperty("trustedCertificate").GetProperty("subject").GetString(),
                StringComparison.Ordinal);

            (int checkedIt, string checkSaid) = await AdminAsync(HttpMethod.Post, $"/admin/identity-providers/{providerId}/check");
            Assert.True(checkedIt == 200, $"Checking the directory with its authority given answered {checkedIt}: {checkSaid}");

            (HttpStatusCode signed, string? token) = await PasswordAsync(root, uid, password);
            Assert.True(signed == HttpStatusCode.OK, $"Signing in over ldaps:// answered {(int)signed}.");
            Assert.NotNull(token);
            Assert.Equal(1, ldap.PersonBinds);

            // ---- a form that does not send it keeps it ----
            (int kept, string keptSaid) = await AdminAsync(HttpMethod.Put, $"/admin/identity-providers/{providerId}",
                JsonSerializer.Serialize(new
                {
                    kind = "ldap", name = $"zz Ldaps {suffix}", issuer = ldap.Address, clientId = ldap.SearchDn,
                    userBase = $"ou=people,{ldap.BaseDn}", userFilter = "(uid={0})", autoCreate = true, defaultRole = "viewer",
                }));
            Assert.True(kept == 200, $"Saving without the certificate answered {kept}: {keptSaid}");
            Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(keptSaid).RootElement.GetProperty("trustedCertificate").ValueKind);

            // ---- taken away: refused again ----
            (int removed, string removedSaid) = await AdminAsync(HttpMethod.Put, $"/admin/identity-providers/{providerId}",
                JsonSerializer.Serialize(Settings("")));
            Assert.True(removed == 200, $"Removing the authority answered {removed}: {removedSaid}");
            Assert.True((await AdminAsync(HttpMethod.Post, $"/admin/identity-providers/{providerId}/check")).Item1 != 200,
                "With the authority taken away the directory was still trusted.");
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{uid}");
            if (providerId is not null) await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{providerId}");
        }
    }

    [Fact]
    public async Task An_account_with_a_password_here_is_never_asked_of_the_directory()
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeLdapServer ldap = new($"dc=u{suffix}");

        string name = $"dave{suffix}";
        string? providerId = null;

        try
        {
            // A local account, and a directory person of the same name with another password.
            (int made, string said) = await AdminAsync(HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name, role = "viewer" }));
            Assert.True(made == 201, $"Making the local account answered {made}: {said}");

            ldap.AddPerson(name, "directory-password", "Dave Directory");

            (int _, string providerSaid) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers",
                JsonSerializer.Serialize(new
                {
                    kind = "ldap", name = $"zz Directory {suffix}", issuer = ldap.Address, clientId = ldap.SearchDn,
                    clientSecret = ldap.SearchPassword, userBase = $"ou=people,{ldap.BaseDn}", userFilter = "(uid={0})",
                    autoCreate = true,
                }));
            providerId = JsonDocument.Parse(providerSaid).RootElement.GetProperty("id").GetString();

            Assert.Equal(HttpStatusCode.Unauthorized, (await PasswordAsync(root, name, "directory-password")).Status);
            Assert.Equal(0, ldap.PersonBinds);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{name}");
            if (providerId is not null) await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{providerId}");
        }
    }

    [Fact]
    public async Task An_openid_providers_groups_claim_goes_through_the_same_mapping()
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeOidcProvider idp = new($"graticula-{suffix}", $"secret-{suffix}");

        string? made = null;

        (int _, string providerSaid) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers",
            JsonSerializer.Serialize(new
            {
                name = $"zz Staff {suffix}", issuer = idp.Issuer, clientId = idp.ClientId, clientSecret = idp.Secret,
                autoCreate = true, defaultRole = "viewer",
            }));
        string id = JsonDocument.Parse(providerSaid).RootElement.GetProperty("id").GetString()!;

        try
        {
            (int mapped, string mapSaid) = await AdminAsync(HttpMethod.Put, $"/admin/identity-providers/{id}/groups",
                JsonSerializer.Serialize(new { mappings = new object[] { new { externalGroup = "gis-editors", role = "data_editor" } } }));
            Assert.True(mapped == 200, $"Mapping answered {mapped}: {mapSaid}");

            idp.Next = ($"sub-{suffix}", $"erin{suffix}@example.org");
            idp.NextGroups = ["everyone", "gis-editors"];

            // Walked a redirect at a time, as a browser does: HttpClient does not follow HTTPS to the provider's HTTP.
            OidcSignInConformanceTests.Outcome outcome = await OidcSignInConformanceTests.SignInAsync(root, id);
            made = outcome.Name;
            Assert.True(made is not null, $"The sign-in did not complete ({(int)outcome.Callback}): {outcome.Body}");

            JsonElement member = await MemberAsync(made!);
            Assert.Contains("data_editor", member.GetProperty("roles").ToString(), StringComparison.Ordinal);
            Assert.True(member.GetProperty("roleManaged").GetBoolean());
        }
        finally
        {
            if (made is not null) await AdminAsync(HttpMethod.Delete, $"/admin/members/{made}");
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }
}
