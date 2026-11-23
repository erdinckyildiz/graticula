using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Testing;
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
/// — a text code as an index, a range as one slot, Any as no slot, a retired value served as retired — and the protobuf
/// answer are the ones ADR-314 observed an ArcGIS server write.
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
            Assert.False((await GetJsonAsync(serviceRoot)).TryGetProperty("supportedContingentValuesFormats", out _));

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

            // ---- the layer document's flags (ADR-314 §5.4) ----
            JsonElement document = await GetJsonAsync(feature);
            Assert.True(document.GetProperty("hasContingentValuesDefinition").GetBoolean());
            Assert.True(document.GetProperty("hasFieldGroups").GetBoolean());
            Assert.Equal("JSON, PBF", document.GetProperty("supportedContingentValuesFormats").GetString());

            // ---- the service document says both formats (ADR-314 §5.4) ----
            Assert.Equal("JSON, PBF", (await GetJsonAsync(serviceRoot)).GetProperty("supportedContingentValuesFormats").GetString());

            // ---- the fieldGroups resource, which a protobuf reader needs beside the answer ----
            JsonElement fieldGroups = await GetJsonAsync($"{feature}/fieldGroups");
            Assert.Equal(
                "[{\"name\":\"make_model\",\"restrictive\":true,\"fields\":[\"make\",\"model\",\"size\"]},{\"name\":\"loose\",\"restrictive\":false,\"fields\":[\"rating\"]}]",
                fieldGroups.GetProperty("fieldGroups").GetRawText());

            // ---- the resource, in the shape ADR-314 §5.5 observed ----
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

            // A field's id is its position in the group.
            Assert.Equal([0, 1, 2], group.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("id").GetInt32()).ToArray());
            Assert.Equal("esriFieldTypeInteger", group.GetProperty("fields")[1].GetProperty("fieldType").GetString());

            // A layer without subtypes puts its domains and values on the group.
            Assert.False(group.TryGetProperty("subTypes", out _));
            Assert.Equal(makes, group.GetProperty("domains").GetProperty("make").GetString());

            // Value 1 is Code, Code, Range; value 2 is Code, Any, Null; the retired value 3 is served as retired.
            JsonElement[] values = [.. group.GetProperty("contingentValues").EnumerateArray()];
            Assert.Equal([1, 2, 3], values.Select(v => v.GetProperty("id").GetInt32()).ToArray());
            Assert.Equal([3, 3, 4], values[0].GetProperty("types").EnumerateArray().Select(t => t.GetInt32()).ToArray());

            // "AIR" is index 0 of the Makes dictionary, which lists the codes used, in first-use order; a range is one slot.
            JsonElement dictionary = resource.GetProperty("stringDicts").EnumerateArray().Single(d => d.GetProperty("domain").GetString() == makes);
            Assert.Equal(["AIR", "BOE"], dictionary.GetProperty("entries").EnumerateArray().Select(e => e.GetString()!).ToArray());
            Assert.Equal("[0,320,{\"range\":[1.5,4]}]", values[0].GetProperty("values").GetRawText());

            Assert.Equal([3, 1, 2], values[1].GetProperty("types").EnumerateArray().Select(t => t.GetInt32()).ToArray());
            Assert.Equal([1], values[1].GetProperty("values").EnumerateArray().Select(t => t.GetInt32()).ToArray());
            Assert.False(values[1].TryGetProperty("retired", out _));
            Assert.Equal(1, values[2].GetProperty("retired").GetInt32());

            // A text field with no coded domain gets a dictionary named for the field.
            JsonElement loose = cvd.GetProperty("fieldGroups").EnumerateArray().Single(g => g.GetProperty("name").GetString() == "loose");
            Assert.False(loose.GetProperty("restrictive").GetBoolean());
            Assert.Contains(resource.GetProperty("stringDicts").EnumerateArray(), d => d.GetProperty("domain").GetString() == "rating");

            // ---- f=pbf: the protobuf answer decodes to the JSON answer's definition (ADR-314 condition 2) ----
            (HttpStatusCode pbfStatus, string? pbfType, byte[] pbf) = await GetBytesAsync(root, token!, $"{feature}/contingentValues?f=pbf");
            Assert.Equal(HttpStatusCode.OK, pbfStatus);
            Assert.Equal("application/x-protobuf", pbfType);
            ContingentValuesPbfReader.Layer fromPbf = ContingentValuesPbfReader.FromPbf(pbf).Single();
            ContingentValuesPbfReader.Layer fromJson = ContingentValuesPbfReader.FromJson(resource).Single();
            Assert.Equal(fromJson.Groups.Select(g => g.Name), fromPbf.Groups.Select(g => g.Name));
            Assert.Equal(
                ["1/0/live: Code AIR, Code 320, Range 1.5..4", "2/0/live: Code BOE, Any, Null", "3/0/retired: Code BOE, Code 707, Any", "4/0/live: Code Good"],
                fromJson.Groups.SelectMany(g => g.Values).Select(v => v.ToString()));

            // The same, except value 1's Double range: its bound 1.5 is fractional, a case no observed answer showed, so a
            // protobuf reader is sent Any for it (ADR-314 §5.3, D-484).
            Assert.Equal(
                ["1/0/live: Code AIR, Code 320, Any", "2/0/live: Code BOE, Any, Null", "3/0/retired: Code BOE, Code 707, Any", "4/0/live: Code Good"],
                fromPbf.Groups.SelectMany(g => g.Values).Select(v => v.ToString()));

            (HttpStatusCode queryPbfStatus, _, byte[] queryPbf) = await GetBytesAsync(root, token!, $"{serviceRoot}/queryContingentValues?f=pbf&layers=0");
            Assert.Equal(HttpStatusCode.OK, queryPbfStatus);
            Assert.Equal(pbf, queryPbf);

            // ---- compactFormat=false and domainDictionaries (ADR-314 §5.5; D-474 repaid) ----
            JsonElement full = await GetJsonAsync($"{feature}/contingentValues?compactFormat=false&domainDictionaries=trimmed");
            Assert.False(full.TryGetProperty("typeCodes", out _));
            JsonElement fullFirst = full.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups")[0].GetProperty("contingentValues")[0];
            Assert.Equal("[\"Code\",\"Code\",\"Range\"]", fullFirst.GetProperty("types").GetRawText());
            Assert.Equal("[\"Airbus\",320,{\"range\":[1.5,4]}]", fullFirst.GetProperty("values").GetRawText());
            Assert.False(fullFirst.GetProperty("retired").GetBoolean());
            Assert.Equal("{\"BOE\":\"Boeing\",\"AIR\":\"Airbus\"}", full.GetProperty("domainDicts").GetProperty(makes).GetRawText());
            Assert.Equal(400, await StatusAsync(root, token!, $"{feature}/contingentValues?domainDictionaries=all"));

            // ---- 12.2's subsets ----
            JsonElement onlyLoose = await GetJsonAsync($"{feature}/contingentValues?fieldGroupSubset=LOOSE");
            Assert.Equal(["loose"], onlyLoose.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups").EnumerateArray()
                .Select(g => g.GetProperty("name").GetString()!).ToArray());

            JsonElement onlyTwo = await GetJsonAsync($"{feature}/contingentValues?idSubset=2-2&fieldGroupSubset=make_model");
            Assert.Equal([2], onlyTwo.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups")[0]
                .GetProperty("contingentValues").EnumerateArray().Select(v => v.GetProperty("id").GetInt32()).ToArray());

            // ---- the service's operation ----
            JsonElement query = await GetJsonAsync($"{serviceRoot}/queryContingentValues?layers=0");
            Assert.Equal([0], query.GetProperty("contingentValuesDefinitions").EnumerateArray().Select(d => d.GetProperty("layerID").GetInt32()).ToArray());
            JsonElement excluded = await GetJsonAsync($"{serviceRoot}/queryContingentValues?layers=-0");
            Assert.Equal(0, excluded.GetProperty("contingentValuesDefinitions").GetArrayLength());

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
                .Single(g => g.GetProperty("name").GetString() == "make_model").GetProperty("contingentValues")[0];
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
            Assert.False((await GetJsonAsync(serviceRoot)).TryGetProperty("supportedContingentValuesFormats", out _));
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

            // The protobuf answer carries each value's subtype, zigzag, and agrees with the JSON (ADR-314 §5.2).
            (HttpStatusCode status, _, byte[] pbf) = await GetBytesAsync(root, token!, $"{feature}/contingentValues?f=pbf");
            Assert.Equal(HttpStatusCode.OK, status);
            ContingentValuesPbfReader.Group fromPbf = ContingentValuesPbfReader.FromPbf(pbf).Single().Groups.Single();
            Assert.Equal([(1, 2L), (2, 1L)], fromPbf.Values.Select(v => (v.Id, v.Subtype)).ToArray());
            JsonElement answer = await GetJsonAsync($"{feature}/contingentValues");
            Assert.Equal(
                ContingentValuesPbfReader.FromJson(answer).Single().Groups.Single().Values.Select(v => v.ToString()),
                fromPbf.Values.Select(v => v.ToString()));
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token!, json: null);
        }
    }

    private async Task<(HttpStatusCode Status, string? MediaType, byte[] Body)> GetBytesAsync(string root, string token, string path)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{root}{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsByteArrayAsync());
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
