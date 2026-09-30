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
/// Portal's Change owner for one item — a service and a web map, an administrator's act (2026-10-01).
/// </summary>
/// <remarks>
/// <b>This test creates what it hands over</b>, under `zz_` names, and deletes it; the new owner is read back through
/// the listings a client reads, not the route's own answer.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ChangeOwnerConformanceTests : ArcGisClient
{
    private const string Receiver = "ci_ordinary";

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string root, string? token, HttpMethod method, string path, string? json = null, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}")
        {
            Content = content ?? (json is null ? null : new StringContent(json, Encoding.UTF8, "application/json")),
        };

        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_service_and_a_web_map_are_given_to_another_member()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_owner_{Guid.NewGuid():N}"[..18];
        string feature = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[29,41]},"properties":{"n":1}}]}""";

        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(feature)), "file", "one.geojson");
        form.Add(new StringContent(name), "name");

        (HttpStatusCode made, string madeBody) = await SendAsync(root, token, HttpMethod.Post, "/admin/hosted/import", content: form);
        Assert.True(made is HttpStatusCode.Created or HttpStatusCode.OK, $"The import failed: {(int)made} {madeBody}");

        string? map = null;

        try
        {
            // Nobody signed in may not.
            (HttpStatusCode anonymous, _) = await SendAsync(root, null, HttpMethod.Put,
                $"/admin/services/{name}/owner?folder=hosted", JsonSerializer.Serialize(new { to = Receiver }));
            Assert.True(anonymous is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"Anonymous answered {(int)anonymous}.");

            (HttpStatusCode nobody, _) = await SendAsync(root, token, HttpMethod.Put,
                $"/admin/services/{name}/owner?folder=hosted", JsonSerializer.Serialize(new { to = "zz_no_such_member" }));
            Assert.Equal(HttpStatusCode.NotFound, nobody);

            (HttpStatusCode given, string givenBody) = await SendAsync(root, token, HttpMethod.Put,
                $"/admin/services/{name}/owner?folder=hosted", JsonSerializer.Serialize(new { to = Receiver }));
            Assert.True(given == HttpStatusCode.OK, $"Changing the owner answered {(int)given}: {givenBody}");

            (_, string items) = await SendAsync(root, token, HttpMethod.Get, "/content/items");
            Assert.Contains($"\"owner\":\"{Receiver}\"", items.Replace(" ", "", StringComparison.Ordinal)
                [Math.Max(0, items.IndexOf(name, StringComparison.Ordinal) - 400)..], StringComparison.Ordinal);

            // A web map, the same way.
            string document = JsonSerializer.Serialize(new
            {
                title = "zz owner map", sharing = "private",
                document = new { operationalLayers = Array.Empty<object>(), baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" }, version = "2.31" },
            });

            (HttpStatusCode created, string createdBody) = await SendAsync(root, token, HttpMethod.Post, "/content/webmaps", document);
            Assert.Equal(HttpStatusCode.Created, created);
            map = JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetString();

            (HttpStatusCode mapGiven, _) = await SendAsync(root, token, HttpMethod.Put,
                $"/content/webmaps/{map}/owner", JsonSerializer.Serialize(new { to = Receiver }));
            Assert.Equal(HttpStatusCode.OK, mapGiven);

            (_, string read) = await SendAsync(root, token, HttpMethod.Get, $"/content/webmaps/{map}");
            Assert.Equal(Receiver, JsonDocument.Parse(read).RootElement.GetProperty("owner").GetString());
        }
        finally
        {
            await SendAsync(root, token, HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");
            if (map is not null) await SendAsync(root, token, HttpMethod.Delete, $"/content/webmaps/{map}");
        }
    }
}
