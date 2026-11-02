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
/// Portal content adds as doors onto Studio's import — ADR-257, Q-225: the ArcGIS API for Python's
/// <c>gis.content.add</c>, <c>item.publish()</c>, <c>create_service</c>, <c>create_view</c> and
/// <c>FeatureLayer.append(item_id=…)</c>, driven by their documented requests.
/// </summary>
/// <remarks>
/// <b>Each test makes what it changes</b>, under <c>zz_</c> names, and removes it. What a door made is read back where
/// Studio reads it — Studio's documents and the layer's own query — not from the door's answer.
/// </remarks>
[Collection("catalogue walk")]
public sealed class PortalContentAddsTests : ArcGisClient
{
    private const string Places = "name,x,y\nAnkara,32.85,39.93\nIstanbul,28.97,41.01\nIzmir,27.14,38.42\n";

    private const string MorePlaces = "name,x,y\nBursa,29.06,40.19\nKonya,32.49,37.87\n";

    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set; this test signs in as that user.");

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, HttpContent? content = null, string? token = null)
    {
        string root = await RequireServerAsync();
        token ??= await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");

        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new(fields);

    private async Task<string> AddCsvAsync(string title, string csv, string fileName = "places.csv", string? token = null, string? user = null)
    {
        using MultipartFormDataContent form = new()
        {
            { new StringContent("CSV"), "type" },
            { new StringContent(title), "title" },
            { new StringContent("zz,places"), "tags" },
            { new StringContent("json"), "f" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", fileName },
        };

        JsonElement added = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{user ?? User}/addItem", form, token);
        Assert.True(added.TryGetProperty("success", out JsonElement ok) && ok.GetBoolean(), added.GetRawText());
        return added.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> PublishAsync(string itemId, string fileType, string name, string? token = null, string? user = null) =>
        await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{user ?? User}/publish", Form(new()
        {
            ["itemid"] = itemId,
            ["filetype"] = fileType,
            ["publishParameters"] = JsonSerializer.Serialize(new { name, locationType = "coordinates", longitudeFieldName = "x", latitudeFieldName = "y" }),
            ["f"] = "json",
        }), token);

    private async Task<long> CountAsync(string service)
    {
        JsonElement counted = await SendAsync(HttpMethod.Get,
            $"/rest/services/hosted/{service}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json");
        Assert.True(counted.TryGetProperty("count", out JsonElement count), counted.GetRawText());
        return count.GetInt64();
    }

    private static string ErrorOf(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("error", out JsonElement error)
            ? error.GetProperty("message").GetString() ?? string.Empty
            : throw new Xunit.Sdk.XunitException($"Expected a portal error, and was answered {answer.GetRawText()}");

    private async Task RemoveAsync(string? service, params string?[] documents)
    {
        if (service is not null)
        {
            await UnpublishAsync(service, service, "hosted");
        }

        foreach (string? document in documents)
        {
            if (document is not null)
            {
                await SendAsync(HttpMethod.Delete, $"/content/documents/{document}");
            }
        }
    }

    private async Task<string> MemberTokenAsync(string root, string name, string role)
    {
        string admin = await TokenAsync(root) ?? throw new InvalidOperationException("No token.");

        JsonElement created = await SendAsync(HttpMethod.Post, "/admin/members",
            new StringContent(JsonSerializer.Serialize(new { name, role, userType = "creator" }), Encoding.UTF8, "application/json"), admin);
        string issued = created.GetProperty("password").GetString()!;

        async Task<string> LoginAsync(string password)
        {
            using HttpResponseMessage response = await Http.PostAsync($"{root}/rest/auth/login",
                new StringContent(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Signing in as '{name}' answered {(int)response.StatusCode}");
            return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
        }

        string first = await LoginAsync(issued);
        await SendAsync(HttpMethod.Post, "/rest/auth/password",
            new StringContent(JsonSerializer.Serialize(new { currentPassword = issued, newPassword = issued + "X1" }), Encoding.UTF8, "application/json"),
            first);
        return await LoginAsync(issued + "X1");
    }

    [Fact]
    public async Task A_csv_added_and_published_through_the_portal_is_a_queryable_layer_and_a_studio_document()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string name = $"zz_csvpub_{tag}";
        string? item = null;
        bool published = false;

        try
        {
            item = await AddCsvAsync($"zz places {tag}", Places);

            // Studio's content holds it, as Studio's own upload would have made it.
            JsonElement studio = await SendAsync(HttpMethod.Get, $"/content/documents/{item}");
            Assert.Equal("CSV", studio.GetProperty("type").GetString());
            Assert.Contains("places", studio.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

            JsonElement portal = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{item}?f=json");
            Assert.Equal("CSV", portal.GetProperty("type").GetString());

            JsonElement analyzed = await SendAsync(HttpMethod.Post, "/sharing/rest/content/features/analyze",
                Form(new() { ["itemid"] = item, ["filetype"] = "csv", ["f"] = "json" }));
            Assert.Equal("coordinates", analyzed.GetProperty("publishParameters").GetProperty("locationType").GetString());

            JsonElement answer = await PublishAsync(item, "csv", name);
            JsonElement service = answer.GetProperty("services")[0];
            Assert.True(service.GetProperty("success").GetBoolean(), answer.GetRawText());
            published = true;
            Assert.EndsWith($"/rest/services/hosted/{name}/FeatureServer", service.GetProperty("serviceurl").GetString(), StringComparison.Ordinal);

            // The job the answer names is done, as the Python API's status poll reads it.
            string serviceItemId = service.GetProperty("serviceItemId").GetString()!;
            JsonElement status = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{serviceItemId}/status",
                Form(new() { ["jobid"] = service.GetProperty("jobId").GetString()!, ["f"] = "json" }));
            Assert.Equal("completed", status.GetProperty("status").GetString());

            Assert.Equal(3, await CountAsync(name));

            // A second publish under the same name is Studio's refusal, through the door.
            Assert.NotEmpty(ErrorOf(await PublishAsync(item, "csv", name)));

            // And a file type the item is not is refused by name.
            Assert.Contains("CSV", ErrorOf(await PublishAsync(item, "shapefile", name + "x")), StringComparison.Ordinal);
        }
        finally
        {
            await RemoveAsync(published ? name : null, item);
        }
    }

    [Fact]
    public async Task A_document_is_added_as_Studio_keeps_one_and_is_not_published_as_data()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string? item = null;

        try
        {
            using MultipartFormDataContent form = new()
            {
                { new StringContent("PDF"), "type" },
                { new StringContent($"zz manual {tag}"), "title" },
                { new StringContent("json"), "f" },
                { new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n")), "file", "manual.pdf" },
            };

            JsonElement added = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem", form);
            Assert.True(added.TryGetProperty("success", out JsonElement ok) && ok.GetBoolean(), added.GetRawText());
            item = added.GetProperty("id").GetString();

            Assert.Equal("PDF", (await SendAsync(HttpMethod.Get, $"/content/documents/{item}")).GetProperty("type").GetString());
            Assert.NotEmpty(ErrorOf(await PublishAsync(item!, "csv", $"zz_cpdf_{tag}")));

            // A zip that names no data type is refused by Studio's own upload, through the door.
            using MultipartFormDataContent zip = new()
            {
                { new StringContent("Shapefile"), "type" },
                { new StringContent("json"), "f" },
                { new ByteArrayContent([0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), "file", "empty.zip" },
            };
            Assert.NotEmpty(ErrorOf(await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem", zip)));
        }
        finally
        {
            await RemoveAsync(null, item);
        }
    }

    [Fact]
    public async Task A_member_who_may_not_publish_is_refused_the_publish_and_the_service()
    {
        string root = await RequireServerAsync();
        string tag = Guid.NewGuid().ToString("N")[..8];
        string reader = $"zz_cnoread_{tag}";
        string? item = null;

        try
        {
            string token = await MemberTokenAsync(root, reader, "user");

            item = await AddCsvAsync($"zz reader {tag}", Places, token: token, user: reader);

            JsonElement refused = await PublishAsync(item, "csv", $"zz_cnoread_{tag}", token, reader);
            Assert.Contains("privilege to publish", ErrorOf(refused), StringComparison.Ordinal);

            JsonElement noService = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{reader}/createService", Form(new()
            {
                ["createParameters"] = JsonSerializer.Serialize(new { name = $"zz_cnoread_{tag}s" }),
                ["outputType"] = "featureService",
                ["f"] = "json",
            }), token);
            Assert.Contains("privilege to publish", ErrorOf(noService), StringComparison.Ordinal);

            Assert.Equal(404, await StatusOfAsync($"/rest/services/hosted/zz_cnoread_{tag}/FeatureServer?f=json"));
        }
        finally
        {
            await RemoveAsync(null, item);
            await SendAsync(HttpMethod.Delete, $"/admin/members/{reader}");
        }
    }

    [Fact]
    public async Task A_file_added_in_parts_becomes_one_document_at_its_commit()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string? document = null;

        try
        {
            JsonElement added = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem", Form(new()
            {
                ["type"] = "CSV", ["title"] = $"zz parts {tag}", ["multipart"] = "true", ["fileName"] = "parts.csv", ["f"] = "json",
            }));
            string id = added.GetProperty("id").GetString()!;

            byte[] bytes = Encoding.UTF8.GetBytes(Places);
            int half = bytes.Length / 2;

            // Sent second part first: the commit joins them in their numbers' order.
            foreach ((int number, byte[] part) in new[] { (2, bytes[half..]), (1, bytes[..half]) })
            {
                using MultipartFormDataContent form = new()
                {
                    { new StringContent(number.ToString(System.Globalization.CultureInfo.InvariantCulture)), "partNum" },
                    { new ByteArrayContent(part), "file", $"split{number}" },
                };
                JsonElement sent = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/addPart?f=json", form);
                Assert.True(sent.GetProperty("success").GetBoolean(), sent.GetRawText());
            }

            JsonElement committed = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/commit",
                Form(new() { ["type"] = "CSV", ["tags"] = "zz", ["f"] = "json" }));
            Assert.True(committed.GetProperty("success").GetBoolean(), committed.GetRawText());

            JsonElement status = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/status",
                Form(new() { ["f"] = "json" }));
            Assert.Equal("completed", status.GetProperty("status").GetString());
            document = status.GetProperty("itemId").GetString();

            JsonElement studio = await SendAsync(HttpMethod.Get, $"/content/documents/{document}");
            Assert.Equal($"zz parts {tag}", studio.GetProperty("title").GetString());
            Assert.Equal(Encoding.UTF8.GetByteCount(Places), studio.GetProperty("size").GetInt64());
        }
        finally
        {
            await RemoveAsync(null, document);
        }
    }

    [Fact]
    public async Task A_csv_item_is_appended_to_a_hosted_layer_by_its_item_id()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string name = $"zz_csvapp_{tag}";
        string? first = null;
        string? more = null;
        bool published = false;

        try
        {
            first = await AddCsvAsync($"zz append {tag}", Places);
            Assert.True((await PublishAsync(first, "csv", name)).GetProperty("services")[0].GetProperty("success").GetBoolean());
            published = true;

            more = await AddCsvAsync($"zz append more {tag}", MorePlaces, "more.csv");

            JsonElement layer = await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{name}/FeatureServer/0?f=json");
            Assert.Contains("csv", layer.GetProperty("supportedAppendFormats").GetString()!.Split(','));

            JsonElement appended = await SendAsync(HttpMethod.Post, $"/rest/services/hosted/{name}/FeatureServer/0/append", Form(new()
            {
                ["appendItemId"] = more, ["appendUploadFormat"] = "csv", ["f"] = "json",
            }));
            Assert.Equal("Completed", appended.GetProperty("status").GetString());
            Assert.Equal(5, await CountAsync(name));

            // The item is not spent: it is still the caller's document.
            Assert.Equal("CSV", (await SendAsync(HttpMethod.Get, $"/content/documents/{more}")).GetProperty("type").GetString());

            // A format the item is not is refused, and nothing is written.
            JsonElement wrong = await SendAsync(HttpMethod.Post, $"/rest/services/hosted/{name}/FeatureServer/0/append", Form(new()
            {
                ["appendItemId"] = more, ["appendUploadFormat"] = "geojson", ["f"] = "json",
            }));
            Assert.NotEmpty(ErrorOf(wrong));
            Assert.Equal(5, await CountAsync(name));
        }
        finally
        {
            await RemoveAsync(published ? name : null, first, more);
        }
    }

    [Fact]
    public async Task A_service_is_created_with_its_layers_and_a_view_of_it_with_its_source_and_an_empty_one_is_refused()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string name = $"zz_csvc_{tag}";
        string view = $"zz_csvv_{tag}";
        bool made = false;
        bool viewed = false;

        try
        {
            JsonElement empty = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/createService", Form(new()
            {
                ["createParameters"] = JsonSerializer.Serialize(new { name }), ["outputType"] = "featureService", ["f"] = "json",
            }));
            Assert.Contains("ADR-057", ErrorOf(empty), StringComparison.Ordinal);

            JsonElement created = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/createService", Form(new()
            {
                ["createParameters"] = JsonSerializer.Serialize(new
                {
                    name,
                    spatialReference = new { wkid = 4326 },
                    layers = new[]
                    {
                        new
                        {
                            name,
                            geometryType = "esriGeometryPoint",
                            fields = new object[]
                            {
                                new { name = "OBJECTID", type = "esriFieldTypeOID" },
                                new { name = "label", type = "esriFieldTypeString", length = 64 },
                            },
                        },
                    },
                }),
                ["outputType"] = "featureService",
                ["f"] = "json",
            }));
            Assert.True(created.TryGetProperty("success", out JsonElement ok) && ok.GetBoolean(), created.GetRawText());
            made = true;
            Assert.Equal(0, await CountAsync(name));

            JsonElement fields = await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{name}/FeatureServer/0?f=json");
            Assert.Contains("label", fields.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));

            JsonElement emptyView = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/createService", Form(new()
            {
                ["isView"] = "true",
                ["createParameters"] = JsonSerializer.Serialize(new { name = view, isView = true }),
                ["outputType"] = "featureService",
                ["f"] = "json",
            }));
            Assert.NotEmpty(ErrorOf(emptyView));

            JsonElement madeView = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/createService", Form(new()
            {
                ["isView"] = "true",
                ["createParameters"] = JsonSerializer.Serialize(new
                {
                    name = view,
                    isView = true,
                    capabilities = "Query",
                    layers = new[]
                    {
                        new { adminLayerInfo = new { viewLayerDefinition = new { sourceServiceName = name, sourceLayerId = 0 } } },
                    },
                }),
                ["outputType"] = "featureService",
                ["f"] = "json",
            }));
            Assert.True(madeView.TryGetProperty("isView", out JsonElement isView) && isView.GetBoolean(), madeView.GetRawText());
            viewed = true;
            Assert.Equal(0, await CountAsync(view));
        }
        finally
        {
            if (viewed)
            {
                await UnpublishAsync(view, view, "hosted");
            }

            await RemoveAsync(made ? name : null);
        }
    }
}
