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
/// ADR-310: ArcGIS's <c>append</c> on the service, over several layers and answered with each layer's count; and the
/// layer-level <c>append</c> on a registered layer, JSON Lines among its formats.
/// </summary>
/// <remarks>
/// <b>Each test makes what it changes</b> under <c>zz_</c> names and removes it. The one exception is the registered
/// table, which belongs to the fixture: the features this test adds are deleted again through <c>applyEdits</c>, and
/// the service that publishes it is taken down without dropping anything.
/// </remarks>
[Collection("catalogue walk")]
public sealed class AppendServiceAndRegisteredTests : ArcGisClient
{
    private static string Points(params string[] names) =>
        """{"type":"FeatureCollection","features":["""
        + string.Join(",", names.Select((n, i) => string.Create(CultureInfo.InvariantCulture,
            $$$"""{"type":"Feature","geometry":{"type":"Point","coordinates":[{{{28.97 + (i * 0.001)}}},41.0]},"properties":{"name":"{{{n}}}"}}""")))
        + "]}";

    private static string Squares(params string[] names) =>
        """{"type":"FeatureCollection","features":["""
        + string.Join(",", names.Select((n, i) => string.Create(CultureInfo.InvariantCulture,
            $$$"""{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[{{{28.97 + (i * 0.001)}}},41.0],[{{{28.97 + (i * 0.001)}}},41.0002],[{{{28.9703 + (i * 0.001)}}},41.0002],[{{{28.9703 + (i * 0.001)}}},41.0],[{{{28.97 + (i * 0.001)}}},41.0]]]},"properties":{"name":"{{{n}}}"}}""")))
        + "]}";

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string token, HttpMethod method, string url, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<string> UploadAsync(string service, string token, string text, string fileName)
    {
        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(Encoding.UTF8.GetBytes(text));

        form.Add(bytes, "file", fileName);
        form.Add(new StringContent("json"), "f");

        (HttpStatusCode status, string body) = await SendAsync(token, HttpMethod.Post, $"{service}/uploads/upload", form);

        Assert.True(status == HttpStatusCode.OK, $"uploads/upload answered {(int)status}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("item").GetProperty("itemID").GetString()!;
    }

    private Task<(HttpStatusCode Status, string Body)> PostFormAsync(string token, string url, Dictionary<string, string> fields) =>
        SendAsync(token, HttpMethod.Post, url, new FormUrlEncodedContent(fields));

    private async Task<int> CountAsync(string token, string service, int layer, string where = "1=1")
    {
        (HttpStatusCode status, string body) = await SendAsync(token, HttpMethod.Get,
            $"{service}/{layer}/query?where={Uri.EscapeDataString(where)}&returnCountOnly=true&f=json");

        Assert.True(status == HttpStatusCode.OK, $"Counting {service}/{layer} answered {(int)status}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("count").GetInt32();
    }

    private async Task DefineAsync(string token, string root, string name, string service)
    {
        string design = JsonSerializer.Serialize(new
        {
            name,
            geometryType = "Point",
            fields = new[] { new { name = "name", type = "Text" } },
            sharing = "private",
            serviceName = service,
        });

        (HttpStatusCode status, string body) = await SendAsync(
            token, HttpMethod.Post, $"{root}/admin/hosted/define", new StringContent(design, Encoding.UTF8, "application/json"));

        Assert.True(status is HttpStatusCode.Created or HttpStatusCode.OK, $"Defining '{name}' answered {(int)status}: {body}");
    }

    private async Task DropServiceAsync(string token, string root, string service) =>
        await SendAsync(token, HttpMethod.Delete, $"{root}/admin/featureservices/{service}?folder=hosted&drop=true");

    [Fact]
    public async Task The_service_level_append_writes_each_layer_and_answers_with_the_layers_array()
    {
        string root = await RequireServerAsync();
        string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
        string service = $"zz_svcapp_{Guid.NewGuid():N}"[..20];

        await DefineAsync(token, root, service + "_a", service);
        await DefineAsync(token, root, service + "_b", service);

        string address = $"{root}/rest/services/hosted/{service}/FeatureServer";

        try
        {
            // The service says it before it is asked, and only because the route works (below).
            (HttpStatusCode got, string document) = await SendAsync(token, HttpMethod.Get, $"{address}?f=json");
            Assert.True(got == HttpStatusCode.OK, document);
            JsonElement described = JsonDocument.Parse(document).RootElement;
            Assert.True(described.GetProperty("supportsAppend").GetBoolean(), "A hosted service does not say supportsAppend.");
            Assert.Equal("geojson,jsonl,shapefile,csv,excel", described.GetProperty("supportedAppendFormats").GetString());

            string id = await UploadAsync(address, token, Points("one", "two", "three").Replace("\"name\"", "\"label\""), "more.geojson");

            // Two layers and one file: the mappings have to say that is meant. Nothing is written.
            (HttpStatusCode unsaid, string unsaidBody) = await PostFormAsync(token, $"{address}/append", new()
            {
                ["layers"] = "[0,1]", ["appendUploadId"] = id, ["appendUploadFormat"] = "geojson", ["f"] = "json",
            });
            Assert.True(unsaid == HttpStatusCode.BadRequest, $"Two layers without layerMappings answered {(int)unsaid}: {unsaidBody}");
            Assert.Contains("layerMappings", unsaidBody, StringComparison.Ordinal);
            Assert.Equal(0, await CountAsync(token, address, 0));
            Assert.Equal(0, await CountAsync(token, address, 1));

            // A layer the service does not have is refused before any write.
            (HttpStatusCode missing, string missingBody) = await PostFormAsync(token, $"{address}/append", new()
            {
                ["layers"] = "[0,7]", ["layerMappings"] = "[{\"id\":0},{\"id\":7}]",
                ["appendUploadId"] = id, ["appendUploadFormat"] = "geojson", ["f"] = "json",
            });
            Assert.True(missing == HttpStatusCode.NotFound, $"A layer that is not there answered {(int)missing}: {missingBody}");
            Assert.Equal(0, await CountAsync(token, address, 0));

            // layerMappings names a layer that layers does not: refused as the reference describes the pair.
            (HttpStatusCode stray, _) = await PostFormAsync(token, $"{address}/append", new()
            {
                ["layers"] = "[0]", ["layerMappings"] = "[{\"id\":1}]",
                ["appendUploadId"] = id, ["appendUploadFormat"] = "geojson", ["f"] = "json",
            });
            Assert.Equal(HttpStatusCode.BadRequest, stray);

            (HttpStatusCode appended, string body) = await PostFormAsync(token, $"{address}/append", new()
            {
                ["layers"] = "[0,1]",
                ["layerMappings"] =
                    "[{\"id\":0,\"sourceTableName\":\"more\",\"fieldMappings\":[{\"name\":\"name\",\"source\":\"label\"}]},"
                    + "{\"id\":1,\"fieldMappings\":[{\"name\":\"name\",\"source\":\"label\"}]}]",
                ["appendUploadId"] = id, ["appendUploadFormat"] = "geojson", ["f"] = "json",
            });

            Assert.True(appended == HttpStatusCode.OK, $"service append answered {(int)appended}: {body}");

            JsonElement said = JsonDocument.Parse(body).RootElement;

            Assert.Equal("Completed", said.GetProperty("status").GetString());
            Assert.Equal("append", said.GetProperty("operationName").GetString());
            Assert.Equal(6, said.GetProperty("recordCount").GetInt32());

            JsonElement[] layers = [.. said.GetProperty("layers").EnumerateArray()];

            Assert.Equal(2, layers.Length);
            Assert.Equal([0, 1], layers.Select(l => l.GetProperty("id").GetInt32()));
            Assert.All(layers, l =>
            {
                Assert.Equal(3, l.GetProperty("recordCount").GetInt32());
                Assert.Equal(0, l.GetProperty("attachmentCount").GetInt32());
                Assert.False(string.IsNullOrEmpty(l.GetProperty("layerName").GetString()));
            });

            // What a client sees afterwards, and the mapping that put 'label' into 'name'.
            Assert.Equal(3, await CountAsync(token, address, 0, "name IS NOT NULL"));
            Assert.Equal(3, await CountAsync(token, address, 1, "name IS NOT NULL"));

            // The upload is spent by one append, as the layer's is.
            (HttpStatusCode again, _) = await PostFormAsync(token, $"{address}/append", new()
            {
                ["layers"] = "[0]", ["appendUploadId"] = id, ["appendUploadFormat"] = "geojson", ["f"] = "json",
            });
            Assert.Equal(HttpStatusCode.BadRequest, again);

            // A service of one layer needs no layers at all, and a stranger who cannot publish is refused.
            string second = await UploadAsync(address, token, Points("four"), "four.geojson");
            (HttpStatusCode anonymous, _) = await SendAsync("not-a-token", HttpMethod.Post, $"{address}/append",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["appendUploadId"] = second, ["appendUploadFormat"] = "geojson" }));
            Assert.True((int)anonymous is 401 or 403 or 404 or 498,
                $"A bad token answered {(int)anonymous}.");
            Assert.Equal(3, await CountAsync(token, address, 0));
        }
        finally
        {
            await DropServiceAsync(token, root, service);
        }
    }

    [Fact]
    public async Task A_JSON_Lines_file_is_appended_to_a_hosted_layer_and_a_bad_line_is_named()
    {
        string root = await RequireServerAsync();
        string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
        string service = $"zz_jsonl_{Guid.NewGuid():N}"[..20];

        await DefineAsync(token, root, service, service);

        string address = $"{root}/rest/services/hosted/{service}/FeatureServer";

        try
        {
            string lines = string.Join("\n",
                JsonDocument.Parse(Points("l1", "l2", "l3")).RootElement.GetProperty("features").EnumerateArray()
                    .Select(f => f.GetRawText())) + "\n\n";

            string id = await UploadAsync(address, token, lines, "rows.jsonl");

            (HttpStatusCode appended, string body) = await PostFormAsync(token, $"{address}/0/append", new()
            {
                ["appendUploadId"] = id, ["appendUploadFormat"] = "jsonl", ["f"] = "json",
            });

            Assert.True(appended == HttpStatusCode.OK, $"jsonl append answered {(int)appended}: {body}");
            Assert.Equal(3, JsonDocument.Parse(body).RootElement.GetProperty("recordCount").GetInt32());
            Assert.Equal(3, await CountAsync(token, address, 0));

            string broken = await UploadAsync(address, token, lines.Split('\n')[0] + "\n{not json}\n", "broken.jsonl");

            (HttpStatusCode refused, string refusedBody) = await PostFormAsync(token, $"{address}/0/append", new()
            {
                ["appendUploadId"] = broken, ["appendUploadFormat"] = "jsonl", ["f"] = "json",
            });

            Assert.True(refused == HttpStatusCode.BadRequest, $"A bad line answered {(int)refused}: {refusedBody}");
            Assert.Contains("Line 2", refusedBody, StringComparison.Ordinal);
            Assert.Equal(3, await CountAsync(token, address, 0));
        }
        finally
        {
            await DropServiceAsync(token, root, service);
        }
    }

    [Fact]
    public async Task A_registered_layer_is_appended_to_through_its_writer_and_truncate_and_upsert_are_refused()
    {
        string root = await RequireServerAsync();
        string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
        string registered = $"zz_regapp_{Guid.NewGuid():N}"[..20];

        string address = $"{root}/rest/services/{registered}/FeatureServer";

        // One of the fixture's free tables, which have an `objectid` and a polygon column. Published with the object id
        // named, as a registered layer is published to be edited through ArcGIS.
        bool found = false;
        string last = "none";
        string? datastore = await DatastoreIdAsync();

        Assert.False(datastore is null, "No datastore data source is registered.");

        for (int skip = 0; skip < 80 && !found; skip++)
        {
            if (await FreeTableAsync(skip) is not { } free) break;

            if (free.Schema != "cifree") continue;

            (int published, string publishBody) = await AdminAsync(HttpMethod.Post, "/admin/publish", JsonSerializer.Serialize(new
            {
                name = registered,
                sharing = "private",
                nodes = new[]
                {
                    new
                    {
                        layer = new
                        {
                            name = registered, dataSourceId = datastore, schemaName = free.Schema, tableName = free.Table,
                            geometryColumn = free.Geometry, geometryType = free.Type, identityColumn = free.Identity,
                            objectIdColumn = free.Identity, srid = free.Srid,
                        },
                    },
                },
            }));

            Assert.True(published is 200 or 201, $"Publishing a registered layer answered {published}: {publishBody}");

            (HttpStatusCode served, string servedBody) = await SendAsync(token, HttpMethod.Get, $"{address}/0/query?where=1%3D1&returnCountOnly=true&f=json");
            found = served == HttpStatusCode.OK;
            last = $"{free.Schema}.{free.Table} {(int)served}: {servedBody}";

            if (!found) await UnpublishAsync(registered, registered);
        }

        Assert.True(found, "The fixture has no free table the ArcGIS surface serves; the last was " + last);
        string tag = "regapp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            // The layer says it takes an append — the caller may add features and the source takes writes.
            (_, string layerBody) = await SendAsync(token, HttpMethod.Get, $"{address}/0?f=json");
            JsonElement layer = JsonDocument.Parse(layerBody).RootElement;
            Assert.True(layer.GetProperty("supportsAppend").GetBoolean(), $"A writable registered layer does not say supportsAppend: {layerBody}");
            Assert.Contains("jsonl", layer.GetProperty("supportedAppendFormats").GetString()!, StringComparison.Ordinal);

            int before = await CountAsync(token, address, 0);

            string id = await UploadAsync(address, token, Squares(tag + "-a", tag + "-b"), "more.geojson");

            (HttpStatusCode appended, string body) = await PostFormAsync(token, $"{address}/0/append", new()
            {
                ["appendUploadId"] = id, ["appendUploadFormat"] = "geojson", ["f"] = "json",
            });

            Assert.True(appended == HttpStatusCode.OK, $"append to a registered layer answered {(int)appended}: {body}");

            JsonElement said = JsonDocument.Parse(body).RootElement;
            Assert.Equal("Completed", said.GetProperty("status").GetString());
            Assert.Equal(2, said.GetProperty("recordCount").GetInt32());
            Assert.Equal(before + 2, await CountAsync(token, address, 0));
            Assert.Equal(2, await CountAsync(token, address, 0, $"name LIKE '{tag}%'"));

            // JSON Lines too — the same reader, the same writer.
            string lines = string.Join("\n", JsonDocument.Parse(Squares(tag + "-c")).RootElement.GetProperty("features")
                .EnumerateArray().Select(f => f.GetRawText()));
            string jsonl = await UploadAsync(address, token, lines, "rows.jsonl");

            (HttpStatusCode viaLines, string linesBody) = await PostFormAsync(token, $"{address}/0/append", new()
            {
                ["appendUploadId"] = jsonl, ["appendUploadFormat"] = "jsonl", ["f"] = "json",
            });

            Assert.True(viaLines == HttpStatusCode.OK, $"jsonl append to a registered layer answered {(int)viaLines}: {linesBody}");
            Assert.Equal(3, await CountAsync(token, address, 0, $"name LIKE '{tag}%'"));

            // A table is the database owner's to empty; an update is an edit. Both refused, and nothing moves.
            string again = await UploadAsync(address, token, Squares(tag + "-d"), "again.geojson");

            foreach ((string field, string value, string says) in new[]
            {
                ("truncateExisting", "true", "truncateExisting"),
                ("upsert", "true", "upsert"),
            })
            {
                (HttpStatusCode refused, string refusedBody) = await PostFormAsync(token, $"{address}/0/append", new()
                {
                    ["appendUploadId"] = again, ["appendUploadFormat"] = "geojson", [field] = value, ["upsertMatchingField"] = "name", ["f"] = "json",
                });

                Assert.True(refused == HttpStatusCode.BadRequest, $"{field} on a registered layer answered {(int)refused}: {refusedBody}");
                Assert.Contains(says, refusedBody, StringComparison.Ordinal);
                Assert.Equal(before + 3, await CountAsync(token, address, 0));
            }

            // A file whose geometry is not the layer's is refused by the writer, and the layer is as it was.
            string wrong = await UploadAsync(address, token, Points(tag + "-e"), "points.geojson");

            (HttpStatusCode mismatch, string mismatchBody) = await PostFormAsync(token, $"{address}/0/append", new()
            {
                ["appendUploadId"] = wrong, ["appendUploadFormat"] = "geojson", ["f"] = "json",
            });

            Assert.True(mismatch == HttpStatusCode.BadRequest, $"Points into a polygon layer answered {(int)mismatch}: {mismatchBody}");
            Assert.Equal(before + 3, await CountAsync(token, address, 0));
        }
        finally
        {
            // The table is the fixture's: take back what was added, then the service.
            (_, string ids) = await SendAsync(token, HttpMethod.Get,
                $"{address}/0/query?where={Uri.EscapeDataString($"name LIKE '{tag}%'")}&returnIdsOnly=true&f=json");

            try
            {
                string[] objectIds = [.. JsonDocument.Parse(ids).RootElement.GetProperty("objectIds").EnumerateArray().Select(i => i.GetInt64().ToString(CultureInfo.InvariantCulture))];

                if (objectIds.Length > 0)
                {
                    await PostFormAsync(token, $"{address}/0/deleteFeatures", new() { ["objectIds"] = string.Join(",", objectIds), ["f"] = "json" });
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // The query did not answer ids; the unpublish below still runs.
            }

            await UnpublishAsync(registered, registered);
        }
    }
}
