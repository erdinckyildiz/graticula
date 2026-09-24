using System;
using System.Collections.Generic;
using System.Globalization;
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
/// A domain or a subtype this server reports is one it enforces on write — ADR-065, and ADR-013
/// condition 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>The condition's own test, in its own words</b>: not that the document contains the key, but
/// that a write of a value outside the domain is refused and that the refusal names the domain.
/// Through both writing faces, because a drop-down that ArcGIS <c>applyEdits</c> enforces and OGC
/// API Features does not is a drop-down a second client can walk around.
/// </para>
/// <para>
/// <b>Its own layer, defined and dropped here</b>, as <see cref="EditorTrackingConformanceTests"/>
/// does: domains change what every write to a layer accepts, and every other test reads the
/// fixture's layers expecting to write what it likes.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class DomainConformanceTests : ArcGisClient
{
    private const string MergePatch = "application/merge-patch+json";

    private static readonly object Materials = new
    {
        type = "codedValue",
        name = "Material",
        codedValues = new object[]
        {
            new { code = "CU", name = "Copper" },
            new { code = "PVC", name = "PVC" },
            new { code = "DI", name = "Ductile iron" },
        },
    };

    private static readonly object Diameters = new { type = "range", name = "Diameter", range = new[] { 50, 1200 } };

    private static readonly int[] TenAtMost = [0, 10];

    [Fact]
    public async Task A_domain_the_document_reports_is_refused_outside_on_both_faces()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_adr065_" + Guid.NewGuid().ToString("N")[..8];

        // ADR-087: the names below are the server's now, not this layer's, and a run that stopped before its
        // cleanup leaves them behind with whatever values it gave them. One left with other values would make
        // this save a 409 — measured 2026-09-24, from a console run that died mid-test — so they go first.
        await DeleteSharedAsync(root, token!, "Material", "Diameter", "MainMaterial");

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
                    new { name = "label", type = "text", nullable = true },
                    new { name = "kind", type = "smallinteger", nullable = true },
                    new { name = "material", type = "text", nullable = true },
                    new { name = "diameter", type = "integer", nullable = true },
                },
                sharing = "public",
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement
            .GetProperty("services").GetProperty("feature").GetString()!;

        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string folder = parts[2];
        string service = parts[3];

        try
        {
            // ---- the domains and subtypes, set through the admin surface ----
            string fields = JsonSerializer.Serialize(new
            {
                overrides = new object[]
                {
                    new { column = "material", alias = "Material", domain = Materials },
                    new { column = "diameter", domain = Diameters },
                },
                subtypes = new
                {
                    field = "kind",
                    defaultCode = 1,
                    types = new object[]
                    {
                        new
                        {
                            code = 1,
                            name = "Main",
                            defaultValues = new { material = "DI", diameter = 300 },
                            domains = new Dictionary<string, object>
                            {
                                ["material"] = new
                                {
                                    type = "codedValue",
                                    name = "MainMaterial",
                                    codedValues = new object[]
                                    {
                                        new { code = "CU", name = "Copper" },
                                        new { code = "DI", name = "Ductile iron" },
                                    },
                                },
                            },
                        },
                        new { code = 2, name = "Lateral" },
                    },
                },
            });

            (HttpStatusCode set, string stored) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/fields", token!, fields);

            Assert.True(set == HttpStatusCode.OK, $"Setting domains and subtypes answered {(int)set}: {stored}");

            // The admin answer gives back what the Fields page will send again.
            JsonElement answer = JsonDocument.Parse(stored).RootElement;

            Assert.Equal("kind", answer.GetProperty("subtypes").GetProperty("field").GetString());
            Assert.Equal(
                "Material",
                answer.GetProperty("columns").EnumerateArray()
                    .Single(c => c.GetProperty("name").GetString() == "material")
                    .GetProperty("domain").GetProperty("name").GetString());

            // ---- the document says so, in the keys an ArcGIS client reads ----
            JsonElement document = await GetJsonAsync(feature);

            JsonElement material = document.GetProperty("fields").EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "material");

            Assert.Equal("codedValue", material.GetProperty("domain").GetProperty("type").GetString());
            Assert.Equal("kind", document.GetProperty("typeIdField").GetString());
            Assert.Equal("kind", document.GetProperty("subtypeField").GetString());
            Assert.Equal(1, document.GetProperty("defaultSubtypeCode").GetInt64());

            JsonElement main = document.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("id").GetInt64() == 1);

            Assert.Equal("MainMaterial", main.GetProperty("domains").GetProperty("material").GetProperty("name").GetString());

            JsonElement prototype = Assert.Single(main.GetProperty("templates").EnumerateArray())
                .GetProperty("prototype").GetProperty("attributes");

            Assert.Equal(1, prototype.GetProperty("kind").GetInt64());
            Assert.Equal("DI", prototype.GetProperty("material").GetString());

            // ---- ArcGIS applyEdits: inside is written, outside is refused by name ----
            long kept = Assert.Single(Ids(await EditAsync(
                root, token!, feature, "addFeatures", ("features", Point(kind: 2, material: "PVC", diameter: 110)))));

            JsonElement lead = Result(await EditAsync(
                root, token!, feature, "addFeatures", ("features", Point(kind: 2, material: "LEAD", diameter: 110))));

            Assert.False(lead.GetProperty("success").GetBoolean(), $"A material outside the domain was written: {lead}");
            Assert.Contains("'Material'", lead.ToString(), StringComparison.Ordinal);

            JsonElement wide = Result(await EditAsync(
                root, token!, feature, "addFeatures", ("features", Point(kind: 2, material: "CU", diameter: 5000))));

            Assert.False(wide.GetProperty("success").GetBoolean(), $"A diameter outside the range was written: {wide}");
            Assert.Contains("'Diameter'", wide.ToString(), StringComparison.Ordinal);

            JsonElement unknownKind = Result(await EditAsync(
                root, token!, feature, "addFeatures", ("features", Point(kind: 9, material: "CU", diameter: 110))));

            Assert.False(unknownKind.GetProperty("success").GetBoolean(), $"A subtype that does not exist was written: {unknownKind}");
            Assert.Contains("not one of this layer's subtypes", unknownKind.ToString(), StringComparison.Ordinal);

            // PVC is the column's own list, and Main narrows it away.
            JsonElement narrowed = Result(await EditAsync(
                root, token!, feature, "addFeatures", ("features", Point(kind: 1, material: "PVC", diameter: 300))));

            Assert.False(narrowed.GetProperty("success").GetBoolean(), $"Main took PVC: {narrowed}");
            Assert.Contains("for subtype 1 (Main)", narrowed.ToString(), StringComparison.Ordinal);

            long mainFeature = Assert.Single(Ids(await EditAsync(
                root, token!, feature, "addFeatures", ("features", Point(kind: 1, material: "CU", diameter: 300)))));

            // An update that does not say its subtype is checked against the one the feature is.
            JsonElement changed = Result(await EditAsync(
                root, token!, feature, "updateFeatures",
                ("features", $"[{{\"attributes\":{{\"objectid\":{Id(mainFeature)},\"material\":\"PVC\"}}}}]")));

            Assert.False(changed.GetProperty("success").GetBoolean(), $"Main was changed to PVC: {changed}");
            Assert.Contains("for subtype 1 (Main)", changed.ToString(), StringComparison.Ordinal);

            Assert.Equal("CU", (await AttributesAsync(feature, mainFeature)).GetProperty("material").GetString());

            // ---- the OGC API Features face: the same rule, in HTTP's words ----
            string items = $"{root}/ogc/features/v1/collections/{Uri.EscapeDataString(layer)}/items";

            (HttpStatusCode ogcLead, string ogcLeadWhy, _) = await OgcAsync(
                HttpMethod.Post,
                items,
                token!,
                "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[32.87,39.96]},"
                + "\"properties\":{\"kind\":2,\"material\":\"LEAD\",\"diameter\":110}}",
                "application/geo+json");

            Assert.True(ogcLead == HttpStatusCode.BadRequest, $"Over OGC, a material outside the domain answered {(int)ogcLead}: {ogcLeadWhy}");
            Assert.True(Detail(ogcLeadWhy).Contains("'Material'", StringComparison.Ordinal), $"Over OGC the refusal does not name the domain: {ogcLeadWhy}");

            (HttpStatusCode ogcWide, string ogcWideWhy, _) = await OgcAsync(
                HttpMethod.Patch, $"{items}/{Id(kept)}", token!, "{\"type\":\"Feature\",\"properties\":{\"diameter\":5000}}");

            Assert.True(ogcWide == HttpStatusCode.BadRequest, $"Over OGC, a diameter outside the range answered {(int)ogcWide}: {ogcWideWhy}");
            Assert.True(Detail(ogcWideWhy).Contains("'Diameter'", StringComparison.Ordinal), $"Over OGC the refusal does not name the domain: {ogcWideWhy}");

            (HttpStatusCode ogcKept, string ogcKeptWhy, _) = await OgcAsync(
                HttpMethod.Patch, $"{items}/{Id(kept)}", token!, "{\"type\":\"Feature\",\"properties\":{\"diameter\":600}}");

            Assert.True(ogcKept == HttpStatusCode.NoContent, $"Over OGC, a diameter inside the range answered {(int)ogcKept}: {ogcKeptWhy}");

            // ---- refused where they are set, and the refusals leave what was stored ----
            foreach ((string body, string expected) in ((string, string)[])
            [
                ("{\"overrides\":[{\"column\":\"objectid\",\"domain\":{\"type\":\"range\",\"name\":\"Id\",\"range\":[1,9]}}]}", "cannot have a domain"),
                ("{\"overrides\":[{\"column\":\"label\",\"domain\":{\"type\":\"range\",\"name\":\"R\",\"range\":[1,9]}}]}", "a range bounds a number or a date"),
                ("{\"overrides\":[{\"column\":\"diameter\",\"domain\":{\"type\":\"codedValue\",\"name\":\"C\",\"codedValues\":[{\"code\":\"A\",\"name\":\"a\"}]}}]}", "is text"),
                ("{\"overrides\":[],\"subtypes\":{\"field\":\"label\",\"types\":[{\"code\":1,\"name\":\"One\"}]}}", "A subtype column holds short or long integers"),
                ("{\"overrides\":[],\"subtypes\":{\"field\":\"kind\",\"defaultCode\":5,\"types\":[{\"code\":1,\"name\":\"One\"}]}}", "default subtype 5"),
                ("{\"overrides\":[{\"column\":\"kind\",\"hidden\":true}],\"subtypes\":{\"field\":\"kind\",\"types\":[{\"code\":1,\"name\":\"One\"}]}}", "it is hidden"),
                ("{\"overrides\":[{\"column\":\"kind\",\"domain\":{\"type\":\"range\",\"name\":\"K\",\"range\":[1,2]}}],\"subtypes\":{\"field\":\"kind\",\"types\":[{\"code\":1,\"name\":\"One\"}]}}", "is the subtype column"),
                ("{\"overrides\":[{\"column\":\"material\",\"domain\":" + JsonSerializer.Serialize(Materials) + "}],\"subtypes\":{\"field\":\"kind\",\"types\":[{\"code\":1,\"name\":\"One\",\"defaultValues\":{\"material\":\"LEAD\"}}]}}", "does not allow"),
            ])
            {
                (HttpStatusCode status, string why) = await RequestAsync(
                    HttpMethod.Put, $"{root}/admin/layers/{layer}/fields", token!, body);

                Assert.True(status == HttpStatusCode.BadRequest, $"Setting {body} answered {(int)status}, and should have been refused: {why}");
                Assert.Contains(expected, why, StringComparison.Ordinal);
            }

            JsonElement after = await GetJsonAsync(feature);

            Assert.Equal("kind", after.GetProperty("subtypeField").GetString());
            Assert.Equal(2, after.GetProperty("types").GetArrayLength());

            // ---- the subtype column is held: dropping it would take every subtype with it ----
            (HttpStatusCode dropped, string droppedWhy) = await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/hosted/{layer}/fields/kind", token!, json: null);

            Assert.False(dropped is HttpStatusCode.OK or HttpStatusCode.NoContent, $"The subtype column was dropped: {droppedWhy}");
            Assert.Contains("subtype column", droppedWhy, StringComparison.Ordinal);

            // ---- and removing them is a save without them ----
            (HttpStatusCode cleared, string clearedWhy) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/fields", token!, "{\"overrides\":[]}");

            Assert.True(cleared == HttpStatusCode.OK, $"Clearing answered {(int)cleared}: {clearedWhy}");

            JsonElement plain = await GetJsonAsync(feature);

            Assert.Equal(JsonValueKind.Null, plain.GetProperty("subtypeField").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                plain.GetProperty("fields").EnumerateArray()
                    .Single(f => f.GetProperty("name").GetString() == "material").GetProperty("domain").ValueKind);

            // With nothing reported, nothing is refused.
            Assert.Single(Ids(await EditAsync(
                root, token!, feature, "addFeatures", ("features", Point(kind: 9, material: "LEAD", diameter: 5000)))));
        }
        finally
        {
            await RequestAsync(
                HttpMethod.Delete,
                $"{root}/admin/featureservices/{service}?folder={folder}&drop=true",
                token!,
                json: null);

            // ADR-087: the save made them shared domains, which outlive the layer as a geodatabase's do.
            await DeleteSharedAsync(root, token!, "Material", "Diameter", "MainMaterial");
        }
    }

    [Fact]
    public async Task A_shared_domain_is_one_object_every_field_points_at_and_one_edit_reaches_them_all()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string name = "zz_shared_" + suffix;

        object Pipes(params string[] codes) => new
        {
            type = "codedValue",
            name,
            codedValues = codes.Select(c => new { code = c, name = c }).ToArray(),
        };

        List<(string Folder, string Service, string Feature, string Layer)> made = [];

        try
        {
            foreach (string which in (string[])["a", "b"])
            {
                string layer = $"zz_adr087_{which}_{suffix}";

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
                            new { name = "material", type = "text", nullable = true },
                            new { name = "diameter", type = "integer", nullable = true },
                        },
                        sharing = "public",
                    }));

                Assert.True(defined == HttpStatusCode.Created, $"Defining {layer} answered {(int)defined}: {design}");

                string feature = JsonDocument.Parse(design).RootElement
                    .GetProperty("services").GetProperty("feature").GetString()!;
                string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);

                made.Add((parts[2], parts[3], feature, layer));

                // Both layers are given the same domain by name, as an import or a hand would.
                (HttpStatusCode set, string said) = await RequestAsync(
                    HttpMethod.Put,
                    $"{root}/admin/layers/{Uri.EscapeDataString(layer)}/fields",
                    token!,
                    JsonSerializer.Serialize(new { overrides = new object[] { new { column = "material", domain = Pipes("CU", "PVC") } } }));

                Assert.True(set == HttpStatusCode.OK, $"Giving {layer} the domain answered {(int)set}: {said}");
            }

            // ---- one domain, two fields ----
            JsonElement shared = await SharedNamedAsync(root, token!, name);
            string id = shared.GetProperty("id").GetString()!;

            Assert.Equal(2, shared.GetProperty("uses").GetArrayLength());
            Assert.True(shared.GetProperty("mayChange").GetBoolean(), "Its creator may not change it.");

            // ---- one edit reaches both layers, in their documents and in what they refuse ----
            (HttpStatusCode edited, string editedSaid) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/domains/{id}", token!,
                JsonSerializer.Serialize(new { domain = Pipes("CU", "PVC", "DI") }));

            Assert.True(edited == HttpStatusCode.OK, $"Changing the shared domain answered {(int)edited}: {editedSaid}");

            foreach ((_, _, string feature, _) in made)
            {
                JsonElement material = (await GetJsonAsync(feature)).GetProperty("fields").EnumerateArray()
                    .Single(f => f.GetProperty("name").GetString() == "material").GetProperty("domain");

                Assert.Equal(name, material.GetProperty("name").GetString());
                Assert.Equal(3, material.GetProperty("codedValues").GetArrayLength());

                Assert.Single(Ids(await EditAsync(
                    root, token!, feature, "addFeatures", ("features", Point(kind: 1, material: "DI", diameter: 100)))));

                JsonElement lead = Result(await EditAsync(
                    root, token!, feature, "addFeatures", ("features", Point(kind: 1, material: "LEAD", diameter: 100))));

                Assert.False(lead.GetProperty("success").GetBoolean(), $"A value outside the shared domain was written: {lead}");
            }

            // ---- a change that does not fit a field that uses it is refused, naming it ----
            (HttpStatusCode unfit, string unfitWhy) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/domains/{id}", token!,
                JsonSerializer.Serialize(new { domain = new { type = "range", name, range = TenAtMost } }));

            Assert.Equal(HttpStatusCode.BadRequest, unfit);
            Assert.Contains("'material'", unfitWhy, StringComparison.Ordinal);

            // ---- the same name with other values, through a layer, is refused rather than renamed ----
            (HttpStatusCode clash, string clashWhy) = await RequestAsync(
                HttpMethod.Put,
                $"{root}/admin/layers/{Uri.EscapeDataString(made[1].Layer)}/fields",
                token!,
                JsonSerializer.Serialize(new { overrides = new object[] { new { column = "material", domain = Pipes("XX") } } }));

            Assert.Equal(HttpStatusCode.Conflict, clash);
            Assert.Contains(name, clashWhy, StringComparison.Ordinal);

            // ---- one in use is not deleted, and the refusal says where it is used ----
            (HttpStatusCode inUse, string inUseWhy) = await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/domains/{id}", token!, json: null);

            Assert.Equal(HttpStatusCode.Conflict, inUse);
            Assert.Contains(made[0].Layer, inUseWhy, StringComparison.Ordinal);
            Assert.Contains(made[1].Layer, inUseWhy, StringComparison.Ordinal);

            // ---- taken off both fields, it goes ----
            foreach ((_, _, _, string layer) in made)
            {
                (HttpStatusCode cleared, string clearedSaid) = await RequestAsync(
                    HttpMethod.Put,
                    $"{root}/admin/layers/{Uri.EscapeDataString(layer)}/fields",
                    token!,
                    JsonSerializer.Serialize(new { overrides = Array.Empty<object>() }));

                Assert.True(cleared == HttpStatusCode.OK, $"Clearing {layer} answered {(int)cleared}: {clearedSaid}");
            }

            (HttpStatusCode deleted, string deletedSaid) = await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/domains/{id}", token!, json: null);

            Assert.True(deleted == HttpStatusCode.NoContent, $"Deleting the unused domain answered {(int)deleted}: {deletedSaid}");
        }
        finally
        {
            foreach ((string folder, string service, _, _) in made)
            {
                await RequestAsync(
                    HttpMethod.Delete, $"{root}/admin/featureservices/{service}?folder={folder}&drop=true", token!, json: null);
            }

            await DeleteSharedAsync(root, token!, name);
        }
    }

    /// <summary>The shared domain of this name, from the admin listing.</summary>
    private async Task<JsonElement> SharedNamedAsync(string root, string token, string name)
    {
        (HttpStatusCode listed, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/domains", token, json: null);

        Assert.True(listed == HttpStatusCode.OK, $"Listing shared domains answered {(int)listed}: {body}");

        JsonElement[] named = [.. JsonDocument.Parse(body).RootElement.GetProperty("domains").EnumerateArray()
            .Where(d => string.Equals(d.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Clone())];

        return Assert.Single(named);
    }

    /// <summary>Deletes the shared domains of these names that nothing uses; one still used is left.</summary>
    private async Task DeleteSharedAsync(string root, string token, params string[] names)
    {
        (HttpStatusCode listed, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/domains", token, json: null);

        if (listed != HttpStatusCode.OK)
        {
            return;
        }

        foreach (JsonElement domain in JsonDocument.Parse(body).RootElement.GetProperty("domains").EnumerateArray())
        {
            if (names.Contains(domain.GetProperty("name").GetString(), StringComparer.OrdinalIgnoreCase))
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/admin/domains/{domain.GetProperty("id").GetString()}", token, json: null);
            }
        }
    }

    /// <summary>One feature's attributes, read through the ArcGIS query.</summary>
    private async Task<JsonElement> AttributesAsync(string feature, long id)
    {
        JsonElement written = await GetJsonAsync(
            $"{feature}/query?where=objectid%3D{Id(id)}&outFields=kind,material,diameter");

        return written.GetProperty("features")[0].GetProperty("attributes");
    }

    /// <summary>Sends an OGC API Features write.</summary>
    private async Task<(HttpStatusCode Status, string Body, string? Location)> OgcAsync(
        HttpMethod method, string url, string token, string? json, string contentType = MergePatch)
    {
        using HttpRequestMessage request = new(method, new Uri(url));

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.Location?.ToString());
    }

    /// <summary>Posts one of the single-operation edit endpoints.</summary>
    private async Task<JsonElement> EditAsync(
        string root, string token, string feature, string operation, params (string Key, string Value)[] fields)
    {
        using FormUrlEncodedContent content = new(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("rollbackOnFailure", "false"))
                .Append(new KeyValuePair<string, string>("f", "json")));

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{feature}/{operation}"))
        {
            Content = content,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{operation} answered {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The object ids an addFeatures answer reports, having asserted each succeeded.</summary>
    private static List<long> Ids(JsonElement answer) =>
        answer.GetProperty("addResults").EnumerateArray()
            .Select(r =>
            {
                Assert.True(r.GetProperty("success").GetBoolean(), $"An add failed: {answer}");
                return r.GetProperty("objectId").GetInt64();
            })
            .ToList();

    /// <summary>The one result of a single-operation answer, whichever it is.</summary>
    private static JsonElement Result(JsonElement answer)
    {
        foreach (string name in (string[])["updateResults", "deleteResults", "addResults"])
        {
            if (answer.TryGetProperty(name, out JsonElement results))
            {
                return Assert.Single(results.EnumerateArray());
            }
        }

        throw new Xunit.Sdk.XunitException($"No results array in the answer: {answer}");
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A problem document's <c>detail</c>, decoded — the serializer escapes an apostrophe as
    /// <c>'</c>, so the raw body never contains the quoted name it carries.
    /// </summary>
    private static string Detail(string body)
    {
        try
        {
            using JsonDocument problem = JsonDocument.Parse(body);
            return problem.RootElement.TryGetProperty("detail", out JsonElement detail)
                ? detail.GetString() ?? body
                : body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string Point(int kind, string material, int diameter) =>
        "[{\"geometry\":{\"x\":1000,\"y\":2000,\"spatialReference\":{\"wkid\":3857}},"
        + $"\"attributes\":{{\"kind\":{kind},\"material\":\"{material}\",\"diameter\":{diameter}}}}}]";
}
