using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A service account's API key is an ArcGIS token: it signs the account in on the ArcGIS faces, not on the native
/// administration API, and stops the moment it is revoked — ADR-200.
/// </summary>
public sealed class AnApiKeyOpensWhatItsAccountMayTests : ArcGisClient
{
    private const string Account = "zz_key_machine";
    private static readonly string[] UserRole = ["user"];
    private static readonly string[] AdministratorRole = ["administrator"];

    [Fact]
    public async Task A_key_signs_its_account_in_until_it_is_revoked()
    {
        string root = await RequireServerAsync();

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/service-accounts",
                JsonSerializer.Serialize(new { name = Account, roles = UserRole }));
            Assert.True(made == 201, $"making the account: {made} {madeBody}");

            (int refused, string why) = await AdminAsync(HttpMethod.Post, "/admin/service-accounts",
                JsonSerializer.Serialize(new { name = "zz_key_admin", roles = AdministratorRole }));
            Assert.Equal(400, refused);
            Assert.Contains("never administers", why, StringComparison.Ordinal);

            (int issued, string issuedBody) = await AdminAsync(HttpMethod.Post, $"/admin/service-accounts/{Account}/keys",
                JsonSerializer.Serialize(new { name = "conformance", expiresInDays = 1 }));
            Assert.True(issued == 201, $"issuing a key: {issued} {issuedBody}");
            JsonElement key = JsonDocument.Parse(issuedBody).RootElement;
            string text = key.GetProperty("key").GetString()!;
            Assert.StartsWith("gak_", text, StringComparison.Ordinal);

            // Who the key is, on the ArcGIS side.
            (HttpStatusCode whoStatus, string who) = await AnonymousAsync($"/rest/whoami?token={Uri.EscapeDataString(text)}");
            Assert.Equal(HttpStatusCode.OK, whoStatus);
            JsonElement whoami = JsonDocument.Parse(who).RootElement;
            Assert.Equal(Account, whoami.GetProperty("name").GetString());
            Assert.Equal("Service", whoami.GetProperty("kind").GetString());

            // Not the administration API.
            (HttpStatusCode adminStatus, _) = await AnonymousAsync($"/admin/service-accounts?token={Uri.EscapeDataString(text)}");
            Assert.True(adminStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"the admin API answered {adminStatus}");

            // Listed without its text.
            (int listed, string listBody) = await AdminAsync(HttpMethod.Get, $"/admin/service-accounts/{Account}/keys");
            Assert.Equal(200, listed);
            Assert.DoesNotContain(text, listBody, StringComparison.Ordinal);

            (int revoked, string revokedBody) = await AdminAsync(HttpMethod.Delete,
                $"/admin/service-accounts/{Account}/keys/{key.GetProperty("id").GetString()}");
            Assert.True(revoked == 204, $"revoking: {revoked} {revokedBody}");

            (HttpStatusCode afterStatus, string after) = await AnonymousAsync($"/rest/whoami?token={Uri.EscapeDataString(text)}");
            Assert.False(afterStatus == HttpStatusCode.OK && after.Contains(Account, StringComparison.Ordinal),
                "a revoked key still signed its account in");
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/service-accounts/{Account}");
            await AdminAsync(HttpMethod.Delete, "/admin/service-accounts/zz_key_admin");
        }

        _ = root;
    }
}
