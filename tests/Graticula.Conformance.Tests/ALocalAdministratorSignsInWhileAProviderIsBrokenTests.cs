using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-015 condition 5's first half: a local administrator still signs in while the configured external
/// identity sources are broken — tested by breaking them, not by reading §5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both kinds that a password or a browser reaches</b>: a directory, which a password sign-in may ask, and an
/// OpenID Connect provider. Each is added while its stand-in is answering, so the server accepts it as it would a
/// real one, and the stand-in is then stopped — the state of a directory that went down after it was configured,
/// which is the one an operator meets at 2 AM. <c>/check</c> is asked first, so the test knows the provider is
/// broken rather than assuming it.
/// </para>
/// <para>
/// <b>The wrong password is the control.</b> A sign-in path that let everybody in would pass the first assertion;
/// it must still refuse the administrator's name with a wrong password.
/// </para>
/// </remarks>
public sealed class ALocalAdministratorSignsInWhileAProviderIsBrokenTests : ArcGisClient
{
    private static HttpClient Plain() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private static async Task<(HttpStatusCode Status, string Body)> LoginAsync(string root, string name, string password)
    {
        using HttpClient http = Plain();
        using StringContent body = new(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.PostAsync(new Uri($"{root}/rest/auth/login"), body);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_local_administrator_signs_in_while_the_directory_and_the_OpenID_provider_are_down()
    {
        string root = await RequireServerAsync();

        string? user = Environment.GetEnvironmentVariable("GRATICULA_TEST_USER");
        string? password = Environment.GetEnvironmentVariable("GRATICULA_TEST_PASSWORD");

        Assert.False(
            string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password),
            "GRATICULA_TEST_USER and GRATICULA_TEST_PASSWORD name the local administrator this test signs in as.");

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string? directoryId = null;
        string? openIdId = null;

        try
        {
            FakeLdapServer ldap = new($"dc=b{suffix}");
            FakeOidcProvider idp = new($"graticula-{suffix}", $"secret-{suffix}");

            using (ldap)
            using (idp)
            {
                (int madeDirectory, string directorySaid) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers",
                    JsonSerializer.Serialize(new
                    {
                        kind = "ldap",
                        name = $"zz Broken directory {suffix}",
                        issuer = ldap.Address,
                        clientId = ldap.SearchDn,
                        clientSecret = ldap.SearchPassword,
                        userBase = $"ou=people,{ldap.BaseDn}",
                        userFilter = "(&(objectClass=person)(uid={0}))",
                        autoCreate = true,
                        defaultRole = "viewer",
                    }));
                Assert.True(madeDirectory == 201, $"Adding the directory answered {madeDirectory}: {directorySaid}");
                directoryId = JsonDocument.Parse(directorySaid).RootElement.GetProperty("id").GetString();

                (int madeOpenId, string openIdSaid) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers",
                    JsonSerializer.Serialize(new
                    {
                        name = $"zz Broken OpenID {suffix}",
                        issuer = idp.Issuer,
                        clientId = idp.ClientId,
                        clientSecret = idp.Secret,
                        autoCreate = true,
                        defaultRole = "viewer",
                    }));
                Assert.True(madeOpenId == 201, $"Adding the OpenID provider answered {madeOpenId}: {openIdSaid}");
                openIdId = JsonDocument.Parse(openIdSaid).RootElement.GetProperty("id").GetString();
            }

            // ---- both are down now, and the server says so ----
            foreach (string id in new[] { directoryId!, openIdId! })
            {
                (int checkedIt, string checkSaid) = await AdminAsync(HttpMethod.Post, $"/admin/identity-providers/{id}/check");

                Assert.True(
                    checkedIt != 200,
                    $"Provider {id} was stopped and its check still answered 200: {checkSaid}. The test would be "
                    + "signing in beside a working provider, which proves nothing.");
            }

            // ---- the local administrator signs in, and the token administers ----
            (HttpStatusCode signedIn, string signedInSaid) = await LoginAsync(root, user!, password!);

            Assert.True(
                signedIn == HttpStatusCode.OK,
                $"The local administrator could not sign in while the providers were down: {(int)signedIn} {signedInSaid}");

            string token = JsonDocument.Parse(signedInSaid).RootElement.GetProperty("token").GetString()!;

            using HttpClient http = Plain();
            using HttpRequestMessage members = new(HttpMethod.Get, new Uri($"{root}/admin/members"));
            members.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage administered = await http.SendAsync(members);

            Assert.True(
                administered.StatusCode == HttpStatusCode.OK,
                $"The token signed in while the providers were down did not administer: {(int)administered.StatusCode}");

            // ---- the control: the same name with a wrong password is still refused ----
            (HttpStatusCode wrong, string wrongSaid) = await LoginAsync(root, user!, password + "-not");

            Assert.True(
                wrong == HttpStatusCode.Unauthorized,
                $"A wrong password for the local administrator answered {(int)wrong}: {wrongSaid}");
        }
        finally
        {
            if (directoryId is not null) await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{directoryId}");
            if (openIdId is not null) await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{openIdId}");
        }
    }
}
