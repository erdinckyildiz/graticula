using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-311: <c>extractChanges</c> on a hosted service that tracks its changes — turned on through <c>updateDefinition</c>,
/// answered with ArcGIS's keys (<i>Extract Changes (Feature Service)</i>), and refusing by name what it does not serve.
/// </summary>
/// <remarks>
/// Every test defines its own layer, turns change tracking on, and drops the layer after, so nothing depends on the
/// fixture's data or on the clock: generations are read from the server's own answers.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ExtractChangesTests : ArcGisClient
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>A layer defined for one test, with change tracking on.</summary>
    private sealed record Tracked(string Service, string Admin, string Folder, string Name)
    {
        public string Layer => Service + "/0";
    }

    [Fact]
    public async Task Change_tracking_is_turned_on_and_off_through_update_definition_and_the_documents_say_so()
    {
        Tracked layer = await DefineAsync(track: false);

        try
        {
            JsonElement before = await GetJsonAsync(layer.Service);
            Assert.DoesNotContain("ChangeTracking", before.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);
            Assert.False(before.TryGetProperty("extractChangesCapabilities", out _));

            (HttpStatusCode refused, string refusedBody) = await SendAsync(HttpMethod.Get, $"{layer.Service}/extractChanges?serverGens=0&f=json");
            Assert.Equal(HttpStatusCode.BadRequest, refused);
            Assert.Contains("Change tracking is not enabled", refusedBody, StringComparison.Ordinal);

            await TrackAsync(layer, true);

            JsonElement on = await GetJsonAsync(layer.Service);
            Assert.EndsWith(",ChangeTracking", on.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);
            JsonElement offered = on.GetProperty("extractChangesCapabilities");
            Assert.True(offered.GetProperty("supportsReturnIdsOnly").GetBoolean());
            Assert.True(offered.GetProperty("supportsLayerQueries").GetBoolean());
            Assert.True(offered.GetProperty("supportsServerGens").GetBoolean());
            JsonElement gens = on.GetProperty("changeTrackingInfo").GetProperty("layerServerGens")[0];
            Assert.Equal(0, gens.GetProperty("id").GetInt32());
            Assert.True(gens.GetProperty("serverGen").GetInt64() >= gens.GetProperty("minServerGen").GetInt64());

            JsonElement layerDocument = await GetJsonAsync(layer.Layer);
            Assert.Contains("ChangeTracking", layerDocument.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);
            Assert.True(layerDocument.GetProperty("isDataArchived").GetBoolean(), "Turning change tracking on did not start the history.");

            JsonElement admin = await GetJsonAsync(layer.Admin);
            Assert.Contains("ChangeTracking", admin.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);

            // A capabilities string without it turns it off, as the reference says; the history stays.
            await TrackAsync(layer, false);
            JsonElement off = await GetJsonAsync(layer.Service);
            Assert.DoesNotContain("ChangeTracking", off.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);
            Assert.True((await GetJsonAsync(layer.Layer)).GetProperty("isDataArchived").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"{layer.Service}/extractChanges?serverGens=0&f=json")).Status);
        }
        finally
        {
            await DropAsync(layer);
        }
    }

    [Fact]
    public async Task Inserts_updates_and_deletes_are_answered_across_two_generations()
    {
        Tracked layer = await DefineAsync();

        try
        {
            long g0 = await GenerationAsync(layer);
            long[] ids = await AddAsync(layer, ("one", 10, 20), ("two", 11, 21));

            JsonElement first = await ExtractAsync(layer, $"serverGens={g0}&returnIdsOnly=true");
            long g1 = first.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64();
            JsonElement firstIds = first.GetProperty("edits")[0].GetProperty("objectIds");
            Assert.Equal(ids.Order(), firstIds.GetProperty("adds").EnumerateArray().Select(e => e.GetInt64()).Order());
            Assert.Empty(firstIds.GetProperty("updates").EnumerateArray());
            Assert.Empty(firstIds.GetProperty("deletes").EnumerateArray());

            string deletedGlobalId = await GlobalIdAsync(layer, ids[1]);
            await Task.Delay(20);
            await PostOkAsync($"{layer.Layer}/updateFeatures", new()
            {
                ["features"] = $"[{{\"attributes\":{{\"objectid\":{ids[0]},\"label\":\"changed\"}}}}]",
            });
            await PostOkAsync($"{layer.Layer}/deleteFeatures", new() { ["objectIds"] = ids[1].ToString(System.Globalization.CultureInfo.InvariantCulture) });

            // Features, from the second generation: the update whole, the delete by GlobalID.
            JsonElement second = await ExtractAsync(layer, $"serverGens={g1}");
            Assert.Equal("esriDataChangesResponseTypeEdits", second.GetProperty("responseType").GetString());
            JsonElement features = second.GetProperty("edits")[0].GetProperty("features");
            Assert.Empty(features.GetProperty("adds").EnumerateArray());
            JsonElement updated = Assert.Single(features.GetProperty("updates").EnumerateArray().ToArray());
            Assert.Equal("changed", updated.GetProperty("attributes").GetProperty("label").GetString());
            Assert.Equal(ids[0], updated.GetProperty("attributes").GetProperty("objectid").GetInt64());
            Assert.Equal(deletedGlobalId, Assert.Single(features.GetProperty("deleteIds").EnumerateArray().ToArray()).GetString(),
                StringComparer.OrdinalIgnoreCase);
            Assert.True(second.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64() >= g1);

            // The pair [g0, g1] is the first window only, whatever happened after it.
            JsonElement bounded = await ExtractAsync(layer, $"serverGens=[{g0},{g1}]&returnIdsOnly=true");
            Assert.Equal(g1, bounded.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64());
            Assert.Equal(2, bounded.GetProperty("edits")[0].GetProperty("objectIds").GetProperty("adds").GetArrayLength());

            // What was asked away is not answered, and fieldsToCompare names the update that changed the field.
            JsonElement onlyDeletes = await ExtractAsync(layer,
                $"serverGens={g1}&returnIdsOnly=true&returnInserts=false&returnUpdates=false");
            JsonElement onlyIds = onlyDeletes.GetProperty("edits")[0].GetProperty("objectIds");
            Assert.Empty(onlyIds.GetProperty("updates").EnumerateArray());
            Assert.Equal(ids[1], Assert.Single(onlyIds.GetProperty("deletes").EnumerateArray().ToArray()).GetInt64());

            JsonElement compared = await ExtractAsync(layer,
                $"serverGens={g1}&returnIdsOnly=true&returnHasGeometryUpdates=true&fieldsToCompare={Uri.EscapeDataString("{\"fields\":[\"label\"]}")}");
            JsonElement edits = compared.GetProperty("edits")[0];
            Assert.Equal(ids[0], Assert.Single(edits.GetProperty("fieldUpdates").EnumerateArray().ToArray()).GetInt64());
            Assert.False(edits.GetProperty("hasGeometryUpdates").GetBoolean());

            // The deleted feature whole, as it was.
            JsonElement whole = await ExtractAsync(layer, $"serverGens={g1}&returnDeletedFeatures=true&returnAttachments=false");
            JsonElement deleted = Assert.Single(whole.GetProperty("edits")[0].GetProperty("features").GetProperty("deletes").EnumerateArray().ToArray());
            Assert.Equal("two", deleted.GetProperty("attributes").GetProperty("label").GetString());
        }
        finally
        {
            await DropAsync(layer);
        }
    }

    [Fact]
    public async Task Layer_queries_with_a_where_clause_answer_only_the_features_they_keep()
    {
        Tracked layer = await DefineAsync();

        try
        {
            long g0 = await GenerationAsync(layer);
            long[] ids = await AddAsync(layer, ("keep", 1, 1), ("drop", 2, 2));
            string queries = Uri.EscapeDataString("{\"0\":{\"queryOption\":\"useFilter\",\"where\":\"label = 'keep'\",\"useGeometry\":false}}");

            JsonElement answer = await ExtractAsync(layer, $"layers=0&serverGens={g0}&returnIdsOnly=true&layerQueries={queries}");
            Assert.Equal(ids[0], Assert.Single(answer.GetProperty("edits")[0].GetProperty("objectIds").GetProperty("adds").EnumerateArray().ToArray()).GetInt64());

            // A geometry that holds only the second feature, as an envelope in the features' own reference.
            JsonElement box = await ExtractAsync(layer,
                $"serverGens={g0}&returnIdsOnly=true&geometry=1.5,1.5,2.5,2.5&geometryType=esriGeometryEnvelope&inSR=4326");
            Assert.Equal(ids[1], Assert.Single(box.GetProperty("edits")[0].GetProperty("objectIds").GetProperty("adds").EnumerateArray().ToArray()).GetInt64());

            (HttpStatusCode bad, string badBody) = await SendAsync(HttpMethod.Get,
                $"{layer.Service}/extractChanges?serverGens={g0}&layerQueries={Uri.EscapeDataString("{\"0\":{\"where\":\"nosuchfield = 1\"}}")}&f=json");
            Assert.Equal(HttpStatusCode.BadRequest, bad);
            Assert.Contains("where clause", badBody, StringComparison.Ordinal);
        }
        finally
        {
            await DropAsync(layer);
        }
    }

    [Fact]
    public async Task An_attachment_added_since_a_generation_is_answered_with_its_address()
    {
        Tracked layer = await DefineAsync();

        try
        {
            long[] ids = await AddAsync(layer, ("photographed", 5, 5));
            string parent = await GlobalIdAsync(layer, ids[0]);
            long g0 = await GenerationAsync(layer);
            await Task.Delay(20);
            int attachment = await AttachAsync(layer, ids[0]);

            JsonElement answer = await ExtractAsync(layer, $"serverGens={g0}");
            JsonElement attachments = answer.GetProperty("edits")[0].GetProperty("attachments");
            JsonElement added = Assert.Single(attachments.GetProperty("adds").EnumerateArray().ToArray());
            Assert.Equal(parent, added.GetProperty("parentGlobalid").GetString(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(attachment, added.GetProperty("attachmentId").GetInt32());
            string url = added.GetProperty("url").GetString()!;
            Assert.EndsWith($"/0/{ids[0]}/attachments/{attachment}", url, StringComparison.Ordinal);

            // The bytes inline when asked, and none at all without attachments.
            JsonElement inline = await ExtractAsync(layer, $"serverGens={g0}&returnAttachmentsDataByUrl=false");
            Assert.Equal(Convert.ToBase64String(Png),
                inline.GetProperty("edits")[0].GetProperty("attachments").GetProperty("adds")[0].GetProperty("data").GetString());
            JsonElement without = await ExtractAsync(layer, $"serverGens={g0}&returnAttachments=false");
            Assert.False(without.GetProperty("edits")[0].TryGetProperty("attachments", out _));
        }
        finally
        {
            await DropAsync(layer);
        }
    }

    [Fact]
    public async Task An_expired_generation_and_what_is_not_served_are_refused_by_name()
    {
        Tracked layer = await DefineAsync();

        try
        {
            long g0 = await GenerationAsync(layer);

            (HttpStatusCode expired, string expiredBody) = await SendAsync(HttpMethod.Get, $"{layer.Service}/extractChanges?serverGens=1&f=json");
            Assert.Equal(HttpStatusCode.BadRequest, expired);
            Assert.Contains("minServerGen", expiredBody, StringComparison.Ordinal);
            Assert.Contains("Query the layer in full", expiredBody, StringComparison.Ordinal);

            foreach ((string query, string named) in new[]
            {
                ($"serverGens={g0}&dataFormat=sqlite", "dataFormat"),
                ($"serverGens={g0}&changesExtentGridCell=medium", "changesExtentGridCell"),
                ($"serverGens={g0}&geometry={Uri.EscapeDataString("{\"x\":1,\"y\":1}")}&geometryType=esriGeometryPoint", "geometryType"),
                ($"serverGens={g0}&layerQueries={Uri.EscapeDataString("{\"0\":{\"includeRelated\":true}}")}", "includeRelated"),
                ($"serverGens={g0}&returnHasGeometryUpdates=true&returnUpdates=false", "returnHasGeometryUpdates"),
                ($"serverGens=[{g0 + 10},{g0}]", "serverGens"),
                ("returnIdsOnly=true", "serverGens"),
            })
            {
                (HttpStatusCode status, string body) = await SendAsync(HttpMethod.Get, $"{layer.Service}/extractChanges?{query}&f=json");
                Assert.True(status == HttpStatusCode.BadRequest, $"{query} answered {(int)status}: {body}");
                Assert.Contains(named, body, StringComparison.Ordinal);
            }

            JsonElement capabilities = (await GetJsonAsync(layer.Service)).GetProperty("extractChangesCapabilities");
            Assert.False(capabilities.TryGetProperty("supportsSqlite", out _));
        }
        finally
        {
            await DropAsync(layer);
        }
    }

    [Fact]
    public async Task An_asynchronous_extract_answers_a_status_url_whose_result_holds_the_changes()
    {
        Tracked layer = await DefineAsync();

        try
        {
            long g0 = await GenerationAsync(layer);
            long[] ids = await AddAsync(layer, ("later", 3, 3));

            JsonElement started = await ExtractAsync(layer, $"serverGens={g0}&async=true");
            string status = started.GetProperty("statusUrl").GetString()!;
            JsonElement done = await PollAsync(status);
            Assert.Equal("Completed", done.GetProperty("status").GetString());
            Assert.Equal("esriDataChangesResponseTypeEdits", done.GetProperty("responseType").GetString());

            Assert.True(done.TryGetProperty("resultUrl", out JsonElement resultUrl), done.GetRawText());
            JsonElement result = await GetAbsoluteAsync(resultUrl.GetString()!);
            Assert.Equal("esriTransportTypeUrl", result.GetProperty("transportType").GetString());
            JsonElement added = Assert.Single(result.GetProperty("edits")[0].GetProperty("features").GetProperty("adds").EnumerateArray().ToArray());
            Assert.Equal(ids[0], added.GetProperty("attributes").GetProperty("objectid").GetInt64());
        }
        finally
        {
            await DropAsync(layer);
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private async Task<Tracked> DefineAsync(bool track = true)
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string name = "zz_xc_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post, $"{root}/admin/hosted/define", token,
            JsonSerializer.Serialize(new
            {
                name,
                geometryType = "Point",
                fields = new[] { new { name = "label", type = "text", nullable = true } },
                sharing = "private",
            }));
        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement.GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Tracked layer = new($"/rest/services/{parts[2]}/{parts[3]}/FeatureServer", $"/rest/admin/services/{parts[2]}/{parts[3]}/FeatureServer",
            parts[2], parts[3]);

        if (track)
        {
            await TrackAsync(layer, true);
        }

        return layer;
    }

    private async Task TrackAsync(Tracked layer, bool on)
    {
        (HttpStatusCode status, string body) = await SendAsync(HttpMethod.Post, $"{layer.Admin}/updateDefinition", new()
        {
            ["updateDefinition"] = JsonSerializer.Serialize(new { capabilities = on ? "Query,Create,Update,Delete,ChangeTracking" : "Query,Create,Update,Delete" }),
        });
        Assert.True(status == HttpStatusCode.OK, $"updateDefinition answered {(int)status}: {body}");
    }

    private async Task DropAsync(Tracked layer)
    {
        string root = await RequireServerAsync();
        await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{layer.Name}?folder={layer.Folder}&drop=true", (await TokenAsync(root))!, json: null);
    }

    private async Task<long> GenerationAsync(Tracked layer)
    {
        long gen = (await GetJsonAsync(layer.Service)).GetProperty("changeTrackingInfo").GetProperty("layerServerGens")[0]
            .GetProperty("serverGen").GetInt64();

        // A history row is stamped at its transaction's start, and a generation is a millisecond: wait past it.
        await Task.Delay(20);
        return gen;
    }

    private async Task<long[]> AddAsync(Tracked layer, params (string Label, double X, double Y)[] features)
    {
        string json = JsonSerializer.Serialize(features.Select(f => new
        {
            geometry = new { x = f.X, y = f.Y, spatialReference = new { wkid = 4326 } },
            attributes = new { label = f.Label },
        }));
        JsonElement added = await PostOkAsync($"{layer.Layer}/addFeatures", new() { ["features"] = json });
        return [.. added.GetProperty("addResults").EnumerateArray().Select(r => r.GetProperty("objectId").GetInt64())];
    }

    private async Task<string> GlobalIdAsync(Tracked layer, long objectId) =>
        (await GetJsonAsync($"{layer.Layer}/query?objectIds={objectId}&outFields=globalid&returnGeometry=false"))
            .GetProperty("features")[0].GetProperty("attributes").GetProperty("globalid").GetString()!;

    private async Task<int> AttachAsync(Tracked layer, long objectId)
    {
        string root = await RequireServerAsync();
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "attachment", "photo.png");
        form.Add(new StringContent("json"), "f");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{layer.Layer}/{objectId}/addAttachment") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(root));
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"addAttachment answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("addAttachmentResult").GetProperty("objectId").GetInt32();
    }

    private async Task<JsonElement> ExtractAsync(Tracked layer, string query)
    {
        (HttpStatusCode status, string body) = await SendAsync(HttpMethod.Get, $"{layer.Service}/extractChanges?{query}&f=json");
        Assert.True(status == HttpStatusCode.OK, $"extractChanges?{query} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> PollAsync(string statusUrl)
    {
        for (int i = 0; i < 100; i++)
        {
            JsonElement status = await GetAbsoluteAsync(statusUrl + "?f=json");

            if (status.GetProperty("status").GetString() is "Completed" or "Failed")
            {
                return status;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"{statusUrl} did not finish.");
    }

    private async Task<JsonElement> GetAbsoluteAsync(string url)
    {
        string root = await RequireServerAsync();
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(root));
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> PostOkAsync(string path, Dictionary<string, string> form)
    {
        (HttpStatusCode status, string body) = await SendAsync(HttpMethod.Post, path, form);
        Assert.True(status == HttpStatusCode.OK && !body.Contains("\"error\"", StringComparison.Ordinal), $"{path} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, Dictionary<string, string>? form = null)
    {
        string root = await RequireServerAsync();
        using HttpRequestMessage request = new(method, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(root));

        if (form is not null)
        {
            form["f"] = "json";
            request.Content = new FormUrlEncodedContent(form);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
