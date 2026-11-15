using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A hosted service's admin document and ArcGIS's <c>updateDefinition</c> — both 404 until the ArcGIS gap review of
/// 2026-10-17, so the ArcGIS API for Python's <c>manager.update_definition</c> had nothing to call.
/// </summary>
[Collection("catalogue walk")]
public sealed class UpdateDefinitionTests : ArcGisClient
{
    private static string Editable()
    {
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(qualified), "GRATICULA_TEST_EDITABLE is not set, so there is no hosted service to change.");
        return qualified;
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, Dictionary<string, string> form)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{path}") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_description_is_changed_through_update_definition_and_read_back_from_the_service()
    {
        string service = Editable();
        string admin = $"/rest/admin/services/{service}/FeatureServer";

        JsonElement document = await GetJsonAsync(admin);
        Assert.Equal("FeatureServer", document.GetProperty("adminServiceInfo").GetProperty("type").GetString());
        Assert.Contains("Query", document.GetProperty("capabilities").GetString(), StringComparison.Ordinal);
        string before = document.GetProperty("serviceDescription").GetString() ?? string.Empty;

        string marker = $"Changed by updateDefinition {Guid.NewGuid():N}";

        try
        {
            (HttpStatusCode status, string body) = await PostAsync($"{admin}/updateDefinition", new()
            {
                ["updateDefinition"] = JsonSerializer.Serialize(new { serviceDescription = marker }),
                ["f"] = "json",
            });

            Assert.True(status == HttpStatusCode.OK, body);
            Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean(), body);
            // The service document writes it as `description` (FeatureServerMetadataWriter.Service).
            Assert.Equal(marker, (await GetJsonAsync($"/rest/services/{service}/FeatureServer")).GetProperty("description").GetString());
        }
        finally
        {
            await PostAsync($"{admin}/updateDefinition", new()
            {
                ["updateDefinition"] = JsonSerializer.Serialize(new { serviceDescription = before }),
                ["f"] = "json",
            });
        }
    }

    [Fact]
    public async Task A_key_this_server_does_not_change_is_refused_by_name_and_nothing_is_written()
    {
        string service = Editable();
        string admin = $"/rest/admin/services/{service}/FeatureServer";
        string before = (await GetJsonAsync(admin)).GetProperty("serviceDescription").GetString() ?? string.Empty;

        (HttpStatusCode status, string body) = await PostAsync($"{admin}/updateDefinition", new()
        {
            ["updateDefinition"] = JsonSerializer.Serialize(new { serviceDescription = "should not land", hasStaticData = true }),
            ["f"] = "json",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("hasStaticData", body, StringComparison.Ordinal);
        Assert.Equal(before, (await GetJsonAsync(admin)).GetProperty("serviceDescription").GetString() ?? string.Empty);

        // Sync is kept since ADR-288 (UpdateDefinitionTests.Sync_is_turned_on_and_off_through_update_definition);
        // ChangeTracking is still not served, and is refused by name.
        (HttpStatusCode tracking, string trackingBody) = await PostAsync($"{admin}/updateDefinition", new()
        {
            ["updateDefinition"] = JsonSerializer.Serialize(new { capabilities = "Query,ChangeTracking" }),
            ["f"] = "json",
        });

        Assert.Equal(HttpStatusCode.BadRequest, tracking);
        Assert.Contains("ChangeTracking", trackingBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The typical script — capabilities with <c>hasStaticData</c> beside them — was refused whole until the ArcGIS
    /// veteran's review of 2026-11-03. A derived key that agrees is accepted; one that contradicts, a documented key this
    /// server does not keep, and an unknown key are each refused by name.
    /// </summary>
    [Fact]
    public async Task The_typical_script_is_accepted_and_each_key_this_server_cannot_hold_is_refused_by_name()
    {
        string service = Editable();
        string admin = $"/rest/admin/services/{service}/FeatureServer";
        string capabilities = (await GetJsonAsync(admin)).GetProperty("capabilities").GetString()!;

        Assert.Contains("Update", capabilities, StringComparison.Ordinal);

        (HttpStatusCode typical, string typicalBody) = await PostAsync($"{admin}/updateDefinition", new()
        {
            ["updateDefinition"] = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["capabilities"] = capabilities,
                ["hasStaticData"] = false,
                ["allowGeometryUpdates"] = "true",
                ["syncEnabled"] = false,
            }),
            ["f"] = "json",
        });

        Assert.True(typical == HttpStatusCode.OK, typicalBody);
        Assert.Equal(capabilities, (await GetJsonAsync(admin)).GetProperty("capabilities").GetString());

        foreach ((string key, object value) in ((string, object)[])
        [
            ("allowGeometryUpdates", false),
            ("syncCapabilities", new { supportsPerReplicaSync = true }),
            ("xssPreventionInfo", new { xssPreventionEnabled = true }),
            ("zz_not_a_key", 1),
        ])
        {
            (HttpStatusCode status, string body) = await PostAsync($"{admin}/updateDefinition", new()
            {
                ["updateDefinition"] = JsonSerializer.Serialize(new Dictionary<string, object> { [key] = value }),
                ["f"] = "json",
            });

            Assert.True(status == HttpStatusCode.BadRequest, $"{key}: {(int)status} {body}");
            Assert.Contains(key, body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// ADR-288: <c>syncEnabled</c> turns offline sync on and off for a hosted service, the documents say so, and the
    /// replica operations answer by name — without a mobile geodatabase writer installed, which this fixture has not,
    /// a replica is refused with 501 rather than failing, and <c>perReplica</c> with ArcGIS's own sentence.
    /// </summary>
    [Fact]
    public async Task Sync_is_turned_on_and_off_through_update_definition()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string name = "zz_sync_" + Guid.NewGuid().ToString("N")[..8];

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
        string admin = $"/rest/admin/services/{parts[2]}/{parts[3]}/FeatureServer";
        string service = $"/rest/services/{parts[2]}/{parts[3]}/FeatureServer";

        try
        {
            JsonElement before = await GetJsonAsync(service);
            Assert.False(before.GetProperty("syncEnabled").GetBoolean());
            Assert.DoesNotContain("Sync", before.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);

            (HttpStatusCode on, string onBody) = await PostAsync($"{admin}/updateDefinition", new()
            {
                ["updateDefinition"] = """{"syncEnabled":true}""",
                ["f"] = "json",
            });

            Assert.True(on == HttpStatusCode.OK, onBody);

            JsonElement synced = await GetJsonAsync(service);
            Assert.True(synced.GetProperty("syncEnabled").GetBoolean());
            Assert.EndsWith(",Sync", synced.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);
            Assert.False(synced.GetProperty("syncCapabilities").GetProperty("supportsPerReplicaSync").GetBoolean());

            JsonElement layer = await GetJsonAsync($"{service}/0");
            Assert.Contains("Sync", layer.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);
            Assert.True(layer.GetProperty("isDataArchived").GetBoolean(), "Enabling sync did not turn the layer's history on.");

            (HttpStatusCode perReplica, string perReplicaBody) = await PostAsync($"{service}/createReplica", new()
            {
                ["f"] = "json",
                ["layers"] = "0",
                ["dataFormat"] = "sqlite",
                ["syncModel"] = "perReplica",
            });

            Assert.True(perReplica == HttpStatusCode.BadRequest, perReplicaBody);
            Assert.Contains("Enterprise hosted feature services do not support replicas of type 'perReplica'.", perReplicaBody, StringComparison.Ordinal);

            (HttpStatusCode json, string jsonBody) = await PostAsync($"{service}/createReplica", new()
            {
                ["f"] = "json",
                ["layers"] = "0",
                ["dataFormat"] = "json",
                ["syncModel"] = "perLayer",
            });

            // A JSON replica is written by this server, whatever mobile geodatabase writer a deployment installed (ADR-288 §11a).
            Assert.True(json == HttpStatusCode.OK, jsonBody);
            Assert.Contains("esriReplicaResponseTypeData", jsonBody, StringComparison.Ordinal);

            (HttpStatusCode replica, string replicaBody) = await PostAsync($"{service}/createReplica", new()
            {
                ["f"] = "json",
                ["layers"] = "0",
                ["dataFormat"] = "sqlite",
                ["syncModel"] = "perLayer",
                ["replicaName"] = "conformance_1",
            });

            // The writer is installed by the deployment (ADR-288 §6); without one, a clear refusal and nothing made.
            Assert.True(replica is HttpStatusCode.NotImplemented or HttpStatusCode.OK, replicaBody);

            (HttpStatusCode off, string offBody) = await PostAsync($"{admin}/updateDefinition", new()
            {
                ["updateDefinition"] = """{"syncEnabled":false}""",
                ["f"] = "json",
            });

            Assert.True(off == HttpStatusCode.OK, offBody);
            Assert.False((await GetJsonAsync(service)).GetProperty("syncEnabled").GetBoolean());
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token, json: null);
        }
    }

    private static bool Absent(JsonElement document, string key) =>
        !document.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null;

    /// <summary>
    /// <c>editorTrackingInfo</c> turns editor tracking and the own-features rule on and off, through the work Studio's
    /// own buttons do — on a layer defined here, because tracking adds four columns to its table.
    /// </summary>
    [Fact]
    public async Task Editor_tracking_is_turned_on_and_off_through_update_definition()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string name = "zz_updef_" + Guid.NewGuid().ToString("N")[..8];

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
        string admin = $"/rest/admin/services/{parts[2]}/{parts[3]}/FeatureServer";

        try
        {
            // Others may not update while editors are held to their own: a split this server does not keep.
            (HttpStatusCode split, string splitBody) = await PostAsync($"{admin}/updateDefinition", new()
            {
                ["updateDefinition"] = """{"editorTrackingInfo":{"enableEditorTracking":true,"enableOwnershipAccessControl":true,"allowOthersToUpdate":true,"allowOthersToDelete":false}}""",
                ["f"] = "json",
            });

            Assert.True(split == HttpStatusCode.BadRequest, splitBody);
            Assert.Contains("allowOthersToUpdate", splitBody, StringComparison.Ordinal);
            Assert.True(Absent(await GetJsonAsync(feature), "editFieldsInfo"), "The refused definition turned tracking on.");

            (HttpStatusCode on, string onBody) = await PostAsync($"{admin}/updateDefinition", new()
            {
                ["updateDefinition"] = """{"editorTrackingInfo":{"enableEditorTracking":true,"enableOwnershipAccessControl":true,"allowOthersToUpdate":false,"allowOthersToDelete":false}}""",
                ["f"] = "json",
            });

            Assert.True(on == HttpStatusCode.OK, onBody);
            JsonElement tracked = await GetJsonAsync(feature);
            Assert.Equal("created_user", tracked.GetProperty("editFieldsInfo").GetProperty("creatorField").GetString());
            Assert.False(tracked.GetProperty("ownershipBasedAccessControlForFeatures").GetProperty("allowOthersToUpdate").GetBoolean());

            (HttpStatusCode off, string offBody) = await PostAsync($"{admin}/updateDefinition", new()
            {
                ["updateDefinition"] = """{"editorTrackingInfo":{"enableEditorTracking":false,"enableOwnershipAccessControl":false}}""",
                ["f"] = "json",
            });

            Assert.True(off == HttpStatusCode.OK, offBody);
            JsonElement untracked = await GetJsonAsync(feature);
            Assert.True(Absent(untracked, "editFieldsInfo"), "Tracking was not turned off.");
            Assert.True(Absent(untracked, "ownershipBasedAccessControlForFeatures"), "The own-features rule was not turned off.");
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token, json: null);
        }
    }
}
