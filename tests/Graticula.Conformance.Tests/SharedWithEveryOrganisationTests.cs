using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-220: an item shared with every organisation on the server is read by any organisation's signed-in member — at
/// its own organisation's host, as an outsider — and by nobody who has not signed in; it is found from home by the
/// search beyond one's own organisation; ArcGIS clients see <c>org</c>; and sharing at it takes its own privilege.
/// </summary>
[Collection("server settings")]
public sealed class SharedWithEveryOrganisationTests : ArcGisClient
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
    public async Task An_item_shared_with_every_organisation_is_read_by_their_members_and_by_nobody_anonymous()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"ao{run}", host = $"ao{run}.localhost";
        string everyone = $"ao_every_{run}", own = $"ao_own_{run}", user = $"zzaouser{run}";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key, name = $"All orgs {run}", hosts = new[] { host }, administrator = $"aoadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}.");

        try
        {
            string other = await SignedInAsync($"aoadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host);

            foreach (string name in new[] { everyone, own })
            {
                (HttpStatusCode defined, _) = await SendAsync(HttpMethod.Post, "/admin/hosted/define", host, other, new
                {
                    name, geometryType = "Point", srid = 4326, fields = new[] { new { name = "label", type = "text", nullable = true, length = 20 } },
                });
                Assert.Equal(HttpStatusCode.Created, defined);
            }

            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/services/{everyone}/sharing?folder=hosted", host, other,
                new { sharing = "allorganizations" })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/services/{own}/sharing?folder=hosted", host, other,
                new { sharing = "organization" })).Status);

            // A plain member of the default organisation, at the other's host: reads the first, not the second.
            (_, JsonElement issued) = await SendAsync(HttpMethod.Post, "/admin/members", token: admin, json: new { name = user, role = "user" });
            string member = await SignedInAsync(user, issued.GetProperty("password").GetString()!);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{everyone}/FeatureServer/0?f=json", host, member)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{own}/FeatureServer/0?f=json", host, member)).Status);

            // Nobody who has not signed in.
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{everyone}/FeatureServer/0?f=json", host)).Status);

            // Found from home, with the address to open it at; the organisation-only one is not.
            (HttpStatusCode searched, JsonElement found) = await SendAsync(HttpMethod.Get, $"/admin/search/elsewhere?q={run}", token: member);
            Assert.Equal(HttpStatusCode.OK, searched);
            string[] titles = [.. found.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("title").GetString()!)];
            Assert.Contains(everyone, titles);
            Assert.DoesNotContain(own, titles);
            JsonElement hit = found.GetProperty("results").EnumerateArray().First(r => r.GetProperty("title").GetString() == everyone);
            Assert.Contains(host, hit.GetProperty("url").GetString(), StringComparison.Ordinal);

            // An ArcGIS client sharing it to the organisation keeps it where it is (ADR-220 §5.3): the member still reads.
            using (HttpRequestMessage share = new(HttpMethod.Post, $"{_root}/sharing/rest/content/users/aoadmin{run}/shareItems"))
            {
                share.Headers.Host = host;
                share.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other);
                share.Content = new FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string, string>
                {
                    ["items"] = hit.GetProperty("id").GetString()!, ["org"] = "true", ["everyone"] = "false", ["f"] = "json",
                });
                using HttpResponseMessage shared = await Http.SendAsync(share);
                Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
            }

            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{everyone}/FeatureServer/0?f=json", host, member)).Status);

            // ArcGIS clients are told the organisation; there is no level of theirs in between.
            (_, JsonElement search) = await SendAsync(HttpMethod.Get, $"/sharing/rest/search?q={everyone}&f=json", host, other);
            Assert.Contains(search.GetProperty("results").EnumerateArray(), r => r.GetProperty("access").GetString() == "org");

            // Sharing at it is its own privilege: a user of the other organisation may share with it, not with everyone.
            (_, JsonElement theirUser) = await SendAsync(HttpMethod.Post, "/admin/members", host, other, new { name = $"plain{run}", role = "publisher" });
            string publisher = await SignedInAsync($"plain{run}", theirUser.GetProperty("password").GetString()!, host);
            (HttpStatusCode defined2, _) = await SendAsync(HttpMethod.Post, "/admin/hosted/define", host, publisher, new
            {
                name = $"ao_pub_{run}", geometryType = "Point", srid = 4326, fields = Array.Empty<object>(),
            });
            Assert.Equal(HttpStatusCode.Created, defined2);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/admin/services/ao_pub_{run}/sharing?folder=hosted", host, publisher,
                new { sharing = "allorganizations" })).Status);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/members/{user}", token: admin);
        }
    }
}
