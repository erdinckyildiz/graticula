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
/// <c>gis.content.add</c>, <c>item.publish()</c>, <c>create_service</c>, <c>create_view</c> (in two requests, ADR-257 §11) and
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

    /// <summary>
    /// ADR-257 §12: the service a data item was published to is related to it as ArcGIS's <c>Service2Data</c>, both ways,
    /// and a publish with <c>overwrite=true</c> after the item's file changed is Studio's Overwrite of that service's
    /// layer — the ArcGIS API for Python's <c>manager.overwrite(csv)</c>, which failed at its first step until v1.0.499's
    /// review.
    /// </summary>
    [Fact]
    public async Task A_data_items_service_is_related_to_it_and_overwritten_from_its_changed_file()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string name = $"zz_over_{tag}";
        string? item = null;
        bool published = false;

        try
        {
            item = await AddCsvAsync($"zz over {tag}", Places, $"{name}.csv");
            JsonElement answer = await PublishAsync(item, "csv", name);
            Assert.True(answer.GetProperty("services")[0].GetProperty("success").GetBoolean(), answer.GetRawText());
            published = true;
            string service = answer.GetProperty("services")[0].GetProperty("serviceItemId").GetString()!;

            // ADR-260: the layer is called what its data is called, and keeps the name it was published under.
            JsonElement layerItem = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{service}?f=json");
            Assert.Equal($"zz over {tag}", layerItem.GetProperty("title").GetString());
            Assert.Equal(name, layerItem.GetProperty("name").GetString());

            JsonElement forward = await SendAsync(HttpMethod.Get,
                $"/sharing/rest/content/items/{service}/relatedItems?relationshipType=Service2Data&direction=forward&f=json");
            Assert.Equal(item, Assert.Single(forward.GetProperty("relatedItems").EnumerateArray()).GetProperty("id").GetString());

            JsonElement reverse = await SendAsync(HttpMethod.Get,
                $"/sharing/rest/content/items/{item}/relatedItems?relationshipType=Service2Data&direction=reverse&f=json");
            Assert.Equal(service, Assert.Single(reverse.GetProperty("relatedItems").EnumerateArray()).GetProperty("id").GetString());

            // The other direction from each end is empty, as is a type this portal does not keep.
            Assert.Equal(0, (await SendAsync(HttpMethod.Get,
                $"/sharing/rest/content/items/{item}/relatedItems?relationshipType=Service2Data&direction=forward&f=json")).GetProperty("total").GetInt32());
            Assert.Equal(0, (await SendAsync(HttpMethod.Get,
                $"/sharing/rest/content/items/{service}/relatedItems?relationshipType=Map2Service&f=json")).GetProperty("total").GetInt32());

            JsonElement parameters = await SendAsync(HttpMethod.Post,
                $"/sharing/rest/content/items/{service}/info/publishParameters.json", Form(new() { ["f"] = "json" }));
            Assert.Equal(name, parameters.GetProperty("name").GetString());
            Assert.Equal("csv", parameters.GetProperty("type").GetString());

            // The flags the Python API puts on the item's update before it publishes again are passed over, not refused.
            JsonElement flagged = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{item}/update", Form(new()
            {
                ["overwrite"] = "true", ["overwriteService"] = "on", ["useDescription"] = "on", ["f"] = "json",
            }));
            Assert.True(flagged.TryGetProperty("success", out JsonElement flaggedOk) && flaggedOk.GetBoolean(), flagged.GetRawText());

            // The item's file replaced through the portal's update, then published again with overwrite.
            using (MultipartFormDataContent update = new()
                   {
                       { new StringContent("json"), "f" },
                       { new ByteArrayContent(Encoding.UTF8.GetBytes(Places + MorePlaces[(MorePlaces.IndexOf('\n') + 1)..])), "file", $"{name}.csv" },
                   })
            {
                JsonElement updated = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{item}/update", update);
                Assert.True(updated.TryGetProperty("success", out JsonElement ok) && ok.GetBoolean(), updated.GetRawText());
            }

            JsonElement overwritten = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/publish", Form(new()
            {
                ["itemid"] = item,
                ["filetype"] = "csv",
                ["overwrite"] = "true",
                ["publishParameters"] = JsonSerializer.Serialize(new { name, locationType = "coordinates" }),
                ["f"] = "json",
            }));
            JsonElement result = overwritten.GetProperty("services")[0];
            Assert.True(result.GetProperty("success").GetBoolean(), overwritten.GetRawText());
            Assert.Equal(service, result.GetProperty("serviceItemId").GetString());
            Assert.Equal(5, await CountAsync(name));
        }
        finally
        {
            await RemoveAsync(published ? name : null, item);
        }
    }

    /// <summary>An overwrite of an item that was never published is refused by name and makes nothing (ADR-257 §12).</summary>
    [Fact]
    public async Task An_overwrite_of_an_item_never_published_is_refused()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string? item = null;

        try
        {
            item = await AddCsvAsync($"zz never {tag}", Places);
            JsonElement answer = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/publish", Form(new()
            {
                ["itemid"] = item,
                ["filetype"] = "csv",
                ["overwrite"] = "true",
                ["f"] = "json",
            }));
            Assert.Contains("nothing to overwrite", ErrorOf(answer), StringComparison.Ordinal);
        }
        finally
        {
            await RemoveAsync(null, item);
        }
    }

    [Fact]
    public async Task A_geojson_item_is_published_through_the_same_import()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string name = $"zz_gjpub_{tag}";
        string? item = null;
        bool published = false;

        try
        {
            const string geojson =
                "{\"type\":\"FeatureCollection\",\"features\":["
                + "{\"type\":\"Feature\",\"properties\":{\"name\":\"Ankara\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[32.85,39.93]}},"
                + "{\"type\":\"Feature\",\"properties\":{\"name\":\"Izmir\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[27.14,38.42]}}]}";

            using MultipartFormDataContent form = new()
            {
                { new StringContent("GeoJson"), "type" },
                { new StringContent($"zz geojson {tag}"), "title" },
                { new StringContent("json"), "f" },
                { new ByteArrayContent(Encoding.UTF8.GetBytes(geojson)), "file", "places.geojson" },
            };

            JsonElement added = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem", form);
            item = added.GetProperty("id").GetString();
            Assert.Equal("GeoJson", (await SendAsync(HttpMethod.Get, $"/content/documents/{item}")).GetProperty("type").GetString());

            JsonElement answer = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/publish", Form(new()
            {
                ["itemid"] = item!, ["filetype"] = "geojson", ["publishParameters"] = JsonSerializer.Serialize(new { name }), ["f"] = "json",
            }));
            Assert.True(answer.GetProperty("services")[0].GetProperty("success").GetBoolean(), answer.GetRawText());
            published = true;
            Assert.Equal(2, await CountAsync(name));
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

    private Task<JsonElement> CreateServiceAsync(object createParameters, bool view = false, string? token = null, string? user = null) =>
        SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{user ?? User}/createService", Form(new()
        {
            ["createParameters"] = JsonSerializer.Serialize(createParameters),
            ["outputType"] = "featureService",
            ["isView"] = view ? "true" : "false",
            ["f"] = "json",
        }), token);

    private Task<JsonElement> AddToDefinitionAsync(string service, object definition, string? token = null) =>
        SendAsync(HttpMethod.Post, $"/rest/admin/services/hosted/{service}/FeatureServer/addToDefinition", Form(new()
        {
            ["addToDefinition"] = JsonSerializer.Serialize(definition),
            ["async"] = "false",
            ["f"] = "json",
        }), token);

    private static object PointLayer(string name) => new
    {
        name,
        type = "Feature Layer",
        geometryType = "esriGeometryPoint",
        objectIdField = "OBJECTID",
        hasAttachments = false,
        extent = new { xmin = 25, ymin = 35, xmax = 45, ymax = 43, spatialReference = new { wkid = 4326 } },
        fields = new object[]
        {
            new { name = "OBJECTID", type = "esriFieldTypeOID", nullable = false },
            new { name = "label", type = "esriFieldTypeString", length = 64, nullable = true, domain = (object?)null },
        },
    };

    /// <summary>Removes a service by its layers, then the service itself, as the admin pages do.</summary>
    private async Task RemoveServiceAsync(string service, params string[] layers)
    {
        foreach (string layer in layers)
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/layers/{Uri.EscapeDataString(layer)}");
        }

        await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{Uri.EscapeDataString(service)}?folder=hosted");
    }

    private async Task<JsonElement?> ListedAsync(string service)
    {
        JsonElement listed = await SendAsync(HttpMethod.Get, "/admin/featureservices");
        return listed.GetProperty("services").EnumerateArray()
            .Where(s => s.GetProperty("name").GetString() == service)
            .Select(s => (JsonElement?)s)
            .FirstOrDefault();
    }

    [Fact]
    public async Task A_service_is_created_with_its_layers_and_a_view_of_it_with_its_source()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string name = $"zz_csvc_{tag}";
        string view = $"zz_csvv_{tag}";
        bool made = false;
        bool viewed = false;

        try
        {
            JsonElement created = await CreateServiceAsync(new
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
            });
            Assert.True(created.TryGetProperty("success", out JsonElement ok) && ok.GetBoolean(), created.GetRawText());
            made = true;
            Assert.Equal(0, await CountAsync(name));

            JsonElement fields = await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{name}/FeatureServer/0?f=json");
            Assert.Contains("label", fields.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));

            JsonElement madeView = await CreateServiceAsync(new
            {
                name = view,
                isView = true,
                capabilities = "Query",
                layers = new[]
                {
                    new { adminLayerInfo = new { viewLayerDefinition = new { sourceServiceName = name, sourceLayerId = 0 } } },
                },
            }, view: true);
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

    /// <summary>
    /// ADR-257 §11, owner decision 2026-11-02: the ArcGIS API for Python's <c>create_service</c> makes the service empty
    /// and <c>manager.add_to_definition</c> adds its layer — the two requests it sends, in its order.
    /// </summary>
    [Fact]
    public async Task An_empty_service_is_created_and_filled_by_its_addToDefinition()
    {
        string root = await RequireServerAsync();
        string tag = Guid.NewGuid().ToString("N")[..8];
        string name = $"zz_cempty_{tag}";
        string layer = $"zz_cemptyl_{tag}";
        string member = $"zz_cemptym_{tag}";
        bool made = false;

        try
        {
            JsonElement created = await CreateServiceAsync(new
            {
                name,
                serviceDescription = "made empty",
                capabilities = "Query,Editing",
                spatialReference = new { wkid = 102100 },
                maxRecordCount = 1000,
                hasStaticData = false,
            });
            Assert.True(created.TryGetProperty("success", out JsonElement ok) && ok.GetBoolean(), created.GetRawText());
            made = true;
            Assert.False(created.GetProperty("isView").GetBoolean());
            Assert.Equal(created.GetProperty("itemId").GetString(), created.GetProperty("serviceItemId").GetString());
            Assert.EndsWith($"/rest/services/hosted/{name}/FeatureServer", created.GetProperty("serviceurl").GetString(), StringComparison.Ordinal);

            // It exists and holds nothing, as ArcGIS's empty service answers.
            JsonElement document = await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{name}/FeatureServer?f=json");
            Assert.Equal(0, document.GetProperty("layers").GetArrayLength());
            Assert.Equal(0, document.GetProperty("tables").GetArrayLength());
            Assert.Equal("made empty", document.GetProperty("description").GetString());

            JsonElement admin = await SendAsync(HttpMethod.Get, $"/rest/admin/services/hosted/{name}/FeatureServer?f=json");
            Assert.Equal(created.GetProperty("itemId").GetString(), admin.GetProperty("serviceItemId").GetString());
            Assert.Contains("Create", admin.GetProperty("capabilities").GetString(), StringComparison.Ordinal);

            // Studio's services list carries it, and says it is empty rather than leaving it out.
            JsonElement? listed = await ListedAsync(name);
            Assert.True(listed is not null, "Studio's services list does not carry the empty service.");
            Assert.True(listed!.Value.GetProperty("empty").GetBoolean(), listed.Value.GetRawText());

            // A second one of that name is refused.
            Assert.NotEmpty(ErrorOf(await CreateServiceAsync(new { name })));

            // A key this server does not make a layer from is refused by name, and nothing is made.
            JsonElement drawn = await AddToDefinitionAsync(name, new
            {
                layers = new object[] { new { name = layer, geometryType = "esriGeometryPoint", drawingInfo = new { renderer = new { } } } },
            });
            Assert.Contains("drawingInfo", ErrorOf(drawn), StringComparison.Ordinal);
            Assert.Equal(0, (await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{name}/FeatureServer?f=json")).GetProperty("layers").GetArrayLength());

            // A member who may not publish cannot add to it.
            string token = await MemberTokenAsync(root, member, "user");
            Assert.NotEmpty(ErrorOf(await AddToDefinitionAsync(name, new { layers = new[] { PointLayer(layer) } }, token)));

            JsonElement added = await AddToDefinitionAsync(name, new { layers = new[] { PointLayer(layer) } });
            Assert.True(added.GetProperty("success").GetBoolean(), added.GetRawText());
            Assert.Equal(0, added.GetProperty("layers")[0].GetProperty("id").GetInt32());
            Assert.Equal(layer, added.GetProperty("layers")[0].GetProperty("name").GetString());

            JsonElement edits = await SendAsync(HttpMethod.Post, $"/rest/services/hosted/{name}/FeatureServer/0/applyEdits", Form(new()
            {
                ["adds"] = JsonSerializer.Serialize(new[]
                {
                    new { attributes = new { label = "Ankara" }, geometry = new { x = 32.85, y = 39.93, spatialReference = new { wkid = 4326 } } },
                }),
                ["f"] = "json",
            }));
            Assert.True(edits.GetProperty("addResults")[0].GetProperty("success").GetBoolean(), edits.GetRawText());
            Assert.Equal(1, await CountAsync(name));

            JsonElement? filled = await ListedAsync(name);
            Assert.False(filled!.Value.GetProperty("empty").GetBoolean());

            // A view layer is not added beside a layer of the service's own.
            JsonElement mixed = await AddToDefinitionAsync(name, new
            {
                layers = new[] { new { adminLayerInfo = new { viewLayerDefinition = new { sourceServiceName = name, sourceLayerId = 0 } } } },
            });
            Assert.NotEmpty(ErrorOf(mixed));
        }
        finally
        {
            if (made)
            {
                await RemoveServiceAsync(name, layer);
            }

            await SendAsync(HttpMethod.Delete, $"/admin/members/{member}");
        }
    }

    /// <summary>
    /// The ArcGIS API for Python's <c>create_view</c>: an empty view service, then <c>addToDefinition</c> naming each
    /// source layer — Studio's view, made in two requests (ADR-257 §11). The client builds the address with two slashes,
    /// <c>/sharing/rest//content/…</c>, which every portal it works against answers.
    /// </summary>
    [Fact]
    public async Task An_empty_view_becomes_a_view_of_the_source_its_addToDefinition_names()
    {
        string root = await RequireServerAsync();
        string tag = Guid.NewGuid().ToString("N")[..8];
        string source = $"zz_cvsrc_{tag}";
        string view = $"zz_cvview_{tag}";
        bool madeSource = false;
        bool madeView = false;

        try
        {
            Assert.True((await CreateServiceAsync(new { name = source, layers = new[] { PointLayer(source) } }))
                .GetProperty("success").GetBoolean());
            madeSource = true;

            await SendAsync(HttpMethod.Post, $"/rest/services/hosted/{source}/FeatureServer/0/applyEdits", Form(new()
            {
                ["adds"] = JsonSerializer.Serialize(new[]
                {
                    new { attributes = new { label = "Ankara" }, geometry = new { x = 32.85, y = 39.93, spatialReference = new { wkid = 4326 } } },
                    new { attributes = new { label = "Izmir" }, geometry = new { x = 27.14, y = 38.42, spatialReference = new { wkid = 4326 } } },
                }),
                ["f"] = "json",
            }));

            // The client's own address, with the slash its resturl already ends in.
            JsonElement created = await SendAsync(HttpMethod.Post, $"//sharing/rest//content/users/{User}/createService", Form(new()
            {
                ["createParameters"] = JsonSerializer.Serialize(new
                {
                    name = view, isView = true, sourceSchemaChangesAllowed = true, isUpdatableView = true, capabilities = "Query",
                    preserveLayerIds = true, options = new { dataSourceType = "relational" },
                }),
                ["isView"] = "True",
                ["outputType"] = "featureService",
                ["f"] = "json",
            }));
            Assert.True(created.TryGetProperty("success", out JsonElement ok) && ok.GetBoolean(), created.GetRawText());
            madeView = true;
            Assert.True(created.GetProperty("isView").GetBoolean());
            Assert.Equal(0, (await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{view}/FeatureServer?f=json")).GetProperty("layers").GetArrayLength());

            // The source layer as the client reads it before it names it.
            JsonElement sourceLayer = await SendAsync(HttpMethod.Get, $"/rest/admin/services/hosted/{source}/FeatureServer/0?f=json");
            Assert.Equal(0, sourceLayer.GetProperty("id").GetInt32());
            Assert.Equal(source, sourceLayer.GetProperty("name").GetString());

            // A field subset is not a view this server makes, and nothing is added.
            JsonElement subset = await AddToDefinitionAsync(view, new
            {
                layers = new[] { new { id = 0, adminLayerInfo = new { viewLayerDefinition = new { sourceServiceName = source, sourceLayerId = 0, sourceLayerFields = "label" } } } },
            });
            Assert.Contains("sourceLayerFields", ErrorOf(subset), StringComparison.Ordinal);

            // A member who may not publish is refused.
            string member = $"zz_cvm_{tag}";
            try
            {
                string token = await MemberTokenAsync(root, member, "user");
                Assert.NotEmpty(ErrorOf(await AddToDefinitionAsync(view, new
                {
                    layers = new[] { new { id = 0, adminLayerInfo = new { viewLayerDefinition = new { sourceServiceName = source, sourceLayerId = 0 } } } },
                }, token)));
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"/admin/members/{member}");
            }

            JsonElement added = await AddToDefinitionAsync(view, new
            {
                layers = new[]
                {
                    new
                    {
                        id = 0, name = source,
                        adminLayerInfo = new { popupInfo = (object?)null, viewLayerDefinition = new { sourceServiceName = source, sourceLayerId = 0, sourceLayerFields = "*" } },
                    },
                },
                tables = Array.Empty<object>(),
            });
            Assert.True(added.GetProperty("success").GetBoolean(), added.GetRawText());

            Assert.Equal(2, await CountAsync(view));

            JsonElement viewDocument = await SendAsync(HttpMethod.Get, $"/rest/services/hosted/{view}/FeatureServer?f=json");
            Assert.True(viewDocument.GetProperty("isView").GetBoolean(), viewDocument.GetRawText());

            JsonElement viewLayer = await SendAsync(HttpMethod.Get, $"/rest/admin/services/hosted/{view}/FeatureServer/0?f=json");
            Assert.Equal(source, viewLayer.GetProperty("adminLayerInfo").GetProperty("viewLayerDefinition").GetProperty("sourceServiceName").GetString());

            // Studio's own view page says what it is a view of.
            JsonElement studio = await SendAsync(HttpMethod.Get, $"/admin/services/{view}/views?folder=hosted");
            Assert.Equal(source, studio.GetProperty("source").GetProperty("name").GetString());

            // The same source layer twice is refused.
            Assert.NotEmpty(ErrorOf(await AddToDefinitionAsync(view, new
            {
                layers = new[] { new { adminLayerInfo = new { viewLayerDefinition = new { sourceServiceName = source, sourceLayerId = 0 } } } },
            })));
        }
        finally
        {
            if (madeView)
            {
                await RemoveServiceAsync(view, view);
            }

            if (madeSource)
            {
                await RemoveServiceAsync(source, source);
            }
        }
    }
}
