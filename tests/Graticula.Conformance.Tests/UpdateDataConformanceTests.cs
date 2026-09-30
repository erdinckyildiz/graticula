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
/// Portal's *Update data* on a hosted layer — ADR-103, condition 2: append, overwrite, and who may.
/// </summary>
/// <remarks>
/// <b>This test creates what it changes.</b> It imports a two-feature layer under a `zz_` name, updates that, and
/// deletes it; nothing in the demo data is touched, as <see cref="HostedDeleteTests"/> does for the same reason.
/// The counts are read through the FeatureServer, which is what a client sees — not the route's own word.
/// </remarks>
[Collection("catalogue walk")]
public sealed class UpdateDataConformanceTests : ArcGisClient
{
    private static string Polygons(params string[] names)
    {
        StringBuilder features = new();

        for (int i = 0; i < names.Length; i++)
        {
            double x = 28.97 + (i * 0.001);
            if (i > 0) features.Append(',');
            features.Append(CultureInfo(
                $$$"""{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[{{{x}}},41.0],[{{{x}}},41.0002],[{{{x + 0.0003}}},41.0002],[{{{x + 0.0003}}},41.0],[{{{x}}},41.0]]]},"properties":{"name":"{{{names[i]}}}"}}"""));
        }

        return $$"""{"type":"FeatureCollection","features":[{{features}}]}""";
    }

    private static string CultureInfo(FormattableString text) => FormattableString.Invariant(text);

    private const string APoint =
        """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[28.97,41.0]},"properties":{"name":"p"}}]}""";

    private async Task<(HttpStatusCode Status, string Body)> PostFileAsync(
        string root, string? token, string path, string json, string? name = null)
    {
        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(Encoding.UTF8.GetBytes(json));

        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/geo+json");
        form.Add(bytes, "file", "data.geojson");

        if (name is not null) form.Add(new StringContent(name), "name");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{path}") { Content = form };

        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<int> CountAsync(string root, string token, string name)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"{root}/rest/services/hosted/{name}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Counting '{name}' failed with {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("count").GetInt32();
    }

    [Fact]
    public async Task Append_adds_overwrite_replaces_and_a_wrong_file_changes_nothing()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_update_{Guid.NewGuid():N}"[..20];

        (HttpStatusCode made, string madeBody) =
            await PostFileAsync(root, token, "/admin/hosted/import", Polygons("a", "b"), name);

        Assert.True(made is HttpStatusCode.Created or HttpStatusCode.OK, $"The import failed: {(int)made} {madeBody}");

        try
        {
            Assert.Equal(2, await CountAsync(root, token!, name));

            // ---------------------------------------------------------------- nobody signed in may not
            (HttpStatusCode anonymous, _) =
                await PostFileAsync(root, null, $"/admin/hosted/{name}/append", Polygons("x"));

            Assert.True(anonymous is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"An anonymous append answered {(int)anonymous}.");

            Assert.Equal(2, await CountAsync(root, token!, name));

            // ---------------------------------------------------------------- append adds
            (HttpStatusCode appended, string appendBody) =
                await PostFileAsync(root, token, $"/admin/hosted/{name}/append", Polygons("c"));

            Assert.True(appended == HttpStatusCode.OK, $"The append failed: {(int)appended} {appendBody}");
            Assert.Equal(1, JsonDocument.Parse(appendBody).RootElement.GetProperty("rows").GetInt32());
            Assert.Equal(3, await CountAsync(root, token!, name));

            // ---------------------------------------------------------------- the wrong shape changes nothing
            (HttpStatusCode wrong, string wrongBody) =
                await PostFileAsync(root, token, $"/admin/hosted/{name}/overwrite", APoint);

            Assert.True(wrong == HttpStatusCode.BadRequest, $"Points into a polygon layer answered {(int)wrong}: {wrongBody}");
            Assert.Equal(3, await CountAsync(root, token!, name));

            // ---------------------------------------------------------------- overwrite replaces
            (HttpStatusCode replaced, string replaceBody) =
                await PostFileAsync(root, token, $"/admin/hosted/{name}/overwrite", Polygons("d"));

            Assert.True(replaced == HttpStatusCode.OK, $"The overwrite failed: {(int)replaced} {replaceBody}");
            Assert.True(JsonDocument.Parse(replaceBody).RootElement.GetProperty("replaced").GetBoolean());
            Assert.Equal(1, await CountAsync(root, token!, name));
        }
        finally
        {
            using HttpRequestMessage delete = new(
                HttpMethod.Delete, $"{root}/admin/featureservices/{name}?folder=hosted&drop=true");

            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage gone = await Http.SendAsync(delete);
        }
    }
}
