using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-221 condition 3: an organisation's administrator names and describes their organisation, and its portal answers
/// with them; a member may read them and not change them; the organisation's addresses stay the system administrator's.
/// </summary>
[Collection("server settings")]
public sealed class AnOrganisationNamesItselfTests : ArcGisClient
{
    private string _root = string.Empty;

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, string? host = null, string? token = null, object? json = null)
    {
        using HttpRequestMessage request = new(method, $"{_root}{path}");

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = json is null ? null : JsonContent.Create(json);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string> SignedInAsync(string name, string issued, string? host = null)
    {
        (_, JsonElement first) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, json: new { name, password = issued });
        string own = "Own-" + Guid.NewGuid().ToString("N");
        await SendAsync(HttpMethod.Post, "/rest/auth/password", host, first.GetProperty("token").GetString(),
            new { currentPassword = issued, newPassword = own });
        (_, JsonElement again) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, json: new { name, password = own });
        return again.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task An_organisations_administrator_names_and_describes_it_and_its_portal_says_so()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"nm{run}", host = $"nm{run}.localhost";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key, name = $"Named {run}", hosts = new[] { host }, administrator = $"nmadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}.");

        try
        {
            string theirs = await SignedInAsync($"nmadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host);

            (HttpStatusCode read, JsonElement self) = await SendAsync(HttpMethod.Get, "/admin/organization", host, theirs);
            Assert.Equal(HttpStatusCode.OK, read);
            Assert.True(self.GetProperty("mayChange").GetBoolean());

            string name = $"Survey Partners {run}";
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/admin/organization", host, theirs,
                new { name, description = "Field survey joint venture" })).Status);

            // Its portal answers with them.
            (_, JsonElement portal) = await SendAsync(HttpMethod.Get, "/sharing/rest/portals/self?f=json", host, theirs);
            Assert.Equal(name, portal.GetProperty("name").GetString());
            Assert.Equal("Field survey joint venture", portal.GetProperty("description").GetString());

            // Refused an empty name, and refused its addresses, which are the system's.
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/admin/organization", host, theirs,
                new { name = " " })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/admin/tenants/{key}", host, theirs,
                new { hosts = new[] { $"other{run}.localhost" } })).Status);

            // A member reads it and may not change it.
            (_, JsonElement issued) = await SendAsync(HttpMethod.Post, "/admin/members", host, theirs, new { name = $"plain{run}", role = "user" });
            string member = await SignedInAsync($"plain{run}", issued.GetProperty("password").GetString()!, host);
            (HttpStatusCode memberRead, JsonElement seen) = await SendAsync(HttpMethod.Get, "/admin/organization", host, member);
            Assert.Equal(HttpStatusCode.OK, memberRead);
            Assert.False(seen.GetProperty("mayChange").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, "/admin/organization", host, member,
                new { name = "Taken over" })).Status);

            // The system administrator's list shows the new name.
            (_, JsonElement list) = await SendAsync(HttpMethod.Get, "/admin/tenants", token: admin);
            Assert.Contains(list.GetProperty("tenants").EnumerateArray(),
                t => t.GetProperty("key").GetString() == key && t.GetProperty("name").GetString() == name);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);
        }
    }
}
