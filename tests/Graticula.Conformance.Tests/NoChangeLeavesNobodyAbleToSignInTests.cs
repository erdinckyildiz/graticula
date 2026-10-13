using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-015 condition 5's second half, over HTTP: §5b's invariant — no change to who administers leaves nobody able
/// to sign in when the provider cannot — refused by default, done when the request names the consequence.
/// </summary>
/// <remarks>
/// <para>
/// <b>An administrator who signs in through a provider is the caller the half that was untested needs</b>, and the
/// suite's own directory makes one: a person in a directory group mapped to <c>administrator</c> signs in with the
/// directory's password. Before 2026-10-13 neither the fixture nor the suites could sign one in, which is why the
/// removal and provider refusals were only ever walked by hand — and the walk disabled the fixture's administrator.
/// </para>
/// <para>
/// <b>The suite's administrator is the last local one for the length of the test</b>, because that is the only state
/// the refusals answer. Any other enabled local administrator the fixture has is disabled first and enabled again at
/// the end; nothing is demoted or removed. The one change the test makes on purpose — disabling the suite's
/// administrator with <c>leaveNoLocalAdministrator=true</c> — is made by the directory administrator, who enables it
/// again; disabling keeps the role, so putting it back is one request. If that request never runs,
/// <c>graticula create-admin</c> recovers the store (ADR-015 §5b, <c>AdminCreatorTests</c>), and nothing needs SQL.
/// </para>
/// <para>
/// <b>Every refusal is read for its marker</b>, <c>lastLocalAdministrator</c>, not only its status: the older
/// last-administrator refusal is also a 409, and a test that read the status alone would pass on it.
/// </para>
/// <para>
/// <b>In the collection that runs alone</b>, because for a few requests the token every other test uses is refused:
/// the suite's administrator is disabled, and so is any local administrator set aside.
/// </para>
/// </remarks>
[Collection("server settings")]
public sealed class NoChangeLeavesNobodyAbleToSignInTests : ArcGisClient
{
    private const string Marker = "lastLocalAdministrator";

    private static HttpClient Plain() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    /// <summary>A request with another account's token — the directory administrator's.</summary>
    private static async Task<(int Status, string Body)> AsAsync(
        string root, string token, HttpMethod method, string path, string? json = null)
    {
        using HttpClient http = Plain();
        using HttpRequestMessage request = new(method, new Uri($"{root}{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement[]> MembersAsync()
    {
        (int listed, string members) = await AdminAsync(HttpMethod.Get, "/admin/members");
        Assert.True(listed == 200, $"Listing the members answered {listed}: {members}");

        return [.. JsonDocument.Parse(members).RootElement.GetProperty("members").EnumerateArray().Select(m => m.Clone())];
    }

    private static bool Administers(JsonElement member) =>
        member.GetProperty("roles").EnumerateArray().Any(r => r.GetString() == "administrator");

    private static void Refused(int status, string body, string what)
    {
        Assert.True(status == 409, $"{what} answered {status}, not 409: {body}");
        Assert.True(
            body.Contains(Marker, StringComparison.Ordinal),
            $"{what} was refused, but not as the last local administrator — the marker is missing: {body}");
    }

    [Fact]
    public async Task The_last_local_administrator_is_kept_unless_the_request_says_so_and_the_provider_everybody_uses_is_kept_on()
    {
        string root = await RequireServerAsync();
        string? user = Environment.GetEnvironmentVariable(UserVariable);

        Assert.False(string.IsNullOrEmpty(user), $"{UserVariable} names the local administrator this test keeps.");

        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeLdapServer ldap = new($"dc=k{suffix}");

        string uid = $"erin{suffix}";
        string password = $"pw-{suffix}";
        ldap.AddPerson(uid, password, "Erin Directory", $"cn=GIS-Admins,ou=groups,{ldap.BaseDn}");

        string providerName = $"zz Admin directory {suffix}";
        object providerBody = new
        {
            kind = "ldap",
            name = providerName,
            issuer = ldap.Address,
            clientId = ldap.SearchDn,
            clientSecret = ldap.SearchPassword,
            userBase = $"ou=people,{ldap.BaseDn}",
            userFilter = "(&(objectClass=person)(uid={0}))",
            autoCreate = true,
            defaultRole = "viewer",
        };

        string? providerId = null;
        string? directoryToken = null;
        List<string> setAside = [];

        try
        {
            // ---- an administrator who signs in through a directory ----
            (int made, string madeSaid) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers", JsonSerializer.Serialize(providerBody));
            Assert.True(made == 201, $"Adding the directory answered {made}: {madeSaid}");
            providerId = JsonDocument.Parse(madeSaid).RootElement.GetProperty("id").GetString()!;

            (int mapped, string mapSaid) = await AdminAsync(HttpMethod.Put, $"/admin/identity-providers/{providerId}/groups",
                JsonSerializer.Serialize(new { mappings = new object[] { new { externalGroup = "GIS-Admins", role = "administrator" } } }));
            Assert.True(mapped == 200, $"Mapping the directory's group to administrator answered {mapped}: {mapSaid}");

            using (HttpClient http = Plain())
            using (StringContent body = new(JsonSerializer.Serialize(new { name = uid, password }), Encoding.UTF8, "application/json"))
            using (HttpResponseMessage signedIn = await http.PostAsync(new Uri($"{root}/rest/auth/login"), body))
            {
                string said = await signedIn.Content.ReadAsStringAsync();
                Assert.True(signedIn.IsSuccessStatusCode, $"The directory administrator could not sign in: {(int)signedIn.StatusCode} {said}");
                directoryToken = JsonDocument.Parse(said).RootElement.GetProperty("token").GetString()!;
            }

            JsonElement erin = (await MembersAsync()).Single(m => string.Equals(m.GetProperty("name").GetString(), uid, StringComparison.OrdinalIgnoreCase));
            Assert.True(Administers(erin), $"The directory's group did not make an administrator: {erin}");
            Assert.Equal(providerName, erin.GetProperty("signsInWith").GetString());

            // ---- the suite's administrator is the last local one ----
            foreach (JsonElement other in await MembersAsync())
            {
                string name = other.GetProperty("name").GetString()!;

                if (Administers(other)
                    && other.GetProperty("signsInWith").ValueKind == JsonValueKind.Null
                    && !other.GetProperty("disabled").GetBoolean()
                    && !string.Equals(name, user, StringComparison.OrdinalIgnoreCase))
                {
                    (int aside, string asideSaid) = await AdminAsync(HttpMethod.Post, $"/admin/members/{Uri.EscapeDataString(name)}/disable");
                    Assert.True(aside == 200, $"Setting aside the local administrator {name} answered {aside}: {asideSaid}");
                    setAside.Add(name);
                }
            }

            string self = Uri.EscapeDataString(user!);

            // ---- refused without the flag: demoting, disabling, removing — the last two by either administrator ----
            (int demoted, string demotedSaid) = await AdminAsync(HttpMethod.Put, $"/admin/members/{self}/role",
                JsonSerializer.Serialize(new { role = "viewer" }));
            Refused(demoted, demotedSaid, "Demoting the last local administrator");

            (int disabled, string disabledSaid) = await AdminAsync(HttpMethod.Post, $"/admin/members/{self}/disable");
            Refused(disabled, disabledSaid, "Disabling the last local administrator");

            (int disabledBy, string disabledBySaid) = await AsAsync(root, directoryToken, HttpMethod.Post, $"/admin/members/{self}/disable");
            Refused(disabledBy, disabledBySaid, "Disabling the last local administrator, asked by the directory administrator");

            (int removed, string removedSaid) = await AsAsync(root, directoryToken, HttpMethod.Delete, $"/admin/members/{self}");
            Refused(removed, removedSaid, "Removing the last local administrator, asked by the directory administrator");

            JsonElement still = (await MembersAsync()).Single(m => string.Equals(m.GetProperty("name").GetString(), user, StringComparison.OrdinalIgnoreCase));
            Assert.True(Administers(still) && !still.GetProperty("disabled").GetBoolean(), $"A refused change changed something: {still}");

            // ---- done with the flag: the directory administrator disables the suite's, and nobody has a password ----
            (int flagged, string flaggedSaid) = await AsAsync(root, directoryToken, HttpMethod.Post,
                $"/admin/members/{self}/disable?leaveNoLocalAdministrator=true");
            Assert.True(flagged == 200, $"Disabling with leaveNoLocalAdministrator=true answered {flagged}: {flaggedSaid}");

            // ---- the provider every administrator signs in through: off is refused outright, any change needs the flag ----
            string turnedOff = JsonSerializer.Serialize(new Dictionary<string, object?>(Fields(providerBody)) { ["enabled"] = false });
            string unchanged = JsonSerializer.Serialize(providerBody);

            (int off, string offSaid) = await AsAsync(root, directoryToken, HttpMethod.Put, $"/admin/identity-providers/{providerId}", turnedOff);
            Assert.True(off == 409, $"Turning off the provider every administrator signs in through answered {off}: {offSaid}");

            (int offFlagged, string offFlaggedSaid) = await AsAsync(root, directoryToken, HttpMethod.Put,
                $"/admin/identity-providers/{providerId}?leaveNoLocalAdministrator=true", turnedOff);
            Assert.True(offFlagged == 409, $"Turning it off is refused whatever the request says, and it answered {offFlagged}: {offFlaggedSaid}");

            (int changed, string changedSaid) = await AsAsync(root, directoryToken, HttpMethod.Put, $"/admin/identity-providers/{providerId}", unchanged);
            Refused(changed, changedSaid, "Changing the provider every administrator signs in through");

            (int changedFlagged, string changedFlaggedSaid) = await AsAsync(root, directoryToken, HttpMethod.Put,
                $"/admin/identity-providers/{providerId}?leaveNoLocalAdministrator=true", unchanged);
            Assert.True(changedFlagged == 200, $"Changing it with leaveNoLocalAdministrator=true answered {changedFlagged}: {changedFlaggedSaid}");

            (int gone, string goneSaid) = await AsAsync(root, directoryToken, HttpMethod.Delete, $"/admin/identity-providers/{providerId}");
            Assert.True(gone == 409, $"Removing the provider an administrator signs in with answered {gone}: {goneSaid}");

            // ---- put back: the suite's administrator is enabled, and its token administers again ----
            (int back, string backSaid) = await AsAsync(root, directoryToken, HttpMethod.Post, $"/admin/members/{self}/enable");
            Assert.True(back == 200, $"Enabling the suite's administrator again answered {back}: {backSaid}");

            (int administers, string administersSaid) = await AdminAsync(HttpMethod.Get, "/admin/members");
            Assert.True(administers == 200, $"The suite's token did not administer after it was enabled again: {administers} {administersSaid}");
        }
        finally
        {
            // Whatever failed, and whether or not a refusal held: the suite's administrator is enabled and an
            // administrator again, by the one account that can still say so. Both are no-ops when nothing changed.
            if (directoryToken is not null)
            {
                string self = Uri.EscapeDataString(user!);
                await AsAsync(root, directoryToken, HttpMethod.Post, $"/admin/members/{self}/enable");
                await AsAsync(root, directoryToken, HttpMethod.Put, $"/admin/members/{self}/role",
                    JsonSerializer.Serialize(new { role = "administrator" }));
            }

            foreach (string name in setAside)
            {
                await AdminAsync(HttpMethod.Post, $"/admin/members/{Uri.EscapeDataString(name)}/enable");
            }

            await AdminAsync(HttpMethod.Delete, $"/admin/members/{uid}");
            if (providerId is not null) await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{providerId}");
        }

        (int afterward, string afterwardSaid) = await AdminAsync(HttpMethod.Get, "/admin/members");
        Assert.True(afterward == 200, $"After the test the suite's administrator does not administer: {afterward} {afterwardSaid}");
    }

    /// <summary>An anonymous object's properties, by name, so one field can be changed.</summary>
    private static IEnumerable<KeyValuePair<string, object?>> Fields(object body) =>
        body.GetType().GetProperties().Select(p => new KeyValuePair<string, object?>(p.Name, p.GetValue(body)));
}
