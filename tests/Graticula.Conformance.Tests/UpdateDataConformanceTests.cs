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

    private async Task PutProtectionAsync(string root, string token, string name, bool on)
    {
        using HttpRequestMessage request = new(HttpMethod.Put, $"{root}/admin/services/{name}/protection?folder=hosted")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { @protected = on }), Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode,
            $"Setting protection to {on} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
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

            // ---------------------------------------------------------------- protection stops replacing and emptying
            await PutProtectionAsync(root, token!, name, true);

            try
            {
                (HttpStatusCode guarded, string guardedBody) =
                    await PostFileAsync(root, token, $"/admin/hosted/{name}/overwrite", Polygons("x"));

                Assert.True(guarded == HttpStatusCode.Conflict,
                    $"Replacing a protected layer's features answered {(int)guarded}: {guardedBody}");

                using HttpRequestMessage empty = new(HttpMethod.Post, $"{root}/admin/hosted/{name}/truncate");
                empty.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage emptied = await Http.SendAsync(empty);

                Assert.True(emptied.StatusCode == HttpStatusCode.Conflict,
                    $"Emptying a protected layer answered {(int)emptied.StatusCode}.");

                Assert.Equal(3, await CountAsync(root, token!, name));
            }
            finally
            {
                await PutProtectionAsync(root, token!, name, false);
            }

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

    /// <summary>Update data's mapping step: a look that writes nothing, then a mapping that decides where each column goes.</summary>
    [Fact]
    public async Task A_dry_run_writes_nothing_and_a_mapping_decides_where_columns_go()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_mapping_{Guid.NewGuid():N}"[..20];

        (HttpStatusCode made, string madeBody) = await PostFileAsync(root, token, "/admin/hosted/import", Polygons("a", "b"), name);
        Assert.True(made is HttpStatusCode.Created or HttpStatusCode.OK, $"The import failed: {(int)made} {madeBody}");

        try
        {
            // A file whose column is called something else.
            string renamed = Polygons("mapped").Replace("\"name\"", "\"label\"", StringComparison.Ordinal);

            (HttpStatusCode looked, string lookBody) = await PostFormAsync(root, token!, $"/admin/hosted/{name}/append", renamed,
                ("dryRun", "true"));

            Assert.True(looked == HttpStatusCode.OK, $"The dry run answered {(int)looked}: {lookBody}");

            JsonElement look = JsonDocument.Parse(lookBody).RootElement;
            Assert.Contains(look.GetProperty("layerColumns").EnumerateArray(), c => c.GetString() == "name");
            Assert.Contains(look.GetProperty("fileColumns").EnumerateArray(), c => c.GetProperty("name").GetString() == "label");
            Assert.Equal(2, await CountAsync(root, token!, name));

            // Mapped: the file's `label` goes into the layer's `name`.
            (HttpStatusCode mapped, string mapBody) = await PostFormAsync(root, token!, $"/admin/hosted/{name}/append", renamed,
                ("fieldMappings", "[{\"name\":\"name\",\"source\":\"label\"}]"));

            Assert.True(mapped == HttpStatusCode.OK, $"The mapped append answered {(int)mapped}: {mapBody}");
            Assert.Equal(3, await CountAsync(root, token!, name));

            // Left out: an empty mapping writes the rows and none of the file's columns, though `name` matches.
            (HttpStatusCode left, string leftBody) = await PostFormAsync(root, token!, $"/admin/hosted/{name}/append", Polygons("dropped"),
                ("fieldMappings", "[]"));

            Assert.True(left == HttpStatusCode.OK, $"The append with nothing mapped answered {(int)left}: {leftBody}");

            using HttpRequestMessage ask = new(HttpMethod.Get,
                $"{root}/rest/services/hosted/{name}/FeatureServer/0/query?where=name%3D%27dropped%27&returnCountOnly=true&f=json");
            ask.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage got = await Http.SendAsync(ask);
            Assert.Equal(0, JsonDocument.Parse(await got.Content.ReadAsStringAsync()).RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            using HttpRequestMessage delete = new(HttpMethod.Delete, $"{root}/admin/featureservices/{name}?folder=hosted&drop=true");
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage gone = await Http.SendAsync(delete);
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> PostFormAsync(
        string root, string token, string path, string json, params (string Name, string Value)[] fields)
    {
        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(Encoding.UTF8.GetBytes(json));
        form.Add(bytes, "file", "data.geojson");
        foreach ((string field, string value) in fields) form.Add(new StringContent(value), field);

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{path}") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>ADR-105: ArcGIS's uploads/upload then append, as the ArcGIS API for Python calls them.</summary>
    [Fact]
    public async Task An_ArcGIS_client_uploads_then_appends_maps_fields_and_is_refused_upsert()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_esriapp_{Guid.NewGuid():N}"[..20];

        (HttpStatusCode made, string madeBody) =
            await PostFileAsync(root, token, "/admin/hosted/import", Polygons("a", "b"), name);

        Assert.True(made is HttpStatusCode.Created or HttpStatusCode.OK, $"The import failed: {(int)made} {madeBody}");

        string service = $"{root}/rest/services/hosted/{name}/FeatureServer";

        try
        {
            // A file whose column is called something else, mapped onto the layer's `name`.
            string renamed = Polygons("mapped").Replace("\"name\"", "\"label\"", StringComparison.Ordinal);

            string id = await UploadAsync(service, token!, renamed);

            (HttpStatusCode upserted, string upsertBody) = await AppendFormAsync(service, token!, new()
            {
                ["appendUploadId"] = id, ["appendUploadFormat"] = "geojson", ["upsert"] = "true", ["f"] = "json",
            });

            Assert.True(upserted == HttpStatusCode.BadRequest, $"upsert=true answered {(int)upserted}: {upsertBody}");
            Assert.Equal(2, await CountAsync(root, token!, name));

            (HttpStatusCode appended, string appendBody) = await AppendFormAsync(service, token!, new()
            {
                ["appendUploadId"] = id, ["appendUploadFormat"] = "geojson",
                ["fieldMappings"] = "[{\"name\":\"name\",\"source\":\"label\"}]", ["f"] = "json",
            });

            Assert.True(appended == HttpStatusCode.OK, $"append answered {(int)appended}: {appendBody}");

            JsonElement said = JsonDocument.Parse(appendBody).RootElement;

            Assert.Equal("Completed", said.GetProperty("status").GetString());
            Assert.Equal(1, said.GetProperty("recordCount").GetInt32());
            Assert.Equal(3, await CountAsync(root, token!, name));

            using (HttpRequestMessage ask = new(HttpMethod.Get,
                $"{service}/0/query?where=name%3D%27mapped%27&returnCountOnly=true&f=json"))
            {
                ask.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage got = await Http.SendAsync(ask);
                int count = JsonDocument.Parse(await got.Content.ReadAsStringAsync()).RootElement.GetProperty("count").GetInt32();
                Assert.True(count == 1, "fieldMappings did not put the file's 'label' into the layer's 'name'.");
            }

            // An upload is spent once appended.
            (HttpStatusCode again, _) = await AppendFormAsync(service, token!, new()
            {
                ["appendUploadId"] = id, ["appendUploadFormat"] = "geojson", ["f"] = "json",
            });

            Assert.Equal(HttpStatusCode.BadRequest, again);

            // truncateExisting replaces.
            string replacing = await UploadAsync(service, token!, Polygons("only"));

            (HttpStatusCode replaced, string replaceBody) = await AppendFormAsync(service, token!, new()
            {
                ["appendUploadId"] = replacing, ["appendUploadFormat"] = "geojson", ["truncateExisting"] = "true", ["f"] = "json",
            });

            Assert.True(replaced == HttpStatusCode.OK, $"append with truncateExisting answered {(int)replaced}: {replaceBody}");
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

    private async Task<string> UploadAsync(string service, string token, string json)
    {
        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(Encoding.UTF8.GetBytes(json));

        form.Add(bytes, "file", "more.geojson");
        form.Add(new StringContent("json"), "f");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{service}/uploads/upload") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"uploads/upload answered {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("item").GetProperty("itemID").GetString()!;
    }

    private async Task<(HttpStatusCode Status, string Body)> AppendFormAsync(
        string service, string token, System.Collections.Generic.Dictionary<string, string> fields)
    {
        using FormUrlEncodedContent content = new(fields);
        using HttpRequestMessage request = new(HttpMethod.Post, $"{service}/0/append") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
