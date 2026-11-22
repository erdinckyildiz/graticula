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
/// A hosted layer's contingent values — ADR-312: kept from Studio's admin route and ArcGIS's <c>addToDefinition</c>,
/// served by the layer document's <c>hasContingentValuesDefinition</c>, the layer's <c>contingentValues</c> resource and
/// the service's <c>queryContingentValues</c>, and never a reason for <c>applyEdits</c> to refuse.
/// </summary>
/// <remarks>
/// The keys are the public reference's
/// (<see href="https://developers.arcgis.com/rest/services-reference/enterprise/fs-contingent-values/"/>); the encoding
/// of a text code as an index, of a range as two slots and of Any as no slot is ADR-312 §5.9's INFERRED reading.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ContingentValuesTests : ArcGisClient
{
    [Fact]
    public async Task A_definition_is_kept_served_filtered_and_never_refuses_an_edit()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string layer = "zz_adr312_" + suffix;
        string makes = "Makes_" + suffix;

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token!,
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new object[]
                {
                    new { name = "make", type = "text", nullable = true },
                    new { name = "model", type = "integer", nullable = true },
                    new { name = "size", type = "double", nullable = true },
                    new { name = "rating", type = "text", nullable = false },
                },
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement.GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string folder = parts[2];
        string service = parts[3];
        string serviceRoot = feature[..feature.LastIndexOf('/')];

        try
        {
            (HttpStatusCode domains, string domainSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/fields", token!,
                JsonSerializer.Serialize(new
                {
                    overrides = new object[]
                    {
                        new
                        {
                            column = "make",
                            domain = new
                            {
                                type = "codedValue",
                                name = makes,
                                codedValues = new object[] { new { code = "BOE", name = "Boeing" }, new { code = "AIR", name = "Airbus" } },
                            },
                        },
                    },
                }));
            Assert.True(domains == HttpStatusCode.OK, $"Setting a domain answered {(int)domains}: {domainSaid}");

            // Before: the document says nothing, as the reference allows for false.
            Assert.False((await GetJsonAsync(feature)).TryGetProperty("hasContingentValuesDefinition", out _));

            // ---- Studio's door ----
            const string definition = """
                {"fieldGroups": [
                   {"name": "make_model", "restrictive": true, "fields": ["make", "model", "size"]},
                   {"name": "loose", "restrictive": false, "fields": ["rating"]}],
                 "contingentValues": [
                   {"id": 1, "fieldGroup": "make_model", "entries": [["make", "CODED_VALUE", "AIR"], ["model", "CODED_VALUE", 320], ["size", "RANGE", [1.5, 4]]]},
                   {"id": 2, "fieldGroup": "make_model", "entries": [["make", "CODED_VALUE", "BOE"], ["model", "ANY", null], ["size", "NULL", null]]},
                   {"id": 3, "fieldGroup": "make_model", "retired": true, "entries": [["make", "CODED_VALUE", "BOE"], ["model", "CODED_VALUE", 707], ["size", "ANY", null]]},
                   {"id": 4, "fieldGroup": "loose", "entries": [["rating", "CODED_VALUE", "Good"]]}]}
                """;

            (HttpStatusCode set, string stored) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/contingent-values", token!, definition);
            Assert.True(set == HttpStatusCode.OK, $"Storing contingent values answered {(int)set}: {stored}");
            Assert.Equal(4, JsonDocument.Parse(stored).RootElement.GetProperty("definition").GetProperty("contingentValues").GetArrayLength());

            // ---- the layer document's flag ----
            Assert.True((await GetJsonAsync(feature)).GetProperty("hasContingentValuesDefinition").GetBoolean());

            // ---- the resource ----
            JsonElement resource = await GetJsonAsync($"{feature}/contingentValues");
            Assert.Equal(
                ["Unknown", "Any", "Null", "Code", "Range"],
                resource.GetProperty("typeCodes").EnumerateArray().Select(e => e.GetString()!).ToArray());

            JsonElement cvd = resource.GetProperty("contingentValuesDefinition");
            Assert.Equal(0, cvd.GetProperty("layerID").GetInt32());
            Assert.Equal("esriGeometryPoint", cvd.GetProperty("geometryType").GetString());
            Assert.False(cvd.GetProperty("hasSubType").GetBoolean());

            JsonElement group = cvd.GetProperty("fieldGroups").EnumerateArray().Single(g => g.GetProperty("name").GetString() == "make_model");
            Assert.True(group.GetProperty("restrictive").GetBoolean());

            // A field's id is its position in the layer document's fields (INFERRED, §5.9 item 5).
            string[] documentFields = [.. (await GetJsonAsync(feature)).GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)];
            foreach (JsonElement field in group.GetProperty("fields").EnumerateArray())
            {
                Assert.Equal(Array.IndexOf(documentFields, field.GetProperty("name").GetString()), field.GetProperty("id").GetInt32());
            }

            Assert.Equal("esriFieldTypeInteger", group.GetProperty("fields")[1].GetProperty("fieldType").GetString());

            JsonElement kind = group.GetProperty("subTypes").EnumerateArray().Single();
            Assert.Equal(0, kind.GetProperty("code").GetInt64());
            Assert.Equal(makes, kind.GetProperty("domains").GetProperty("make").GetString());

            // The retired value 3 is not served (§5.9 item 7); value 1 is Code, Code, Range; value 2 is Code, Any, Null.
            JsonElement[] values = [.. kind.GetProperty("contingentValues").EnumerateArray()];
            Assert.Equal([1, 2], values.Select(v => v.GetProperty("id").GetInt32()).ToArray());

            JsonElement first = values[0];
            Assert.Equal([3, 3, 4], first.GetProperty("types").EnumerateArray().Select(t => t.GetInt32()).ToArray());

            JsonElement[] slots = [.. first.GetProperty("values").EnumerateArray()];
            Assert.Equal(4, slots.Length);

            // "AIR" is index 1 of the Makes dictionary, which lists the domain's codes in its order.
            JsonElement dictionary = resource.GetProperty("stringDicts").EnumerateArray().Single(d => d.GetProperty("domain").GetString() == makes);
            Assert.Equal(["BOE", "AIR"], dictionary.GetProperty("entries").EnumerateArray().Select(e => e.GetString()!).ToArray());
            Assert.Equal(1, slots[0].GetInt32());
            Assert.Equal(320, slots[1].GetInt32());
            Assert.Equal(1.5m, slots[2].GetDecimal());
            Assert.Equal(4m, slots[3].GetDecimal());

            JsonElement second = values[1];
            Assert.Equal([3, 1, 2], second.GetProperty("types").EnumerateArray().Select(t => t.GetInt32()).ToArray());
            Assert.Equal([0], second.GetProperty("values").EnumerateArray().Select(t => t.GetInt32()).ToArray());

            // A text field with no coded domain gets a dictionary named for the field.
            JsonElement loose = cvd.GetProperty("fieldGroups").EnumerateArray().Single(g => g.GetProperty("name").GetString() == "loose");
            Assert.False(loose.GetProperty("restrictive").GetBoolean());
            Assert.Contains(resource.GetProperty("stringDicts").EnumerateArray(), d => d.GetProperty("domain").GetString() == "rating");

            // ---- 12.2's subsets ----
            JsonElement onlyLoose = await GetJsonAsync($"{feature}/contingentValues?fieldGroupSubset=LOOSE");
            Assert.Equal(["loose"], onlyLoose.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups").EnumerateArray()
                .Select(g => g.GetProperty("name").GetString()!).ToArray());

            JsonElement onlyTwo = await GetJsonAsync($"{feature}/contingentValues?idSubset=2-3&fieldGroupSubset=make_model");
            Assert.Equal([2], onlyTwo.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups")[0].GetProperty("subTypes")[0]
                .GetProperty("contingentValues").EnumerateArray().Select(v => v.GetProperty("id").GetInt32()).ToArray());

            // ---- the service's operation ----
            JsonElement query = await GetJsonAsync($"{serviceRoot}/queryContingentValues?layers=0");
            Assert.Equal([0], query.GetProperty("contingentValuesDefinitions").EnumerateArray().Select(d => d.GetProperty("layerID").GetInt32()).ToArray());
            JsonElement excluded = await GetJsonAsync($"{serviceRoot}/queryContingentValues?layers=-0");
            Assert.Equal(0, excluded.GetProperty("contingentValuesDefinitions").GetArrayLength());

            // ---- what is refused by name rather than answered wrongly (D-474) ----
            Assert.Equal(400, await StatusAsync(root, token!, $"{feature}/contingentValues?f=pbf"));
            Assert.Equal(400, await StatusAsync(root, token!, $"{serviceRoot}/queryContingentValues?f=json&compactFormat=false"));

            // ---- applyEdits does not refuse a combination no value allows (ArcGIS parity, §5.7) ----
            JsonElement written = await PostAsync(root, token!, $"{feature}/applyEdits",
                ("adds", "[{\"geometry\":{\"x\":1,\"y\":2,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"make\":\"AIR\",\"model\":999,\"size\":100,\"rating\":\"Bad\"}}]"));
            JsonElement result = written.GetProperty("addResults")[0];
            Assert.True(result.GetProperty("success").GetBoolean(), written.ToString());

            JsonElement read = await GetJsonAsync($"{feature}/query?where=model%3D999&outFields=make,model,size");
            Assert.Equal("AIR", read.GetProperty("features")[0].GetProperty("attributes").GetProperty("make").GetString());

            // ---- Studio's door refuses by name ----
            (HttpStatusCode unknown, string unknownSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/contingent-values", token!,
                "{\"fieldGroups\":[{\"name\":\"g\",\"fields\":[\"nosuch\"]}]}");
            Assert.Equal(HttpStatusCode.BadRequest, unknown);
            Assert.Contains("nosuch", unknownSaid, StringComparison.Ordinal);

            (HttpStatusCode notCode, string notCodeSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/contingent-values", token!,
                "{\"fieldGroups\":[{\"name\":\"g\",\"fields\":[\"make\"]}],\"contingentValues\":[{\"id\":1,\"fieldGroup\":\"g\",\"entries\":[[\"make\",\"CODED_VALUE\",\"ZZZ\"]]}]}");
            Assert.Equal(HttpStatusCode.BadRequest, notCode);
            Assert.Contains(makes, notCodeSaid, StringComparison.Ordinal);

            (HttpStatusCode notNull, string notNullSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/contingent-values", token!,
                "{\"fieldGroups\":[{\"name\":\"g\",\"fields\":[\"rating\"]}],\"contingentValues\":[{\"id\":1,\"fieldGroup\":\"g\",\"entries\":[[\"rating\",\"NULL\",null]]}]}");
            Assert.Equal(HttpStatusCode.BadRequest, notNull);
            Assert.Contains("nulls", notNullSaid, StringComparison.Ordinal);

            // ---- ArcGIS's door: addToDefinition with a contingentValuesDefinition (12.2) ----
            string admin = $"/rest/admin/services/{folder}/{service}/FeatureServer/0";
            JsonElement addedGroup = await PostAsync(root, token!, $"{admin}/addToDefinition",
                ("addToDefinition", "{\"contingentValuesDefinition\":{\"fieldGroups\":[{\"name\":\"model_size\",\"contingentValues\":{\"fields\":[{\"name\":\"model\"},{\"name\":\"size\"}]}}]}}"));
            Assert.True(addedGroup.GetProperty("success").GetBoolean(), addedGroup.ToString());

            JsonElement addedValues = await PostAsync(root, token!, $"{admin}/addToDefinition",
                ("addToDefinition", $"{{\"contingentValuesDefinition\":{{\"stringDicts\":[{{\"domain\":\"{makes}\",\"entries\":[\"BOE\",\"AIR\"]}}],\"fieldGroups\":[{{\"name\":\"make_model\",\"restrictive\":true,\"subTypes\":[{{\"code\":0,\"contingentValues\":[{{\"types\":[3,3,1],\"values\":[0,737]}}]}}]}}]}}}}"));
            Assert.True(addedValues.GetProperty("success").GetBoolean(), addedValues.ToString());
            Assert.Equal([5], addedValues.GetProperty("addedContingentValueIds").EnumerateArray().Select(i => i.GetInt32()).ToArray());

            JsonElement after = await GetJsonAsync($"{feature}/contingentValues?idSubset=5");
            JsonElement five = after.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups").EnumerateArray()
                .Single(g => g.GetProperty("name").GetString() == "make_model").GetProperty("subTypes")[0].GetProperty("contingentValues")[0];
            Assert.Equal(737, five.GetProperty("values")[1].GetInt32());
            Assert.Contains(after.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups").EnumerateArray(),
                g => g.GetProperty("name").GetString() == "model_size");

            JsonElement clash = await PostAsync(root, token!, $"{admin}/addToDefinition",
                ("addToDefinition", "{\"contingentValuesDefinition\":{\"fieldGroups\":[{\"name\":\"MODEL_SIZE\",\"fields\":[{\"name\":\"model\"}]}]}}"),
                expectFailure: true);
            Assert.Contains("already exists", clash.ToString(), StringComparison.Ordinal);

            // ---- removing it all takes the flag away ----
            (HttpStatusCode cleared, string clearedSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/contingent-values", token!, "{\"fieldGroups\":[],\"contingentValues\":[]}");
            Assert.True(cleared == HttpStatusCode.OK, clearedSaid);
            Assert.False((await GetJsonAsync(feature)).TryGetProperty("hasContingentValuesDefinition", out _));
            Assert.Equal(0, (await GetJsonAsync($"{feature}/contingentValues")).GetProperty("contingentValuesDefinition").GetProperty("fieldGroups").GetArrayLength());
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{service}?folder={folder}&drop=true", token!, json: null);
            (HttpStatusCode listed, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/domains", token!, json: null);

            if (listed == HttpStatusCode.OK)
            {
                foreach (JsonElement domain in JsonDocument.Parse(body).RootElement.GetProperty("domains").EnumerateArray()
                    .Where(d => d.GetProperty("name").GetString() == makes))
                {
                    await RequestAsync(HttpMethod.Delete, $"{root}/admin/domains/{domain.GetProperty("id").GetString()}", token!, json: null);
                }
            }
        }
    }

    [Fact]
    public async Task A_layer_with_subtypes_serves_its_values_under_each_subtype()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_adr312s_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token!,
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new object[]
                {
                    new { name = "kind", type = "smallinteger", nullable = true },
                    new { name = "a", type = "integer", nullable = true },
                    new { name = "b", type = "integer", nullable = true },
                },
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement.GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);

        try
        {
            (HttpStatusCode typed, string typedSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/fields", token!,
                JsonSerializer.Serialize(new
                {
                    overrides = Array.Empty<object>(),
                    subtypes = new { field = "kind", defaultCode = 1, types = new object[] { new { code = 1, name = "One" }, new { code = 2, name = "Two" } } },
                }));
            Assert.True(typed == HttpStatusCode.OK, typedSaid);

            // The subtype column cannot be in a group, and a value on a subtyped layer names a subtype.
            (HttpStatusCode own, string ownSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/contingent-values", token!,
                "{\"fieldGroups\":[{\"name\":\"g\",\"fields\":[\"kind\",\"a\"]}]}");
            Assert.Equal(HttpStatusCode.BadRequest, own);
            Assert.Contains("kind", ownSaid, StringComparison.Ordinal);

            (HttpStatusCode untyped, string untypedSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/contingent-values", token!,
                "{\"fieldGroups\":[{\"name\":\"g\",\"fields\":[\"a\",\"b\"]}],\"contingentValues\":[{\"id\":1,\"fieldGroup\":\"g\",\"entries\":[[\"a\",\"CODED_VALUE\",1],[\"b\",\"ANY\",null]]}]}");
            Assert.Equal(HttpStatusCode.BadRequest, untyped);
            Assert.Contains("subtype", untypedSaid, StringComparison.Ordinal);

            (HttpStatusCode set, string stored) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/contingent-values", token!,
                "{\"fieldGroups\":[{\"name\":\"g\",\"fields\":[\"a\",\"b\"]}],\"contingentValues\":["
                + "{\"fieldGroup\":\"g\",\"subtype\":2,\"entries\":[[\"a\",\"CODED_VALUE\",1],[\"b\",\"CODED_VALUE\",2]]},"
                + "{\"fieldGroup\":\"g\",\"subtype\":1,\"entries\":[[\"a\",\"CODED_VALUE\",3],[\"b\",\"ANY\",null]]}]}");
            Assert.True(set == HttpStatusCode.OK, stored);

            // Values with no id take one more than the highest held.
            Assert.Equal([1, 2], JsonDocument.Parse(stored).RootElement.GetProperty("definition").GetProperty("contingentValues")
                .EnumerateArray().Select(v => v.GetProperty("id").GetInt32()).ToArray());

            JsonElement cvd = (await GetJsonAsync($"{feature}/contingentValues")).GetProperty("contingentValuesDefinition");
            Assert.True(cvd.GetProperty("hasSubType").GetBoolean());

            JsonElement[] kinds = [.. cvd.GetProperty("fieldGroups")[0].GetProperty("subTypes").EnumerateArray()];
            Assert.Equal([1L, 2L], kinds.Select(k => k.GetProperty("code").GetInt64()).ToArray());
            Assert.Equal(["One", "Two"], kinds.Select(k => k.GetProperty("name").GetString()!).ToArray());
            Assert.Equal(2, kinds[0].GetProperty("contingentValues")[0].GetProperty("id").GetInt32());
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token!, json: null);
        }
    }

    private async Task<int> StatusAsync(string root, string token, string path)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{root}{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (int)response.StatusCode;
    }

    private async Task<JsonElement> PostAsync(
        string root, string token, string path, (string Key, string Value) field, bool expectFailure = false)
    {
        using FormUrlEncodedContent form = new([new(field.Key, field.Value), new("f", "json")]);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{path}")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(
            expectFailure ? !response.IsSuccessStatusCode : response.IsSuccessStatusCode,
            $"{path} answered {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
